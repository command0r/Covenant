using System.Diagnostics;
using System.Text;
using Covenant.Ui.Logic;

namespace Covenant.Ui.Services;

/// <summary>One submitted Playground request: its frozen snapshot, what the transport showed, the streamed text
/// (console-only, memory-only) and the evidence lookup for its correlation id.</summary>
public sealed class PlaygroundRun(RequestSnapshot snapshot)
{
    private readonly StringBuilder _text = new();

    public RequestSnapshot Snapshot { get; } = snapshot;
    public bool InFlight { get; internal set; } = true;
    public InferenceResult? Result { get; internal set; }
    public EvidenceStatus Evidence { get; internal set; } = EvidenceStatus.Idle;
    /// <summary>Elapsed client wall time while in flight (for the "Sending…" line), final once done.</summary>
    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    public string Text => Result is { Text.Length: > 0 } r && !InFlight ? r.Text : _text.ToString();

    internal void Append(string delta) => _text.Append(delta);

    public OutcomeView? Outcome => InFlight || Result is null ? null : OutcomeModel.Compute(Result.ToOutcomeInput(Evidence.Record));
}

/// <summary>Playground session state, kept across navigation (scoped = the page lifetime in WASM) so an in-flight
/// run, its result and its evidence survive a visit to Requests. Nothing here is persisted; AppState.Disconnected
/// clears all of it (and the application key lives in AppState, cleared there). One run at a time.</summary>
public sealed class PlaygroundState : IDisposable
{
    private readonly AppState _state;
    private readonly InferenceApi _inference;
    private readonly EvidenceTracker _evidence;
    private readonly CatalogState _catalog;
    private readonly List<PlaygroundRun> _runs = [];
    private CancellationTokenSource? _runCts;
    private CancellationTokenSource? _evidenceCts;
    private int _sequence;

    public PlaygroundState(AppState state, InferenceApi inference, EvidenceTracker evidence, CatalogState catalog)
    {
        _state = state;
        _inference = inference;
        _evidence = evidence;
        _catalog = catalog;
        _state.Disconnected += Clear;
    }

    public event Action? Changed;

    public PlaygroundForm Form { get; } = new();
    public IReadOnlyList<PlaygroundRun> Runs => _runs;
    public PlaygroundRun? Selected { get; private set; }
    public bool Running => _runCts is not null;

    public void Select(PlaygroundRun run)
    {
        if (!_runs.Contains(run)) return;
        Selected = run;
        Notify();
    }

    public void ApplyPreset(ScenarioPreset preset)
    {
        ScenarioPresets.ApplyTo(preset, Form);
        Notify();
    }

    /// <summary>Freezes the form and sends it. Refuses to start while another run is in flight.</summary>
    public async Task RunAsync()
    {
        if (Running || Form.Problem is not null) return;

        var snapshot = RequestSnapshot.Freeze(++_sequence, Form, DateTimeOffset.UtcNow);
        var run = new PlaygroundRun(snapshot);
        _runs.Add(run);
        Selected = run;
        var cts = new CancellationTokenSource();
        _runCts = cts;
        Notify();

        InferenceResult result;
        try
        {
            result = await _inference.SendAsync(snapshot, delta => { run.Append(delta); Notify(); }, cts.Token);
        }
        finally
        {
            _runCts = null;
            cts.Dispose();
        }
        run.Clock.Stop();
        run.Result = result;
        run.InFlight = false;
        Notify();

        // Only drop this run's results if a disconnect cleared the list while it was in flight.
        if (!_runs.Contains(run)) return;

        if (result.RequestId is { Length: > 0 } && result.HttpStatus is not null && !IsPreGovernance(result))
            await TrackEvidenceAsync(run);

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
        if (run.Result?.RequestId is not { Length: > 0 }) return;
        await TrackEvidenceAsync(run);
    }

    /// <summary>Clear output: forgets every run and response (not the form, not the key).</summary>
    public void ClearOutput()
    {
        if (Running) return;
        _evidenceCts?.Cancel();
        _runs.Clear();
        Selected = null;
        Notify();
    }

    private static bool IsPreGovernance(InferenceResult r)
        => r.HttpStatus == 400 && !string.Equals(r.ErrorCode, "unsupported", StringComparison.Ordinal);

    private async Task TrackEvidenceAsync(PlaygroundRun run)
    {
        var id = run.Result?.RequestId;
        if (id is null) return;
        _evidenceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _evidenceCts = cts;
        run.Evidence = new EvidenceStatus(EvidenceState.Pending);
        Notify();
        var final = await _evidence.TrackAsync(id, s => { run.Evidence = s; Notify(); }, cts.Token);
        if (final.State != EvidenceState.Idle || run.Evidence.State == EvidenceState.Pending)
            run.Evidence = final.State == EvidenceState.Idle
                ? new EvidenceStatus(EvidenceState.Unavailable, Message: "Lookup stopped — retry.")
                : final;
        if (ReferenceEquals(_evidenceCts, cts)) _evidenceCts = null;
        cts.Dispose();
        Notify();
    }

    /// <summary>Disconnect: cancel the in-flight run and forget prompts, responses, snapshots and selection.</summary>
    private void Clear()
    {
        Cancel();
        _evidenceCts?.Cancel();
        _runs.Clear();
        Selected = null;
        Form.Clear();
        _sequence = 0;
        Notify();
    }

    private void Notify() => Changed?.Invoke();

    public void Dispose()
    {
        _state.Disconnected -= Clear;
        Cancel();
        _evidenceCts?.Cancel();
    }
}
