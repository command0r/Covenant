using System.Diagnostics;
using System.Text;
using Covenant.Ui.Logic;

namespace Covenant.Ui.Services;

/// <summary>One submitted request: its frozen snapshot, what the transport showed, the streamed text (console-only,
/// memory-only), its real trace and the evidence lookup for its correlation id.</summary>
public sealed class PlaygroundRun(RequestSnapshot snapshot)
{
    private readonly StringBuilder _text = new();

    public RequestSnapshot Snapshot { get; } = snapshot;
    public bool InFlight { get; internal set; } = true;
    public InferenceResult? Result { get; internal set; }
    public EvidenceStatus Evidence { get; internal set; } = EvidenceStatus.Idle;
    /// <summary>Request id as soon as it is known (streaming: at the response headers).</summary>
    public string? RequestId { get; internal set; }
    /// <summary>Elapsed client wall time while in flight, final once done.</summary>
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    public TraceAccumulator Trace { get; } = new();
    public TraceFeedState TraceState { get; internal set; } = TraceFeedState.Idle;
    public string? TraceMessage { get; internal set; }
    internal bool TraceStarted { get; set; }
    internal CancellationTokenSource? Background { get; set; }

    public string Text => Result is { Text.Length: > 0 } r && !InFlight ? r.Text : _text.ToString();

    internal void Append(string delta) => _text.Append(delta);

    public OutcomeView? Outcome => InFlight || Result is null ? null : OutcomeModel.Compute(Result.ToOutcomeInput(Evidence.Record));

    /// <summary>The trace to show: live/snapshot events when there are any; otherwise (expired, or a Host without
    /// the trace API) the compact trace persisted on the audit record; otherwise the empty view.</summary>
    public TraceView CurrentTrace
    {
        get
        {
            if (Trace.Count > 0) return Trace.View(TraceState == TraceFeedState.Snapshot ? "snapshot" : "live");
            if (Evidence.Record is { } rec && TraceModel.FromAuditSummary(rec.Trace, rec.Attempts, rec.ConfigVersion) is { } summary)
                return summary;
            return InFlight || TraceState is TraceFeedState.Connecting or TraceFeedState.Live or TraceFeedState.Reconnecting
                ? TraceView.Empty
                : TraceView.Empty with { Steps = TraceModel.Reduce([], complete: true).Steps, Complete = true, Source = "none" };
        }
    }
}

/// <summary>The request workspace's state, shared by Playground and the Overview composer (scoped = page lifetime in
/// WASM) so an in-flight run, its result, trace and evidence survive navigation. Nothing is persisted; disconnect
/// clears everything. One run at a time. Opening a page, applying a preset or selecting a history entry NEVER
/// submits — only <see cref="RunAsync"/> does, and each run carries a fresh client_request_id.</summary>
public sealed class PlaygroundState : IDisposable
{
    public const int MaxHistory = 30;

    private readonly AppState _state;
    private readonly ConsoleInferenceApi _inference;
    private readonly EvidenceTracker _evidence;
    private readonly CatalogState _catalog;
    private readonly TraceClient _traces;
    private readonly WorkspaceConfig _workspace;
    private readonly List<PlaygroundRun> _runs = [];
    private CancellationTokenSource? _runCts;
    private int _sequence;

    public PlaygroundState(AppState state, ConsoleInferenceApi inference, EvidenceTracker evidence, CatalogState catalog,
        TraceClient traces, WorkspaceConfig workspace)
    {
        _state = state;
        _inference = inference;
        _evidence = evidence;
        _catalog = catalog;
        _traces = traces;
        _workspace = workspace;
        _state.Disconnected += Clear;
        _workspace.Changed += EnsureDefaultApplication;
    }

    public event Action? Changed;

    public PlaygroundForm Form { get; } = new();
    /// <summary>Newest last.</summary>
    public IReadOnlyList<PlaygroundRun> Runs => _runs;
    public PlaygroundRun? Selected { get; private set; }
    public bool Running => _runCts is not null;
    /// <summary>Note from the last preset application ("No profile with a Public baseline …"), or null.</summary>
    public string? PresetNote { get; private set; }

    /// <summary>Browser-local preference (Settings → Console preferences): the response mode a fresh form starts with.</summary>
    public bool StreamingDefault { get; private set; } = true;

    /// <summary>Applies the stored preference to the default and to the form while it holds no request yet.</summary>
    public void SetStreamingDefault(bool streaming)
    {
        StreamingDefault = streaming;
        if (!Running && string.IsNullOrEmpty(Form.Prompt)) Form.Streaming = streaming;
        Notify();
    }

    public string? Problem => ComposerRules.Problem(Form, _workspace.Applications);
    public bool CanRun => !Running && Problem is null;

    /// <summary>Shows a stored run. Never resubmits.</summary>
    public void Select(PlaygroundRun run)
    {
        if (!_runs.Contains(run)) return;
        Selected = run;
        Notify();
    }

    public void ApplyPreset(ScenarioPreset preset)
    {
        if (Running) return;
        PresetNote = ScenarioPresets.ApplyTo(preset, Form, _workspace.Applications, _workspace.Profiles).Note;
        Notify();
    }

    /// <summary>Gives the form a default "Run as" when none is chosen yet (first console-testable app and its
    /// default profile). Selection only — never runs.</summary>
    public void EnsureDefaultApplication()
    {
        if (Running || _workspace.Applications is not { } apps) return;
        if (ComposerRules.Find(apps, Form.ApplicationId) is { } cur && ComposerRules.WhyNotSelectable(cur) is null) return;
        var first = ComposerRules.Applications(apps).FirstOrDefault(c => c.Selectable);
        if (first is not null && ComposerRules.SelectApplication(Form, apps, first.App.Id)) Notify();
    }

    public bool SelectApplication(string? id)
    {
        if (Running) return false;
        var ok = ComposerRules.SelectApplication(Form, _workspace.Applications, id);
        Notify();
        return ok;
    }

    public bool SelectProfile(string? profileId)
    {
        if (Running) return false;
        var ok = ComposerRules.SelectProfile(Form, _workspace.Applications, profileId);
        Notify();
        return ok;
    }

    public void NotifyFormChanged() => Notify();

    /// <summary>Runs the preset only if the form still holds it and nothing blocks Run (the explicit
    /// "Run preset" action on Overview).</summary>
    public Task RunPresetAsync(ScenarioPreset preset)
        => ScenarioPresets.IsReady(preset, Form, _workspace.Applications) ? RunAsync() : Task.CompletedTask;

    /// <summary>Freezes the form and sends it. Refuses to start while another run is in flight or the composer
    /// has a problem.</summary>
    public async Task RunAsync()
    {
        if (!CanRun) return;

        var snapshot = RequestSnapshot.Freeze(++_sequence, Form, DateTimeOffset.UtcNow);
        var run = new PlaygroundRun(snapshot) { Background = new CancellationTokenSource() };
        _runs.Add(run);
        TrimHistory();
        Selected = run;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        Notify();

        InferenceResult result;
        try
        {
            result = await _inference.SendAsync(snapshot,
                delta => { run.Append(delta); Notify(); },
                id => { run.RequestId = id; StartTrace(run); Notify(); },
                cts.Token);
        }
        finally
        {
            _runCts = null;
            cts.Dispose();
        }
        run.Clock.Stop();
        run.Result = result;
        run.RequestId ??= result.RequestId;
        run.InFlight = false;
        Notify();

        // A disconnect cleared the list while this run was in flight: drop it.
        if (!_runs.Contains(run)) return;

        if (run.RequestId is { Length: > 0 } && result.HttpStatus is not null && !IsPreGovernance(result))
        {
            StartTrace(run);
            await TrackEvidenceAsync(run);
        }

        // The backend records success/failure from real calls: re-read shared health after any provider attempt.
        _ = _catalog.RefreshAsync();
    }

    /// <summary>Stops waiting for the in-flight run. Not a rollback.</summary>
    public void Cancel()
    {
        try { _runCts?.Cancel(); }
        catch (ObjectDisposedException) { /* finished meanwhile */ }
    }

    public async Task RetryEvidenceAsync(PlaygroundRun run)
    {
        if (run.RequestId is not { Length: > 0 }) return;
        await TrackEvidenceAsync(run);
    }

    /// <summary>Forgets every run and response (not the form).</summary>
    public void ClearOutput()
    {
        if (Running) return;
        foreach (var r in _runs) StopBackground(r);
        _runs.Clear();
        Selected = null;
        Notify();
    }

    private static bool IsPreGovernance(InferenceResult r)
        => (r.HttpStatus == 400 && !string.Equals(r.ErrorCode, "unsupported", StringComparison.Ordinal))
           || r.HttpStatus == 401 || r.HttpStatus == 404;

    private void StartTrace(PlaygroundRun run)
    {
        if (run.TraceStarted || run.RequestId is not { Length: > 0 } id || run.Background is not { } bg) return;
        run.TraceStarted = true;
        _ = FollowTraceAsync(run, id, bg.Token);
    }

    private async Task FollowTraceAsync(PlaygroundRun run, string id, CancellationToken ct)
    {
        try
        {
            await _traces.FollowAsync(id, run.Trace, (state, message) =>
            {
                run.TraceState = state;
                run.TraceMessage = message;
                Notify();
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.TraceState = TraceFeedState.Unavailable;
            run.TraceMessage = "Trace lookup failed: " + ex.Message;
            Notify();
        }
        catch (OperationCanceledException) { /* run forgotten */ }
    }

    private async Task TrackEvidenceAsync(PlaygroundRun run)
    {
        var id = run.RequestId;
        if (id is null || run.Background is not { } bg) return;
        run.Evidence = new EvidenceStatus(EvidenceState.Pending);
        Notify();
        var final = await _evidence.TrackAsync(id, s => { run.Evidence = s; Notify(); }, bg.Token);
        if (final.State != EvidenceState.Idle || run.Evidence.State == EvidenceState.Pending)
            run.Evidence = final.State == EvidenceState.Idle
                ? new EvidenceStatus(EvidenceState.Unavailable, Message: "Lookup stopped — retry.")
                : final;
        Notify();
    }

    private void TrimHistory()
    {
        while (_runs.Count > MaxHistory)
        {
            StopBackground(_runs[0]);
            _runs.RemoveAt(0);
        }
    }

    private static void StopBackground(PlaygroundRun run)
    {
        var bg = run.Background;
        run.Background = null;
        if (bg is null) return;
        try { bg.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
        bg.Dispose();
    }

    /// <summary>Disconnect: cancel the in-flight run and forget prompts, responses, traces and selection.</summary>
    private void Clear()
    {
        Cancel();
        foreach (var r in _runs) StopBackground(r);
        _runs.Clear();
        Selected = null;
        Form.Clear();
        Form.Streaming = StreamingDefault;
        PresetNote = null;
        _sequence = 0;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _state.Disconnected -= Clear;
        _workspace.Changed -= EnsureDefaultApplication;
        Cancel();
        foreach (var r in _runs) StopBackground(r);
    }
}
