namespace ClaudGrid.Models;

public sealed class MarketData
{
    public string Symbol { get; set; } = string.Empty;
    public decimal MidPrice { get; set; }
    public decimal BidPrice { get; set; }
    public decimal AskPrice { get; set; }
    public DateTime Timestamp { get; set; }
}

public sealed class AccountState
{
    public decimal MarginUsed { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal TotalEquity { get; set; }
    public List<PositionInfo> Positions { get; set; } = new();
}

public sealed class PositionInfo
{
    public string Symbol { get; set; } = string.Empty;
    /// <summary>Positive = long, negative = short (in asset units).</summary>
    public decimal Size { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
}
