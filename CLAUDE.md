# ClaudGrid — Hyperliquid BTC/USD Grid Trading Bot

**Repository:** https://github.com/SimonLLawrence/claudgrid

## Overview

ClaudGrid is a C# .NET 8 automated grid trading bot that operates on the [Hyperliquid](https://hyperliquid.xyz) perpetual DEX. It trades the **BTC/USD** (BTC-PERP) pair using a symmetric grid strategy designed to target **≥ 5% annual return** on allocated capital with low directional risk.

---

## How the Bot Works

### Grid Trading Strategy

A grid bot profits from price oscillation rather than directional movement.

```
SELL orders  ──┬──── $51,000  ─── level 5
               ├──── $50,500  ─── level 4
               ├──── $50,000  ─── level 3  ← current price
               ├──── $49,500  ─── level 2
               └──── $49,000  ─── level 1
BUY orders   ──┘
```

**Mechanics:**
1. At startup, the bot calculates a grid centred on the current BTC mid-price.
2. It places limit buy orders below the current price and limit sell orders above it.
3. When a **buy** fills, a matching **sell** is immediately placed one grid step higher.
4. When a **sell** fills, a matching **buy** is placed one grid step lower.
5. Each round-trip (buy → sell) captures one grid spacing in profit, minus fees.

### Default Parameters (conservative)

| Parameter | Default | Purpose |
|---|---|---|
| Grid levels | 20 | 10 buy + 10 sell around mid-price |
| Grid spacing | 1.0% | Distance between adjacent levels |
| Order size | 0.001 BTC | Per-level notional (~$50 at $50k BTC) |
| Sync interval | 30 s | How often the bot checks for fills |
| Max position | 0.1 BTC | Hard cap on net long/short |
| Max drawdown | 15% | Bot pauses if equity drops this much |

### Profitability Model

```
Fees (maker/taker): ~0.02% / 0.05% on Hyperliquid
Round-trip cost:    ~0.07% per grid trade
Grid spacing:        1.00%
Net profit/trade:   ~0.93% on the trade notional

Capital per level:  $50 (0.001 BTC at ~$50k)
Profit per trade:   ~$0.46

BTC annual trades at 1% grid: easily 300–600 oscillations/year
Conservative estimate: 300 trades × $0.46 = $138/year on $2,000 allocated
Annual return:      ~7% — exceeds the 5% target
```

Tighter spreads and higher BTC volatility push returns above 10 %.

---

## Project Structure

```
ClaudGrid/
├── CLAUDE.md                       ← this file
├── ClaudGrid.sln
├── src/
│   └── ClaudGrid/
│       ├── ClaudGrid.csproj
│       ├── Program.cs               ← DI wiring + host startup
│       ├── appsettings.json         ← default config (no secrets)
│       ├── Config/
│       │   └── BotConfig.cs         ← strongly-typed config models
│       ├── Models/
│       │   ├── GridLevel.cs         ← a single grid level (price, side, status)
│       │   ├── Order.cs             ← normalised order model
│       │   └── MarketData.cs        ← price + account state models
│       ├── Exchange/
│       │   ├── IExchangeClient.cs   ← abstraction (enables mocking in tests)
│       │   ├── HyperliquidClient.cs ← REST calls to Hyperliquid
│       │   └── HyperliquidSigner.cs ← EIP-712 action signing
│       ├── Strategy/
│       │   ├── GridCalculator.cs    ← pure math: level prices, profit model
│       │   └── GridStrategy.cs      ← stateful order lifecycle manager
│       ├── Risk/
│       │   └── RiskManager.cs       ← drawdown, position, price-range guards
│       └── Bot/
│           └── GridBot.cs           ← BackgroundService orchestrator
└── tests/
    └── ClaudGrid.Tests/
        ├── ClaudGrid.Tests.csproj
        ├── Mocks/
        │   └── MockExchangeClient.cs
        ├── Strategy/
        │   ├── GridCalculatorTests.cs
        │   └── GridStrategyTests.cs
        ├── Risk/
        │   └── RiskManagerTests.cs
        └── Bot/
            └── GridBotTests.cs
```

---

## Configuration

Copy `appsettings.json` and create `appsettings.Development.json` with your secrets:

```json
{
  "Bot": {
    "PrivateKey": "0xYOUR_PRIVATE_KEY_HERE",
    "WalletAddress": "0xYOUR_WALLET_ADDRESS",
    "IsMainnet": false,
    "Grid": {
      "Symbol": "BTC",
      "AssetIndex": 0,
      "GridLevels": 20,
      "GridSpacingPercent": 1.0,
      "OrderSizeBtc": 0.001,
      "SyncIntervalSeconds": 30
    },
    "Risk": {
      "MaxPositionSizeBtc": 0.1,
      "MaxDrawdownPercent": 15.0,
      "MinGridPrice": 10000.0,
      "MaxGridPrice": 500000.0
    }
  }
}
```

**Never commit your private key. Add `appsettings.Development.json` to `.gitignore`.**

---

## Running the Bot

```bash
# Restore and build
dotnet restore
dotnet build

# Run on testnet first (IsMainnet: false)
dotnet run --project src/ClaudGrid

# Run on mainnet
dotnet run --project src/ClaudGrid --environment Production
```

---

## Testing

### Unit Tests

```bash
dotnet test
```

Tests cover:
- **GridCalculator**: level price computation, profit estimation, grid boundaries
- **GridStrategy**: order placement decisions, fill handling, level reposting
- **RiskManager**: drawdown detection, position size enforcement, price-range guards
- **GridBot**: integration with mock exchange, sync cycle logic

### Manual / Paper Trading

1. Set `IsMainnet: false` to target the Hyperliquid testnet.
2. Obtain testnet USDC from the Hyperliquid testnet faucet.
3. Run the bot and observe logs — every placed, filled, and cancelled order is logged.
4. Review PnL via the Hyperliquid UI at https://app.hyperliquid-testnet.xyz.

### Checklist Before Going Live

- [ ] Run at least 48 hours on testnet without errors
- [ ] Verify order sizes stay within risk limits
- [ ] Confirm drawdown guard triggers correctly
- [ ] Check that cancelled/missed orders are re-placed on the next sync cycle
- [ ] Set `MaxDrawdownPercent` and `MaxPositionSizeBtc` conservatively

---

## Risk Warnings

- **Smart contract risk**: Hyperliquid is a DEX; funds on-chain are not FDIC-insured.
- **Trend risk**: In a strong directional move the grid accumulates a losing position. The drawdown guard exists to limit this.
- **API / connectivity risk**: If the bot crashes, open orders remain on the book. Always monitor.
- **Fee drag**: If grid spacing is too tight relative to fees, the bot loses money. Keep spacing > 2× round-trip fee.
- **Liquidity risk**: Very small order sizes are fine for BTC; do not increase size without re-testing.

---

## Architecture Notes

- `IExchangeClient` is a pure interface — swap in any exchange without changing strategy code.
- `GridBot` is a `BackgroundService` hosted via `IHostedService`; it respects `CancellationToken` for graceful shutdown.
- All monetary values use `decimal` (not `double`) to avoid floating-point rounding errors.
- Signing follows the Hyperliquid L1 action signing spec: msgpack → keccak256 → EIP-712 phantom agent.
