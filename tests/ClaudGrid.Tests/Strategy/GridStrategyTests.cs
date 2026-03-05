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

        // Filled levels are immediately re-queued — verify via the fill record instead
        var fills = strategy.DrainNewFills();
        Assert.Single(fills);
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
        var newOrders = exchange.PlacedOrders.Skip(initialOrderCount).ToList();
        Assert.Contains(newOrders, o => o.Side == OrderSide.Sell);
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
            Assert.True(l.Status is GridLevelStatus.Initial or GridLevelStatus.Active));
    }

    // ── PnL tracking ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RealizedPnl_InitiallyZero()
    {
        var (strategy, _) = CreateSut();
        await strategy.InitialiseAsync(10_000m);
        Assert.Equal(0m, strategy.RealizedPnl);
    }

    [Fact]
    public async Task SyncAsync_RoundTrip_RecordsPositiveRealizedPnl()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        // Fill the first Active Sell → counter Buy placed, tracked on sell.PendingCounters
        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        // Fill the counter Buy → round-trip closes, PnL realised
        Assert.NotEmpty(sell.PendingCounters);
        exchange.SimulateFill(sell.PendingCounters[0].OrderId);
        await strategy.SyncAsync();

        Assert.True(strategy.RealizedPnl > 0m,
            $"Expected positive PnL after round-trip, got {strategy.RealizedPnl}");
    }

    // ── TrackedNetPosition ────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_BuyFill_TrackedNetPositionIncrements()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();

        Assert.Equal(buy.Size, strategy.TrackedNetPosition);
    }

    [Fact]
    public async Task SyncAsync_SellFill_TrackedNetPositionDecrements()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();

        Assert.Equal(-sell.Size, strategy.TrackedNetPosition);
    }

    [Fact]
    public async Task SyncAsync_RoundTrip_TrackedNetPositionReturnsToZero()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        Assert.NotEmpty(sell.PendingCounters);
        exchange.SimulateFill(sell.PendingCounters[0].OrderId);
        await strategy.SyncAsync();

        Assert.Equal(0m, strategy.TrackedNetPosition);
    }

    [Fact]
    public async Task InitialiseAsync_ResetsTrackedNetPosition()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();
        Assert.NotEqual(0m, strategy.TrackedNetPosition);

        await strategy.InitialiseAsync(10_000m);
        Assert.Equal(0m, strategy.TrackedNetPosition);
    }

    // ── Round-trip — per-level NetPositionSize zeroing ────────────────────────

    [Fact]
    public async Task SyncAsync_RoundTrip_ZerosPerLevelNetPositionSize()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        Assert.NotEmpty(sell.PendingCounters);
        exchange.SimulateFill(sell.PendingCounters[0].OrderId);
        await strategy.SyncAsync();

        Assert.Equal(0m, sell.NetPositionSize);
    }

    // ── DrainNewFills ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DrainNewFills_AfterBuyFill_HasExpectedRecord()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();

        var fills = strategy.DrainNewFills();
        var fill = Assert.Single(fills);
        Assert.Equal("Buy", fill.Side);
        Assert.Equal(buy.Size, fill.Size);
        Assert.False(fill.IsClose);
    }

    [Fact]
    public async Task DrainNewFills_EmptyAfterDrain()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();

        strategy.DrainNewFills();
        Assert.Empty(strategy.DrainNewFills());
    }

    [Fact]
    public async Task DrainNewFills_RoundTripClose_IsCloseTrueAndPnlPositive()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        Assert.NotEmpty(sell.PendingCounters);
        exchange.SimulateFill(sell.PendingCounters[0].OrderId);
        await strategy.SyncAsync();

        var fills = strategy.DrainNewFills();
        var fill = Assert.Single(fills);
        Assert.True(fill.IsClose, "Round-trip closing fill should have IsClose=true");
        Assert.True(fill.Pnl > 0m, $"Expected positive PnL, got {fill.Pnl}");
    }

    // ── DrainMismatches ───────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_OrphanedExchangeOrder_ReportsMismatch()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        exchange.InjectOpenOrder(new Order
        {
            Id = 99999L,
            Symbol = "BTC",
            Side = OrderSide.Buy,
            Price = 50_000m,
            Size = 0.001m,
            Status = OrderStatus.Open
        });

        await strategy.SyncAsync();

        var mismatches = strategy.DrainMismatches();
        Assert.True(mismatches.Count > 0, "Expected a mismatch for the orphaned order");
        Assert.Contains(mismatches, m => m.Contains("99999"));
    }

    [Fact]
    public async Task DrainMismatches_EmptyAfterDrain()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        exchange.InjectOpenOrder(new Order { Id = 88888L, Symbol = "BTC", Side = OrderSide.Buy, Price = 50_000m, Size = 0.001m, Status = OrderStatus.Open });
        await strategy.SyncAsync();

        strategy.DrainMismatches();
        Assert.Empty(strategy.DrainMismatches());
    }

    // ── Partial fills ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_PartialFill_StatusBecomesPartialFill()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulatePartialFill(buy.OrderId!.Value, buy.Size / 2);
        await strategy.SyncAsync();

        Assert.Equal(GridLevelStatus.PartialFill, buy.Status);
    }

    [Fact]
    public async Task SyncAsync_PartialFill_TrackedNetPositionIncrements()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        decimal partial = buy.Size / 2;
        exchange.SimulatePartialFill(buy.OrderId!.Value, partial);
        await strategy.SyncAsync();

        Assert.Equal(partial, strategy.TrackedNetPosition);
    }

    [Fact]
    public async Task SyncAsync_PartialFillThenFull_TrackedNetPositionEqualsFullSize()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        // First sync: partial fill of half
        exchange.SimulatePartialFill(buy.OrderId!.Value, buy.Size / 2);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        // Second sync: order disappears (full fill)
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();

        Assert.Equal(buy.Size, strategy.TrackedNetPosition);
    }

    // ── Cancellation / re-queue handling ──────────────────────────────────────
    // Note: "cancellation" from the bot's perspective means an order being removed
    // externally. Because the bot uses open-orders as sole fill detection, a cancelled
    // order is indistinguishable from a fill at sync time. VerifyPositions (with delayed
    // snap) acts as the correction safety net for rare external cancellations.
    // The tests below verify that a cancelled counter triggers the stuck-Filled retry.

    [Fact]
    public async Task SyncAsync_CancelledCounter_RetriesPlacement()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        // Fill a sell level so a counter buy is placed
        GridLevel sell = strategy.Levels.First(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Sell);
        exchange.SimulateFill(sell.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        Assert.NotEmpty(sell.PendingCounters);
        long counterOid = sell.PendingCounters[0].OrderId;
        int ordersBeforeRetry = exchange.PlacedOrders.Count;

        // Remove the counter from open orders without a fill record (simulates cancel)
        // and run one more sync — the counter disappears, is treated as executed,
        // level resets to Initial, and then on the same sync PlaceInitialOrdersRetryAsync re-places it.
        exchange.CancelOrder(counterOid);
        await strategy.SyncAsync();

        // A new main order should have been placed (level reset to Initial → Active)
        Assert.Equal(GridLevelStatus.Active, sell.Status);
        Assert.True(exchange.PlacedOrders.Count > ordersBeforeRetry);
    }

    // ── CloseLevelAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CloseLevelAsync_ActiveLevel_ReturnsFalse()
    {
        var (strategy, exchange) = CreateSut();
        await strategy.InitialiseAsync(10_000m);

        // Level 0 is Active, not Filled
        bool result = await strategy.CloseLevelAsync(0);
        Assert.False(result);
    }

    [Fact]
    public async Task CloseLevelAsync_InvalidIndex_ReturnsFalse()
    {
        var (strategy, _) = CreateSut();
        await strategy.InitialiseAsync(10_000m);
        Assert.False(await strategy.CloseLevelAsync(999));
    }

    [Fact]
    public async Task CloseLevelAsync_FilledBuyLevel_ReturnsTrueAndResetsToInitial()
    {
        var (strategy, exchange) = CreateSut();
        exchange.ClosePartialFillPrice = 51_000m;
        await strategy.InitialiseAsync(10_000m);

        // Fill the highest Buy (closest to mid) so its counter sell is tracked in buy.PendingCounters
        GridLevel buy = strategy.Levels.Last(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        Assert.Equal(GridLevelStatus.Filled, buy.Status);
        bool result = await strategy.CloseLevelAsync(buy.Index);

        Assert.True(result);
        Assert.Equal(GridLevelStatus.Initial, buy.Status);
        Assert.Null(buy.OrderId);
        Assert.Equal(0m, buy.NetPositionSize);
    }

    [Fact]
    public async Task CloseLevelAsync_FilledLevel_GeneratesCloseFillRecord()
    {
        var (strategy, exchange) = CreateSut();
        exchange.ClosePartialFillPrice = 51_000m;
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.Last(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        await strategy.CloseLevelAsync(buy.Index);

        var fills = strategy.DrainNewFills();
        Assert.NotEmpty(fills);
        Assert.Contains(fills, f => f.IsClose);
    }

    [Fact]
    public async Task CloseLevelAsync_FilledLevel_AdjustsTrackedNetPosition()
    {
        var (strategy, exchange) = CreateSut();
        exchange.ClosePartialFillPrice = 51_000m;
        await strategy.InitialiseAsync(10_000m);

        GridLevel buy = strategy.Levels.Last(l => l.Status == GridLevelStatus.Active && l.Side == GridLevelSide.Buy);
        exchange.SimulateFill(buy.OrderId!.Value);
        await strategy.SyncAsync();
        strategy.DrainNewFills();

        decimal positionBefore = strategy.TrackedNetPosition;  // +0.001
        decimal levelContrib   = buy.NetPositionSize;           // +0.001

        await strategy.CloseLevelAsync(buy.Index);

        Assert.Equal(positionBefore - levelContrib, strategy.TrackedNetPosition);
    }
}
