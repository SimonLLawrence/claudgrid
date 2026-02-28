using ClaudGrid.Models;

namespace ClaudGrid.Exchange;

/// <summary>
/// Exchange abstraction. Swap implementations without touching strategy code.
/// All methods throw on unrecoverable errors; callers should catch and log.
/// </summary>
public interface IExchangeClient
{
    /// <summary>Returns the current mid, bid, and ask for the given symbol.</summary>
    Task<MarketData> GetMarketDataAsync(string symbol, CancellationToken ct = default);

    /// <summary>Returns margin balances and open positions for the configured wallet.</summary>
    Task<AccountState> GetAccountStateAsync(CancellationToken ct = default);

    /// <summary>Returns all open (resting) orders for the configured wallet.</summary>
    Task<List<Order>> GetOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>
    /// Places a GTC limit order. Returns the exchange-assigned order ID.
    /// </summary>
    Task<long> PlaceLimitOrderAsync(
        string symbol,
        int assetIndex,
        OrderSide side,
        decimal price,
        decimal size,
        CancellationToken ct = default);

    /// <summary>Cancels a single order. Returns true if successfully cancelled.</summary>
    Task<bool> CancelOrderAsync(int assetIndex, long orderId, CancellationToken ct = default);

    /// <summary>Cancels all open orders for the given asset. Returns the number cancelled.</summary>
    Task<int> CancelAllOrdersAsync(int assetIndex, CancellationToken ct = default);

    /// <summary>
    /// Returns the 0-based asset index for the given symbol by querying the /info meta endpoint.
    /// </summary>
    Task<int> GetAssetIndexAsync(string symbol, CancellationToken ct = default);

    /// <summary>Returns the USDC spot balance for the configured wallet.</summary>
    Task<decimal> GetSpotUsdcBalanceAsync(CancellationToken ct = default);

    /// <summary>Transfers USDC from the spot wallet into the perps (cross-margin) account.</summary>
    Task TransferSpotToPerpsAsync(decimal amount, CancellationToken ct = default);
}
