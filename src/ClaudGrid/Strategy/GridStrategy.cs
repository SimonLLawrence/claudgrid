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
/// Responsibilities:
///   1. Initialise the grid from current market price.
///   2. Place missing orders on each sync cycle.
///   3. Detect filled orders by diffing live orders against tracked state.
///   4. Repost counter-orders after fills.
///   5. Cancel stale orders when the grid is reset.
/// </summary>
public sealed class GridStrategy
{
    private readonly IExchangeClient _exchange;
    private readonly BotConfig _config;
    private readonly ILogger<GridStrategy> _logger;

    private List<GridLevel> _levels = new();
    private readonly ConcurrentQueue<FillRecord> _pendingFills = new();
    private decimal _initialEquity;
    private bool _isInitialised;

    public IReadOnlyList<GridLevel> Levels => _levels;
    public decimal RealizedPnl => _levels.Sum(l => l.RealizedPnl);
    public bool IsInitialised => _isInitialised;

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
    /// Also places any levels that are in Pending state but should be active.
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (!_isInitialised) return;

        List<Order> liveOrders = await _exchange.GetOpenOrdersAsync(ct);
        HashSet<long> liveIds = liveOrders.Select(o => o.Id).ToHashSet();

        // Snapshot active order IDs before the fill loop. Counter-orders placed
        // inside HandleFillAsync get new IDs that aren't in liveIds, which would
        // cause them to be falsely detected as filled in the same pass.
        var toCheck = _levels
            .Where(l => l.Status == GridLevelStatus.Active && l.OrderId.HasValue)
            .Select(l => (Level: l, OrderId: l.OrderId!.Value))
            .ToList();

        foreach (var (level, orderId) in toCheck)
        {
            if (!liveIds.Contains(orderId))
                await HandleFillAsync(level, ct);
        }

        // Re-place any pending levels that should now be active
        await PlacePendingOrdersAsync(ct);

        // Check for orders on the exchange that the bot has no record of
        var botOrderIds = _levels
            .Where(l => l.Status == GridLevelStatus.Active && l.OrderId.HasValue)
            .Select(l => l.OrderId!.Value)
            .ToHashSet();

        foreach (var order in liveOrders)
        {
            if (!botOrderIds.Contains(order.Id))
                _logger.LogError(
                    "STATE MISMATCH — orphaned exchange order: oid={Id} {Side} @ {Price} not tracked by bot",
                    order.Id, order.Side, order.Price);
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
                level.Status = GridLevelStatus.Pending;
                continue;
            }

            await TryPlaceOrderAsync(level, ct);
        }
    }

    private async Task PlacePendingOrdersAsync(CancellationToken ct)
    {
        foreach (GridLevel level in _levels.Where(l => l.Status == GridLevelStatus.Pending))
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

            _logger.LogDebug("Placed {Side} order @ {Price:F2} (oid={OId})",
                level.Side, level.Price, orderId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to place {Side} order @ {Price:F2}", level.Side, level.Price);
        }
    }

    /// <summary>
    /// Manually closes the open position from a single filled level:
    /// cancels the counter order and places a reduce-only IOC to flatten.
    /// Resets both levels to Pending so the grid re-places them on the next sync.
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
            if (counterLevel.Status == GridLevelStatus.Active && counterLevel.OrderId.HasValue)
            {
                await _exchange.CancelOrderAsync(_config.Grid.AssetIndex, counterLevel.OrderId.Value, ct);
                _logger.LogInformation("Cancelled counter order at level {Index} for manual close", counterIndex);
            }
            // Reset whether Active or Pending — clears any stale PairedPrice
            if (counterLevel.Status == GridLevelStatus.Active || counterLevel.Status == GridLevelStatus.Pending)
            {
                counterLevel.Status = GridLevelStatus.Pending;
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

        // Reset the filled level so the grid re-places it
        level.Status = GridLevelStatus.Pending;
        level.OrderId = null;
        level.PairedPrice = 0;
        level.FilledAt = null;
        _logger.LogInformation("Manually closed fill pair at level {Index} ({Side} @ {Price:F0}), PnL: {Pnl:F4}", index, level.Side, level.Price, fillPnl);
        return true;
    }

    public IReadOnlyList<FillRecord> DrainNewFills()
    {
        var fills = new List<FillRecord>();
        while (_pendingFills.TryDequeue(out var f)) fills.Add(f);
        return fills;
    }

    private async Task HandleFillAsync(GridLevel filledLevel, CancellationToken ct)
    {
        filledLevel.Status = GridLevelStatus.Filled;
        filledLevel.FilledAt = DateTime.UtcNow;

        _logger.LogInformation("Fill detected: {Side} @ {Price:F2} (level {Index})",
            filledLevel.Side, filledLevel.Price, filledLevel.Index);

        // Place counter-order and compute realised PnL (sell fills only).
        // The fill is always recorded regardless of whether a counter-order is placed.
        decimal fillPnl = 0m;

        if (filledLevel.Side == GridLevelSide.Buy)
        {
            // Realise PnL only if this buy closes a prior sell (PairedPrice = that sell price).
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
                if (counterLevel.Status != GridLevelStatus.Active)
                {
                    counterLevel.Side = GridLevelSide.Sell;
                    counterLevel.PairedPrice = filledLevel.Price; // counter sell will close this buy
                    counterLevel.Status = GridLevelStatus.Pending;
                    await TryPlaceOrderAsync(counterLevel, ct);
                    _logger.LogInformation("Counter SELL @ {Price:F2}", counterPrice.Value);
                }
                else
                {
                    // Order already on the book from initial placement — just record the pairing
                    // so that when it fills it can compute the correct round-trip PnL.
                    counterLevel.PairedPrice = filledLevel.Price;
                }
            }
        }
        else // Sell filled
        {
            // Realise PnL only if this sell closes a prior buy (PairedPrice = that buy price).
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
                if (counterLevel.Status != GridLevelStatus.Active)
                {
                    counterLevel.Side = GridLevelSide.Buy;
                    counterLevel.PairedPrice = filledLevel.Price; // counter buy will close this sell
                    counterLevel.Status = GridLevelStatus.Pending;
                    await TryPlaceOrderAsync(counterLevel, ct);
                    _logger.LogInformation("Counter BUY @ {Price:F2}", counterPrice.Value);
                }
                else
                {
                    // Order already on the book from initial placement — just record the pairing
                    // so that when it fills it can compute the correct round-trip PnL.
                    counterLevel.PairedPrice = filledLevel.Price;
                }
            }
        }

        _pendingFills.Enqueue(new FillRecord(DateTime.UtcNow, filledLevel.Side.ToString(), filledLevel.Price, filledLevel.Size, fillPnl, filledLevel.PairedPrice > 0));
    }
}
