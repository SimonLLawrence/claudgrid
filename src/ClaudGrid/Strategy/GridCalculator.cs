using ClaudGrid.Config;
using ClaudGrid.Models;

namespace ClaudGrid.Strategy;

/// <summary>
/// Pure, stateless grid mathematics. No I/O; easily unit-tested.
///
/// Grid layout (20 levels, 1% spacing, mid = $50,000):
///   Level 19  →  $52,420  SELL
///   ...
///   Level 10  →  $50,000  ← mid-price reference
///   ...
///   Level  0  →  $47,705  BUY
///
/// Price formula: price[i] = midPrice × (1 + spacing)^(i - midIndex)
/// Using compound spacing (geometric) avoids cumulative drift.
/// </summary>
public static class GridCalculator
{
    /// <summary>
    /// Builds all grid levels centred around <paramref name="midPrice"/>.
    /// Levels below mid get BUY orders; levels above get SELL orders.
    /// The level closest to the current price is left pending (no order placed
    /// immediately; it becomes active once the price moves away).
    /// </summary>
    public static List<GridLevel> BuildGrid(decimal midPrice, GridConfig cfg)
    {
        if (midPrice <= 0) throw new ArgumentOutOfRangeException(nameof(midPrice));
        if (cfg.GridLevels < 2) throw new ArgumentException("GridLevels must be ≥ 2");

        decimal spacing = cfg.GridSpacingPercent / 100m;
        int total = cfg.GridLevels;
        int midIndex = total / 2;

        var levels = new List<GridLevel>(total);
        for (int i = 0; i < total; i++)
        {
            int stepsFromMid = i - midIndex;
            decimal price = midPrice * (decimal)Math.Pow((double)(1 + spacing), stepsFromMid);
            price = RoundToTickSize(price);

            GridLevelSide side = i < midIndex ? GridLevelSide.Buy : GridLevelSide.Sell;

            levels.Add(new GridLevel
            {
                Index = i,
                Price = price,
                Side = side,
                Size = cfg.OrderSizeBtc,
                Status = GridLevelStatus.Pending
            });
        }
        return levels;
    }

    /// <summary>
    /// Returns the expected annual profit rate as a fraction (e.g. 0.07 = 7%).
    ///
    /// Model assumptions:
    ///   - Each grid spacing captured = spacing% profit on trade notional.
    ///   - Round-trip fee = 2 × takerFee (conservative).
    ///   - Estimated trades = annualOscillations (caller-provided heuristic).
    ///   - Total capital = gridLevels × orderSize × midPrice.
    /// </summary>
    public static decimal EstimatedAnnualReturnRate(
        decimal midPrice,
        GridConfig cfg,
        int estimatedAnnualOscillations = 300,
        decimal takerFeeRate = 0.00045m)
    {
        if (midPrice <= 0 || cfg.GridLevels == 0 || cfg.OrderSizeBtc == 0) return 0m;

        decimal spacingFraction = cfg.GridSpacingPercent / 100m;
        decimal roundTripFee = 2 * takerFeeRate;
        decimal profitPerTrade = spacingFraction - roundTripFee;

        if (profitPerTrade <= 0) return 0m;

        decimal tradeNotional = cfg.OrderSizeBtc * midPrice;
        decimal totalCapital = cfg.GridLevels * tradeNotional;
        decimal annualProfit = estimatedAnnualOscillations * profitPerTrade * tradeNotional;

        return annualProfit / totalCapital;
    }

    /// <summary>
    /// Returns the grid's lower and upper price bounds.
    /// </summary>
    public static (decimal lower, decimal upper) GetGridBounds(decimal midPrice, GridConfig cfg)
    {
        decimal spacing = cfg.GridSpacingPercent / 100m;
        int midIndex = cfg.GridLevels / 2;
        int maxSteps = cfg.GridLevels - 1 - midIndex;

        decimal lower = midPrice * (decimal)Math.Pow((double)(1 + spacing), -midIndex);
        decimal upper = midPrice * (decimal)Math.Pow((double)(1 + spacing), maxSteps);

        return (RoundToTickSize(lower), RoundToTickSize(upper));
    }

    /// <summary>
    /// Given the index of a filled buy order, returns the sell price for the
    /// counter-order one step higher. Returns null if at the top of the grid.
    /// </summary>
    public static decimal? CounterSellPrice(int filledBuyIndex, List<GridLevel> levels)
    {
        int counterIndex = filledBuyIndex + 1;
        if (counterIndex >= levels.Count) return null;
        return levels[counterIndex].Price;
    }

    /// <summary>
    /// Given the index of a filled sell order, returns the buy price for the
    /// counter-order one step lower. Returns null if at the bottom of the grid.
    /// </summary>
    public static decimal? CounterBuyPrice(int filledSellIndex, List<GridLevel> levels)
    {
        int counterIndex = filledSellIndex - 1;
        if (counterIndex < 0) return null;
        return levels[counterIndex].Price;
    }

    /// <summary>
    /// Rounds price to Hyperliquid BTC tick size (1 cent = $0.1 for BTC perp).
    /// Adjust if the exchange minimum tick changes.
    /// </summary>
    public static decimal RoundToTickSize(decimal price, decimal tick = 0.1m) =>
        Math.Round(price / tick, MidpointRounding.AwayFromZero) * tick;
}
