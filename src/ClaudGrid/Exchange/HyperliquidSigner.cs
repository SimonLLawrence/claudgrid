using System.Text;
using ClaudGrid.Config;
using Nethereum.Signer;
using Nethereum.Util;

namespace ClaudGrid.Exchange;

/// <summary>
/// Signs Hyperliquid actions using two different EIP-712 schemes:
///
/// 1. L1 actions (orders, cancels): "Exchange" domain, phantom Agent struct.
///    Flow: msgpack(action) + nonce_BE + vault_flag → keccak256 → connectionId
///          → EIP-712 Agent { source, connectionId }
///
/// 2. User-signed actions (usdClassTransfer, withdraw, etc.): "HyperliquidSignTransaction"
///    domain, human-readable typed structs signed directly.
///
/// Reference: https://github.com/hyperliquid-dex/hyperliquid-ts-sdk
/// </summary>
public sealed class HyperliquidSigner
{
    private readonly EthECKey _key;
    private readonly bool _isMainnet;

    // Chain IDs per Hyperliquid docs
    private const int L1ExchangeChainId = 1337;  // Used in the L1 "Exchange" phantom-agent domain (all networks)
    private const int MainnetChainId = 42161;    // Arbitrum One  — used for user-signed "HyperliquidSignTransaction" domain
    private const int TestnetChainId = 421614;   // Arbitrum Sepolia — same

    public HyperliquidSigner(BotConfig config)
    {
        _key = new EthECKey(config.PrivateKey);
        _isMainnet = config.IsMainnet;
    }

    /// <summary>Computes and returns the EIP-712 signature for an action.</summary>
    public (string r, string s, int v) SignAction(byte[] msgPackAction, long nonce, string? vaultAddress = null)
    {
        byte[] connectionId = ComputeActionHash(msgPackAction, nonce, vaultAddress);
        byte[] eip712Hash = ComputeEip712Hash(connectionId);

        var signature = _key.SignAndCalculateV(eip712Hash);

        string r = "0x" + BitConverter.ToString(signature.R).Replace("-", "").ToLower();
        string s = "0x" + BitConverter.ToString(signature.S).Replace("-", "").ToLower();
        int v = signature.V[0];

        return (r, s, v);
    }

    /// <summary>
    /// Signs a usdClassTransfer using the "HyperliquidSignTransaction" EIP-712 domain.
    /// This is the "user-signed action" pattern used by the TypeScript SDK.
    ///
    /// Signed message: HyperliquidTransaction:UsdClassTransfer {
    ///   hyperliquidChain: "Mainnet" | "Testnet",
    ///   destination: "USDC",
    ///   amount: string,
    ///   time: uint64
    /// }
    /// </summary>
    public (string r, string s, int v) SignUsdClassTransfer(string amount, long timestamp)
    {
        int chainId = _isMainnet ? MainnetChainId : TestnetChainId;
        string chain = _isMainnet ? "Mainnet" : "Testnet";

        // Domain: HyperliquidSignTransaction
        byte[] domainTypeHash = Keccak256Utf8(
            "EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");
        byte[] domainHash = Keccak256(AbiEncode(
            domainTypeHash,
            Keccak256Utf8("HyperliquidSignTransaction"),
            Keccak256Utf8("1"),
            PadUint256(chainId),
            PadAddress("0x0000000000000000000000000000000000000000")
        ));

        // Struct: HyperliquidTransaction:UsdClassTransfer(string hyperliquidChain,string destination,string amount,uint64 time)
        byte[] structTypeHash = Keccak256Utf8(
            "HyperliquidTransaction:UsdClassTransfer(string hyperliquidChain,string destination,string amount,uint64 time)");
        byte[] structHash = Keccak256(AbiEncode(
            structTypeHash,
            Keccak256Utf8(chain),
            Keccak256Utf8("USDC"),
            Keccak256Utf8(amount),
            PadUint256(timestamp)
        ));

        byte[] final = new byte[2 + 32 + 32];
        final[0] = 0x19;
        final[1] = 0x01;
        Buffer.BlockCopy(domainHash, 0, final, 2, 32);
        Buffer.BlockCopy(structHash, 0, final, 34, 32);

        var signature = _key.SignAndCalculateV(Sha3Keccack.Current.CalculateHash(final));

        string r = "0x" + BitConverter.ToString(signature.R).Replace("-", "").ToLower();
        string s = "0x" + BitConverter.ToString(signature.S).Replace("-", "").ToLower();
        int v = signature.V[0];
        return (r, s, v);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    private static byte[] ComputeActionHash(byte[] msgPackBytes, long nonce, string? vaultAddress)
    {
        // Layout: msgpack | nonce (8 bytes BE) | vault_flag (1 byte) [| vault_addr (20 bytes)]
        int extraLen = vaultAddress is null ? 9 : 29;
        byte[] data = new byte[msgPackBytes.Length + extraLen];

        Buffer.BlockCopy(msgPackBytes, 0, data, 0, msgPackBytes.Length);

        // Nonce as big-endian uint64
        byte[] nonceBytes = BitConverter.GetBytes(nonce);
        if (BitConverter.IsLittleEndian) Array.Reverse(nonceBytes);
        Buffer.BlockCopy(nonceBytes, 0, data, msgPackBytes.Length, 8);

        if (vaultAddress is null)
        {
            data[msgPackBytes.Length + 8] = 0;
        }
        else
        {
            data[msgPackBytes.Length + 8] = 1;
            string hex = vaultAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? vaultAddress[2..] : vaultAddress;
            byte[] addrBytes = Convert.FromHexString(hex);
            Buffer.BlockCopy(addrBytes, 0, data, msgPackBytes.Length + 9, 20);
        }

        return Sha3Keccack.Current.CalculateHash(data);
    }

    private byte[] ComputeEip712Hash(byte[] connectionId)
    {
        // The L1 "Exchange" domain always uses chainId 1337 regardless of mainnet/testnet.
        // The actual Arbitrum chain IDs (42161 / 421614) are only used for user-signed actions.
        string source = _isMainnet ? "a" : "b";

        // --- Domain separator ---
        // EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)
        byte[] domainTypeHash = Keccak256Utf8("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");

        byte[] domainHash = Keccak256(AbiEncode(
            domainTypeHash,
            Keccak256Utf8("Exchange"),
            Keccak256Utf8("1"),
            PadUint256(L1ExchangeChainId),
            PadAddress("0x0000000000000000000000000000000000000000")
        ));

        // --- Struct hash: Agent(string source, bytes32 connectionId) ---
        byte[] agentTypeHash = Keccak256Utf8("Agent(string source,bytes32 connectionId)");

        // connectionId must be exactly 32 bytes
        byte[] connId32 = new byte[32];
        int copyLen = Math.Min(connectionId.Length, 32);
        Buffer.BlockCopy(connectionId, 0, connId32, 32 - copyLen, copyLen);

        byte[] structHash = Keccak256(AbiEncode(
            agentTypeHash,
            Keccak256Utf8(source),
            connId32
        ));

        // --- Final: 0x1901 || domainSeparator || structHash ---
        byte[] final = new byte[2 + 32 + 32];
        final[0] = 0x19;
        final[1] = 0x01;
        Buffer.BlockCopy(domainHash, 0, final, 2, 32);
        Buffer.BlockCopy(structHash, 0, final, 34, 32);

        return Sha3Keccack.Current.CalculateHash(final);
    }

    // ── ABI encoding primitives ───────────────────────────────────────────────

    private static byte[] AbiEncode(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] PadUint256(long value)
    {
        byte[] result = new byte[32];
        byte[] valueBytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian) Array.Reverse(valueBytes);
        Buffer.BlockCopy(valueBytes, 0, result, 32 - valueBytes.Length, valueBytes.Length);
        return result;
    }

    private static byte[] PadAddress(string hexAddress)
    {
        byte[] result = new byte[32];
        string hex = hexAddress.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? hexAddress[2..] : hexAddress;
        byte[] addrBytes = Convert.FromHexString(hex.PadLeft(40, '0'));
        Buffer.BlockCopy(addrBytes, 0, result, 12, 20);
        return result;
    }

    private static byte[] Keccak256Utf8(string text) =>
        Sha3Keccack.Current.CalculateHash(Encoding.UTF8.GetBytes(text));

    private static byte[] Keccak256(byte[] data) =>
        Sha3Keccack.Current.CalculateHash(data);
}
