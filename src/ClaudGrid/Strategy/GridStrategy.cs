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
    private readonly List<FillRecord> _pendingFills = new();
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

    public IReadOnlyList<FillRecord> DrainNewFills()
    {
        var fills = _pendingFills.ToList();
        _pendingFills.Clear();
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
            decimal? counterPrice = GridCalculator.CounterSellPrice(filledLevel.Index, _levels);
            if (counterPrice.HasValue)
            {
                GridLevel counterLevel = _levels[filledLevel.Index + 1];
                counterLevel.Side = GridLevelSide.Sell; // always sell as counter to a buy fill
                if (counterLevel.Status != GridLevelStatus.Active)
                {
                    counterLevel.Status = GridLevelStatus.Pending;
                    await TryPlaceOrderAsync(counterLevel, ct);
                    _logger.LogInformation("Counter SELL @ {Price:F2}", counterPrice.Value);
                }
            }
            // PnL not realised until the counter sell fills.
        }
        else // Sell filled
        {
            decimal? counterPrice = GridCalculator.CounterBuyPrice(filledLevel.Index, _levels);
            if (counterPrice.HasValue)
            {
                GridLevel counterLevel = _levels[filledLevel.Index - 1];
                counterLevel.Side = GridLevelSide.Buy; // always buy as counter to a sell fill
                if (counterLevel.Status != GridLevelStatus.Active)
                {
                    counterLevel.Status = GridLevelStatus.Pending;
                    await TryPlaceOrderAsync(counterLevel, ct);
                }
                // Sell closes the round-trip — profit realised here.
                fillPnl = (filledLevel.Price - counterPrice.Value) * filledLevel.Size;
                filledLevel.RealizedPnl += fillPnl;
                _logger.LogInformation("Counter BUY @ {Price:F2}, Realized PnL: {Pnl:F4}",
                    counterPrice.Value, fillPnl);
            }
        }

        _pendingFills.Add(new FillRecord(DateTime.UtcNow, filledLevel.Side.ToString(), filledLevel.Price, filledLevel.Size, fillPnl));
    }
}
