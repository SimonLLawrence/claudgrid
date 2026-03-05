using System.Collections.Concurrent;
using ClaudGrid.Config;
using ClaudGrid.Exchange;
using ClaudGrid.Models;
using ClaudGrid.Web;
using Microsoft.Extensions.Logging;

namespace ClaudGrid.Strategy;

/// <summary>
/// Stateful grid order lifecycle manager.
///
/// Level status machine:
///   Initial     — no order on the exchange (unplaced, failed, or mid-adjacent skip)
///   Active      — live limit order on the book, 0% filled
///   PartialFill — live limit order on the book, partially filled
///   Filled      — order fully executed; off the exchange
///
/// Transitions:
///   Initial     → Active      : TryPlaceOrderAsync succeeds
///   Active      → PartialFill : partial fill detected; counter placed for delta
///   Active      → Filled      : order disappears from exchange (full fill)
///   PartialFill → PartialFill : more partial fill; another counter placed
///   PartialFill → Filled      : order disappears; counter placed for remaining
///   Filled      → Initial     : all PendingCounters executed
///
/// Each level owns its counter orders via PendingCounters. Adjacent levels are
/// never mutated during fill handling, eliminating the orphaned-order bug.
/// </summary>
public sealed class GridStrategy
{
    private readonly IExchangeClient _exchange;
    private readonly BotConfig _config;
    private readonly ILogger<GridStrategy> _logger;

    private List<GridLevel> _levels = new();
    private readonly ConcurrentQueue<FillRecord> _pendingFills = new();
    private readonly ConcurrentQueue<string> _pendingMismatches = new();
    private decimal _initialEquity;
    private bool _isInitialised;

    /// <summary>
    /// Running net position derived by summing every fill event:
    ///   +size on each buy fill, −size on each sell fill.
    /// Reset to 0 on initialisation (after all positions are closed).
    /// Used by GridBot.VerifyPositions to compare against the exchange.
    /// </summary>
    private decimal _trackedNetPosition;

    /// <summary>
    /// OrderIds placed during the current SyncAsync call.
    /// Used to exclude newly placed counter/retry orders from the
    /// "bot Active level not on exchange" mismatch check, because
    /// liveOrders was fetched before those orders were placed.
    /// </summary>
    private readonly HashSet<long> _newlyPlacedThisCycle = new();

public IReadOnlyList<GridLevel> Levels => _levels;
    public decimal RealizedPnl => _levels.Sum(l => l.RealizedPnl);
    public bool IsInitialised => _isInitialised;
    public decimal TrackedNetPosition => _trackedNetPosition;

    /// <summary>
    /// Corrects the fill-derived position tracker to match the exchange's actual net position.
    /// Called by GridBot.VerifyPositions when a mismatch is detected so the error doesn't
    /// compound across future syncs.
    /// </summary>
    public void SnapTrackedPosition(decimal exchangeNetPosition)
    {
        decimal delta = exchangeNetPosition - _trackedNetPosition;
        _trackedNetPosition = exchangeNetPosition;
        _logger.LogWarning(
            "Position tracker corrected: was {Old:F4}, exchange shows {New:F4} (delta {Delta:+0.0000;-0.0000;0})",
            _trackedNetPosition - delta, exchangeNetPosition, delta);
    }

    public GridStrategy(IExchangeClient exchange, BotConfig config, ILogger<GridStrategy> logger)
    {
        _exchange = exchange;
        _config = config;
        _logger = logger;
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Cancels any existing orders for the asset, builds a fresh grid centred
    /// on the current price, and places all initial orders.
    /// </summary>
    public async Task InitialiseAsync(decimal initialEquity, CancellationToken ct = default)
    {
        _logger.LogInformation("Initialising grid...");
        _initialEquity = initialEquity;

        // Cancel anything left on the book
        int cancelled = await _exchange.CancelAllOrdersAsync(_config.Grid.AssetIndex, ct);
        if (cancelled > 0)
            _logger.LogInformation("Cancelled {Count} stale orders", cancelled);

        // Close any open position left from a previous session
        int closed = await _exchange.CloseAllPositionsAsync(_config.Grid.Symbol, _config.Grid.AssetIndex, ct);
        if (closed > 0)
            _logger.LogInformation("Closed {Count} stale position(s) — starting flat", closed);

        // All positions closed — reset the fill-derived position tracker
        _trackedNetPosition = 0;
        _newlyPlacedThisCycle.Clear();

        MarketData market = await _exchange.GetMarketDataAsync(_config.Grid.Symbol, ct);
        _logger.LogInformation("Grid anchor price: {Price:F2}", market.MidPrice);

        _levels = GridCalculator.BuildGrid(market.MidPrice, _config.Grid);

        decimal annualReturn = GridCalculator.EstimatedAnnualReturnRate(market.MidPrice, _config.Grid);
        _logger.LogInformation(
            "Grid: {Levels} levels, {Spacing}% spacing. Est. annual return: {Return:P1}",
            _config.Grid.GridLevels, _config.Grid.GridSpacingPercent, annualReturn);

        await PlaceInitialOrdersAsync(market.MidPrice, ct);
        _isInitialised = true;
    }

    // ── Sync cycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called on each periodic tick. Detects fills, places counter orders, and
    /// checks whether pending counter orders have executed.
    /// Also places any levels that are in Initial state (failed/skipped placement).
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (!_isInitialised) return;

        _newlyPlacedThisCycle.Clear();

        List<Order> liveOrders = await _exchange.GetOpenOrdersAsync(ct);
        Dictionary<long, Order> liveById = liveOrders.ToDictionary(o => o.Id);

        // Snapshot levels that have live main orders before the fill loop.
        // Counter-orders placed inside HandleMainFillAsync/HandlePartialIncrementAsync
        // get new IDs not in liveById yet, so they must not be checked this pass.
        var toCheck = _levels
            .Where(l => (l.Status == GridLevelStatus.Active || l.Status == GridLevelStatus.PartialFill)
                        && l.OrderId.HasValue)
            .Select(l => (Level: l, OrderId: l.OrderId!.Value))
            .ToList();

        foreach (var (level, orderId) in toCheck)
        {
            if (!liveById.TryGetValue(orderId, out var liveOrder))
            {
                await HandleMainFillAsync(level, ct);
            }
            else if (liveOrder.FilledSize > level.PartialFilledSize)
            {
                decimal delta = liveOrder.FilledSize - level.PartialFilledSize;
                level.PartialFilledSize = liveOrder.FilledSize;
                await HandlePartialIncrementAsync(level, delta, ct);
            }
        }

        // Check whether any pending counter orders have executed
        foreach (var level in _levels.Where(l => l.PendingCounters.Count > 0).ToList())
            CheckCounterOrders(level, liveById);

        // Re-place any Initial levels (failed placements, mid-adjacent skips, or re-queued after fill)
        await PlaceInitialOrdersRetryAsync(ct);

        // Retry counter placement for Filled levels whose counter failed to place last cycle.
        // Without this, a placement failure leaves the level stuck in Filled forever.
        foreach (var level in _levels.Where(l => l.Status == GridLevelStatus.Filled && l.PendingCounters.Count == 0).ToList())
        {
            _logger.LogWarning("Retrying counter for stuck Filled level {Index} {Side} @ {Price:F2}", level.Index, level.Side, level.Price);
            decimal remaining = level.Size; // full size — partial tracking already settled
            await PlaceCounterOrderAsync(level, remaining, ct);
        }

        // ── Two-way order reconciliation ──────────────────────────────────────

        // Build the set of all bot-tracked order IDs (main + counters)
        var botOrderIds = new HashSet<long>();
        foreach (var level in _levels)
        {
            if ((level.Status == GridLevelStatus.Active || level.Status == GridLevelStatus.PartialFill)
                && level.OrderId.HasValue)
                botOrderIds.Add(level.OrderId.Value);
            foreach (var counter in level.PendingCounters)
                botOrderIds.Add(counter.OrderId);
        }

        // 1. Exchange → bot: orphaned exchange orders the bot doesn't know about
        foreach (var order in liveById.Values)
        {
            if (!botOrderIds.Contains(order.Id))
            {
                _logger.LogError(
                    "STATE MISMATCH — orphaned exchange order: oid={Id} {Side} @ {Price} not tracked by bot",
                    order.Id, order.Side, order.Price);
                _pendingMismatches.Enqueue(
                    $"Orphaned order oid={order.Id} {order.Side} @ {order.Price:F2} not tracked by bot");
            }
        }

        // 2. Bot → exchange: bot thinks an order is active but it's gone from the exchange
        foreach (var level in _levels)
        {
            if ((level.Status == GridLevelStatus.Active || level.Status == GridLevelStatus.PartialFill)
                && level.OrderId.HasValue
                && !_newlyPlacedThisCycle.Contains(level.OrderId.Value)
                && !liveById.ContainsKey(level.OrderId.Value))
            {
                _logger.LogError(
                    "STATE MISMATCH — bot level {Index} {Side} @ {Price:F2} (oid={Id}) not found on exchange",
                    level.Index, level.Side, level.Price, level.OrderId.Value);
                _pendingMismatches.Enqueue(
                    $"Missing order: level {level.Index} {level.Side} @ {level.Price:F2} (oid={level.OrderId}) not on exchange");
            }
        }
    }

    // ── Grid reset ────────────────────────────────────────────────────────────

    /// <summary>
    /// Full grid reset: cancel everything, rebuild from current price.
    /// Called by RiskManager when price drifts outside bounds.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        _logger.LogWarning("Resetting grid...");
        _isInitialised = false;
        AccountState account = await _exchange.GetAccountStateAsync(ct);
        await InitialiseAsync(account.TotalEquity, ct);
    }

    // ── Internal order management ─────────────────────────────────────────────

    private async Task PlaceInitialOrdersAsync(decimal midPrice, CancellationToken ct)
    {
        foreach (GridLevel level in _levels)
        {
            // Skip the level nearest to the current price (inside the spread)
            if (Math.Abs(level.Price - midPrice) / midPrice < _config.Grid.GridSpacingPercent / 200m)
            {
                level.Status = GridLevelStatus.Initial;
                continue;
            }

            await TryPlaceOrderAsync(level, ct);
        }
    }

    private async Task PlaceInitialOrdersRetryAsync(CancellationToken ct)
    {
        foreach (GridLevel level in _levels.Where(l => l.Status == GridLevelStatus.Initial))
            await TryPlaceOrderAsync(level, ct);
    }

    private async Task TryPlaceOrderAsync(GridLevel level, CancellationToken ct)
    {
        try
        {
            long orderId = await _exchange.PlaceLimitOrderAsync(
                _config.Grid.Symbol,
                _config.Grid.AssetIndex,
                level.Side == GridLevelSide.Buy ? OrderSide.Buy : OrderSide.Sell,
                level.Price,
                level.Size,
                ct);

            level.OrderId = orderId;
            level.Status = GridLevelStatus.Active;
            level.PlacedAt = DateTime.UtcNow;
            level.PartialFilledSize = 0;
            level.PendingCounters.Clear();
            _newlyPlacedThisCycle.Add(orderId);

            _logger.LogDebug("Placed {Side} order @ {Price:F2} (oid={OId})",
                level.Side, level.Price, orderId);
        }
        catch (Exception ex)
        {
            level.Status = GridLevelStatus.Initial;
            _logger.LogWarning(ex, "Failed to place {Side} order @ {Price:F2}", level.Side, level.Price);
        }
    }

    /// <summary>
    /// Handles the final fill of a main order (order has disappeared from the exchange).
    /// Places a counter order for the remaining unfilled size and records the fill.
    /// </summary>
    private async Task HandleMainFillAsync(GridLevel level, CancellationToken ct)
    {
        decimal remaining = level.Size - level.PartialFilledSize;
        decimal positionDelta = level.Side == GridLevelSide.Sell ? -remaining : remaining;
        level.NetPositionSize += positionDelta;
        _trackedNetPosition += positionDelta;

        level.PartialFilledSize = 0;
        level.OrderId = null;
        level.Status = GridLevelStatus.Filled;
        level.FilledAt = DateTime.UtcNow;

        _logger.LogInformation("Fill: {Side} @ {Price:F2} (level {Index})",
            level.Side, level.Price, level.Index);

        await PlaceCounterOrderAsync(level, remaining, ct);

        _pendingFills.Enqueue(new FillRecord(
            DateTime.UtcNow, level.Side.ToString(), level.Price, remaining, 0m, false));
    }

    /// <summary>
    /// Handles a partial fill increment (order still open but more has filled since last sync).
    /// Places a counter order for the delta and records the fill.
    /// </summary>
    private async Task HandlePartialIncrementAsync(GridLevel level, decimal delta, CancellationToken ct)
    {
        decimal positionDelta = level.Side == GridLevelSide.Sell ? -delta : delta;
        level.NetPositionSize += positionDelta;
        _trackedNetPosition += positionDelta;

        level.Status = GridLevelStatus.PartialFill;

        _logger.LogInformation("Partial fill: {Side} {Filled:F4}/{Total:F4} @ {Price:F2} (level {Index})",
            level.Side, level.PartialFilledSize, level.Size, level.Price, level.Index);

        await PlaceCounterOrderAsync(level, delta, ct);

        _pendingFills.Enqueue(new FillRecord(
            DateTime.UtcNow, level.Side.ToString(), level.Price, delta, 0m, false));
    }

    /// <summary>
    /// Places a counter order at the adjacent grid level price and tracks it on this level's PendingCounters.
    /// </summary>
    private async Task PlaceCounterOrderAsync(GridLevel level, decimal size, CancellationToken ct)
    {
        int counterIndex = level.Side == GridLevelSide.Buy ? level.Index + 1 : level.Index - 1;
        if (counterIndex < 0 || counterIndex >= _levels.Count) return;

        decimal counterPrice = _levels[counterIndex].Price;
        OrderSide counterSide = level.Side == GridLevelSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        try
        {
            long orderId = await _exchange.PlaceLimitOrderAsync(
                _config.Grid.Symbol,
                _config.Grid.AssetIndex,
                counterSide,
                counterPrice,
                size,
                ct);

            level.PendingCounters.Add(new PendingCounter(orderId, size));
            _newlyPlacedThisCycle.Add(orderId);

            _logger.LogInformation("Counter {Side} @ {Price:F2} (oid={OId})", counterSide, counterPrice, orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to place counter {Side} @ {Price:F2}", counterSide, counterPrice);
        }
    }

    /// <summary>
    /// Checks whether any pending counter orders for this level have executed.
    /// Adjusts position tracking, computes PnL, and resets the level when fully settled.
    /// </summary>
    private void CheckCounterOrders(GridLevel level, Dictionary<long, Order> liveById)
    {
        var executed = level.PendingCounters
            .Where(c => !liveById.ContainsKey(c.OrderId) && !_newlyPlacedThisCycle.Contains(c.OrderId))
            .ToList();

        if (executed.Count == 0) return;

        int counterIndex = level.Side == GridLevelSide.Buy ? level.Index + 1 : level.Index - 1;
        decimal counterPrice = (counterIndex >= 0 && counterIndex < _levels.Count)
            ? _levels[counterIndex].Price
            : 0m;

        foreach (var counter in executed)
        {
            // Counter reverses the position: sell counter reduces long, buy counter reduces short
            decimal positionDelta = level.Side == GridLevelSide.Buy ? -counter.Size : counter.Size;
            level.NetPositionSize += positionDelta;
            _trackedNetPosition += positionDelta;

            decimal pnl = 0m;
            if (counterPrice > 0)
            {
                pnl = level.Side == GridLevelSide.Buy
                    ? (counterPrice - level.Price) * counter.Size   // bought low, sold high
                    : (level.Price - counterPrice) * counter.Size;  // sold high, bought low
                level.RealizedPnl += pnl;
            }

            _pendingFills.Enqueue(new FillRecord(
                DateTime.UtcNow,
                level.Side == GridLevelSide.Buy ? "Sell" : "Buy",
                counterPrice,
                counter.Size,
                pnl,
                true));

            level.PendingCounters.Remove(counter);

            _logger.LogInformation(
                "Counter executed: {Side} @ {Price:F2}, PnL: {Pnl:F4} (level {Index})",
                level.Side == GridLevelSide.Buy ? "Sell" : "Buy", counterPrice, pnl, level.Index);
        }

        // Reset to Initial once fully filled and all counters have executed
        if (level.Status == GridLevelStatus.Filled && level.PendingCounters.Count == 0)
        {
            level.NetPositionSize = 0;
            level.Status = GridLevelStatus.Initial;
            level.OrderId = null;
            level.FilledAt = null;
            _logger.LogInformation("Level {Index} reset to Initial after round-trip complete", level.Index);
        }
    }

    /// <summary>
    /// Manually closes the open position from a single filled level:
    /// cancels all pending counter orders and places a reduce-only IOC to flatten.
    /// Resets the level to Initial so the grid re-places it on the next sync.
    /// </summary>
    public async Task<bool> CloseLevelAsync(int index, CancellationToken ct = default)
    {
        var level = _levels.FirstOrDefault(l => l.Index == index);
        if (level == null || level.Status != GridLevelStatus.Filled) return false;

        // Cancel all pending counter orders
        foreach (var counter in level.PendingCounters.ToList())
        {
            try
            {
                await _exchange.CancelOrderAsync(_config.Grid.AssetIndex, counter.OrderId, ct);
                _logger.LogInformation("Cancelled counter order oid={OId} for manual close", counter.OrderId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cancel counter order oid={OId}", counter.OrderId);
            }
        }
        level.PendingCounters.Clear();

        // Close the open position with an opposite-side reduce-only IOC
        var closeSide = level.Side == GridLevelSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        decimal actualFillPrice = await _exchange.ClosePartialPositionAsync(
            _config.Grid.Symbol, _config.Grid.AssetIndex, closeSide, level.Size, ct);

        // Compute and record PnL from the actual fill price
        decimal fillPnl = 0m;
        if (actualFillPrice > 0)
        {
            fillPnl = level.Side == GridLevelSide.Buy
                ? (actualFillPrice - level.Price) * level.Size   // bought at level.Price, sold to close
                : (level.Price - actualFillPrice) * level.Size;  // sold at level.Price, bought to close
            level.RealizedPnl += fillPnl;
        }

        _pendingFills.Enqueue(new FillRecord(DateTime.UtcNow, closeSide.ToString(), actualFillPrice, level.Size, fillPnl, true));

        // Adjust tracked position to account for removing this level's contribution
        _trackedNetPosition -= level.NetPositionSize;

        // Reset the filled level so the grid re-places it
        level.Status = GridLevelStatus.Initial;
        level.OrderId = null;
        level.FilledAt = null;
        level.NetPositionSize = 0;
        level.PartialFilledSize = 0;
        _logger.LogInformation("Manually closed level {Index} ({Side} @ {Price:F0}), PnL: {Pnl:F4}",
            index, level.Side, level.Price, fillPnl);
        return true;
    }

    public IReadOnlyList<FillRecord> DrainNewFills()
    {
        var fills = new List<FillRecord>();
        while (_pendingFills.TryDequeue(out var f)) fills.Add(f);
        return fills;
    }

    public IReadOnlyList<string> DrainMismatches()
    {
        var list = new List<string>();
        while (_pendingMismatches.TryDequeue(out var m)) list.Add(m);
        return list;
    }
}
