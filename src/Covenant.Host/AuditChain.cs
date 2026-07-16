using System.Security.Cryptography;
using System.Text;

namespace Covenant.Host;

/// <summary>
/// The hash-chain primitives shared by the writer (FileAuditSink) and the verifier (AuditChainVerifier).
/// One line = <c>{previousHash}\t{entryHash}\t{contentJson}</c>, where
/// <c>entryHash = SHA256(previousHash + contentJson)</c> and the first line's previousHash is the genesis
/// value. Altering, removing, or reordering any past line breaks every hash after it.
///
/// Known limit (deliberate, first slice): truncating the file from the END is not detectable from the
/// file alone — that requires anchoring the chain head externally (WORM store / periodic notarization),
/// which is the deploy-grade audit-store decision (deploy/CLAUDE.md) and needs its own ADR.
/// </summary>
public static class AuditChain
{
    public const char Separator = '\t';
    public static readonly string GenesisHash = new('0', 64);

    public static string Hash(string previousHash, string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + content))).ToLowerInvariant();
}
