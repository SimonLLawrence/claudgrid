using ClaudGrid.Models;

namespace ClaudGrid.Web;

public record PricePoint(DateTime Time, decimal Price);
public record PnlPoint(DateTime Time, decimal Pnl);
public record FillRecord(DateTime Time, string Side, decimal Price, decimal Size, decimal Pnl, bool IsClose);
public record GridLevelDto(int Index, string Side, decimal Price, decimal Size, string Status, decimal Pnl, decimal NetPositionSize, bool HasExchangeOrder, int PendingCounterCount);
public record MismatchRecord(DateTime Time, string Message);

public sealed class BotSnapshot
{
    public bool IsRunning { get; set; }
    public int SyncCount { get; set; }
    public decimal MidPrice { get; set; }
    public decimal TotalEquity { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal RealizedPnl { get; set; }
    public decimal DrawdownPercent { get; set; }
    public int ActiveOrders { get; set; }
    public int FilledLevels { get; set; }
    public int TotalFills { get; set; }
    public decimal NetPosition { get; set; }
    public List<GridLevelDto> Levels { get; set; } = new();
    public List<FillRecord> RecentFills { get; set; } = new();
    public List<PricePoint> PriceHistory { get; set; } = new();
    public List<PnlPoint> PnlHistory { get; set; } = new();
    public List<MismatchRecord> RecentMismatches { get; set; } = new();
}

public sealed class BotStatusService
{
    private readonly object _lock = new();
    private readonly Queue<PricePoint> _priceHistory = new();
    private readonly Queue<PnlPoint> _pnlHistory = new();
    private readonly Queue<FillRecord> _recentFills = new();
    private readonly Queue<MismatchRecord> _mismatches = new();
    private int _totalFills;
    private decimal _peakEquity;
    private BotSnapshot _snapshot = new();

    private const int MaxHistory = 120;
    private const int MaxFills = 50;
    private const int MaxMismatches = 20;

    public void UpdateFromSync(
        decimal midPrice, decimal equity, decimal available, decimal pnl,
        int syncCount, IReadOnlyList<GridLevel> levels,
        IEnumerable<FillRecord> newFills, decimal trackedNetPosition)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            _priceHistory.Enqueue(new PricePoint(now, midPrice));
            if (_priceHistory.Count > MaxHistory) _priceHistory.Dequeue();

            _pnlHistory.Enqueue(new PnlPoint(now, pnl));
            if (_pnlHistory.Count > MaxHistory) _pnlHistory.Dequeue();

            foreach (var f in newFills)
            {
                _recentFills.Enqueue(f);
                if (_recentFills.Count > MaxFills) _recentFills.Dequeue();
                _totalFills++;
            }

            if (equity > _peakEquity) _peakEquity = equity;
            decimal drawdown = _peakEquity > 0
                ? Math.Max(0m, (_peakEquity - equity) / _peakEquity * 100m)
                : 0m;

            _snapshot = new BotSnapshot
            {
                IsRunning = true,
                SyncCount = syncCount,
                MidPrice = midPrice,
                TotalEquity = equity,
                AvailableBalance = available,
                RealizedPnl = pnl,
                DrawdownPercent = drawdown,
                ActiveOrders = levels.Count(l => l.Status == GridLevelStatus.Active || l.Status == GridLevelStatus.PartialFill),
                FilledLevels = levels.Count(l => l.Status == GridLevelStatus.Filled),
                TotalFills = _totalFills,
                NetPosition = trackedNetPosition,
                Levels = levels.Select(l => new GridLevelDto(
                    l.Index, l.Side.ToString(), l.Price, l.Size,
                    l.Status.ToString(), l.RealizedPnl, l.NetPositionSize,
                    l.Status is GridLevelStatus.Active or GridLevelStatus.PartialFill,
                    l.PendingCounters.Count)).ToList(),
                RecentFills = _recentFills.Reverse().ToList(),
                PriceHistory = _priceHistory.ToList(),
                PnlHistory = _pnlHistory.ToList(),
                RecentMismatches = _mismatches.Reverse().ToList()
            };
        }
    }

    /// <summary>
    /// Immediately publishes fills from a manual close without waiting for the next sync cycle.
    /// Re-uses the most recent snapshot for all non-fill fields.
    /// </summary>
    public void AddFills(IEnumerable<FillRecord> fills, IReadOnlyList<GridLevel> levels)
    {
        lock (_lock)
        {
            bool any = false;
            foreach (var f in fills)
            {
                _recentFills.Enqueue(f);
                if (_recentFills.Count > MaxFills) _recentFills.Dequeue();
                _totalFills++;
                any = true;
            }
            if (!any) return;

            var s = _snapshot;
            _snapshot = new BotSnapshot
            {
                IsRunning = s.IsRunning,
                SyncCount = s.SyncCount,
                MidPrice = s.MidPrice,
                TotalEquity = s.TotalEquity,
                AvailableBalance = s.AvailableBalance,
                RealizedPnl = levels.Sum(l => l.RealizedPnl),
                DrawdownPercent = s.DrawdownPercent,
                ActiveOrders = levels.Count(l => l.Status == GridLevelStatus.Active || l.Status == GridLevelStatus.PartialFill),
                FilledLevels = levels.Count(l => l.Status == GridLevelStatus.Filled),
                TotalFills = _totalFills,
                NetPosition = levels.Sum(l => l.NetPositionSize),
                Levels = levels.Select(l => new GridLevelDto(
                    l.Index, l.Side.ToString(), l.Price, l.Size,
                    l.Status.ToString(), l.RealizedPnl, l.NetPositionSize,
                    l.Status is GridLevelStatus.Active or GridLevelStatus.PartialFill,
                    l.PendingCounters.Count)).ToList(),
                RecentFills = _recentFills.Reverse().ToList(),
                PriceHistory = s.PriceHistory,
                PnlHistory = s.PnlHistory,
                RecentMismatches = _mismatches.Reverse().ToList()
            };
        }
    }

    public void RecordMismatch(string message)
    {
        lock (_lock)
        {
            _mismatches.Enqueue(new MismatchRecord(DateTime.UtcNow, message));
            if (_mismatches.Count > MaxMismatches) _mismatches.Dequeue();
            _snapshot.RecentMismatches = _mismatches.Reverse().ToList();
        }
    }

    public BotSnapshot GetSnapshot()
    {
        lock (_lock) return _snapshot;
    }
}
