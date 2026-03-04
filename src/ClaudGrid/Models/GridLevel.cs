namespace ClaudGrid.Models;

public enum GridLevelSide { Buy, Sell }

public enum GridLevelStatus
{
    Initial,      // No order on the exchange (unplaced, failed, or mid-adjacent skip)
    Active,       // Live limit order on the book, 0% filled
    PartialFill,  // Live limit order on the book, partially filled
    Filled,       // Order fully filled; off the exchange
}

/// <summary>Counter order placed against one fill increment on this level.</summary>
public sealed record PendingCounter(long OrderId, decimal Size);

/// <summary>Represents one level in the grid.</summary>
public sealed class GridLevel
{
    /// <summary>Zero-based index within the grid (0 = lowest price level).</summary>
    public int Index { get; set; }

    public decimal Price { get; set; }

    public GridLevelSide Side { get; set; }

    public GridLevelStatus Status { get; set; } = GridLevelStatus.Initial;

    /// <summary>Exchange-assigned order ID once the order is placed.</summary>
    public long? OrderId { get; set; }

    public decimal Size { get; set; }

    public DateTime? PlacedAt { get; set; }
    public DateTime? FilledAt { get; set; }
    public decimal? FillPrice { get; set; }

    /// <summary>
    /// Counter orders placed against fill increments on this level.
    /// Each entry represents one counter order (supporting partial fills).
    /// When all counters execute and Status == Filled, the level resets to Initial.
    /// </summary>
    public List<PendingCounter> PendingCounters { get; } = new();

    /// <summary>Running profit accumulated at this level (realised on round-trip close).</summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>
    /// Cumulative exchange position contribution from this level.
    /// Decrements by Size on each sell fill, increments on each buy fill.
    /// Matches the exchange's net position for this level at all times.
    /// </summary>
    public decimal NetPositionSize { get; set; }

    /// <summary>
    /// How much of the current active order has been partially filled so far.
    /// Reset to zero each time a new order is placed or the order fully fills.
    /// Used to detect incremental partial fills between sync cycles.
    /// </summary>
    public decimal PartialFilledSize { get; set; }

    public override string ToString() =>
        $"Level[{Index}] {Side} @ {Price:F2} — {Status}";
}
