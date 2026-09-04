using System.Security.Cryptography;
using System.Text;

namespace Covenant.Host;

/// <summary>Hash-chain primitives shared by writer and verifier: line = <c>{previousHash}\t{entryHash}\t{contentJson}</c>, <c>entryHash = SHA256(previousHash + contentJson)</c>; altering, removing, or reordering any past line breaks every hash after it.
/// Truncation from the END is not detectable from the file alone — closed by opt-in chain-head anchoring (ADR-0007, Audit:AnchorPath).</summary>
public static class AuditChain
{
    public const char Separator = '\t';
    public static readonly string GenesisHash = new('0', 64);

    public static string Hash(string previousHash, string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + content))).ToLowerInvariant();
}
