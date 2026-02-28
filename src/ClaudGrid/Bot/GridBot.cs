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
        if (account.TotalEquity == 0m)
        {
            decimal spotUsdc = await _exchange.GetSpotUsdcBalanceAsync(ct);
            if (spotUsdc > 0m)
            {
                _logger.LogInformation(
                    "Perps balance is zero. Transferring {Amount:F2} USDC from spot wallet...", spotUsdc);
                await _exchange.TransferSpotToPerpsAsync(spotUsdc, ct);
                await Task.Delay(2000, ct); // brief pause for the transfer to settle
                account = await _exchange.GetAccountStateAsync(ct);
            }
        }

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
                    var newFills = _strategy.DrainNewFills();
                    _status.UpdateFromSync(market.MidPrice, account.TotalEquity,
                        account.AvailableBalance, _strategy.RealizedPnl,
                        _syncCount, _strategy.Levels, newFills);
                    LogPnlSummary(account);
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

    private void LogPnlSummary(AccountState account)
    {
        int active = _strategy.Levels.Count(l => l.Status == Models.GridLevelStatus.Active);
        int filled = _strategy.Levels.Count(l => l.Status == Models.GridLevelStatus.Filled);
        _logger.LogInformation(
            "Grid status — Active orders: {Active}, Fills: {Filled}, Realized PnL: {Pnl:F4} USDC",
            active, filled, _strategy.RealizedPnl);
    }
}
