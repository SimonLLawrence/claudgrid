namespace ClaudGrid.Config;

public sealed class BotConfig
{
    public const string Section = "Bot";

    public string PrivateKey { get; set; } = string.Empty;
    public string WalletAddress { get; set; } = string.Empty;

    /// <summary>true = Hyperliquid mainnet, false = testnet.</summary>
    public bool IsMainnet { get; set; } = false;

    public GridConfig Grid { get; set; } = new();
    public RiskConfig Risk { get; set; } = new();
}

public sealed class GridConfig
{
    /// <summary>Hyperliquid coin symbol, e.g. "BTC".</summary>
    public string Symbol { get; set; } = "BTC";

    /// <summary>
    /// Hyperliquid asset index (0-based). BTC-PERP is 0 on mainnet.
    /// Verify via GET /info?type=meta before going live.
    /// </summary>
    public int AssetIndex { get; set; } = 0;

    /// <summary>Total number of grid levels (split evenly above/below mid).</summary>
    public int GridLevels { get; set; } = 20;

    /// <summary>Percentage gap between adjacent grid levels (e.g. 1.0 = 1%).</summary>
    public decimal GridSpacingPercent { get; set; } = 1.0m;

    /// <summary>BTC quantity placed at each grid level.</summary>
    public decimal OrderSizeBtc { get; set; } = 0.001m;

    /// <summary>How often the bot syncs open orders with the exchange (seconds).</summary>
    public int SyncIntervalSeconds { get; set; } = 30;
}

public sealed class RiskConfig
{
    /// <summary>Maximum net BTC position the bot may accumulate.</summary>
    public decimal MaxPositionSizeBtc { get; set; } = 0.1m;

    /// <summary>Bot pauses trading if equity drawdown exceeds this percentage.</summary>
    public decimal MaxDrawdownPercent { get; set; } = 15.0m;

    /// <summary>Emergency lower price bound — bot will not place orders below this.</summary>
    public decimal MinGridPrice { get; set; } = 10_000m;

    /// <summary>Emergency upper price bound — bot will not place orders above this.</summary>
    public decimal MaxGridPrice { get; set; } = 500_000m;
}
