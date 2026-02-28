using System.Text;
using ClaudGrid.Config;
using Nethereum.Signer;
using Nethereum.Util;

namespace ClaudGrid.Exchange;

/// <summary>
/// Signs Hyperliquid L1 actions using the EIP-712 phantom-agent scheme.
///
/// Signing flow (from the Hyperliquid SDK):
///   1. MsgPack-encode the action dict.
///   2. Append nonce (big-endian uint64) + vault flag byte.
///   3. Keccak256 → connectionId (bytes32).
///   4. Build EIP-712 typed data: Agent { source, connectionId }.
///   5. Sign and return (r, s, v).
///
/// Reference: https://github.com/hyperliquid-dex/hyperliquid-ts-sdk
/// </summary>
public sealed class HyperliquidSigner
{
    private readonly EthECKey _key;
    private readonly bool _isMainnet;

    // Chain IDs per Hyperliquid docs
    private const int MainnetChainId = 42161;   // Arbitrum One
    private const int TestnetChainId = 421614;  // Arbitrum Sepolia

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
        int chainId = _isMainnet ? MainnetChainId : TestnetChainId;
        string source = _isMainnet ? "a" : "b";

        // --- Domain separator ---
        // EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)
        byte[] domainTypeHash = Keccak256Utf8("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)");

        byte[] domainHash = Keccak256(AbiEncode(
            domainTypeHash,
            Keccak256Utf8("Exchange"),
            Keccak256Utf8("1"),
            PadUint256(chainId),
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
