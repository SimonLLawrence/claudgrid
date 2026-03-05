# ClaudGrid

A C# .NET 8 automated grid trading bot for the [Hyperliquid](https://hyperliquid.xyz) perpetual DEX, trading BTC/USD (BTC-PERP).

## Overview

ClaudGrid profits from BTC price oscillation by placing a symmetric ladder of limit orders above and below the current price. Each time a buy fills, a sell is placed one step higher; each time a sell fills, a buy is placed one step lower. Every completed round-trip captures the grid spacing as profit.

Target: **≥ 5% annual return** on allocated capital with low directional risk.

## Features

- **Symmetric grid strategy** — configurable number of levels and spacing
- **Automatic grid recentring** — repositions the grid when price drifts outside 80% of range
- **Partial fill tracking** — handles partial fills gracefully, placing counter orders for the filled portion
- **Position mismatch self-healing** — detects and corrects divergence between the bot's internal tracker and exchange reality
- **Stuck order retry** — re-places counter orders on Filled levels that are missing them
- **Risk guards** — max drawdown halt, max position size cap, price range enforcement
- **Auto asset index discovery** — queries exchange metadata to verify the asset index at startup
- **Web UI** — live grid status, PnL, fills, and mismatch log at `http://localhost:5000`
- **Graceful shutdown** — `POST /api/shutdown` cleanly stops the bot leaving orders on the book
- **Testnet support** — run against Hyperliquid testnet before going live

## Architecture

```
src/ClaudGrid/
├── Program.cs                    DI host wiring and config validation
├── Config/BotConfig.cs           Strongly-typed configuration models
├── Models/                       GridLevel, Order, MarketData, AccountState
├── Exchange/
│   ├── IExchangeClient.cs        Exchange abstraction (enables unit testing)
│   ├── HyperliquidClient.cs      Hyperliquid REST API client
│   └── HyperliquidSigner.cs      EIP-712 action signing
├── Strategy/
│   ├── GridCalculator.cs         Pure math: level prices, profit model
│   └── GridStrategy.cs           Stateful order lifecycle manager
├── Risk/RiskManager.cs           Drawdown, position, and price-range guards
├── Bot/GridBot.cs                BackgroundService orchestrator
└── Web/BotStatusService.cs       Web UI status snapshot service

tests/ClaudGrid.Tests/
├── Mocks/MockExchangeClient.cs   Controllable in-memory exchange
├── Strategy/                     GridCalculator and GridStrategy tests
├── Risk/                         RiskManager tests
└── Bot/                          GridBot integration tests
```

## Grid Strategy

```
SELL orders  ──┬──── $51,000  ─── level 6
               ├──── $50,500  ─── level 5
               ├──── $50,000  ─── level 4  ← mid-price at startup
               ├──── $49,500  ─── level 3
               ├──── $49,000  ─── level 2
               └──── $48,500  ─── level 1
BUY orders   ──┘
```

Level spacing is **geometric**: `price[i] = mid × (1 + spacing%)^(i − midIndex)`

### Fill Handling

1. At sync, open orders are fetched from the exchange.
2. Any level whose order has disappeared is treated as filled.
3. A counter order is placed immediately: buy fill → sell one step up; sell fill → buy one step down.
4. Partial fills are detected when `filledSize` increases without the order disappearing; a counter is placed for the filled portion.
5. After each fill the net position tracker is updated; `VerifyPositions` cross-checks against the exchange and auto-corrects on divergence.

## Default Parameters

| Parameter | Default | Notes |
|---|---|---|
| Grid levels | 20 | 10 buy + 10 sell around mid |
| Grid spacing | 0.25% | Distance between adjacent levels |
| Order size | 0.001 BTC | ~$70 notional at $70k BTC |
| Sync interval | 10 s | Fill-check frequency |
| Max position | 0.1 BTC | Hard cap on net long/short |
| Max drawdown | 15% | Bot halts if equity drops this much |

## Profitability Model

```
Hyperliquid fees (maker/taker): ~0.02% / 0.05%
Round-trip cost:                ~0.07% per trade
Grid spacing (0.25%):            0.25%
Net profit per trade:           ~0.18%

Capital per level:  ~$70 (0.001 BTC at $70k)
Profit per trade:   ~$0.126

Typical daily oscillations at 0.25% spacing: 50–200+
Conservative (50/day): 50 × $0.126 × 365 = ~$2,300/year on ~$1,400 allocated
Annual return: ~165%
```

Returns scale with BTC volatility. Tighter spacing captures more trades but increases fee drag if spacing approaches the round-trip fee.

## Configuration

Copy `appsettings.json` and create `appsettings.Development.json`:

```json
{
  "Bot": {
    "PrivateKey": "0xYOUR_PRIVATE_KEY",
    "WalletAddress": "0xYOUR_WALLET_ADDRESS",
    "IsMainnet": false,
    "Grid": {
      "Symbol": "BTC",
      "AssetIndex": 0,
      "GridLevels": 20,
      "GridSpacingPercent": 0.25,
      "OrderSizeBtc": 0.001,
      "SyncIntervalSeconds": 10
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

## Running

```bash
# Build
dotnet restore
dotnet build

# Run on testnet (IsMainnet: false)
dotnet run --project src/ClaudGrid

# Run on mainnet
dotnet run --project src/ClaudGrid --environment Production

# Graceful shutdown (leaves orders on the book)
curl -X POST http://localhost:5000/api/shutdown
```

## Web UI

Navigate to `http://localhost:5000` while the bot is running.

Displays:
- Live BTC mid-price and account equity
- Per-level grid table: side, price, status, exchange order presence, pending counter count, PnL
- Realized PnL and total fill count
- Recent position mismatch log

## Tests

```bash
dotnet test
```

90 unit and integration tests covering the grid calculator, strategy lifecycle, risk manager, and bot sync logic using an in-memory mock exchange.

## API Endpoints

| Method | Path | Description |
|---|---|---|
| GET | `/api/status` | Full bot status snapshot (JSON) |
| GET | `/api/config` | Active grid and risk configuration |
| POST | `/api/shutdown` | Graceful bot shutdown |

## Risk Warnings

- **Trend risk** — a sustained directional move accumulates a losing position. The drawdown guard limits damage but does not eliminate it.
- **Smart contract risk** — Hyperliquid is a DEX; funds are not insured.
- **API / connectivity risk** — if the bot crashes, open orders remain on the book. Monitor continuously.
- **Fee drag** — grid spacing must exceed 2× the round-trip fee (~0.14%). Default 0.25% provides a thin but positive margin.
- **Liquidity risk** — 0.001 BTC orders are fine for BTC; do not increase size without re-testing.

## Pre-Live Checklist

- [ ] Run 48+ hours on testnet without errors
- [ ] Verify order sizes stay within risk limits
- [ ] Confirm drawdown guard triggers correctly
- [ ] Check that cancelled/missed orders are re-placed on the next sync
- [ ] Set `MaxDrawdownPercent` and `MaxPositionSizeBtc` conservatively
- [ ] Monitor the mismatch log for the first few hours live

## Technical Notes

- All monetary values use `decimal`, never `double`, to avoid floating-point rounding errors.
- `IExchangeClient` is a pure interface — the exchange backend can be swapped without touching strategy or risk code.
- Hyperliquid signing follows the L1 action spec: msgpack → keccak256 → EIP-712 phantom agent (Nethereum.Signer, no external EIP-712 library needed).
- `GridBot` is a `BackgroundService` hosted via `IHostedService` with clean `CancellationToken` propagation.
