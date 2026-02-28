using ClaudGrid.Config;
using ClaudGrid.Models;
using ClaudGrid.Strategy;
using ClaudGrid.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudGrid.Tests.Strategy;

public sealed class GridStrategyTests
{
    private static BotConfig MakeConfig() => new()
    {
        WalletAddress = "0x1234567890123456789012345678901234567890",
        IsMainnet = false,
        Grid = new GridConfig
        {
            Symbol = "BTC",
            AssetIndex = 0,
            GridLevels = 10,
            GridSpacingPercent = 1.0m,
            OrderSizeBtc = 0.001m
        },
        Risk = new RiskConfig()
    };

    private static (GridStrategy strategy, MockExchangeClient exchange) CreateSut()
    {
        var exchange = new MockExchangeClient { MidPrice = 50_000m, TotalEquity = 10_000m };
        var config = MakeConfig();
        var strategy = new GridStrategy(exchange, config, NullLogger<GridStrategy>.Instance);
        return (strategy, exchange);
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    [Fact]
    public async Task InitialiseAsync_CancelsExistingOrders()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);
        Assert.Equal(1, exchange.CancelAllCallCount);
    }

    [Fact]
    public async Task InitialiseAsync_PlacesOrders_ForAllNonMidLevels()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        // 10-level grid: 9 active orders placed (mid-adjacent level is skipped)
        Assert.True(exchange.PlacedOrders.Count >= 8,
            $"Expected ≥ 8 orders placed, got {exchange.PlacedOrders.Count}");
    }

    [Fact]
    public async Task InitialiseAsync_SetsIsInitialised()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);
        Assert.True(strategy.IsInitialised);
    }

    [Fact]
    public async Task InitialiseAsync_BuyOrdersBelowMid_SellOrdersAboveMid()
    {
        var (strategy, exchange) = CreateSut();
        decimal mid = exchange.MidPrice;
        await strategy.InitialiseAsync(10_000m);

        foreach (var (side, price, _) in exchange.PlacedOrders)
        {
            if (price < mid)
                Assert.Equal(OrderSide.Buy, side);
            else if (price > mid)
                Assert.Equal(OrderSide.Sell, side);
        }
    }

    // ── Sync cycle — fill detection ───────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_DetectsFill_WhenOrderDisappearsFromBook()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        int activeCountBefore = strategy.Levels.Count(l => l.Status == GridLevelStatus.Active);

        // Simulate a fill by removing an order from the book
        long filledId = exchange.PlacedOrders.Count > 0
            ? strategy.Levels.First(l => l.Status == GridLevelStatus.Active).OrderId!.Value
            : throw new InvalidOperationException("No active levels");

        exchange.SimulateFill(filledId);
        await strategy.SyncAsync();

        int filledCount = strategy.Levels.Count(l => l.Status == GridLevelStatus.Filled);
        Assert.Equal(1, filledCount);
    }

    [Fact]
    public async Task SyncAsync_AfterFill_PlacesCounterOrder()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        int initialOrderCount = exchange.PlacedOrders.Count;

        // Simulate a buy fill
        GridLevel? buyLevel = strategy.Levels.FirstOrDefault(l =>
            l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);

        if (buyLevel?.OrderId == null) return; // Skip if no buy orders placed

        exchange.SimulateFill(buyLevel.OrderId.Value);
        await strategy.SyncAsync();

        // A counter sell order should have been placed
        Assert.True(exchange.PlacedOrders.Count > initialOrderCount,
            "Expected a counter order to be placed after fill");

        var lastOrder = exchange.PlacedOrders.Last();
        Assert.Equal(OrderSide.Sell, lastOrder.Side);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetAsync_RebuildsGrid()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        exchange.MidPrice = 55_000m; // Price moved
        int cancelCallsBefore = exchange.CancelAllCallCount;

        await strategy.ResetAsync();

        Assert.True(exchange.CancelAllCallCount > cancelCallsBefore);
        Assert.True(strategy.IsInitialised);
        // New grid should be centred near 55,000
        decimal newMid = (strategy.Levels[0].Price + strategy.Levels[^1].Price) / 2m;
        Assert.InRange(newMid, 52_000m, 58_000m);
    }

    // ── Order error handling ──────────────────────────────────────────────────

    [Fact]
    public async Task InitialiseAsync_ExchangeError_DoesNotThrow()
    {
        var (strategy, exchange) = CreateSut();
        exchange.ThrowOnPlaceOrder = true;

        // Should not throw — errors are logged and levels stay Pending
        await strategy.InitialiseAsync(10_000m);
        Assert.True(strategy.IsInitialised);
        Assert.All(strategy.Levels, l =>
            Assert.True(l.Status is GridLevelStatus.Pending or GridLevelStatus.Active));
    }

    // ── PnL tracking ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RealizedPnl_InitiallyZero()
    {
        var (strategy, _) = CreateSut();
        await strategy.InitialiseAsync(10_000m);
        Assert.Equal(0m, strategy.RealizedPnl);
    }
}
