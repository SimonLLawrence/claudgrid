using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClaudGrid.Config;
using ClaudGrid.Models;
using Microsoft.Extensions.Logging;

namespace ClaudGrid.Exchange;

/// <summary>
/// Hyperliquid REST API client.
///
/// Endpoints:
///   POST /info    — read-only market and account data (no auth)
///   POST /exchange — signed order actions
///
/// Docs: https://hyperliquid.gitbook.io/hyperliquid-docs/for-developers/api
/// </summary>
public sealed class HyperliquidClient : IExchangeClient
{
    private readonly HttpClient _http;
    private readonly BotConfig _config;
    private readonly HyperliquidSigner _signer;
    private readonly ILogger<HyperliquidClient> _logger;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HyperliquidClient(
        HttpClient http,
        BotConfig config,
        HyperliquidSigner signer,
        ILogger<HyperliquidClient> logger)
    {
        _http = http;
        _config = config;
        _signer = signer;
        _logger = logger;
        _baseUrl = config.IsMainnet
            ? "https://api.hyperliquid.xyz"
            : "https://api.hyperliquid-testnet.xyz";
    }

    // ── Public interface ──────────────────────────────────────────────────────

    public async Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken ct = default)
    {
        string midsJson = await PostInfoAsync(new { type = "allMids" }, ct);
        var mids = JsonSerializer.Deserialize<Dictionary<string, string>>(midsJson, _jsonOpts)
                   ?? throw new InvalidOperationException("allMids returned null");

        if (!mids.TryGetValue(symbol, out string? midStr) || !decimal.TryParse(midStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal mid))
            throw new InvalidOperationException($"Symbol {symbol} not found in allMids");

        string l2Json = await PostInfoAsync(new { type = "l2Book", coin = symbol }, ct);
        var l2 = JsonNode.Parse(l2Json);

        decimal bid = ParseBestPrice(l2?["levels"]?[0]);
        decimal ask = ParseBestPrice(l2?["levels"]?[1]);

        return new MarketData
        {
            Symbol = symbol,
            MidPrice = mid,
            BidPrice = bid > 0 ? bid : mid,
            AskPrice = ask > 0 ? ask : mid,
            Timestamp = DateTime.UtcNow
        };
    }

    public async Task<AccountState> GetAccountStateAsync(CancellationToken ct = default)
    {
        string json = await PostInfoAsync(new { type = "clearinghouseState", user = _config.WalletAddress }, ct);
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("clearinghouseState null");

        decimal equity = ParseDecimal(node["marginSummary"]?["accountValue"]);
        decimal margin = ParseDecimal(node["marginSummary"]?["totalMarginUsed"]);

        var positions = new List<PositionInfo>();
        var assetPositions = node["assetPositions"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();
        foreach (var pos in assetPositions)
        {
            var p = pos?["position"];
            if (p == null) continue;
            string coin = p["coin"]?.GetValue<string>() ?? "";
            decimal size = ParseDecimal(p["szi"]);
            decimal entry = ParseDecimal(p["entryPx"]);
            decimal upnl = ParseDecimal(p["unrealizedPnl"]);
            positions.Add(new PositionInfo { Symbol = coin, Size = size, EntryPrice = entry, UnrealizedPnl = upnl });
        }

        return new AccountState
        {
            TotalEquity = equity,
            MarginUsed = margin,
            AvailableBalance = equity - margin,
            Positions = positions
        };
    }

    public async Task<List<Order>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        string json = await PostInfoAsync(new { type = "openOrders", user = _config.WalletAddress }, ct);
        var arr = JsonNode.Parse(json)?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();

        var orders = new List<Order>();
        foreach (var o in arr)
        {
            if (o == null) continue;
            orders.Add(new Order
            {
                Id = o["oid"]?.GetValue<long>() ?? 0,
                Symbol = o["coin"]?.GetValue<string>() ?? "",
                Side = (o["side"]?.GetValue<string>() ?? "B") == "B" ? OrderSide.Buy : OrderSide.Sell,
                Price = ParseDecimal(o["limitPx"]),
                Size = ParseDecimal(o["sz"]),
                FilledSize = ParseDecimal(o["origSz"]) - ParseDecimal(o["sz"]),
                Status = OrderStatus.Open,
                CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(o["timestamp"]?.GetValue<long>() ?? 0).UtcDateTime
            });
        }
        return orders;
    }

    public async Task<long> PlaceLimitOrderAsync(
        string symbol,
        int assetIndex,
        OrderSide side,
        decimal price,
        decimal size,
        CancellationToken ct = default)
    {
        long nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Build the order wire object
        var orderWire = new Dictionary<string, object>
        {
            ["a"] = assetIndex,
            ["b"] = side == OrderSide.Buy,
            ["p"] = FloatToWire(price),
            ["s"] = FloatToWire(size),
            ["r"] = false,
            ["t"] = new Dictionary<string, object>
            {
                ["limit"] = new Dictionary<string, object> { ["tif"] = "Gtc" }
            }
        };

        var action = new Dictionary<string, object>
        {
            ["type"] = "order",
            ["orders"] = new List<object> { orderWire },
            ["grouping"] = "na"
        };

        // L1 actions (orders) are signed via phantom-agent EIP-712.
        // The action dict itself contains only {type, orders, grouping} — no extra metadata.
        byte[] msgPack = MsgPackEncode(action);
        var (r, s, v) = _signer.SignAction(msgPack, nonce);

        string responseJson = await PostExchangeAsync(action, nonce, r, s, v, ct);
        return ParseOrderId(responseJson);
    }

    public async Task<bool> CancelOrderAsync(int assetIndex, long orderId, CancellationToken ct = default)
    {
        long nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var action = new Dictionary<string, object>
        {
            ["type"] = "cancel",
            ["cancels"] = new List<object>
            {
                new Dictionary<string, object> { ["a"] = assetIndex, ["o"] = orderId }
            }
        };

        // L1 cancel — no metadata in action dict.
        byte[] msgPack = MsgPackEncode(action);
        var (r, s, v) = _signer.SignAction(msgPack, nonce);
        string json = await PostExchangeAsync(action, nonce, r, s, v, ct);

        var node = JsonNode.Parse(json);
        string? status = node?["response"]?["data"]?["statuses"]?[0]?.GetValue<string>();
        return status == "success";
    }

    public async Task<int> CancelAllOrdersAsync(int assetIndex, CancellationToken ct = default)
    {
        var openOrders = await GetOpenOrdersAsync(ct);
        int cancelled = 0;
        foreach (var o in openOrders.Where(o => o.Status == OrderStatus.Open))
        {
            if (await CancelOrderAsync(assetIndex, o.Id, ct))
                cancelled++;
        }
        return cancelled;
    }

    public async Task<int> GetAssetIndexAsync(string symbol, CancellationToken ct = default)
    {
        string json = await PostInfoAsync(new { type = "meta" }, ct);
        var node = JsonNode.Parse(json);
        var universe = node?["universe"]?.AsArray();
        if (universe != null)
        {
            for (int i = 0; i < universe.Count; i++)
            {
                if (universe[i]?["name"]?.GetValue<string>() == symbol)
                    return i;
            }
        }
        throw new InvalidOperationException($"Symbol '{symbol}' not found in Hyperliquid meta");
    }

    public async Task<decimal> GetSpotUsdcBalanceAsync(CancellationToken ct = default)
    {
        string json = await PostInfoAsync(new { type = "spotClearinghouseState", user = _config.WalletAddress }, ct);
        var node = JsonNode.Parse(json);
        foreach (var balance in node?["balances"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
        {
            if (balance?["coin"]?.GetValue<string>() == "USDC")
                return ParseDecimal(balance["total"]);
        }
        return 0m;
    }

    public async Task TransferSpotToPerpsAsync(decimal amount, CancellationToken ct = default)
    {
        long nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var action = new Dictionary<string, object>
        {
            ["type"] = "usdClassTransfer",
            ["amount"] = FloatToWire(amount),
            ["toPerp"] = true
        };

        AppendActionMetadata(action, nonce);
        byte[] msgPack = MsgPackEncode(action);
        var (r, s, v) = _signer.SignAction(msgPack, nonce);
        string response = await PostExchangeAsync(action, nonce, r, s, v, ct);
        _logger.LogInformation("Spot→Perps transfer response: {Response}", response);
    }

    // ── HTTP helpers ──────────────────────────────────────────────────────────

    private async Task<string> PostInfoAsync(object request, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(request);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/info", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> PostExchangeAsync(
        object action, long nonce,
        string r, string s, int v,
        CancellationToken ct)
    {
        var request = new
        {
            action,
            nonce,
            signature = new { r, s, v },
            vaultAddress = (string?)null,
            expiresAfter = (long?)null
        };
        string body = JsonSerializer.Serialize(request);
        _logger.LogDebug("POST /exchange: {Body}", body);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{_baseUrl}/exchange", content, ct);
        string responseStr = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Exchange POST failed {response.StatusCode}: {responseStr}");

        _logger.LogDebug("Exchange response: {Response}", responseStr);
        return responseStr;
    }

    /// <summary>
    /// Appends the nonce, signatureChainId, and hyperliquidChain fields that the
    /// Hyperliquid API requires on every action before it is msgpack-encoded and signed.
    /// Mirrors the Python SDK's _post_with_nonce_and_sig helper.
    /// </summary>
    private void AppendActionMetadata(Dictionary<string, object> action, long nonce)
    {
        int chainId = _config.IsMainnet ? 42161 : 421614;
        action["nonce"] = nonce;
        action["signatureChainId"] = "0x" + chainId.ToString("x");
        action["hyperliquidChain"] = _config.IsMainnet ? "Mainnet" : "Testnet";
    }

    // ── MsgPack encoding ──────────────────────────────────────────────────────
    // Minimal hand-rolled encoder for the Hyperliquid action dict structure.
    // Key insertion order is preserved to match the Python SDK's msgpack.packb.

    private static byte[] MsgPackEncode(object value)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteMsgPack(w, value);
        return ms.ToArray();
    }

    private static void WriteMsgPack(BinaryWriter w, object? value)
    {
        switch (value)
        {
            case null:
                w.Write((byte)0xC0); break;
            case bool b:
                w.Write(b ? (byte)0xC3 : (byte)0xC2); break;
            case int i:
                WriteMsgPackInt(w, i); break;
            case long l:
                WriteMsgPackLong(w, l); break;
            case string str:
                WriteMsgPackStr(w, str); break;
            case Dictionary<string, object> dict:
                WriteMsgPackMap(w, dict); break;
            case List<object> list:
                WriteMsgPackArray(w, list); break;
            default:
                throw new NotSupportedException($"MsgPack: unsupported type {value.GetType()}");
        }
    }

    private static void WriteMsgPackInt(BinaryWriter w, int value)
    {
        if (value >= 0 && value <= 127) { w.Write((byte)value); return; }
        if (value >= -32 && value < 0) { w.Write((byte)(0xE0 | (value + 32))); return; }
        if (value >= 0 && value <= 0xFF) { w.Write((byte)0xCC); w.Write((byte)value); return; }
        if (value >= 0 && value <= 0xFFFF) { w.Write((byte)0xCD); WriteUInt16BE(w, (ushort)value); return; }
        if (value >= 0) { w.Write((byte)0xCE); WriteUInt32BE(w, (uint)value); return; }
        if (value >= -128) { w.Write((byte)0xD0); w.Write((sbyte)value); return; }
        if (value >= -32768) { w.Write((byte)0xD1); WriteInt16BE(w, (short)value); return; }
        w.Write((byte)0xD2); WriteInt32BE(w, value);
    }

    private static void WriteMsgPackLong(BinaryWriter w, long value)
    {
        if (value >= 0)
        {
            // Use the most compact unsigned representation — matches Python msgpack.packb behaviour.
            if (value <= int.MaxValue) { WriteMsgPackInt(w, (int)value); return; }      // 0xCE or smaller
            if (value <= 0xFFFFFFFFL) { w.Write((byte)0xCE); WriteUInt32BE(w, (uint)value); return; } // uint32
            w.Write((byte)0xCF); WriteInt64BE(w, value); return;                                       // uint64
        }
        // Negative values
        if (value >= int.MinValue) { WriteMsgPackInt(w, (int)value); return; }
        w.Write((byte)0xD3); WriteInt64BE(w, value); // int64
    }

    private static void WriteMsgPackStr(BinaryWriter w, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= 31) { w.Write((byte)(0xA0 | bytes.Length)); }
        else if (bytes.Length <= 0xFF) { w.Write((byte)0xD9); w.Write((byte)bytes.Length); }
        else if (bytes.Length <= 0xFFFF) { w.Write((byte)0xDA); WriteUInt16BE(w, (ushort)bytes.Length); }
        else { w.Write((byte)0xDB); WriteUInt32BE(w, (uint)bytes.Length); }
        w.Write(bytes);
    }

    private static void WriteMsgPackMap(BinaryWriter w, Dictionary<string, object> dict)
    {
        int count = dict.Count;
        if (count <= 15) w.Write((byte)(0x80 | count));
        else if (count <= 0xFFFF) { w.Write((byte)0xDE); WriteUInt16BE(w, (ushort)count); }
        else { w.Write((byte)0xDF); WriteUInt32BE(w, (uint)count); }

        foreach (var (key, val) in dict)
        {
            WriteMsgPackStr(w, key);
            WriteMsgPack(w, val);
        }
    }

    private static void WriteMsgPackArray(BinaryWriter w, List<object> list)
    {
        int count = list.Count;
        if (count <= 15) w.Write((byte)(0x90 | count));
        else if (count <= 0xFFFF) { w.Write((byte)0xDC); WriteUInt16BE(w, (ushort)count); }
        else { w.Write((byte)0xDD); WriteUInt32BE(w, (uint)count); }

        foreach (var item in list) WriteMsgPack(w, item);
    }

    private static void WriteUInt16BE(BinaryWriter w, ushort v)
    { w.Write((byte)(v >> 8)); w.Write((byte)(v & 0xFF)); }

    private static void WriteInt16BE(BinaryWriter w, short v)
    { w.Write((byte)((ushort)v >> 8)); w.Write((byte)((ushort)v & 0xFF)); }

    private static void WriteUInt32BE(BinaryWriter w, uint v)
    { w.Write((byte)(v >> 24)); w.Write((byte)(v >> 16 & 0xFF)); w.Write((byte)(v >> 8 & 0xFF)); w.Write((byte)(v & 0xFF)); }

    private static void WriteInt32BE(BinaryWriter w, int v)
    => WriteUInt32BE(w, (uint)v);

    private static void WriteInt64BE(BinaryWriter w, long v)
    { WriteUInt32BE(w, (uint)(v >> 32)); WriteUInt32BE(w, (uint)(v & 0xFFFFFFFF)); }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a decimal to Hyperliquid wire format: up to 8 significant figures,
    /// no trailing zeros, no scientific notation.
    /// </summary>
    public static string FloatToWire(decimal value)
    {
        // G8 gives up to 8 significant figures without trailing zeros
        string result = value.ToString("G8", CultureInfo.InvariantCulture);
        // Prevent scientific notation for very small/large values
        if (result.Contains('E', StringComparison.OrdinalIgnoreCase))
            result = value.ToString("0.########", CultureInfo.InvariantCulture);
        return result;
    }

    private static decimal ParseDecimal(JsonNode? node)
    {
        if (node is null) return 0m;
        string? s = node.GetValue<string?>();
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal d) ? d : 0m;
    }

    private static decimal ParseBestPrice(JsonNode? levelArray)
    {
        var first = levelArray?[0];
        if (first == null) return 0m;
        return ParseDecimal(first["px"]);
    }

    private static long ParseOrderId(string json)
    {
        var node = JsonNode.Parse(json);
        var statuses = node?["response"]?["data"]?["statuses"]?[0];
        // Resting order: { "resting": { "oid": 12345 } }
        long? oid = statuses?["resting"]?["oid"]?.GetValue<long>()
                 ?? statuses?["filled"]?["oid"]?.GetValue<long>();
        if (oid is null)
        {
            string? error = statuses?["error"]?.GetValue<string>();
            throw new InvalidOperationException($"Order placement failed: {error ?? json}");
        }
        return oid.Value;
    }
}
