using System.Globalization;
using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>One model row in the connection form. <see cref="Selected"/> = permitted on the connection (saved);
/// discovered tags start unselected — discovery never approves a model.</summary>
public sealed class ModelForm
{
    public string ModelId { get; set; } = "";
    public string? DisplayName { get; set; }
    public int? ContextTokens { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool Selected { get; set; }
    /// <summary>True when the row came from the saved configuration (prefilled with actual values).</summary>
    public bool Configured { get; set; }
    /// <summary>True/false once the endpoint was probed in this form; null = not probed.</summary>
    public bool? ReportedByEndpoint { get; set; }
    public bool Priced { get; set; }
    public decimal? InputPer1M { get; set; }
    public decimal? OutputPer1M { get; set; }
    public string Basis { get; set; } = "";
    public string? EffectiveDate { get; set; }
}

/// <summary>Editable copy of one connection (Add or Edit). Pure: produces a <see cref="ConnectionDto"/> and is put
/// into the configuration DRAFT only by the page; nothing is applied until the Host confirms a new version.</summary>
public sealed class ConnectionForm
{
    public const string BasisLocal = "zero-marginal-local";
    public const string BasisList = "provider-list";
    public const string BasisContract = "contract";
    /// <summary>Provider call timeout a NEW connection starts with, for every kind (the Host's own default; 1–600).</summary>
    public const int DefaultTimeoutSeconds = 120;

    public bool IsNew { get; init; }
    /// <summary>Id of the saved connection being edited (immutable on edit: policies reference it).</summary>
    public string? OriginalId { get; init; }
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Ollama | OpenAiCompatible | OpenAi | Anthropic (wire spelling).</summary>
    public string Kind { get; set; } = "Ollama";
    /// <summary>Local | Cloud (wire spelling).</summary>
    public string Boundary { get; set; } = "Local";
    public string Endpoint { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string? SecretRef { get; set; }
    public int? TimeoutSeconds { get; set; }
    public List<ModelForm> Models { get; set; } = [];

    public bool IsOllama => Kind == "Ollama";
    public bool IsCloudKind => Kind is "OpenAi" or "Anthropic";

    public static ConnectionForm NewOllama(IEnumerable<string> existingIds) => new()
    {
        IsNew = true,
        Kind = "Ollama",
        Boundary = "Local",
        DisplayName = "Ollama (in perimeter)",
        Id = ConnectionRules.UniqueId("ollama", existingIds),
        TimeoutSeconds = DefaultTimeoutSeconds,
    };

    public static ConnectionForm NewCloud(string kind, IEnumerable<string> existingIds)
    {
        var f = new ConnectionForm { IsNew = true, TimeoutSeconds = DefaultTimeoutSeconds };
        f.SetKind(kind);
        f.DisplayName = ConnectionRules.KindLabel(kind);
        f.Id = ConnectionRules.UniqueId(kind.ToLowerInvariant(), existingIds);
        return f;
    }

    /// <summary>Prefills the form with the saved connection's ACTUAL values.</summary>
    public static ConnectionForm FromConnection(ConnectionDto c) => new()
    {
        IsNew = false,
        OriginalId = c.Id,
        Id = c.Id,
        DisplayName = c.DisplayName ?? c.Id,
        Kind = c.Kind ?? "",
        Boundary = c.Boundary ?? "",
        Endpoint = c.Endpoint ?? "",
        Enabled = c.Enabled,
        SecretRef = c.SecretRef,
        TimeoutSeconds = c.TimeoutSeconds,
        Models = c.Models.Select(m => new ModelForm
        {
            ModelId = m.ModelId,
            DisplayName = m.DisplayName,
            ContextTokens = m.ContextTokens,
            MaxOutputTokens = m.MaxOutputTokens,
            Selected = true,
            Configured = true,
            Priced = m.Price is not null,
            InputPer1M = m.Price?.InputPer1M,
            OutputPer1M = m.Price?.OutputPer1M,
            Basis = m.Price?.Basis ?? "",
            EffectiveDate = m.Price?.EffectiveDate,
        }).ToList(),
    };

    /// <summary>Kind fixes the boundary for the SDK kinds (OpenAI/Anthropic are always Cloud, Ollama is Local);
    /// OpenAI-compatible keeps the operator's choice (Local needs a startup-approved endpoint).</summary>
    public void SetKind(string kind)
    {
        var previousDefault = DefaultSecretRef(Kind);
        if (string.IsNullOrWhiteSpace(SecretRef) || string.Equals(SecretRef, previousDefault, StringComparison.Ordinal))
            SecretRef = DefaultSecretRef(kind);
        Kind = kind;
        if (kind is "OpenAi" or "Anthropic") Boundary = "Cloud";
        else if (kind == "Ollama") Boundary = "Local";
        else if (Boundary is not ("Local" or "Cloud")) Boundary = "Cloud";
    }

    private static string? DefaultSecretRef(string? kind) => kind switch
    {
        "OpenAi" => "OpenAI:ApiKey",
        "Anthropic" => "Anthropic:ApiKey",
        _ => null,
    };

    /// <summary>Merges probe results: configured rows are marked reported/not reported; NEW tags are added
    /// UNSELECTED with an explicit zero-marginal-local price for a local connection (editable, never hidden).</summary>
    public void ApplyDiscovery(IEnumerable<string> tags)
    {
        var reported = new HashSet<string>(tags.Where(t => !string.IsNullOrWhiteSpace(t)), StringComparer.Ordinal);
        foreach (var m in Models) m.ReportedByEndpoint = reported.Contains(m.ModelId);
        foreach (var tag in reported.OrderBy(t => t, StringComparer.Ordinal))
        {
            if (Models.Any(m => string.Equals(m.ModelId, tag, StringComparison.Ordinal))) continue;
            Models.Add(DefaultModel(tag, reportedByEndpoint: true));
        }
    }

    /// <summary>A manually added model row (cloud model ids are typed, not discovered). Starts selected.</summary>
    public ModelForm AddModel(string modelId)
    {
        var m = DefaultModel(modelId.Trim(), reportedByEndpoint: null);
        m.Selected = true;
        Models.Add(m);
        return m;
    }

    private ModelForm DefaultModel(string id, bool? reportedByEndpoint)
    {
        bool local = Boundary == "Local";
        return new ModelForm
        {
            ModelId = id,
            Selected = false,
            Configured = false,
            ReportedByEndpoint = reportedByEndpoint,
            Priced = local,
            InputPer1M = local ? 0m : null,
            OutputPer1M = local ? 0m : null,
            Basis = local ? BasisLocal : BasisList,
        };
    }

    public IReadOnlyList<ModelForm> SelectedModels => Models.Where(m => m.Selected).ToList();

    /// <summary>Problems that block "Preview impact" (the Host validator still has the final word).</summary>
    public IReadOnlyList<string> Problems(IEnumerable<string> otherConnectionIds, IReadOnlyDictionary<string, SecretAvailabilityDto>? secrets)
    {
        var p = new List<string>();
        var others = otherConnectionIds.ToHashSet(StringComparer.Ordinal);
        if (!ConnectionRules.IsValidId(Id)) p.Add("Connection id must be 1–64 characters of letters, digits, '.', '_' or '-'.");
        else if (IsNew && others.Contains(Id)) p.Add("A connection with id '" + Id + "' already exists.");
        if (string.IsNullOrWhiteSpace(DisplayName)) p.Add("Enter a display name.");
        if (Kind is not ("Ollama" or "OpenAiCompatible" or "OpenAi" or "Anthropic")) p.Add("Choose a provider kind.");
        if (ConnectionRules.EndpointRequired(Kind) || Endpoint.Trim().Length > 0)
        {
            if (ConnectionLogic.EndpointProblem(Endpoint) is { } e) p.Add(e);
        }
        if (Boundary is not ("Local" or "Cloud")) p.Add("Choose Local or Cloud.");
        if (IsCloudKind && Boundary != "Cloud") p.Add("OpenAI and Anthropic connections are always Cloud.");
        if (TimeoutSeconds is < 1 or > 600) p.Add("Timeout must be 1–600 seconds.");

        var selected = SelectedModels;
        if (selected.Count == 0) p.Add("Select at least one exact model id to permit — nothing is approved by default.");
        var dup = selected.GroupBy(m => m.ModelId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) p.Add("Model '" + dup.Key + "' is listed twice.");
        foreach (var m in selected)
        {
            if (string.IsNullOrWhiteSpace(m.ModelId) || m.ModelId.Length > 200) p.Add("Every model needs its exact provider id/tag (≤ 200 characters).");
            if (m.Priced)
            {
                if (m.InputPer1M is null || m.OutputPer1M is null) p.Add(m.ModelId + ": enter both input and output prices (per 1M tokens), or mark it unpriced.");
                else if (m.InputPer1M < 0 || m.OutputPer1M < 0) p.Add(m.ModelId + ": prices cannot be negative.");
                if (string.IsNullOrWhiteSpace(m.Basis)) p.Add(m.ModelId + ": state the price basis (e.g. provider-list, contract, zero-marginal-local).");
            }
            else if (Boundary == "Cloud")
            {
                p.Add(m.ModelId + ": a paid cloud model needs prices — an unknown price is never treated as $0.");
            }
            if (!string.IsNullOrWhiteSpace(m.EffectiveDate) && !DateOnly.TryParseExact(m.EffectiveDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                p.Add(m.ModelId + ": effective date must be YYYY-MM-DD.");
        }

        if (Boundary == "Cloud" || IsCloudKind)
        {
            if (string.IsNullOrWhiteSpace(SecretRef)) p.Add("Choose the server-side secret reference that holds this provider's credential.");
            else if (!ConnectionRules.IsValidSecretRef(SecretRef)) p.Add("Secret references must be Secrets:<name> or OpenAI:ApiKey / Anthropic:ApiKey / Local:ApiKey.");
            else if (secrets is not null && secrets.TryGetValue(SecretRef.Trim(), out var s) && !s.Available)
                p.Add("Secret " + SecretRef.Trim() + " is not available on the server — set it server-side and restart before saving.");
        }
        return p;
    }

    /// <summary>The connection as it will appear in the draft. Unselected rows are dropped.</summary>
    public ConnectionDto ToConnection() => new()
    {
        Id = Id.Trim(),
        DisplayName = DisplayName.Trim(),
        Kind = Kind,
        Boundary = Boundary,
        Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? null : Endpoint.Trim(),
        Enabled = Enabled,
        SecretRef = string.IsNullOrWhiteSpace(SecretRef) ? null : SecretRef.Trim(),
        TimeoutSeconds = TimeoutSeconds,
        Models = SelectedModels.Select(m => new ModelDto
        {
            ModelId = m.ModelId.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(m.DisplayName) ? null : m.DisplayName.Trim(),
            ContextTokens = m.ContextTokens,
            MaxOutputTokens = m.MaxOutputTokens,
            Price = m.Priced
                ? new PriceDto
                {
                    InputPer1M = m.InputPer1M,
                    OutputPer1M = m.OutputPer1M,
                    Basis = m.Basis.Trim(),
                    EffectiveDate = string.IsNullOrWhiteSpace(m.EffectiveDate) ? null : m.EffectiveDate.Trim(),
                }
                : null,
        }).ToList(),
    };

    /// <summary>Id under which this form was last put into the draft (a new connection's id may change between previews).</summary>
    public string? AppliedAsId { get; private set; }

    /// <summary>Puts this connection into <paramref name="draft"/> (replacing the edited one — or this form's earlier
    /// copy — in place, or appending). Returns its index in the draft's connection list (for issue paths).</summary>
    public int ApplyTo(CovenantConfigDto draft)
    {
        var conn = ToConnection();
        var key = AppliedAsId ?? OriginalId ?? conn.Id;
        int i = draft.Connections.FindIndex(c => string.Equals(c.Id, key, StringComparison.Ordinal));
        if (i >= 0) draft.Connections[i] = conn;
        else
        {
            draft.Connections.Add(conn);
            i = draft.Connections.Count - 1;
        }
        AppliedAsId = conn.Id;
        return i;
    }

    /// <summary>Index in the issue path of the n-th SELECTED model row (unselected rows are not sent).</summary>
    public int? SentIndex(ModelForm row)
    {
        if (!row.Selected) return null;
        int n = 0;
        foreach (var m in Models)
        {
            if (ReferenceEquals(m, row)) return n;
            if (m.Selected) n++;
        }
        return null;
    }

    /// <summary>Model ids removed from an existing connection that enabled policies still name (shown before preview).</summary>
    public IReadOnlyList<string> RemovedModelsStillRouted(CovenantConfigDto config)
    {
        if (OriginalId is null) return [];
        var keep = SelectedModels.Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        return config.Policies.Where(p => p.Enabled)
            .SelectMany(p => p.Targets)
            .Where(t => string.Equals(t.ConnectionId, OriginalId, StringComparison.Ordinal) && !keep.Contains(t.ModelId))
            .Select(t => t.ConnectionId + "/" + t.ModelId)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>Connection rules mirrored from the Host validator (for early, inline feedback only).</summary>
public static class ConnectionRules
{
    public static readonly IReadOnlyList<string> LegacySecretRefs = ["OpenAI:ApiKey", "Anthropic:ApiKey", "Local:ApiKey"];

    public static bool IsValidId(string? id)
        => id is { Length: >= 1 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool IsValidSecretRef(string? r)
    {
        if (string.IsNullOrWhiteSpace(r)) return false;
        var t = r.Trim();
        if (LegacySecretRefs.Contains(t, StringComparer.Ordinal)) return true;
        return t.StartsWith("Secrets:", StringComparison.Ordinal) && t.Length > 8 && t.Length <= 136
               && t[8..].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }

    /// <summary>The environment variable name an operator sets for a secret reference ("Secrets:openai" → "Secrets__openai").</summary>
    public static string EnvVarFor(string secretRef) => secretRef.Trim().Replace(":", "__", StringComparison.Ordinal);

    public static bool EndpointRequired(string? kind) => kind is "Ollama" or "OpenAiCompatible";

    public static string KindLabel(string? kind) => kind switch
    {
        "Ollama" => "Ollama",
        "OpenAiCompatible" => "OpenAI-compatible",
        "OpenAi" => "OpenAI",
        "Anthropic" => "Anthropic",
        null or "" => "Kind not reported",
        _ => kind,
    };

    /// <summary>"ollama", "ollama-2", … — never reuses an existing id.</summary>
    public static string UniqueId(string seed, IEnumerable<string> existing)
    {
        var set = existing.ToHashSet(StringComparer.Ordinal);
        var cleaned = new string(seed.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).ToLowerInvariant();
        if (cleaned.Length == 0) cleaned = "connection";
        if (cleaned.Length > 56) cleaned = cleaned[..56];
        if (!set.Contains(cleaned)) return cleaned;
        for (int i = 2; i < 1000; i++)
        {
            var candidate = cleaned + "-" + i.ToString(CultureInfo.InvariantCulture);
            if (!set.Contains(candidate)) return candidate;
        }
        return cleaned + "-" + Guid.NewGuid().ToString("N")[..6];
    }

    /// <summary>"host:port" of an endpoint, or the provider default authority for OpenAI/Anthropic without one.</summary>
    public static string? AuthorityOf(string? kind, string? endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint)) return ConnectionLogic.Authority(endpoint);
        return kind switch
        {
            "OpenAi" => "api.openai.com:443",
            "Anthropic" => "api.anthropic.com:443",
            _ => null,
        };
    }

    /// <summary>Secret references the Host reported (names only, never values) with their availability.</summary>
    public static IReadOnlyList<(string Ref, bool Available)> SecretChoices(IReadOnlyDictionary<string, SecretAvailabilityDto>? secrets)
        => LegacySecretRefs.Concat(secrets?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => (x, secrets is not null && secrets.TryGetValue(x, out var value) && value.Available)).ToList();
}

/// <summary>A connection's observed health, one summary line plus supporting timestamps (spec §5): the last
/// endpoint PROBE and the last successful governed INFERENCE are separate facts and are never merged into a
/// contradictory "ready" / "never succeeded".</summary>
public sealed record ConnectionHealthView(string Summary, string Tone, IReadOnlyList<(string Label, string Value)> Details);

public static class ConnectionHealth
{
    /// <param name="enabled">Draft/applied enabled state.</param>
    /// <param name="models">Per-model health from the catalog (observed from real governed calls).</param>
    /// <param name="probe">This session's probe of the endpoint (point-in-time), if any.</param>
    /// <param name="probedAtUtc">When that probe ran (browser clock) when the Host did not stamp it.</param>
    /// <param name="endpointProbeHealth">The Host's recorded probe health for an APPLIED endpoint, when reported.</param>
    public static ConnectionHealthView Summarize(bool enabled, IReadOnlyList<ModelHealth?> models, ProbeResponse? probe,
        DateTimeOffset? probedAtUtc, ModelHealth? endpointProbeHealth, DateTimeOffset nowUtc)
    {
        DateTimeOffset? lastOk = Max(models.Select(h => h?.LastSuccessUtc));
        DateTimeOffset? lastFail = Max(models.Select(h => h?.LastFailureUtc));
        string? lastError = models.Where(h => h is not null && h.LastFailureUtc is not null && h.LastFailureUtc == lastFail).Select(h => h!.LastError).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));

        // Probe: this session's result wins over the Host's recorded endpoint health (newer, and it is what the operator just did).
        DateTimeOffset? probeAt = null;
        bool? probeOk = null;
        string? probeError = null;
        if (probe is not null)
        {
            probeAt = probe.CheckedUtc ?? probedAtUtc;
            probeOk = probe.Allowed && probe.Reachable;
            probeError = !probe.Allowed ? "destination not on the probe allow-list" : probe.Reachable ? null : (probe.Error ?? "unreachable");
        }
        else if (endpointProbeHealth is { } eh && (eh.LastSuccessUtc is not null || eh.LastFailureUtc is not null))
        {
            bool failedLast = eh.LastFailureUtc is { } f && (eh.LastSuccessUtc is null || f > eh.LastSuccessUtc);
            probeAt = failedLast ? eh.LastFailureUtc : eh.LastSuccessUtc;
            probeOk = !failedLast;
            probeError = failedLast ? (eh.LastError ?? "unreachable") : null;
        }

        var details = new List<(string, string)>
        {
            ("Last endpoint probe", probeAt is null ? "Not probed" : (probeOk == true ? "Reachable" : "Failed") + " · " + Stamp(probeAt, nowUtc) + (probeError is null ? "" : " · " + probeError)),
            ("Last successful governed call", lastOk is null ? "None observed" : Stamp(lastOk, nowUtc)),
            ("Last failed governed call", lastFail is null ? "None observed" : Stamp(lastFail, nowUtc) + (lastError is null ? "" : " · " + lastError)),
        };

        if (!enabled) return new("Disabled — not eligible for any route", "hollow", details);

        bool failureNewest = lastFail is { } lf && (lastOk is null || lf > lastOk);
        if (failureNewest)
            return new("Last governed call failed " + Ago(lastFail, nowUtc) + (lastOk is null ? " · no successful call observed" : " · last success " + Ago(lastOk, nowUtc)), "bad", details);
        if (lastOk is not null)
            return new("Last successful governed call " + Ago(lastOk, nowUtc), "ok", details);
        if (probeOk == true)
            return new("Endpoint reachable (probe " + Ago(probeAt, nowUtc) + ") · no governed call observed yet", "hollow", details);
        if (probeOk == false)
            return new("Endpoint probe failed " + Ago(probeAt, nowUtc) + " · no governed call observed yet", "bad", details);
        return new("Not observed yet — test the endpoint or run a request", "hollow", details);
    }

    private static DateTimeOffset? Max(IEnumerable<DateTimeOffset?> values)
    {
        DateTimeOffset? best = null;
        foreach (var v in values)
            if (v is { } t && (best is null || t > best)) best = t;
        return best;
    }

    public static string Ago(DateTimeOffset? t, DateTimeOffset now)
    {
        if (t is null) return "never";
        var s = (long)Math.Max(0, (now - t.Value).TotalSeconds);
        if (s < 60) return s.ToString(CultureInfo.InvariantCulture) + " s ago";
        if (s < 3600) return (s / 60).ToString(CultureInfo.InvariantCulture) + " min ago";
        if (s < 86400) return (s / 3600).ToString(CultureInfo.InvariantCulture) + " h ago";
        return (s / 86400).ToString(CultureInfo.InvariantCulture) + " d ago";
    }

    private static string Stamp(DateTimeOffset? t, DateTimeOffset now)
        => t is null ? "never" : t.Value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC (" + Ago(t, now) + ")";
}
