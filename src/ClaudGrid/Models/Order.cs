namespace ClaudGrid.Models;

public enum OrderSide { Buy, Sell }
public enum OrderStatus { Open, Filled, Cancelled, Rejected }

/// <summary>A single fill event from the exchange fills history.</summary>
public sealed class UserFill
{
    public long OrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Price { get; set; }
    public decimal Size { get; set; }
    public long TimestampMs { get; set; }
}

/// <summary>Normalised exchange order — decoupled from Hyperliquid wire format.</summary>
public sealed class Order
{
    public long Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Price { get; set; }
    public decimal Size { get; set; }
    public decimal FilledSize { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public bool IsFullyFilled => FilledSize >= Size;
}
