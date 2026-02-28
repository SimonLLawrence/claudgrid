using ClaudGrid.Config;
using ClaudGrid.Models;
using Microsoft.Extensions.Logging;

namespace ClaudGrid.Risk;

/// <summary>
/// Evaluates risk conditions before each sync cycle.
/// Returns a <see cref="RiskVerdict"/> that the bot acts on.
/// </summary>
public sealed class RiskManager
{
    private readonly RiskConfig _cfg;
    private readonly ILogger<RiskManager> _logger;
    private decimal _peakEquity;

    public RiskManager(RiskConfig cfg, ILogger<RiskManager> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    public void SetInitialEquity(decimal equity)
    {
        _peakEquity = equity;
        _logger.LogInformation("Peak equity initialised at {Equity:F2}", equity);
    }

    /// <summary>
    /// Evaluate all risk conditions. Returns a verdict instructing the bot what to do.
    /// </summary>
    public RiskVerdict Evaluate(AccountState account, MarketData market)
    {
        // Track high-water mark
        if (account.TotalEquity > _peakEquity)
            _peakEquity = account.TotalEquity;

        // 1. Drawdown guard
        if (_peakEquity > 0)
        {
            decimal drawdown = (_peakEquity - account.TotalEquity) / _peakEquity;
            if (drawdown >= _cfg.MaxDrawdownPercent / 100m)
            {
                _logger.LogWarning(
                    "Drawdown guard triggered: {Drawdown:P2} >= {Limit:P0}. Halting.",
                    drawdown, _cfg.MaxDrawdownPercent / 100m);
                return RiskVerdict.Halt(
                    $"Drawdown {drawdown:P2} exceeds limit {_cfg.MaxDrawdownPercent}%");
            }
        }

        // 2. Price out of permitted range
        if (market.MidPrice < _cfg.MinGridPrice || market.MidPrice > _cfg.MaxGridPrice)
        {
            _logger.LogWarning(
                "Price {Price:F2} is outside permitted range [{Min:F0}, {Max:F0}]. Halting.",
                market.MidPrice, _cfg.MinGridPrice, _cfg.MaxGridPrice);
            return RiskVerdict.Halt(
                $"Price {market.MidPrice:F2} outside [{_cfg.MinGridPrice}, {_cfg.MaxGridPrice}]");
        }

        // 3. Net position too large
        decimal netBtc = account.Positions
            .Where(p => p.Symbol == "BTC")
            .Sum(p => p.Size);

        if (Math.Abs(netBtc) > _cfg.MaxPositionSizeBtc)
        {
            _logger.LogWarning(
                "Position size {Size:F4} BTC exceeds max {Max:F4} BTC. Requesting grid reset.",
                netBtc, _cfg.MaxPositionSizeBtc);
            return RiskVerdict.Reset(
                $"Net position {netBtc:F4} BTC exceeds max {_cfg.MaxPositionSizeBtc} BTC");
        }

        return RiskVerdict.Ok();
    }

    /// <summary>
    /// Checks whether the current price has drifted more than half the grid
    /// range from the grid anchor, meaning the grid should be re-centred.
    /// </summary>
    public static bool ShouldResetGrid(decimal currentPrice, decimal gridLower, decimal gridUpper)
    {
        decimal gridMid = (gridLower + gridUpper) / 2m;
        decimal gridHalfRange = (gridUpper - gridLower) / 2m;
        return Math.Abs(currentPrice - gridMid) > gridHalfRange * 0.8m;
    }
}

public sealed class RiskVerdict
{
    public RiskAction Action { get; private init; }
    public string? Reason { get; private init; }

    public static RiskVerdict Ok() => new() { Action = RiskAction.Continue };
    public static RiskVerdict Halt(string reason) => new() { Action = RiskAction.Halt, Reason = reason };
    public static RiskVerdict Reset(string reason) => new() { Action = RiskAction.ResetGrid, Reason = reason };
}

public enum RiskAction { Continue, ResetGrid, Halt }
