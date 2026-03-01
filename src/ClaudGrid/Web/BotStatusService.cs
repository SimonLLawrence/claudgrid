using ClaudGrid.Models;

namespace ClaudGrid.Web;

public record PricePoint(DateTime Time, decimal Price);
public record PnlPoint(DateTime Time, decimal Pnl);
public record FillRecord(DateTime Time, string Side, decimal Price, decimal Size, decimal Pnl);
public record GridLevelDto(int Index, string Side, decimal Price, decimal Size, string Status, decimal Pnl);

public sealed class BotSnapshot
{
    public bool IsRunning { get; set; }
    public int SyncCount { get; set; }
    public decimal MidPrice { get; set; }
    public decimal TotalEquity { get; set; }
    public decimal AvailableBalance { get; set; }
    public decimal RealizedPnl { get; set; }
    public int ActiveOrders { get; set; }
    public int FilledLevels { get; set; }
    public int TotalFills { get; set; }
    public List<GridLevelDto> Levels { get; set; } = new();
    public List<FillRecord> RecentFills { get; set; } = new();
    public List<PricePoint> PriceHistory { get; set; } = new();
    public List<PnlPoint> PnlHistory { get; set; } = new();
}

public sealed class BotStatusService
{
    private readonly object _lock = new();
    private readonly Queue<PricePoint> _priceHistory = new();
    private readonly Queue<PnlPoint> _pnlHistory = new();
    private readonly Queue<FillRecord> _recentFills = new();
    private int _totalFills;
    private BotSnapshot _snapshot = new();

    private const int MaxHistory = 120;
    private const int MaxFills = 50;

    public void UpdateFromSync(
        decimal midPrice, decimal equity, decimal available, decimal pnl,
        int syncCount, IReadOnlyList<GridLevel> levels,
        IEnumerable<FillRecord> newFills)
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

            _snapshot = new BotSnapshot
            {
                IsRunning = true,
                SyncCount = syncCount,
                MidPrice = midPrice,
                TotalEquity = equity,
                AvailableBalance = available,
                RealizedPnl = pnl,
                ActiveOrders = levels.Count(l => l.Status == GridLevelStatus.Active),
                FilledLevels = levels.Count(l => l.Status == GridLevelStatus.Filled),
                TotalFills = _totalFills,
                Levels = levels.Select(l => new GridLevelDto(
                    l.Index, l.Side.ToString(), l.Price, l.Size,
                    l.Status.ToString(), l.RealizedPnl)).ToList(),
                RecentFills = _recentFills.Reverse().ToList(),
                PriceHistory = _priceHistory.ToList(),
                PnlHistory = _pnlHistory.ToList()
            };
        }
    }

    public BotSnapshot GetSnapshot()
    {
        lock (_lock) return _snapshot;
    }
}
