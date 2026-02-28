using ClaudGrid.Bot;
using ClaudGrid.Config;
using ClaudGrid.Risk;
using ClaudGrid.Strategy;
using ClaudGrid.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ClaudGrid.Tests.Bot;

public sealed class GridBotTests
{
    private static BotConfig MakeConfig() => new()
    {
        WalletAddress = "0x1234567890123456789012345678901234567890",
        IsMainnet = false,
        Grid = new GridConfig
        {
            Symbol = "BTC",
            AssetIndex = 0,
            GridLevels = 10,
            GridSpacingPercent = 1.0m,
            OrderSizeBtc = 0.001m,
            SyncIntervalSeconds = 1
        },
        Risk = new RiskConfig
        {
            MaxPositionSizeBtc = 0.5m,
            MaxDrawdownPercent = 20m,
            MinGridPrice = 10_000m,
            MaxGridPrice = 500_000m
        }
    };

    private static (GridBot bot, MockExchangeClient exchange) CreateSut()
    {
        var cfg = MakeConfig();
        var exchange = new MockExchangeClient { MidPrice = 50_000m, TotalEquity = 10_000m };
        var strategy = new GridStrategy(exchange, cfg, NullLogger<GridStrategy>.Instance);
        var risk = new RiskManager(cfg.Risk, NullLogger<RiskManager>.Instance);
        var bot = new GridBot(exchange, strategy, risk, cfg, NullLogger<GridBot>.Instance);
        return (bot, exchange);
    }

    [Fact]
    public async Task Bot_StartsAndInitialises_PlacesOrders()
    {
        var (bot, exchange) = CreateSut();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await bot.StartAsync(cts.Token);
            // Give the bot a moment to initialise
            await Task.Delay(500, CancellationToken.None);

            Assert.True(exchange.PlacedOrders.Count > 0,
                "Bot should have placed orders during initialisation");
        }
        finally
        {
            await bot.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bot_CancelsOrders_OnHaltCondition()
    {
        var (bot, exchange) = CreateSut();

        // Trigger halt: price below minimum
        exchange.MidPrice = 1_000m;
        exchange.TotalEquity = 10_000m;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await bot.StartAsync(cts.Token);
            await Task.Delay(1_500, CancellationToken.None);

            // The bot should have cancelled all orders when the halt triggered
            Assert.True(exchange.CancelAllCallCount >= 1,
                "Expected at least one CancelAll call on halt");
        }
        finally
        {
            await bot.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Bot_GracefulShutdown_DoesNotThrow()
    {
        var (bot, _) = CreateSut();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await bot.StartAsync(cts.Token);
        await Task.Delay(300, CancellationToken.None);

        Exception? ex = await Record.ExceptionAsync(() => bot.StopAsync(CancellationToken.None));
        Assert.Null(ex);
    }
}
