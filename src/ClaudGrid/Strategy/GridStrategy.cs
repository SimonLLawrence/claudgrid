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
///   Active      → PartialFill : partial fill detected in SyncAsync
///   Active      → Filled      : order disappears from exchange (full fill)
///   PartialFill → Filled      : order disappears from exchange (remaining filled)
///   Filled      → Initial     : counter/closing order fires and re-queues the level
///   Initial     → Initial     : TryPlaceOrderAsync fails (stays Initial for retry)
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
    /// Called on each periodic tick. Detects fills and reposts counter-orders.
    /// Also places any levels that are in Initial state (failed/skipped placement).
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (!_isInitialised) return;

        _newlyPlacedThisCycle.Clear();

        List<Order> liveOrders = await _exchange.GetOpenOrdersAsync(ct);
        Dictionary<long, Order> liveOrdersById = liveOrders.ToDictionary(o => o.Id);

        // Snapshot levels that have live orders before the fill loop. Counter-orders placed
        // inside HandleFillAsync get new IDs that aren't in liveOrdersById yet, so they
        // must not be checked in this same pass.
        var toCheck = _levels
            .Where(l => (l.Status == GridLevelStatus.Active || l.Status == GridLevelStatus.PartialFill)
                        && l.OrderId.HasValue)
            .Select(l => (Level: l, OrderId: l.OrderId!.Value))
            .ToList();

        foreach (var (level, orderId) in toCheck)
        {
            if (!liveOrdersById.TryGetValue(orderId, out var liveOrder))
            {
                await HandleFillAsync(level, ct);
            }
            else if (liveOrder.FilledSize > level.PartialFilledSize)
            {
                // Partial fill: order still open but more has been filled since last sync
                decimal newPartial = liveOrder.FilledSize - level.PartialFilledSize;
                level.PartialFilledSize = liveOrder.FilledSize;
                level.NetPositionSize += level.Side == GridLevelSide.Sell ? -newPartial : newPartial;
                _trackedNetPosition   += level.Side == GridLevelSide.Sell ? -newPartial : newPartial;
                level.Status = GridLevelStatus.PartialFill;
                _logger.LogInformation(
                    "Partial fill: {Side} {Filled:F4}/{Total:F4} @ {Price:F2} (level {Index})",
                    level.Side, liveOrder.FilledSize, level.Size, level.Price, level.Index);
                _pendingFills.Enqueue(new FillRecord(
                    DateTime.UtcNow, level.Side.ToString(), level.Price, newPartial, 0m, false));
            }
        }

        // Re-place any Initial levels (failed placements, mid-adjacent skips, or re-queued after fill)
        await PlaceInitialOrdersRetryAsync(ct);

        // ── Two-way order reconciliation ──────────────────────────────────────
        //
        // Build the set of bot-tracked order IDs for fast lookup.
        // Exclude orders placed during this cycle (_newlyPlacedThisCycle) because
        // liveOrdersById was fetched before those orders existed on the exchange.
        var botOrderIds = _levels
            .Where(l => (l.Status == GridLevelStatus.Active || l.Status == GridLevelStatus.PartialFill)
                        && l.OrderId.HasValue)
            .Select(l => l.OrderId!.Value)
            .ToHashSet();

        // 1. Exchange → bot: orphaned exchange orders the bot doesn't know about
        foreach (var order in liveOrdersById.Values)
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
                && !liveOrdersById.ContainsKey(level.OrderId.Value))
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
    /// Manually closes the open position from a single filled level:
    /// cancels the counter order and places a reduce-only IOC to flatten.
    /// Resets both levels to Initial so the grid re-places them on the next sync.
    /// </summary>
    public async Task<bool> CloseLevelAsync(int index, CancellationToken ct = default)
    {
        var level = _levels.FirstOrDefault(l => l.Index == index);
        if (level == null || level.Status != GridLevelStatus.Filled) return false;

        // Cancel the counter order and reset it so the grid re-places it cleanly
        int counterIndex = level.Side == GridLevelSide.Buy ? index + 1 : index - 1;
        if (counterIndex >= 0 && counterIndex < _levels.Count)
        {
            var counterLevel = _levels[counterIndex];
            if (counterLevel.Status == GridLevelStatus.Active || counterLevel.Status == GridLevelStatus.PartialFill)
            {
                await _exchange.CancelOrderAsync(_config.Grid.AssetIndex, counterLevel.OrderId!.Value, ct);
                _logger.LogInformation("Cancelled counter order at level {Index} for manual close", counterIndex);
            }
            // Reset whether Active or PartialFill — clears any stale PairedPrice
            if (counterLevel.Status == GridLevelStatus.Active || counterLevel.Status == GridLevelStatus.PartialFill)
            {
                counterLevel.Status = GridLevelStatus.Initial;
                counterLevel.OrderId = null;
                counterLevel.PairedPrice = 0;
            }
        }

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
        level.PairedPrice = 0;
        level.FilledAt = null;
        level.NetPositionSize = 0;
        level.PartialFilledSize = 0;
        _logger.LogInformation("Manually closed fill pair at level {Index} ({Side} @ {Price:F0}), PnL: {Pnl:F4}", index, level.Side, level.Price, fillPnl);
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

    private async Task HandleFillAsync(GridLevel filledLevel, CancellationToken ct)
    {
        filledLevel.Status = GridLevelStatus.Filled;
        filledLevel.FilledAt = DateTime.UtcNow;

        // Only add the portion not already tracked by partial fill detection
        decimal remainingFill = filledLevel.Size - filledLevel.PartialFilledSize;
        decimal positionDelta = filledLevel.Side == GridLevelSide.Sell ? -remainingFill : remainingFill;
        filledLevel.NetPositionSize += positionDelta;
        _trackedNetPosition         += positionDelta;
        filledLevel.PartialFilledSize = 0;

        _logger.LogInformation("Fill detected: {Side} @ {Price:F2} (level {Index})",
            filledLevel.Side, filledLevel.Price, filledLevel.Index);

        decimal fillPnl = 0m;

        if (filledLevel.Side == GridLevelSide.Buy)
        {
            // Realise PnL only if this buy closes a prior sell (PairedPrice = that sell price)
            if (filledLevel.PairedPrice > 0)
            {
                fillPnl = (filledLevel.PairedPrice - filledLevel.Price) * filledLevel.Size;
                filledLevel.RealizedPnl += fillPnl;
                _logger.LogInformation(
                    "Round-trip closed (sell {Sell:F2} → buy {Buy:F2}), Realized PnL: {Pnl:F4}",
                    filledLevel.PairedPrice, filledLevel.Price, fillPnl);
            }

            decimal? counterPrice = GridCalculator.CounterSellPrice(filledLevel.Index, _levels);
            if (counterPrice.HasValue)
            {
                GridLevel counterLevel = _levels[filledLevel.Index + 1];
                if (counterLevel.Status is GridLevelStatus.Active or GridLevelStatus.PartialFill)
                {
                    // Already has a live order — just record the pairing
                    counterLevel.PairedPrice = filledLevel.Price;
                }
                else
                {
                    // No live order (Initial or Filled) — reset and place a new Sell
                    if (counterLevel.Status == GridLevelStatus.Filled && counterLevel.Side == GridLevelSide.Sell)
                    {
                        // Both sides of a round trip are now complete: zero both position contributions
                        counterLevel.NetPositionSize = 0;
                        filledLevel.NetPositionSize  = 0;
                    }
                    counterLevel.Status = GridLevelStatus.Initial;
                    counterLevel.OrderId = null;
                    counterLevel.FilledAt = null;
                    counterLevel.PartialFilledSize = 0;
                    counterLevel.Side = GridLevelSide.Sell;
                    counterLevel.PairedPrice = filledLevel.Price;
                    await TryPlaceOrderAsync(counterLevel, ct);
                    _logger.LogInformation("Counter SELL @ {Price:F2}", counterPrice.Value);
                }
            }
        }
        else // Sell filled
        {
            // Realise PnL only if this sell closes a prior buy (PairedPrice = that buy price)
            if (filledLevel.PairedPrice > 0)
            {
                fillPnl = (filledLevel.Price - filledLevel.PairedPrice) * filledLevel.Size;
                filledLevel.RealizedPnl += fillPnl;
                _logger.LogInformation(
                    "Round-trip closed (buy {Buy:F2} → sell {Sell:F2}), Realized PnL: {Pnl:F4}",
                    filledLevel.PairedPrice, filledLevel.Price, fillPnl);
            }

            decimal? counterPrice = GridCalculator.CounterBuyPrice(filledLevel.Index, _levels);
            if (counterPrice.HasValue)
            {
                GridLevel counterLevel = _levels[filledLevel.Index - 1];
                if (counterLevel.Status is GridLevelStatus.Active or GridLevelStatus.PartialFill)
                {
                    // Already has a live order — just record the pairing
                    counterLevel.PairedPrice = filledLevel.Price;
                }
                else
                {
                    // No live order (Initial or Filled) — reset and place a new Buy
                    if (counterLevel.Status == GridLevelStatus.Filled && counterLevel.Side == GridLevelSide.Buy)
                    {
                        // Both sides of a round trip are now complete: zero both position contributions
                        counterLevel.NetPositionSize = 0;
                        filledLevel.NetPositionSize  = 0;
                    }
                    counterLevel.Status = GridLevelStatus.Initial;
                    counterLevel.OrderId = null;
                    counterLevel.FilledAt = null;
                    counterLevel.PartialFilledSize = 0;
                    counterLevel.Side = GridLevelSide.Buy;
                    counterLevel.PairedPrice = filledLevel.Price;
                    await TryPlaceOrderAsync(counterLevel, ct);
                    _logger.LogInformation("Counter BUY @ {Price:F2}", counterPrice.Value);
                }
            }
        }

        _pendingFills.Enqueue(new FillRecord(DateTime.UtcNow, filledLevel.Side.ToString(), filledLevel.Price, filledLevel.Size, fillPnl, filledLevel.PairedPrice > 0));
    }
}
