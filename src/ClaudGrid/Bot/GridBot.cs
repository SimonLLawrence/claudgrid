using ClaudGrid.Config;
using ClaudGrid.Exchange;
using ClaudGrid.Models;
using ClaudGrid.Risk;
using ClaudGrid.Strategy;
using ClaudGrid.Web;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClaudGrid.Bot;

/// <summary>
/// Main bot orchestrator implemented as a .NET BackgroundService.
/// Lifecycle: Start → Initialise → [Sync loop] → Graceful shutdown.
///
/// Each sync cycle:
///   1. Fetch market data + account state.
///   2. Evaluate risk (halt, reset, or continue).
///   3. Call GridStrategy.SyncAsync to handle fills and place orders.
///   4. Log PnL summary.
///   5. Wait for the next interval.
/// </summary>
public sealed class GridBot : BackgroundService
{
    private readonly IExchangeClient _exchange;
    private readonly GridStrategy _strategy;
    private readonly RiskManager _risk;
    private readonly BotConfig _config;
    private readonly BotStatusService _status;
    private readonly ILogger<GridBot> _logger;

    private decimal _gridLower;
    private decimal _gridUpper;
    private int _syncCount;
    private int _consecutiveMismatchCount;

    public GridBot(
        IExchangeClient exchange,
        GridStrategy strategy,
        RiskManager risk,
        BotConfig config,
        BotStatusService status,
        ILogger<GridBot> logger)
    {
        _exchange = exchange;
        _strategy = strategy;
        _risk = risk;
        _config = config;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ClaudGrid starting. Network: {Network}, Symbol: {Symbol}",
            _config.IsMainnet ? "Mainnet" : "Testnet",
            _config.Grid.Symbol);

        try
        {
            await InitialiseAsync(stoppingToken);
            await RunLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Bot received shutdown signal.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Unhandled bot error. Shutting down.");
        }
        finally
        {
            await ShutdownAsync();
        }
    }

    // ── Initialisation ────────────────────────────────────────────────────────

    private async Task InitialiseAsync(CancellationToken ct)
    {
        // Discover the correct asset index from the exchange meta endpoint
        int assetIndex = await _exchange.GetAssetIndexAsync(_config.Grid.Symbol, ct);
        if (assetIndex != _config.Grid.AssetIndex)
        {
            _logger.LogInformation(
                "Asset index for {Symbol}: {Index} (was {Old} in config)",
                _config.Grid.Symbol, assetIndex, _config.Grid.AssetIndex);
            _config.Grid.AssetIndex = assetIndex;
        }

        // Auto-transfer: if perps balance is zero but spot has USDC, move it across
        AccountState account = await _exchange.GetAccountStateAsync(ct);

        _logger.LogInformation(
            "Account equity: {Equity:F2} USDC, available: {Available:F2} USDC",
            account.TotalEquity, account.AvailableBalance);

        _risk.SetInitialEquity(account.TotalEquity);
        await _strategy.InitialiseAsync(account.TotalEquity, ct);

        UpdateGridBounds();
    }

    // ── Main loop ─────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = TimeSpan.FromSeconds(_config.Grid.SyncIntervalSeconds);
        _logger.LogInformation("Sync loop started. Interval: {Interval}s", interval.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct);
            await RunSyncCycleAsync(ct);
        }
    }

    private async Task RunSyncCycleAsync(CancellationToken ct)
    {
        _syncCount++;
        try
        {
            MarketData market = await _exchange.GetMarketDataAsync(_config.Grid.Symbol, ct);
            AccountState account = await _exchange.GetAccountStateAsync(ct);

            _logger.LogDebug("[Sync #{Count}] BTC mid={Mid:F2}, equity={Equity:F2}",
                _syncCount, market.MidPrice, account.TotalEquity);

            RiskVerdict verdict = _risk.Evaluate(account, market);

            switch (verdict.Action)
            {
                case RiskAction.Halt:
                    _logger.LogCritical("HALT: {Reason}. Cancelling all orders.", verdict.Reason);
                    await _exchange.CancelAllOrdersAsync(_config.Grid.AssetIndex, ct);
                    return;

                case RiskAction.ResetGrid:
                    _logger.LogWarning("Grid reset triggered: {Reason}", verdict.Reason);
                    await _strategy.ResetAsync(ct);
                    UpdateGridBounds();
                    return;

                case RiskAction.Continue:
                    if (RiskManager.ShouldResetGrid(market.MidPrice, _gridLower, _gridUpper))
                    {
                        _logger.LogInformation(
                            "Price drifted outside 80% of grid range. Recentring grid.");
                        await _strategy.ResetAsync(ct);
                        UpdateGridBounds();
                        return;
                    }

                    await _strategy.SyncAsync(ct);

                    // Re-fetch account after sync so VerifyPositions sees post-fill positions.
                    AccountState postSyncAccount = await _exchange.GetAccountStateAsync(ct);
                    var newFills = _strategy.DrainNewFills();
                    _status.UpdateFromSync(market.MidPrice, postSyncAccount.TotalEquity,
                        postSyncAccount.AvailableBalance, _strategy.RealizedPnl,
                        _syncCount, _strategy.Levels, newFills, _strategy.TrackedNetPosition);
                    foreach (var msg in _strategy.DrainMismatches())
                        _status.RecordMismatch(msg);
                    VerifyPositions(postSyncAccount);
                    LogPnlSummary();
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync #{Count} failed. Will retry next interval.", _syncCount);
        }
    }

    // ── Shutdown ──────────────────────────────────────────────────────────────

    private async Task ShutdownAsync()
    {
        _logger.LogInformation(
            "Shutting down. Total syncs: {Count}, Realized PnL: {Pnl:F4} USDC",
            _syncCount, _strategy.RealizedPnl);
        // Leave orders on the book — remove this if you want cancel-on-exit
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateGridBounds()
    {
        if (_strategy.Levels.Count == 0) return;
        _gridLower = _strategy.Levels[0].Price;
        _gridUpper = _strategy.Levels[^1].Price;
        _logger.LogInformation(
            "Grid bounds updated: [{Lower:F2}, {Upper:F2}]", _gridLower, _gridUpper);
    }

    // Number of consecutive syncs with a position mismatch before the tracker is snapped.
    // Mismatches caused by fills landing in the gap between SyncAsync and GetAccountStateAsync
    // self-correct on the next sync without any intervention. Only snap for persistent
    // divergence that doesn't resolve on its own (genuine state corruption).
    private const int MismatchSnapThreshold = 5;

    private void VerifyPositions(AccountState account)
    {
        decimal expectedNet = _strategy.TrackedNetPosition;

        decimal actualNet = account.Positions
            .Where(p => p.Symbol == _config.Grid.Symbol)
            .Sum(p => p.Size);

        if (Math.Abs(expectedNet - actualNet) > 0.0001m)
        {
            _consecutiveMismatchCount++;
            string msg = $"Position mismatch: bot expects {expectedNet:F4} {_config.Grid.Symbol}, exchange shows {actualNet:F4}";

            if (_consecutiveMismatchCount >= MismatchSnapThreshold)
            {
                _logger.LogError(
                    "Persistent mismatch after {N} syncs — auto-correcting tracker. {Msg}",
                    MismatchSnapThreshold, msg);
                _status.RecordMismatch(msg + " — auto-corrected");
                _strategy.SnapTrackedPosition(actualNet);
                _consecutiveMismatchCount = 0;
            }
            else
            {
                _logger.LogWarning(
                    "Transient mismatch #{Count}/{Threshold}: {Msg}",
                    _consecutiveMismatchCount, MismatchSnapThreshold, msg);
                // Do not snap — a fill that landed between SyncAsync and this account fetch
                // will be picked up by the fills API on the next sync cycle and self-correct.
            }
        }
        else
        {
            _consecutiveMismatchCount = 0;
        }
    }

    private void LogPnlSummary()
    {
        int active = _strategy.Levels.Count(l => l.Status == Models.GridLevelStatus.Active);
        int filled = _strategy.Levels.Count(l => l.Status == Models.GridLevelStatus.Filled);
        _logger.LogInformation(
            "Grid status — Active orders: {Active}, Fills: {Filled}, Realized PnL: {Pnl:F4} USDC",
            active, filled, _strategy.RealizedPnl);
    }
}
