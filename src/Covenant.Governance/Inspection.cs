using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>Fixed evidence labels of the local inspector (ADR-0018). A label names WHAT kind of thing matched — never the
/// value. The audit Signal is the strongest label found.</summary>
public static class InspectionSignals
{
    public const string SsnFull = "US SSN pattern";
    public const string SsnContextual = "US SSN (masked, partial or unseparated, with context)";
    public const string SsnMasked = "US SSN (masked)";
    public const string SsnField = "labelled identifier field (SSN / taxpayer id)";
    public const string NineDigit = "possible identifier (standalone 9-digit number)";
    public const string EmailField = "labelled identifier field (e-mail)";
    public const string PhoneField = "labelled identifier field (phone)";
    public const string DobField = "labelled identifier field (date of birth)";
    public const string DocumentField = "labelled identifier field (account / document number)";
    public const string PaymentCard = "payment card number (Luhn-valid)";
    public const string MedicalIdentifier = "medical record / patient identifier";
    public const string HealthNarrative = "health information about an identifiable person";
    public const string Incomplete = "inspection incomplete";

    /// <summary>What the inspector covers — and does not — for documentation and the console.</summary>
    public const string Coverage =
        "Local pattern inspection (ADR-0018) over every message, after decoding JSON string escapes and normalizing Unicode: US SSNs "
        + "(separated, obfuscated, masked, partial with context), standalone 9-digit numbers as possible identifiers, labelled identifier "
        + "fields in prose or structured keys (SSN, e-mail, phone, date of birth, name, account/document numbers), Luhn-valid payment "
        + "cards, medical record/patient identifiers, and health information about an identifiable person — also when identity and "
        + "health detail sit in different messages. It does not detect unlabelled free-text names or addresses, paraphrased or encoded "
        + "(e.g. base64) facts, or non-US national identifiers outside labelled fields. It is a routing safeguard, not comprehensive "
        + "DLP; no match is not proof that content is public-safe. If inspection cannot complete, the request is refused.";
}

/// <summary>Work bounds of one inspection (ADR-0018 §3). Exceeding any of them makes the result <see cref="ClassificationResult.Incomplete"/>.</summary>
public sealed record InspectionLimits
{
    public int MaxChars { get; init; } = 512 * 1024;
    /// <summary>Matches considered per pattern per message; more ⇒ incomplete (bounds the nearby-context scan).</summary>
    public int MaxMatchesPerPattern { get; init; } = 512;
    /// <summary>Aggregate wall-clock budget for the whole request (checked before every pattern; each pattern also has its own timeout).</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Passes of JSON string-escape decoding (nested/double-encoded strings).</summary>
    public int MaxEscapePasses { get; init; } = 3;

    public static readonly InspectionLimits Default = new();
}

/// <summary>ADR-0018 local, bounded, deterministic sensitive-data inspection — in-process only (no text leaves the perimeter
/// for inspection). Every message is escape-decoded (bounded), normalized and inspected; identity and health detail are
/// combined across the messages of one request (each message is matched on its own — no text is joined across a boundary).
/// Results are fixed labels, never values. Inspection that cannot complete (size, aggregate time budget, match cap, pattern
/// timeout, normalization failure) returns <see cref="ClassificationResult.Incomplete"/>: the request is REFUSED by the
/// classify stage — no content reaches any provider.</summary>
public sealed partial class LocalSensitiveDataInspector(InspectionLimits? limits = null) : IDataClassifier
{
    public const int MaxInspectedChars = 512 * 1024;
    private const int ContextWindow = 40;
    private const int TimeoutMs = 200;

    private readonly InspectionLimits _limits = limits ?? InspectionLimits.Default;

    private sealed class IncompleteException : Exception;

    /// <summary>Aggregate work accounting for one request.</summary>
    private sealed class Work(InspectionLimits limits)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        public InspectionLimits Limits { get; } = limits;

        public void Check()
        {
            if (_watch.Elapsed >= Limits.Budget) throw new IncompleteException();
        }

        public bool Any(Regex r, string text)
        {
            Check();
            bool hit = r.IsMatch(text);
            Check();   // the call itself may have used up the budget: its answer is then not trusted
            return hit;
        }

        /// <summary>Non-overlapping matches in order, capped.</summary>
        public List<(int Start, int End, string Value)> Find(Regex r, string text)
        {
            Check();
            var list = new List<(int, int, string)>();
            for (var m = r.Match(text); m.Success; m = m.NextMatch())
            {
                Check();   // every NextMatch is real work (up to one pattern timeout): the aggregate budget bounds the loop
                if (list.Count >= Limits.MaxMatchesPerPattern) throw new IncompleteException();
                list.Add((m.Index, m.Index + m.Length, m.Value));
            }
            Check();
            return list;
        }
    }

    private sealed record MessageFacts(string? Pii, bool MedicalId, bool Identifiable, bool Medical);

    public ClassificationResult Classify(InferenceRequest request)
    {
        long total = 0;
        foreach (var m in request.Messages) total += m.Content?.Length ?? 0;
        if (total > _limits.MaxChars) return Incomplete();

        try
        {
            var work = new Work(_limits);
            string? pii = null;
            bool medicalId = false, identifiable = false, medical = false;
            foreach (var m in request.Messages)   // every role: system, user, assistant, tool
            {
                var f = InspectMessage(m.Content ?? "", work);
                pii ??= f.Pii;
                medicalId |= f.MedicalId;
                identifiable |= f.Identifiable;
                medical |= f.Medical;
            }
            work.Check();   // a result is only reported if the WHOLE inspection finished within the budget
            // Conversation-level relationship: identity in one message + health detail in another is still PHI.
            if (medicalId) return new(DataClassification.Phi, InspectionSignals.MedicalIdentifier);
            if (identifiable && medical) return new(DataClassification.Phi, InspectionSignals.HealthNarrative);
            if (pii is not null) return new(DataClassification.Pii, pii);
            return new(DataClassification.Internal, null);   // "no escalation" — never proof of public safety
        }
        catch (IncompleteException) { return Incomplete(); }
        catch (RegexMatchTimeoutException) { return Incomplete(); }
        catch (ArgumentException) { return Incomplete(); }      // normalization/decoding of malformed text
        catch (InvalidOperationException) { return Incomplete(); }
    }

    private static ClassificationResult Incomplete() => new(DataClassification.Phi, InspectionSignals.Incomplete, Incomplete: true);

    private static MessageFacts InspectMessage(string raw, Work work)
    {
        if (raw.Length == 0) return new(null, false, false, false);
        var text = Normalize(DecodeEscapes(raw, work.Limits.MaxEscapePasses));
        work.Check();
        // Structured keys read as words: us_ssn → "us ssn", social_security_number, patient_id, date_of_birth.
        var lower = text.ToLowerInvariant().Replace('_', ' ');

        var pii = PiiLabel(lower, work);
        bool medicalId = work.Any(MedicalIdField(), lower);
        bool identifiable = pii is not null
                            || work.Any(TitledName(), text) || work.Any(SelfName(), text) || work.Any(NameField(), lower)
                            || work.Any(PatientNarrativeName(), lower);
        bool medical = work.Any(MedicalTerm(), lower) || work.Any(MedicalField(), lower) || work.Any(SelfDisclosure(), lower)
                       || work.Any(Dose(), lower) || work.Any(Icd10(), lower);
        return new(pii, medicalId, identifiable, medical);
    }

    private static string? PiiLabel(string t, Work work)
    {
        if (work.Any(SsnFull(), t)) return InspectionSignals.SsnFull;
        if (work.Any(SsnMasked(), t)) return InspectionSignals.SsnMasked;
        if (work.Any(SsnField(), t)) return InspectionSignals.SsnField;
        if (work.Any(SsnPartialLabelled(), t)) return InspectionSignals.SsnContextual;   // "last four of SSN: 3456"
        var contexts = work.Find(SsnContext(), t);
        if (contexts.Count > 0 && Near(work.Find(SsnPartial(), t), contexts)) return InspectionSignals.SsnContextual;
        var nine = work.Find(NineDigits(), t);
        if (nine.Count > 0) return contexts.Count > 0 && Near(nine, contexts) ? InspectionSignals.SsnContextual : InspectionSignals.NineDigit;
        if (PaymentCardLabel(t, work) is { } card) return card;
        if (work.Any(EmailField(), t)) return InspectionSignals.EmailField;
        if (work.Any(PhoneField(), t)) return InspectionSignals.PhoneField;
        if (work.Any(DobField(), t)) return InspectionSignals.DobField;
        if (work.Any(DocumentField(), t)) return InspectionSignals.DocumentField;
        return null;
    }

    private static string? PaymentCardLabel(string t, Work work)
    {
        foreach (var m in work.Find(CardGrouped(), t))
            if (Luhn(m.Value)) return InspectionSignals.PaymentCard;
        var context = work.Find(CardContext(), t);
        if (context.Count == 0) return null;
        var luhn = work.Find(CardDigits(), t).Where(m => Luhn(m.Value)).ToList();
        return Near(luhn, context) ? InspectionSignals.PaymentCard : null;
    }

    /// <summary>Any candidate within <see cref="ContextWindow"/> characters of a context match. Both lists are non-overlapping
    /// regex matches in text order (so starts and ends are sorted): one binary search per candidate — O(n log m), no
    /// nested scan.</summary>
    private static bool Near(List<(int Start, int End, string Value)> candidates, List<(int Start, int End, string Value)> contexts)
    {
        if (candidates.Count == 0 || contexts.Count == 0) return false;
        foreach (var c in candidates)
        {
            // First context whose END reaches the window left of the candidate.
            int lo = 0, hi = contexts.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (contexts[mid].End < c.Start - ContextWindow) lo = mid + 1; else hi = mid;
            }
            if (lo < contexts.Count && contexts[lo].Start <= c.End + ContextWindow) return true;
        }
        return false;
    }

    public static bool Luhn(string value)
    {
        int sum = 0, count = 0;
        bool dbl = false;
        for (int i = value.Length - 1; i >= 0; i--)
        {
            char ch = value[i];
            if (ch is < '0' or > '9') continue;
            int d = ch - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
            count++;
        }
        return count is >= 13 and <= 19 && sum % 10 == 0;
    }

    /// <summary>Decodes JSON string escapes (\uXXXX, \", \\, \/, \n, \r, \t, \b, \f) in up to <paramref name="maxPasses"/>
    /// linear passes, so double-encoded (nested) JSON strings are seen as their characters. Malformed escapes are kept as-is.</summary>
    public static string DecodeEscapes(string s, int maxPasses = 3)
    {
        for (int pass = 0; pass < maxPasses && s.Contains('\\', StringComparison.Ordinal); pass++)
        {
            var sb = new StringBuilder(s.Length);
            bool changed = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch != '\\' || i + 1 >= s.Length) { sb.Append(ch); continue; }
                char n = s[i + 1];
                switch (n)
                {
                    case 'u' when i + 5 < s.Length && int.TryParse(s.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        sb.Append((char)code); i += 5; changed = true; break;
                    case '"' or '\\' or '/': sb.Append(n); i++; changed = true; break;
                    case 'n' or 'r' or 't' or 'b' or 'f': sb.Append(' '); i++; changed = true; break;
                    default: sb.Append(ch); break;
                }
            }
            if (!changed) break;
            s = sb.ToString();
        }
        return s;
    }

    /// <summary>NFKC; every Unicode decimal digit → ASCII; zero-width/soft-hyphen removed; dash, dot and space variants
    /// unified. Used for matching only — the request text is never modified.</summary>
    public static string Normalize(string s)
    {
        // Malformed Unicode (e.g. lone surrogates) throws ArgumentException here; Classify turns it into Incomplete — the request is
        // refused, never inspected "as-is" and sent on.
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(n.Length);
        foreach (var ch in n)
        {
            switch (ch)
            {
                case '­' or '᠎' or (>= '​' and <= '‏') or (>= '⁠' and <= '⁤') or '﻿':
                    continue;
                case (>= '‐' and <= '―') or '−' or '﹘' or '﹣' or '－':
                    sb.Append('-');
                    continue;
                case '․' or '．' or '。' or '·':
                    sb.Append('.');
                    continue;
            }
            if (ch > '\u007F' && char.IsDigit(ch) && char.GetNumericValue(ch) is var v && v is >= 0 and <= 9)
            {
                sb.Append((char)('0' + (int)v));
                continue;
            }
            sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        }
        return sb.ToString();
    }

    // ---- patterns (lower-case, normalized text with '_' read as ' '; bounded timeouts) ----

    /// <summary>Label → value separator: prose (": ", "=", " is "), structured ("\"key\": \""), or a plain space.</summary>
    private const string Sep = @"(?:[""']?\s*[:=#]\s*[""']?|\s+(?:is|was)\s+|\s+)";
    /// <summary>Strict separator for name/medical FIELDS (a label, not prose): ":" or "=" only.</summary>
    private const string FieldSep = @"[""']?\s*[:=]\s*[""']?";

    [GeneratedRegex(@"(?<![\d-])\d{3}[-. ]\d{2}[-. ]\d{4}(?![\d-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnFull();

    [GeneratedRegex(@"(?<![\w*#•])[x*#•]{3}[-. ]?[x*#•]{2}[-. ]?\d{4}(?!\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnMasked();

    [GeneratedRegex(@"\b(?:ssn|social security(?: number| no\.?| #)?|ss#|ss no\.?|taxpayer id(?:entification)?(?: number)?|tax id|tin)" + Sep + @"\d[\d .-]{7,10}\d(?!\d)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnField();

    [GeneratedRegex(@"\b(?:ssn|social security|soc\.? sec\.?|ss no|taxpayer id|tax id)\b|(?<!\w)ss#", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnContext();

    /// <summary>The partial form that names the SSN itself: "last four of SSN: 3456", "last 4 digits of my social security number is 3456".</summary>
    [GeneratedRegex(@"\blast (?:4|four)(?: digits)? of (?:the |my |his |her |their |your )?(?:ssn|social security(?: number)?|ss#)\W{0,3}(?:is |are )?\d{4}(?!\d)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnPartialLabelled();

    /// <summary>Lower-case patient narratives: "patient jane roe has diabetes", "patient jane roe, 54, …".</summary>
    [GeneratedRegex(@"\bpatient (?!id\b|identifier\b|number\b|portal\b|care\b|safety\b|record\b|records\b)[a-z]{2,} [a-z]{2,}(?: (?:has|had|is|was|with|who)\b|(?=[,;:]))",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PatientNarrativeName();

    /// <summary>A standalone 9-digit token (not inside a longer number, decimal, grouped figure, hex id or word): a POSSIBLE identifier.</summary>
    [GeneratedRegex(@"(?<![\w.,/-])\d{9}(?![\w.,/-])", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex NineDigits();

    [GeneratedRegex(@"\b(?:ending(?: in| with)?|ends (?:in|with)|last (?:4|four)(?: digits)?(?: of| are| is)?)[: ]+(?:is |are )?\d{4}(?!\d)",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SsnPartial();

    [GeneratedRegex(@"(?<!\d)\d{4}[ -]\d{4}[ -]\d{4}[ -]\d{1,7}(?!\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CardGrouped();

    [GeneratedRegex(@"(?<!\d)\d{13,19}(?!\d)", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CardDigits();

    [GeneratedRegex(@"\b(?:card|visa|mastercard|amex|american express|credit|debit|cc)\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex CardContext();

    [GeneratedRegex(@"\be-?mail(?: address)?" + Sep + @"[a-z0-9._%+ -]*[a-z0-9]@[a-z0-9.-]+\.[a-z]{2,}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex EmailField();

    [GeneratedRegex(@"\b(?:phone|mobile|cell|tel|telephone)(?: number| no\.?| #)?" + Sep + @"\+?[\d(][\d ().-]{5,}\d", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex PhoneField();

    [GeneratedRegex(@"\b(?:dob|d\.o\.b\.?|date of birth|birth ?date|born on)" + Sep + @"(?:\d{1,4}[-/.]\d{1,2}[-/.]\d{1,4}|[a-z]{3,9} \d{1,2},? \d{4}|\d{1,2} [a-z]{3,9},? \d{4})",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex DobField();

    [GeneratedRegex(@"\b(?:passport(?: number| no\.?| #)?|driver'?s? licen[cs]e(?: number| no\.?| #)?|bank account(?: number| no\.?| #)?|account (?:number|no\.?|#)|iban|card number|credit card(?: number)?)"
        + Sep + @"(?=[a-z0-9 -]{0,32}\d)[a-z0-9][a-z0-9 -]{4,32}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex DocumentField();

    [GeneratedRegex(@"\b(?:mrn|medical record(?: number| no\.?| #)?|patient(?: id| identifier| number| no\.?| #)|health plan(?: id| number)?|member id)"
        + Sep + @"(?=[a-z0-9-]{0,20}\d)[a-z0-9-]{2,20}", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex MedicalIdField();

    /// <summary>Case-SENSITIVE on the normalized text: "patient John Smith", "Patient Jane", "Mrs. Doe", "Mr Jones".</summary>
    [GeneratedRegex(@"\b(?:[Pp]atient|Mr|Mrs|Ms|Miss|Mx)\.?\s+[A-Z][a-z]+(?:\s+[A-Z][a-z]+)?", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex TitledName();

    /// <summary>Self-identification: "My name is Jane Roe", "I am Jane Roe", "This is Jane Roe".</summary>
    [GeneratedRegex(@"\b(?:[Mm]y name is|[Ii] am|[Ii]'m|[Tt]his is)\s+[A-Z][a-z]+\s+[A-Z][a-z]+", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SelfName();

    /// <summary>Labelled name fields (prose or structured keys): "patient_name": "…", "first name = …", or a bare "name: Jane Roe"
    /// (two words — "file name: report" is not a person).</summary>
    [GeneratedRegex(@"\b(?:(?:patient|full|first|last|given|family|member|client|customer|employee|legal) name" + FieldSep + @"[a-z][a-z'.-]{1,30}|(?<!(?:file|user|host|domain|model|display|company|product|project|brand|server|app|team|field|column|table|variable|class|method) )name" + FieldSep + @"[a-z][a-z'.-]{1,30} [a-z][a-z'.-]{1,30})",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex NameField();

    /// <summary>Labelled medical fields: "diagnosis: …", "medication": "…", "icd10": "…".</summary>
    [GeneratedRegex(@"\b(?:diagnosis|diagnoses|condition|conditions|medication|medications|prescription|allergy|allergies|treatment|procedure|icd(?: ?-?10)?(?: code)?|lab result|test result|symptoms)"
        + FieldSep + @"[a-z0-9]", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex MedicalField();

    /// <summary>Self-disclosed health facts: "I was diagnosed with…", "I have diabetes", "my medication…", "I'm taking 20 mg…".</summary>
    [GeneratedRegex(@"\b(?:i (?:was|am|have been|got|'ve been) diagnosed|i(?:'m| am) (?:pregnant|on (?:insulin|chemo\w*|dialysis|antidepressants?|medication))|i(?:'m| am) taking \d|i have (?:hiv|aids|cancer|diabetes|epilepsy|depression|bipolar|schizophreni\w*|hepatitis|asthma|copd|dementia|ptsd|adhd)|my (?:diagnosis|medication|medications|prescription|therapist|psychiatrist|oncologist|cardiologist|treatment|test results?|lab results?|biopsy))\b",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex SelfDisclosure();

    [GeneratedRegex(@"\b(?:diagnos\w*|prescri\w*|treated for|treatment for|chemotherapy|chemo|radiation therapy|surgery|hospitali[sz]ed|admitted (?:to|for)|discharged|tested (?:positive|negative)|positive for|lab results?|blood test|biopsy|mri|ct scan|x-ray|ultrasound|prognosis|dialysis|transplant|hiv|aids|cancer|tumou?r|diabetes|diabetic|asthma|copd|stroke|heart attack|myocardial|seizures?|epilepsy|depression|anxiety disorder|ptsd|adhd|schizophreni\w*|bipolar|pregnan\w*|miscarriage|dementia|alzheimer\w*|hepatitis|tuberculosis|overdose|psychiatric|insulin|metformin|antidepressants?|opioid\w*|medication)\b",
        RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex MedicalTerm();

    [GeneratedRegex(@"\b\d+(?:\.\d+)? ?(?:mg|mcg|µg|ml|iu|units?)\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Dose();

    [GeneratedRegex(@"\b[a-tv-z]\d{2}\.\d{1,4}\b", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex Icd10();
}
