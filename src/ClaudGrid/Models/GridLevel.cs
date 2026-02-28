namespace ClaudGrid.Models;

public enum GridLevelSide { Buy, Sell }

public enum GridLevelStatus
{
    Pending,    // Not yet placed on the exchange
    Active,     // Live limit order on the book
    Filled,     // Order has been fully filled
    Cancelled   // Order was cancelled
}

/// <summary>Represents one level in the grid.</summary>
public sealed class GridLevel
{
    /// <summary>Zero-based index within the grid (0 = lowest price level).</summary>
    public int Index { get; set; }

    public decimal Price { get; set; }

    public GridLevelSide Side { get; set; }

    public GridLevelStatus Status { get; set; } = GridLevelStatus.Pending;

    /// <summary>Exchange-assigned order ID once the order is placed.</summary>
    public long? OrderId { get; set; }

    public decimal Size { get; set; }

    public DateTime? PlacedAt { get; set; }
    public DateTime? FilledAt { get; set; }
    public decimal? FillPrice { get; set; }

    /// <summary>Running profit accumulated at this level (filled sell price − buy price).</summary>
    public decimal RealizedPnl { get; set; }

    public override string ToString() =>
        $"Level[{Index}] {Side} @ {Price:F2} — {Status}";
}
