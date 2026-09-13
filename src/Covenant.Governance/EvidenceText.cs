namespace Covenant.Governance;

/// <summary>Denial reasons are evidence (append-only, forever). Client-supplied identifiers may appear
/// in them only bounded: short, printable, and truncated with a marker — never an unbounded payload.</summary>
public static class Evidence
{
    public const int MaxIdentifierChars = 64;

    public static string Bounded(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= MaxIdentifierChars ? clean : clean[..MaxIdentifierChars] + "…(truncated)";
    }
}
