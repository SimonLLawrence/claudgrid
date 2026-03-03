using ClaudGrid.Models;
using ClaudGrid.Web;
using Xunit;

namespace ClaudGrid.Tests.Web;

public sealed class BotStatusServiceTests
{
    private static List<GridLevel> NoLevels() => new();

    private static List<GridLevel> MakeLevels(params GridLevelStatus[] statuses)
    {
        return statuses.Select((s, i) => new GridLevel
        {
            Index = i,
            Price = 50_000m + i * 500m,
            Side = i < statuses.Length / 2 ? GridLevelSide.Buy : GridLevelSide.Sell,
            Size = 0.001m,
            Status = s
        }).ToList();
    }

    private static IEnumerable<FillRecord> NoFills() => Enumerable.Empty<FillRecord>();

    private static FillRecord MakeFill(string side = "Buy", decimal pnl = 0m, bool isClose = false) =>
        new(DateTime.UtcNow, side, 50_000m, 0.001m, pnl, isClose);

    // ── Initial state ─────────────────────────────────────────────────────────

    [Fact]
    public void GetSnapshot_InitialState_IsRunningFalse()
    {
        var svc = new BotStatusService();
        Assert.False(svc.GetSnapshot().IsRunning);
    }

    [Fact]
    public void GetSnapshot_InitialState_AllFieldsAreDefault()
    {
        var svc = new BotStatusService();
        var snap = svc.GetSnapshot();
        Assert.Equal(0m, snap.MidPrice);
        Assert.Equal(0m, snap.TotalEquity);
        Assert.Equal(0m, snap.RealizedPnl);
        Assert.Equal(0, snap.SyncCount);
        Assert.Empty(snap.Levels);
        Assert.Empty(snap.PriceHistory);
        Assert.Empty(snap.PnlHistory);
        Assert.Empty(snap.RecentFills);
        Assert.Empty(snap.RecentMismatches);
    }

    // ── UpdateFromSync ────────────────────────────────────────────────────────

    [Fact]
    public void UpdateFromSync_SetsIsRunningTrue()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 9_000m, 0m, 1, NoLevels(), NoFills(), 0m);
        Assert.True(svc.GetSnapshot().IsRunning);
    }

    [Fact]
    public void UpdateFromSync_SetsAllFields()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(
            midPrice: 48_000m, equity: 10_500m, available: 9_800m,
            pnl: 2.5m, syncCount: 17, levels: NoLevels(),
            newFills: NoFills(), trackedNetPosition: 0.002m);

        var snap = svc.GetSnapshot();
        Assert.Equal(48_000m, snap.MidPrice);
        Assert.Equal(10_500m, snap.TotalEquity);
        Assert.Equal(9_800m, snap.AvailableBalance);
        Assert.Equal(2.5m, snap.RealizedPnl);
        Assert.Equal(17, snap.SyncCount);
        Assert.Equal(0.002m, snap.NetPosition);
    }

    [Fact]
    public void UpdateFromSync_BuildsPriceHistory()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);
        svc.UpdateFromSync(51_000m, 10_000m, 10_000m, 0m, 2, NoLevels(), NoFills(), 0m);

        var snap = svc.GetSnapshot();
        Assert.Equal(2, snap.PriceHistory.Count);
        Assert.Equal(50_000m, snap.PriceHistory[0].Price);
        Assert.Equal(51_000m, snap.PriceHistory[1].Price);
    }

    [Fact]
    public void UpdateFromSync_PriceHistoryCappedAt120()
    {
        var svc = new BotStatusService();
        for (int i = 0; i < 130; i++)
            svc.UpdateFromSync(50_000m + i, 10_000m, 10_000m, 0m, i, NoLevels(), NoFills(), 0m);

        Assert.Equal(120, svc.GetSnapshot().PriceHistory.Count);
    }

    [Fact]
    public void UpdateFromSync_PriceHistory_OldestEntryDropped()
    {
        var svc = new BotStatusService();
        // Fill to cap, first entry has price 1
        for (int i = 1; i <= 120; i++)
            svc.UpdateFromSync(i, 10_000m, 10_000m, 0m, i, NoLevels(), NoFills(), 0m);

        // Add one more — price 1 should be evicted
        svc.UpdateFromSync(121m, 10_000m, 10_000m, 0m, 121, NoLevels(), NoFills(), 0m);
        Assert.Equal(2m, svc.GetSnapshot().PriceHistory[0].Price);
        Assert.Equal(121m, svc.GetSnapshot().PriceHistory[^1].Price);
    }

    [Fact]
    public void UpdateFromSync_PnlHistoryCappedAt120()
    {
        var svc = new BotStatusService();
        for (int i = 0; i < 130; i++)
            svc.UpdateFromSync(50_000m, 10_000m, 10_000m, i, i, NoLevels(), NoFills(), 0m);

        Assert.Equal(120, svc.GetSnapshot().PnlHistory.Count);
    }

    [Fact]
    public void UpdateFromSync_AccumulatesFills()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(),
            new[] { MakeFill() }, 0m);

        var snap = svc.GetSnapshot();
        Assert.Equal(1, snap.TotalFills);
        Assert.Single(snap.RecentFills);
    }

    [Fact]
    public void UpdateFromSync_FillsCappedAt50()
    {
        var svc = new BotStatusService();
        for (int i = 0; i < 60; i++)
            svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, i, NoLevels(),
                new[] { MakeFill() }, 0m);

        Assert.Equal(50, svc.GetSnapshot().RecentFills.Count);
        Assert.Equal(60, svc.GetSnapshot().TotalFills);
    }

    [Fact]
    public void UpdateFromSync_RecentFills_MostRecentFirst()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(),
            new[] { MakeFill("Buy"), MakeFill("Sell") }, 0m);

        var fills = svc.GetSnapshot().RecentFills;
        // DrainedNewFills is enqueued in order; GetSnapshot reverses (most recent first)
        Assert.Equal("Sell", fills[0].Side);
        Assert.Equal("Buy", fills[1].Side);
    }

    [Fact]
    public void UpdateFromSync_ComputesDrawdown()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m); // peak = 10000
        svc.UpdateFromSync(50_000m,  9_000m,  9_000m, 0m, 2, NoLevels(), NoFills(), 0m); // 10% down

        Assert.InRange(svc.GetSnapshot().DrawdownPercent, 9.9m, 10.1m);
    }

    [Fact]
    public void UpdateFromSync_DrawdownZeroWhenAtPeak()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);
        svc.UpdateFromSync(50_000m, 11_000m, 11_000m, 0m, 2, NoLevels(), NoFills(), 0m); // new peak

        Assert.Equal(0m, svc.GetSnapshot().DrawdownPercent);
    }

    [Fact]
    public void UpdateFromSync_PeakEquityTracked_DrawdownFromNewPeak()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);
        svc.UpdateFromSync(50_000m, 12_000m, 12_000m, 0m, 2, NoLevels(), NoFills(), 0m); // peak = 12000
        svc.UpdateFromSync(50_000m, 10_200m, 10_200m, 0m, 3, NoLevels(), NoFills(), 0m); // 15% down from 12000

        Assert.InRange(svc.GetSnapshot().DrawdownPercent, 14.9m, 15.1m);
    }

    [Fact]
    public void UpdateFromSync_CountsActiveAndPartialFillAsActiveOrders()
    {
        var svc = new BotStatusService();
        var levels = MakeLevels(
            GridLevelStatus.Active,
            GridLevelStatus.Active,
            GridLevelStatus.PartialFill,
            GridLevelStatus.Filled,
            GridLevelStatus.Initial
        );
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, levels, NoFills(), 0m);

        var snap = svc.GetSnapshot();
        Assert.Equal(3, snap.ActiveOrders);   // Active(2) + PartialFill(1)
        Assert.Equal(1, snap.FilledLevels);   // Filled(1) only
    }

    [Fact]
    public void UpdateFromSync_LevelDtos_MatchInputLevels()
    {
        var svc = new BotStatusService();
        var levels = new List<GridLevel>
        {
            new() { Index = 0, Price = 49_000m, Side = GridLevelSide.Buy, Size = 0.001m,
                    Status = GridLevelStatus.Active, RealizedPnl = 0.5m, NetPositionSize = 0m }
        };
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, levels, NoFills(), 0m);

        var dto = svc.GetSnapshot().Levels.Single();
        Assert.Equal(0, dto.Index);
        Assert.Equal("Buy", dto.Side);
        Assert.Equal(49_000m, dto.Price);
        Assert.Equal("Active", dto.Status);
        Assert.Equal(0.5m, dto.Pnl);
        Assert.True(dto.HasExchangeOrder);
    }

    // ── RecordMismatch ────────────────────────────────────────────────────────

    [Fact]
    public void RecordMismatch_AppendsToSnapshot()
    {
        var svc = new BotStatusService();
        svc.RecordMismatch("test mismatch");

        var snap = svc.GetSnapshot();
        Assert.Single(snap.RecentMismatches);
        Assert.Equal("test mismatch", snap.RecentMismatches[0].Message);
    }

    [Fact]
    public void RecordMismatch_CappedAt20()
    {
        var svc = new BotStatusService();
        for (int i = 0; i < 25; i++)
            svc.RecordMismatch($"mismatch {i}");

        Assert.Equal(20, svc.GetSnapshot().RecentMismatches.Count);
    }

    [Fact]
    public void RecordMismatch_MostRecentFirst()
    {
        var svc = new BotStatusService();
        svc.RecordMismatch("first");
        svc.RecordMismatch("second");

        var mismatches = svc.GetSnapshot().RecentMismatches;
        Assert.Equal("second", mismatches[0].Message);
        Assert.Equal("first", mismatches[1].Message);
    }

    // ── AddFills ──────────────────────────────────────────────────────────────

    [Fact]
    public void AddFills_UpdatesFillCountAndRecentFills()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);

        svc.AddFills(new[] { MakeFill("Sell", pnl: 1.5m, isClose: true) }, NoLevels());

        var snap = svc.GetSnapshot();
        Assert.Equal(1, snap.TotalFills);
        Assert.Single(snap.RecentFills);
        Assert.Equal("Sell", snap.RecentFills[0].Side);
        Assert.True(snap.RecentFills[0].IsClose);
    }

    [Fact]
    public void AddFills_EmptyEnumerable_SnapshotUnchanged()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);
        var before = svc.GetSnapshot();

        svc.AddFills(NoFills(), NoLevels());
        var after = svc.GetSnapshot();

        Assert.Same(before, after);
    }

    [Fact]
    public void AddFills_PreservesExistingFills()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(),
            new[] { MakeFill("Buy") }, 0m);

        svc.AddFills(new[] { MakeFill("Sell") }, NoLevels());

        Assert.Equal(2, svc.GetSnapshot().TotalFills);
        Assert.Equal(2, svc.GetSnapshot().RecentFills.Count);
    }

    [Fact]
    public void AddFills_FillsCappedAt50()
    {
        var svc = new BotStatusService();
        svc.UpdateFromSync(50_000m, 10_000m, 10_000m, 0m, 1, NoLevels(), NoFills(), 0m);

        for (int i = 0; i < 60; i++)
            svc.AddFills(new[] { MakeFill() }, NoLevels());

        Assert.Equal(50, svc.GetSnapshot().RecentFills.Count);
        Assert.Equal(60, svc.GetSnapshot().TotalFills);
    }
}
