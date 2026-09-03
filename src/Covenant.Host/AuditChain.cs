using System.Security.Cryptography;
using System.Text;

namespace Covenant.Host;

/// <summary>Hash-chain primitives shared by writer and verifier: line = <c>{previousHash}\t{entryHash}\t{contentJson}</c>, <c>entryHash = SHA256(previousHash + contentJson)</c>; altering, removing, or reordering any past line breaks every hash after it.
/// Known limit (deliberate, first slice): truncation from the END is NOT detectable from the file alone — needs external chain-head anchoring (WORM / notarization), the audit-store ADR.</summary>
public static class AuditChain
{
    public const char Separator = '\t';
    public static readonly string GenesisHash = new('0', 64);

    public static string Hash(string previousHash, string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + content))).ToLowerInvariant();
}
