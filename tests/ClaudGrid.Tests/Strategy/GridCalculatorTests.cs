using ClaudGrid.Config;
using ClaudGrid.Models;
using ClaudGrid.Strategy;
using Xunit;

namespace ClaudGrid.Tests.Strategy;

public sealed class GridCalculatorTests
{
    private static GridConfig DefaultConfig(int levels = 20, decimal spacing = 1.0m) => new()
    {
        Symbol = "BTC",
        AssetIndex = 0,
        GridLevels = levels,
        GridSpacingPercent = spacing,
        OrderSizeBtc = 0.001m
    };

    // ── BuildGrid ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildGrid_ReturnsCorrectNumberOfLevels()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig(20));
        Assert.Equal(20, levels.Count);
    }

    [Fact]
    public void BuildGrid_LevelsArePriceSortedAscending()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig(20));
        for (int i = 1; i < levels.Count; i++)
            Assert.True(levels[i].Price > levels[i - 1].Price,
                $"Level {i} price {levels[i].Price} should be > level {i - 1} price {levels[i - 1].Price}");
    }

    [Fact]
    public void BuildGrid_BuyLevelsBelowMid_SellLevelsAboveMid()
    {
        decimal mid = 50_000m;
        var levels = GridCalculator.BuildGrid(mid, DefaultConfig(20));
        int midIndex = 10;

        for (int i = 0; i < midIndex; i++)
            Assert.Equal(GridLevelSide.Buy, levels[i].Side);

        for (int i = midIndex; i < 20; i++)
            Assert.Equal(GridLevelSide.Sell, levels[i].Side);
    }

    [Fact]
    public void BuildGrid_SpacingIsApproximatelyCorrect()
    {
        decimal mid = 50_000m;
        decimal spacingPct = 1.0m;
        var levels = GridCalculator.BuildGrid(mid, DefaultConfig(20, spacingPct));

        // Adjacent prices should differ by approximately 1%
        for (int i = 1; i < levels.Count; i++)
        {
            decimal ratio = levels[i].Price / levels[i - 1].Price;
            decimal expectedRatio = 1m + spacingPct / 100m;
            Assert.InRange(ratio, expectedRatio * 0.999m, expectedRatio * 1.001m);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildGrid_InvalidMidPrice_Throws(decimal midPrice)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GridCalculator.BuildGrid(midPrice, DefaultConfig()));
    }

    [Fact]
    public void BuildGrid_AllLevelsHaveCorrectSize()
    {
        decimal orderSize = 0.002m;
        var cfg = DefaultConfig();
        cfg.OrderSizeBtc = orderSize;
        var levels = GridCalculator.BuildGrid(50_000m, cfg);
        Assert.All(levels, l => Assert.Equal(orderSize, l.Size));
    }

    [Fact]
    public void BuildGrid_AllLevelsStartPending()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig());
        Assert.All(levels, l => Assert.Equal(GridLevelStatus.Pending, l.Status));
    }

    // ── GetGridBounds ─────────────────────────────────────────────────────────

    [Fact]
    public void GetGridBounds_LowerLessThanUpper()
    {
        var (lower, upper) = GridCalculator.GetGridBounds(50_000m, DefaultConfig());
        Assert.True(lower < upper);
    }

    [Fact]
    public void GetGridBounds_MidPriceWithinBounds()
    {
        decimal mid = 50_000m;
        var (lower, upper) = GridCalculator.GetGridBounds(mid, DefaultConfig());
        Assert.True(lower < mid && mid < upper);
    }

    // ── EstimatedAnnualReturnRate ──────────────────────────────────────────────

    [Fact]
    public void EstimatedAnnualReturnRate_Returns_At_Least_5_Percent_DefaultConfig()
    {
        decimal rate = GridCalculator.EstimatedAnnualReturnRate(
            midPrice: 50_000m,
            cfg: DefaultConfig(),
            estimatedAnnualOscillations: 300);

        Assert.True(rate >= 0.05m,
            $"Expected ≥ 5% annual return, got {rate:P2}");
    }

    [Fact]
    public void EstimatedAnnualReturnRate_ZeroSpacing_ReturnsNonPositive()
    {
        var cfg = DefaultConfig(spacing: 0m);
        decimal rate = GridCalculator.EstimatedAnnualReturnRate(50_000m, cfg);
        Assert.True(rate <= 0m);
    }

    [Fact]
    public void EstimatedAnnualReturnRate_VeryTightSpacing_IsNegative()
    {
        // Spacing smaller than fees → loss
        var cfg = DefaultConfig(spacing: 0.05m);
        decimal rate = GridCalculator.EstimatedAnnualReturnRate(50_000m, cfg, takerFeeRate: 0.00045m);
        Assert.True(rate <= 0m);
    }

    // ── Counter-order prices ──────────────────────────────────────────────────

    [Fact]
    public void CounterSellPrice_ReturnsNextHigherLevel()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig());
        decimal? counter = GridCalculator.CounterSellPrice(5, levels);
        Assert.Equal(levels[6].Price, counter);
    }

    [Fact]
    public void CounterSellPrice_AtTopLevel_ReturnsNull()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig(20));
        decimal? counter = GridCalculator.CounterSellPrice(19, levels); // top level
        Assert.Null(counter);
    }

    [Fact]
    public void CounterBuyPrice_ReturnsNextLowerLevel()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig());
        decimal? counter = GridCalculator.CounterBuyPrice(10, levels);
        Assert.Equal(levels[9].Price, counter);
    }

    [Fact]
    public void CounterBuyPrice_AtBottomLevel_ReturnsNull()
    {
        var levels = GridCalculator.BuildGrid(50_000m, DefaultConfig());
        decimal? counter = GridCalculator.CounterBuyPrice(0, levels);
        Assert.Null(counter);
    }

    // ── RoundToTickSize ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(50000.12345, 0.1, 50000.1)]
    [InlineData(49999.96, 0.1, 50000.0)]
    [InlineData(100.005, 0.01, 100.01)]
    public void RoundToTickSize_RoundsCorrectly(decimal input, decimal tick, decimal expected)
    {
        decimal result = GridCalculator.RoundToTickSize(input, tick);
        Assert.Equal(expected, result);
    }
}
