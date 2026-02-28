using ClaudGrid.Config;
using ClaudGrid.Models;
using ClaudGrid.Risk;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudGrid.Tests.Risk;

public sealed class RiskManagerTests
{
    private static RiskConfig DefaultRiskConfig() => new()
    {
        MaxPositionSizeBtc = 0.1m,
        MaxDrawdownPercent = 15m,
        MinGridPrice = 10_000m,
        MaxGridPrice = 500_000m
    };

    private static MarketData Market(decimal mid = 50_000m) => new()
    {
        Symbol = "BTC",
        MidPrice = mid,
        BidPrice = mid - 10,
        AskPrice = mid + 10,
        Timestamp = DateTime.UtcNow
    };

    private static AccountState Account(decimal equity = 10_000m, decimal btcPosition = 0m) => new()
    {
        TotalEquity = equity,
        AvailableBalance = equity,
        MarginUsed = 0,
        Positions = btcPosition != 0
            ? new List<PositionInfo> { new PositionInfo { Symbol = "BTC", Size = btcPosition, EntryPrice = 50_000m } }
            : new List<PositionInfo>()
    };

    private RiskManager CreateSut()
    {
        var rm = new RiskManager(DefaultRiskConfig(), NullLogger<RiskManager>.Instance);
        rm.SetInitialEquity(10_000m);
        return rm;
    }

    // ── Normal conditions ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_NormalConditions_ReturnsContinue()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(), Market());
        Assert.Equal(RiskAction.Continue, verdict.Action);
    }

    // ── Drawdown guard ────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_DrawdownExceedsLimit_ReturnsHalt()
    {
        var rm = CreateSut();
        // 16% drawdown on 10,000 peak → 8,400 equity
        var account = Account(equity: 8_400m);
        var verdict = rm.Evaluate(account, Market());
        Assert.Equal(RiskAction.Halt, verdict.Action);
        Assert.NotNull(verdict.Reason);
    }

    [Fact]
    public void Evaluate_DrawdownBelowLimit_ReturnsContinue()
    {
        var rm = CreateSut();
        // 10% drawdown — below the 15% limit
        var account = Account(equity: 9_000m);
        var verdict = rm.Evaluate(account, Market());
        Assert.Equal(RiskAction.Continue, verdict.Action);
    }

    [Fact]
    public void Evaluate_EquityRises_UpdatesPeakEquity()
    {
        var rm = CreateSut();
        // Equity rises to 12,000 — new peak
        rm.Evaluate(Account(equity: 12_000m), Market());

        // Then drops 10% from new peak (12,000 → 10,800) — still below 15% limit
        var verdict = rm.Evaluate(Account(equity: 10_800m), Market());
        Assert.Equal(RiskAction.Continue, verdict.Action);
    }

    [Fact]
    public void Evaluate_EquityRises_ThenDrawdownFromNewPeak_Halts()
    {
        var rm = CreateSut();
        rm.Evaluate(Account(equity: 12_000m), Market());

        // 20% drawdown from new 12,000 peak → 9,600
        var verdict = rm.Evaluate(Account(equity: 9_600m), Market());
        Assert.Equal(RiskAction.Halt, verdict.Action);
    }

    // ── Price range guard ─────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_PriceBelowMinimum_ReturnsHalt()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(), Market(mid: 5_000m));
        Assert.Equal(RiskAction.Halt, verdict.Action);
    }

    [Fact]
    public void Evaluate_PriceAboveMaximum_ReturnsHalt()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(), Market(mid: 600_000m));
        Assert.Equal(RiskAction.Halt, verdict.Action);
    }

    [Fact]
    public void Evaluate_PriceAtBoundary_ReturnsContinue()
    {
        var rm = CreateSut();
        // Exactly at the minimum — should still pass
        var verdict = rm.Evaluate(Account(), Market(mid: 10_000m));
        Assert.Equal(RiskAction.Continue, verdict.Action);
    }

    // ── Position size guard ───────────────────────────────────────────────────

    [Fact]
    public void Evaluate_PositionExceedsMax_ReturnsReset()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(btcPosition: 0.15m), Market());
        Assert.Equal(RiskAction.ResetGrid, verdict.Action);
    }

    [Fact]
    public void Evaluate_ShortPositionExceedsMax_ReturnsReset()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(btcPosition: -0.15m), Market());
        Assert.Equal(RiskAction.ResetGrid, verdict.Action);
    }

    [Fact]
    public void Evaluate_PositionBelowMax_ReturnsContinue()
    {
        var rm = CreateSut();
        var verdict = rm.Evaluate(Account(btcPosition: 0.05m), Market());
        Assert.Equal(RiskAction.Continue, verdict.Action);
    }

    // ── ShouldResetGrid ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(50_000, 45_000, 55_000, false)]   // Price at centre — no reset
    [InlineData(54_000, 45_000, 55_000, false)]   // 80% from centre — borderline, no reset
    [InlineData(54_100, 45_000, 55_000, true)]    // Just past 80% threshold — reset
    [InlineData(55_500, 45_000, 55_000, true)]    // Outside the grid — reset
    [InlineData(44_500, 45_000, 55_000, true)]    // Below the grid — reset
    public void ShouldResetGrid_VariousPositions(
        decimal price, decimal lower, decimal upper, bool expectedReset)
    {
        bool result = RiskManager.ShouldResetGrid(price, lower, upper);
        Assert.Equal(expectedReset, result);
    }
}
