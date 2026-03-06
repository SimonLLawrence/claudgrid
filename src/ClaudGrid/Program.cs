using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudGrid.Bot;
using ClaudGrid.Config;
using ClaudGrid.Exchange;
using ClaudGrid.Risk;
using ClaudGrid.Strategy;
using ClaudGrid.Web;
using Nethereum.Signer;

// ── Host setup ────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("appsettings.Runtime.json", optional: true)
    .AddEnvironmentVariables("CLAUDGRID_");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "HH:mm:ss ";
    opts.SingleLine = true;
});

// Config
BotConfig botConfig = builder.Configuration.GetSection(BotConfig.Section).Get<BotConfig>()
                      ?? throw new InvalidOperationException("Bot config section missing");

ValidateConfig(botConfig);

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton(botConfig.Grid);
builder.Services.AddSingleton(botConfig.Risk);

// Status service
builder.Services.AddSingleton<BotStatusService>();

// Exchange layer
builder.Services.AddHttpClient<IExchangeClient, HyperliquidClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "ClaudGrid/1.0");
});
builder.Services.AddSingleton<HyperliquidSigner>();

// Strategy and risk
builder.Services.AddSingleton<GridStrategy>();
builder.Services.AddSingleton<RiskManager>();

// Bot
builder.Services.AddHostedService<GridBot>();

var app = builder.Build();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate"
});

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
};

app.MapGet("/api/status", (BotStatusService status) =>
    Results.Json(status.GetSnapshot(), jsonOptions));

app.MapPost("/api/close-level/{index}", async (int index, GridStrategy strategy, BotStatusService status) =>
{
    bool closed = await strategy.CloseLevelAsync(index);
    if (!closed) return Results.BadRequest("Level not found or not in Filled state");
    // Immediately publish the fill so the UI reflects it on the next poll
    status.AddFills(strategy.DrainNewFills(), strategy.Levels);
    return Results.Ok();
});

app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) =>
{
    lifetime.StopApplication();
    return Results.Ok("Shutting down");
});

app.MapGet("/api/config", (GridConfig grid, RiskConfig risk) =>
    Results.Json(new
    {
        gridLevels           = grid.GridLevels,
        gridSpacingPercent   = grid.GridSpacingPercent,
        orderSizeBtc         = grid.OrderSizeBtc,
        syncIntervalSeconds  = grid.SyncIntervalSeconds,
        maxPositionSizeBtc   = risk.MaxPositionSizeBtc,
        maxDrawdownPercent   = risk.MaxDrawdownPercent,
        minGridPrice         = risk.MinGridPrice,
        maxGridPrice         = risk.MaxGridPrice
    }, jsonOptions));

app.MapPost("/api/config", async (ConfigUpdateRequest req, GridConfig grid, RiskConfig risk, GridStrategy strategy, BotConfig cfg) =>
{
    if (req.GridLevels < 4)                                  return Results.BadRequest("GridLevels must be >= 4");
    if (req.GridSpacingPercent <= 0)                         return Results.BadRequest("GridSpacingPercent must be > 0");
    if (req.OrderSizeBtc <= 0)                               return Results.BadRequest("OrderSizeBtc must be > 0");
    if (req.SyncIntervalSeconds < 5)                         return Results.BadRequest("SyncIntervalSeconds must be >= 5");
    if (req.MaxPositionSizeBtc <= 0)                         return Results.BadRequest("MaxPositionSizeBtc must be > 0");
    if (req.MaxDrawdownPercent is <= 0 or > 100)             return Results.BadRequest("MaxDrawdownPercent must be 1–100");
    if (req.MinGridPrice <= 0)                               return Results.BadRequest("MinGridPrice must be > 0");
    if (req.MaxGridPrice <= req.MinGridPrice)                return Results.BadRequest("MaxGridPrice must be > MinGridPrice");

    grid.GridLevels          = req.GridLevels;
    grid.GridSpacingPercent  = req.GridSpacingPercent;
    grid.OrderSizeBtc        = req.OrderSizeBtc;
    grid.SyncIntervalSeconds = req.SyncIntervalSeconds;
    risk.MaxPositionSizeBtc  = req.MaxPositionSizeBtc;
    risk.MaxDrawdownPercent  = req.MaxDrawdownPercent;
    risk.MinGridPrice        = req.MinGridPrice;
    risk.MaxGridPrice        = req.MaxGridPrice;

    await PersistRuntimeConfigAsync(cfg);
    await strategy.ResetAsync();
    return Results.Ok();
});

app.MapGet("/api/wallet", (BotConfig cfg) =>
    Results.Json(new
    {
        walletAddress    = cfg.WalletAddress,
        privateKeyMasked = MaskKey(cfg.PrivateKey),
        isMainnet        = cfg.IsMainnet
    }, jsonOptions));

app.MapPost("/api/wallet", async (WalletActionRequest req, BotConfig cfg) =>
{
    switch (req.Action?.ToLower())
    {
        case "generate":
        {
            var key  = EthECKey.GenerateKey();
            string pk   = "0x" + key.GetPrivateKey();
            string addr = key.GetPublicAddress();
            return Results.Json(new { privateKey = pk, walletAddress = addr }, jsonOptions);
        }
        case "derive":
        {
            if (string.IsNullOrWhiteSpace(req.PrivateKey))
                return Results.BadRequest("privateKey required");
            EthECKey key;
            try   { key = new EthECKey(req.PrivateKey); }
            catch { return Results.BadRequest("Invalid private key"); }
            return Results.Json(new { walletAddress = key.GetPublicAddress() }, jsonOptions);
        }
        case "save":
        {
            if (string.IsNullOrWhiteSpace(req.PrivateKey))
                return Results.BadRequest("privateKey required");
            if (string.IsNullOrWhiteSpace(req.WalletAddress))
                return Results.BadRequest("walletAddress required");
            try   { _ = new EthECKey(req.PrivateKey); }
            catch { return Results.BadRequest("Invalid private key"); }
            cfg.PrivateKey     = req.PrivateKey;
            cfg.WalletAddress  = req.WalletAddress;
            await PersistRuntimeConfigAsync(cfg);
            return Results.Json(new { message = "Wallet saved. Restart the bot to connect with the new wallet." }, jsonOptions);
        }
        default:
            return Results.BadRequest("Unknown action. Use: generate, derive, save");
    }
});

app.MapGet("/", () => Results.Redirect("/index.html"));

await app.RunAsync();

// ── Config validation ─────────────────────────────────────────────────────────

static async Task PersistRuntimeConfigAsync(BotConfig cfg)
{
    var doc = new
    {
        Bot = new
        {
            PrivateKey    = cfg.PrivateKey,
            WalletAddress = cfg.WalletAddress,
            Grid = new
            {
                cfg.Grid.GridLevels,
                cfg.Grid.GridSpacingPercent,
                cfg.Grid.OrderSizeBtc,
                cfg.Grid.SyncIntervalSeconds
            },
            Risk = new
            {
                cfg.Risk.MaxPositionSizeBtc,
                cfg.Risk.MaxDrawdownPercent,
                cfg.Risk.MinGridPrice,
                cfg.Risk.MaxGridPrice
            }
        }
    };
    var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(
        Path.Combine(AppContext.BaseDirectory, "appsettings.Runtime.json"), json);
}

static string MaskKey(string key)
{
    if (string.IsNullOrEmpty(key) || key.Length <= 12) return "••••••••";
    return key[..8] + "••••••••••••••••••••••••••" + key[^4..];
}

static void ValidateConfig(BotConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.PrivateKey))
        throw new InvalidOperationException(
            "Bot:PrivateKey is not set. Add it to appsettings.Development.json or via environment variable CLAUDGRID_BOT__PRIVATEKEY.");

    if (string.IsNullOrWhiteSpace(cfg.WalletAddress))
        throw new InvalidOperationException(
            "Bot:WalletAddress is not set.");

    if (cfg.Grid.GridLevels < 4)
        throw new InvalidOperationException("GridLevels must be at least 4.");

    if (cfg.Grid.GridSpacingPercent <= 0)
        throw new InvalidOperationException("GridSpacingPercent must be > 0.");

    if (cfg.Grid.OrderSizeBtc <= 0)
        throw new InvalidOperationException("OrderSizeBtc must be > 0.");
}

// ── Types ─────────────────────────────────────────────────────────────────────

record ConfigUpdateRequest(
    int GridLevels,
    decimal GridSpacingPercent,
    decimal OrderSizeBtc,
    int SyncIntervalSeconds,
    decimal MaxPositionSizeBtc,
    decimal MaxDrawdownPercent,
    decimal MinGridPrice,
    decimal MaxGridPrice);

record WalletActionRequest(string Action, string? PrivateKey = null, string? WalletAddress = null);
