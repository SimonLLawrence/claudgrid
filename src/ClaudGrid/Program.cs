using ClaudGrid.Bot;
using ClaudGrid.Config;
using ClaudGrid.Exchange;
using ClaudGrid.Risk;
using ClaudGrid.Strategy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ── Host setup ────────────────────────────────────────────────────────────────

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.SetBasePath(AppContext.BaseDirectory)
           .AddJsonFile("appsettings.json", optional: false)
           .AddJsonFile($"appsettings.{ctx.HostingEnvironment.EnvironmentName}.json", optional: true)
           .AddEnvironmentVariables("CLAUDGRID_");
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddSimpleConsole(opts =>
        {
            opts.TimestampFormat = "HH:mm:ss ";
            opts.SingleLine = true;
        });
    })
    .ConfigureServices((ctx, services) =>
    {
        // Config
        BotConfig botConfig = ctx.Configuration.GetSection(BotConfig.Section).Get<BotConfig>()
                              ?? throw new InvalidOperationException("Bot config section missing");

        ValidateConfig(botConfig);

        services.AddSingleton(botConfig);
        services.AddSingleton(botConfig.Grid);
        services.AddSingleton(botConfig.Risk);

        // Exchange layer
        services.AddHttpClient<IExchangeClient, HyperliquidClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "ClaudGrid/1.0");
        });
        services.AddSingleton<HyperliquidSigner>();

        // Strategy and risk
        services.AddSingleton<GridStrategy>();
        services.AddSingleton<RiskManager>();

        // Bot
        services.AddHostedService<GridBot>();
    })
    .Build();

await host.RunAsync();

// ── Config validation ─────────────────────────────────────────────────────────

static void ValidateConfig(BotConfig cfg)
{
    if (string.IsNullOrWhiteSpace(cfg.PrivateKey))
        throw new InvalidOperationException(
            "Bot:PrivateKey is not set. Add it to appsettings.Development.json or via environment variable CLAUDGRID_BOT__PRIVATEKEY.");

    if (string.IsNullOrWhiteSpace(cfg.WalletAddress))
        throw new InvalidOperationException(
            "Bot:WalletAddress is not set.");

    if (cfg.Grid.GridLevels < 4)
        throw new InvalidOperationException("GridLevels must be at least 4.");

    if (cfg.Grid.GridSpacingPercent <= 0)
        throw new InvalidOperationException("GridSpacingPercent must be > 0.");

    if (cfg.Grid.OrderSizeBtc <= 0)
        throw new InvalidOperationException("OrderSizeBtc must be > 0.");
}
