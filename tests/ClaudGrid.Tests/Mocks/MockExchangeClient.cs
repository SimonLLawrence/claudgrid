using ClaudGrid.Exchange;
using ClaudGrid.Models;

namespace ClaudGrid.Tests.Mocks;

/// <summary>
/// Controllable in-memory exchange for unit tests.
/// </summary>
public sealed class MockExchangeClient : IExchangeClient
{
    private long _nextOrderId = 1000;
    private readonly List<Order> _openOrders = new();

    // ── Configurable state ───────────────────────────────────────────────────

    public decimal MidPrice { get; set; } = 50_000m;
    public decimal TotalEquity { get; set; } = 10_000m;
    public decimal AvailableBalance { get; set; } = 10_000m;
    public List<PositionInfo> Positions { get; set; } = new();

    // ── Call tracking ────────────────────────────────────────────────────────

    public List<(OrderSide Side, decimal Price, decimal Size)> PlacedOrders { get; } = new();
    public List<long> CancelledOrderIds { get; } = new();
    public int CancelAllCallCount { get; private set; }

    // ── Behaviour overrides ──────────────────────────────────────────────────

    /// <summary>When true, PlaceLimitOrderAsync throws.</summary>
    public bool ThrowOnPlaceOrder { get; set; }

    // ── IExchangeClient ──────────────────────────────────────────────────────

    public Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken ct = default)
    {
        return Task.FromResult(new MarketData
        {
            Symbol = symbol,
            MidPrice = MidPrice,
            BidPrice = MidPrice - 5m,
            AskPrice = MidPrice + 5m,
            Timestamp = DateTime.UtcNow
        });
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new AccountState
        {
            TotalEquity = TotalEquity,
            AvailableBalance = AvailableBalance,
            MarginUsed = TotalEquity - AvailableBalance,
            Positions = new List<PositionInfo>(Positions)
        });
    }

    public Task<List<Order>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_openOrders.ToList());
    }

    public Task<long> PlaceLimitOrderAsync(
        string symbol, int assetIndex, OrderSide side,
        decimal price, decimal size, CancellationToken ct = default)
    {
        if (ThrowOnPlaceOrder)
            throw new HttpRequestException("Mock exchange error");

        long id = _nextOrderId++;
        PlacedOrders.Add((side, price, size));
        _openOrders.Add(new Order
        {
            Id = id,
            Symbol = symbol,
            Side = side,
            Price = price,
            Size = size,
            Status = OrderStatus.Open,
            CreatedAt = DateTime.UtcNow
        });
        return Task.FromResult(id);
    }

    public Task<bool> CancelOrderAsync(int assetIndex, long orderId, CancellationToken ct = default)
    {
        CancelledOrderIds.Add(orderId);
        bool removed = _openOrders.RemoveAll(o => o.Id == orderId) > 0;
        return Task.FromResult(removed);
    }

    public Task<int> CancelAllOrdersAsync(int assetIndex, CancellationToken ct = default)
    {
        CancelAllCallCount++;
        int count = _openOrders.Count;
        _openOrders.Clear();
        return Task.FromResult(count);
    }

    // ── Helpers for test scenarios ───────────────────────────────────────────

    public Task<int> GetAssetIndexAsync(string symbol, CancellationToken ct = default) =>
        Task.FromResult(0);

    public Task<decimal> GetSpotUsdcBalanceAsync(CancellationToken ct = default) =>
        Task.FromResult(0m);

    public Task TransferSpotToPerpsAsync(decimal amount, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Simulates an order being filled by removing it from the open orders list.</summary>
    public void SimulateFill(long orderId)
    {
        _openOrders.RemoveAll(o => o.Id == orderId);
    }

    /// <summary>Simulates all open orders being filled.</summary>
    public void SimulateAllFilled()
    {
        _openOrders.Clear();
    }
}
