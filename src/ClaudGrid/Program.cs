using System.Text.Json;
using System.Text.Json.Serialization;
using ClaudGrid.Bot;
using ClaudGrid.Config;
using ClaudGrid.Exchange;
using ClaudGrid.Risk;
using ClaudGrid.Strategy;
using ClaudGrid.Web;

// ── Host setup ────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddEnvironmentVariables("CLAUDGRID_");

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(opts =>
{
    opts.TimestampFormat = "HH:mm:ss ";
    opts.SingleLine = true;
});

// Config
BotConfig botConfig = builder.Configuration.GetSection(BotConfig.Section).Get<BotConfig>()
                      ?? throw new InvalidOperationException("Bot config section missing");

ValidateConfig(botConfig);

builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton(botConfig.Grid);
builder.Services.AddSingleton(botConfig.Risk);

// Status service
builder.Services.AddSingleton<BotStatusService>();

// Exchange layer
builder.Services.AddHttpClient<IExchangeClient, HyperliquidClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("User-Agent", "ClaudGrid/1.0");
});
builder.Services.AddSingleton<HyperliquidSigner>();

// Strategy and risk
builder.Services.AddSingleton<GridStrategy>();
builder.Services.AddSingleton<RiskManager>();

// Bot
builder.Services.AddHostedService<GridBot>();

var app = builder.Build();

app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Converters = { new JsonStringEnumConverter() }
};

app.MapGet("/api/status", (BotStatusService status) =>
    Results.Json(status.GetSnapshot(), jsonOptions));

app.MapGet("/", () => Results.Redirect("/index.html"));

await app.RunAsync();

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
