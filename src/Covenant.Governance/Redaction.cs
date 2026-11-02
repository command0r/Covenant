using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Covenant.Core;

namespace Covenant.Governance;

/// <summary>ADR-0016 bounded input contract <c>covenant.record.v1</c>: the ONLY input a sanitized-payload policy accepts.
/// Identifier fields are replaced by placeholders; attribute fields are enumerations or bounded numbers; there is no
/// free-text field; the task instruction is Covenant's fixed text. Defined in code on purpose (a change is a reviewed
/// code change, not configuration).</summary>
public static class StructuredRecordSchema
{
    public const string Id = "covenant.record.v1";
    public const int MaxChars = 4096;

    /// <summary>Identifier field → redaction type.</summary>
    public static readonly IReadOnlyDictionary<string, string> Identifiers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["email"] = RedactionTypes.Email,
        ["phone"] = RedactionTypes.Phone,
        ["us_ssn"] = RedactionTypes.UsSsn,
    };

    /// <summary>Enumerated attribute fields (sent as-is).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Enums = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["service"] = ["subscription", "workspace", "device", "account", "storage"],
        ["issue"] = ["login_failed", "billing_question", "service_unavailable", "access_request", "data_export"],
        ["channel"] = ["email", "phone", "portal", "app"],
        ["priority"] = ["low", "normal", "high"],
    };

    /// <summary>Bounded integer fields: name → (min, max).</summary>
    public static readonly IReadOnlyDictionary<string, (int Min, int Max)> Integers = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
    {
        ["days_open"] = (0, 365),
    };

    /// <summary>Bounded decimal fields (≤ 2 decimals): name → (min, max).</summary>
    public static readonly IReadOnlyDictionary<string, (decimal Min, decimal Max)> Decimals = new Dictionary<string, (decimal, decimal)>(StringComparer.Ordinal)
    {
        ["amount_usd"] = (0m, 1_000_000m),
    };

    /// <summary>Fixed tasks: id → the exact instruction Covenant sends. The caller's text is never forwarded.</summary>
    public static readonly IReadOnlyDictionary<string, string> Tasks = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["triage_case"] = "Classify the urgency of this service support case (low, normal or high) and list the next three operational steps.",
        ["reply_outline"] = "Write a short outline (at most five bullets) of a reply to the requester about this service support case.",
        ["compliance_checklist"] = "List the checks an agent must complete before closing this service support case.",
    };

    public const string SystemInstruction =
        "You receive a structured service support record. Identifier values were replaced by placeholders such as [EMAIL_1], "
        + "[PHONE_1] or [SSN_1] before this request left the organisation. Treat placeholders as opaque labels: do not guess, "
        + "invent or ask for the real values, and do not try to identify the customer. Use only the fields provided.";

    /// <summary>Synthetic demo record: SSN area 000 is never assigned, example.test is a reserved domain, 555-01xx numbers are
    /// fictional. No real person's data.</summary>
    public const string SyntheticExample =
        "{\"schema\":\"covenant.record.v1\",\"task\":\"triage_case\",\"fields\":{\"email\":\"jordan.sample@example.test\","
        + "\"phone\":\"555-010-0199\",\"us_ssn\":\"000-12-3456\",\"service\":\"subscription\",\"issue\":\"billing_question\","
        + "\"channel\":\"portal\",\"days_open\":2,\"amount_usd\":129.99}}";

    public const string Limits =
        "Restricted structured-data demonstration, not anonymisation: only the listed fields are accepted, identifiers are "
        + "replaced by placeholders, and the remaining attributes may still allow re-identification together with other data. "
        + "Free text, names, addresses and health data are never eligible. Web search is not available on this route.";

    /// <summary>Stable field order for rendering (identifiers first, then attributes).</summary>
    public static readonly IReadOnlyList<string> FieldOrder = ["email", "phone", "us_ssn", "service", "issue", "channel", "priority", "days_open", "amount_usd"];
}

/// <summary>A validated structured record: task id and fields in canonical string form (identifiers still original —
/// never leaves <see cref="StructuredRecord"/>' build step).</summary>
public sealed record ParsedRecord(string Task, IReadOnlyDictionary<string, string> Fields);

/// <summary>Parse/validate/build for <c>covenant.record.v1</c> (ADR-0016). Every refusal carries a bounded code and a
/// specific, actionable reason that never quotes a field VALUE.</summary>
public static partial class StructuredRecord
{
    public sealed record Refusal(string Code, string Reason);

    public static (ParsedRecord? Record, Refusal? Refusal) Parse(IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string> allowedTypes, IReadOnlyCollection<string> allowedTasks)
    {
        static (ParsedRecord?, Refusal?) No(string code, string reason) => (null, new Refusal(code, reason));

        if (messages.Count != 1 || messages[0].Role != ChatRole.User)
            return No(RedactionReasons.UnsupportedShape, "send exactly one user message containing only the structured record (no system or earlier turns)");
        var text = messages[0].Content.Trim();
        if (text.Length == 0 || text.Length > StructuredRecordSchema.MaxChars || text[0] != '{' || text[^1] != '}')
            return No(RedactionReasons.UnsupportedShape, $"the message must be one JSON object ({StructuredRecordSchema.Id}, at most {StructuredRecordSchema.MaxChars} characters) with no text around it");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            return No(RedactionReasons.UnsupportedShape, "the message is not valid JSON");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return No(RedactionReasons.UnsupportedShape, "the record must be a JSON object");
            string? schema = null, task = null;
            JsonElement? fields = null;
            var top = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!top.Add(p.Name)) return No(RedactionReasons.UnsupportedShape, "the record repeats a top-level key (allowed once each: schema, task, fields)");
                switch (p.Name)
                {
                    case "schema" when p.Value.ValueKind == JsonValueKind.String: schema = p.Value.GetString(); break;
                    case "task" when p.Value.ValueKind == JsonValueKind.String: task = p.Value.GetString(); break;
                    case "fields" when p.Value.ValueKind == JsonValueKind.Object: fields = p.Value; break;
                    case "schema" or "task" or "fields":
                        return No(RedactionReasons.UnsupportedShape, $"'{p.Name}' has the wrong JSON type (schema and task are strings, fields is an object)");
                    default:
                        return No(RedactionReasons.UnknownField, "the record has an unknown top-level key (allowed: schema, task, fields)");
                }
            }
            if (schema != StructuredRecordSchema.Id) return No(RedactionReasons.UnsupportedShape, $"schema must be \"{StructuredRecordSchema.Id}\"");
            if (task is null || !StructuredRecordSchema.Tasks.ContainsKey(task))
                return No(RedactionReasons.NotAllowed, "task must be one of: " + string.Join(", ", StructuredRecordSchema.Tasks.Keys));
            if (!allowedTasks.Contains(task))
                return No(RedactionReasons.NotAllowed, $"task '{task}' is not allowed by this policy (allowed: {string.Join(", ", allowedTasks)})");
            if (fields is not { } f) return No(RedactionReasons.UnsupportedShape, "'fields' object is required");

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in f.EnumerateObject())
            {
                if (values.ContainsKey(p.Name)) return No(RedactionReasons.UnsupportedShape, "a field appears more than once (each supported field at most once)");
                if (FieldValue(p.Name, p.Value, allowedTypes) is { } refusal) return (null, refusal);
                values[p.Name] = Canonical(p.Name, p.Value);
            }
            if (values.Count == 0) return No(RedactionReasons.UnsupportedShape, "the record has no fields");
            return (new ParsedRecord(task, values), null);
        }
    }

    private static Refusal? FieldValue(string name, JsonElement v, IReadOnlyCollection<string> allowedTypes)
    {
        if (StructuredRecordSchema.Identifiers.TryGetValue(name, out var type))
        {
            if (!allowedTypes.Contains(type)) return new(RedactionReasons.NotAllowed, $"identifier field '{name}' is not allowed by this policy");
            if (v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 and <= 254 } s || !FullMatch(type, s))
                return new(RedactionReasons.InvalidValue, $"'{name}' must be a single well-formed {Describe(type)}");
            return null;
        }
        if (StructuredRecordSchema.Enums.TryGetValue(name, out var allowed))
            return v.ValueKind == JsonValueKind.String && v.GetString() is { } e && allowed.Contains(e)
                ? null
                : new(RedactionReasons.InvalidValue, $"'{name}' must be one of: {string.Join(", ", allowed)}");
        if (StructuredRecordSchema.Integers.TryGetValue(name, out var ir))
            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= ir.Min && i <= ir.Max
                ? null
                : new(RedactionReasons.InvalidValue, $"'{name}' must be a whole number {ir.Min}–{ir.Max}");
        if (StructuredRecordSchema.Decimals.TryGetValue(name, out var dr))
            return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d >= dr.Min && d <= dr.Max && decimal.Round(d, 2) == d
                ? null
                : new(RedactionReasons.InvalidValue, $"'{name}' must be a number {dr.Min.ToString(CultureInfo.InvariantCulture)}–{dr.Max.ToString(CultureInfo.InvariantCulture)} with at most 2 decimals");
        // Fixed wording: an unknown property NAME is caller data (it may itself hold an identifier) and is never echoed.
        return new(RedactionReasons.UnknownField,
            "the record contains a field the schema does not define — free text is not accepted; supported fields: " + string.Join(", ", StructuredRecordSchema.FieldOrder));
    }

    private static string Canonical(string name, JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Number when StructuredRecordSchema.Integers.ContainsKey(name) => v.GetInt32().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number => v.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture),
        _ => "",
    };

    private static string Describe(string type) => type switch
    {
        RedactionTypes.Email => "e-mail address",
        RedactionTypes.Phone => "phone number (e.g. 555-010-0199)",
        RedactionTypes.UsSsn => "SSN in the form 000-00-0000",
        _ => "value",
    };

    private static bool FullMatch(string type, string s) => type switch
    {
        RedactionTypes.Email => EmailFull().IsMatch(s),
        RedactionTypes.Phone => PhoneFull().IsMatch(s),
        RedactionTypes.UsSsn => SsnFull().IsMatch(s),
        _ => false,
    };

    /// <summary>Builds the ONLY messages any attempt may receive: Covenant's fixed system instruction, then the fixed task
    /// instruction with the rendered record — identifier values replaced by typed placeholders. Returns per-type counts.</summary>
    public static (IReadOnlyList<ChatMessage> Messages, Dictionary<string, int> Counts) Build(ParsedRecord record)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append(StructuredRecordSchema.Tasks[record.Task]).Append("\n\nRecord (schema ").Append(StructuredRecordSchema.Id)
          .Append("; identifiers are placeholders, not real values):\n");
        foreach (var name in StructuredRecordSchema.FieldOrder)
        {
            if (!record.Fields.TryGetValue(name, out var value)) continue;
            if (StructuredRecordSchema.Identifiers.TryGetValue(name, out var type))
            {
                counts[type] = counts.GetValueOrDefault(type) + 1;
                value = "[" + Label(type) + "_1]";   // one field per type ⇒ always _1
            }
            sb.Append("- ").Append(name).Append(": ").Append(value).Append('\n');
        }
        IReadOnlyList<ChatMessage> messages = new List<ChatMessage>
        {
            new(ChatRole.System, StructuredRecordSchema.SystemInstruction),
            new(ChatRole.User, sb.ToString().TrimEnd()),
        }.AsReadOnly();
        return (messages, counts);
    }

    public static string Label(string type) => type switch
    {
        RedactionTypes.Email => "EMAIL",
        RedactionTypes.Phone => "PHONE",
        RedactionTypes.UsSsn => "SSN",
        _ => "VALUE",
    };

    [GeneratedRegex(@"^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailFull();

    [GeneratedRegex(@"^(\+?1[\s.\-]?)?(\(\d{3}\)\s?|\d{3}[\s.\-])\d{3}[\s.\-]\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneFull();

    [GeneratedRegex(@"^\d{3}-\d{2}-\d{4}$", RegexOptions.CultureInvariant)]
    private static partial Regex SsnFull();
}

/// <summary>Identifier patterns over free text — used ONLY for masking evidence previews and for the post-build
/// re-check. Never used to make prose eligible for egress (ADR-0016 review).</summary>
public static partial class IdentifierPatterns
{
    public static string Mask(string text)
    {
        text = Email().Replace(text, "[EMAIL]");
        text = Ssn().Replace(text, "[SSN]");
        return Phone().Replace(text, "[PHONE]");
    }

    public static bool AnyMatch(IReadOnlyList<ChatMessage> messages)
        => messages.Any(m => Email().IsMatch(m.Content) || Ssn().IsMatch(m.Content) || Phone().IsMatch(m.Content));

    [GeneratedRegex(@"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)(?:\+?1[\s.\-]?)?(?:\(\d{3}\)\s?|\d{3}[\s.\-])\d{3}[\s.\-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ssn();
}

/// <summary>Shared decision of the redact stage and the console preview (so preview and execution cannot diverge; the
/// preview is still never authorization — execution re-evaluates on its own bound configuration).</summary>
public static class SanitizedPayload
{
    public sealed record Decision(bool Applied, string ReasonCode, string? Reason, IReadOnlyList<ChatMessage>? Messages,
        IReadOnlyDictionary<string, int> Counts, DataClassification? TransformedClassification);

    /// <summary>TransformedClassification = the detector's own result on the built payload (Internal = nothing detected); it is
    /// evidence only and never used for routing (the workload's baseline stays PII).</summary>
    public static Decision Evaluate(IReadOnlyList<ChatMessage> messages, DataClassification classification,
        PolicyOutcome outcome, IDataClassifier classifier, InferenceRequest template)
    {
        var none = new Dictionary<string, int>();
        if (classification != DataClassification.Pii)
            return new(false, RedactionReasons.NotCovered, $"a sanitized payload is authorized only for PII; this request is {classification}", null, none, null);
        var (record, refusal) = StructuredRecord.Parse(messages, outcome.RedactionTypes, outcome.RedactionTasks);
        if (refusal is not null) return new(false, refusal.Code, refusal.Reason, null, none, null);
        var (built, counts) = StructuredRecord.Build(record!);
        var detected = classifier.Classify(template with { Messages = built });
        if (detected.Incomplete || (detected.Signal is not null && detected.Classification >= DataClassification.Pii) || IdentifierPatterns.AnyMatch(built))
            return new(false, RedactionReasons.Incomplete, "the built payload still contains sensitive data; nothing was sent", null, counts, null);
        return new(true, RedactionReasons.Applied, null, built, counts, detected.Classification);
    }
}

/// <summary>ADR-0016 redact stage, between policy and cache, in the shared pipeline (/v1 and console alike). When the
/// matched policy requires a sanitized payload it validates the structured record, builds the fixed-instruction payload,
/// re-checks it and refuses on any doubt — before cache, budget, search or any provider. Classification is never
/// rewritten. Otherwise it is skipped.</summary>
public sealed class RedactStage(IDataClassifier classifier) : IPipelineStage
{
    public const string Stage = "redact";

    public async Task InvokeAsync(InferenceContext ctx, PipelineDelegate next, CancellationToken ct)
    {
        if (ctx.Policy is not { SanitizedPayloadRequired: true } outcome)
        {
            ctx.MarkCurrentStageSkipped(RedactionReasons.NotRequired);
            await next(ctx, ct);
            return;
        }

        var state = new RedactionState { PolicyId = outcome.PolicyId ?? "unknown", OriginalClassification = ctx.Classification };
        ctx.Redaction = state;
        var decision = SanitizedPayload.Evaluate(ctx.Request.Messages, ctx.Classification, outcome, classifier, ctx.Request);
        foreach (var (type, count) in decision.Counts) state.Counts[type] = count;
        state.ReasonCode = decision.ReasonCode;
        if (!decision.Applied)
        {
            state.Outcome = "refused";
            ctx.Deny("sanitized external route refused: " + decision.Reason, DenialKind.Governance, Stage, decision.ReasonCode);
            return;
        }

        state.Outcome = "applied";
        state.TransformedClassification = decision.TransformedClassification;
        state.Messages = decision.Messages;
        ctx.ModelMessages = decision.Messages;   // every attempt (primary and approved fallback) receives exactly this set
        ctx.MarkCurrentStagePassed(RedactionReasons.Applied);
        await next(ctx, ct);
    }
}

/// <summary>ADR-0016 per-attempt defence in depth: a cloud target may receive a PII/PHI request only when the redact stage
/// applied AND the model messages are exactly its built set (not replaced afterwards, e.g. by grounding).</summary>
public static class EgressGuard
{
    public static string? Refusal(InferenceContext ctx, RouteTarget target)
    {
        if (ctx.Configuration is not { } cfg) return null;
        if (cfg.FindConnection(target.AdapterKey) is not { Boundary: Boundary.Cloud }) return null;
        if (ctx.Classification < DataClassification.Pii) return null;
        bool verified = ctx.Classification == DataClassification.Pii
                        && ctx.Redaction is { Applied: true } r
                        && ctx.ModelMessages is { } sent && ReferenceEquals(sent, r.Messages);
        return verified ? null : $"{ctx.Classification} is never sent to a cloud destination without a verified sanitized payload";
    }
}
