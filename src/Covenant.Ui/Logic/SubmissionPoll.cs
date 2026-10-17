using Covenant.Ui.Models;

namespace Covenant.Ui.Logic;

/// <summary>What the console does next while it locates a console submission's request id.</summary>
public enum SubmissionPollAction
{
    /// <summary>Not registered yet (404) or a transient failure: ask again after <see cref="SubmissionPoll.Interval"/>.</summary>
    PollAgain,
    /// <summary>The request id is known for the first time: subscribe to its live trace NOW (exactly once).</summary>
    Subscribe,
    /// <summary>Stop polling (id already known, admin token rejected, the POST finished without one, or the bound).</summary>
    Stop,
}

/// <summary>Locates the server request id of one console submission BEFORE the first response byte (review round 2,
/// B7; docs/ui-api-contract.md "Early request id"): the console creates <c>client_request_id</c> (GUID "N"), starts the
/// POST and concurrently polls <c>GET /admin/playground/submissions/{client_request_id}</c> every 250 ms until it
/// answers 200 with the request id (404 = not registered yet). The first id known — from the poll or, as a backstop,
/// from the POST's response — yields exactly one <see cref="SubmissionPollAction.Subscribe"/>. Nothing here invents an
/// id or a timing. Pure state machine (no I/O, no clock).</summary>
public sealed class SubmissionPoll(string clientRequestId)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);
    /// <summary>Bound: 240 polls ≈ 60 s. The Host registers the id before the pipeline starts, so it normally answers
    /// on the first or second poll; past the bound the response headers still carry the id.</summary>
    public const int MaxPolls = 240;

    public string ClientRequestId { get; } = clientRequestId;
    /// <summary>The server request id once known (poll or response).</summary>
    public string? RequestId { get; private set; }
    /// <summary>Where the id came from: "submission lookup" | "response" | null.</summary>
    public string? Source { get; private set; }
    public int Polls { get; private set; }
    public bool Stopped { get; private set; }
    public string? StopReason { get; private set; }

    /// <summary>True while another GET should be sent.</summary>
    public bool ShouldPoll => !Stopped && RequestId is null && Polls < MaxPolls;

    /// <summary>Applies one GET answer: HTTP <paramref name="status"/> (null = transport failure) and the 200 body.</summary>
    public SubmissionPollAction OnPollResult(int? status, PlaygroundSubmissionDto? body)
    {
        if (Stopped || RequestId is not null) return SubmissionPollAction.Stop;
        Polls++;
        if (status == 200 && body is not null && body.RequestId.Length > 0
            && string.Equals(body.ClientRequestId, ClientRequestId, StringComparison.Ordinal))
        {
            RequestId = body.RequestId;
            Source = "submission lookup";
            return SubmissionPollAction.Subscribe;
        }
        if (status == 401) return StopWith("admin token rejected");
        if (Polls >= MaxPolls) return StopWith("request id not reported within the polling bound");
        return SubmissionPollAction.PollAgain;
    }

    /// <summary>The POST finished. <paramref name="responseRequestId"/> = X-Covenant-Request-Id (or the error body's
    /// request_id). Subscribe only if polling had not already found the id; then polling stops.</summary>
    public SubmissionPollAction OnSendCompleted(string? responseRequestId)
    {
        if (RequestId is null && !string.IsNullOrWhiteSpace(responseRequestId))
        {
            RequestId = responseRequestId;
            Source = "response";
            StopWith("request finished");
            return SubmissionPollAction.Subscribe;
        }
        StopWith(RequestId is null ? "request finished without a request id" : "request finished");
        return SubmissionPollAction.Stop;
    }

    private SubmissionPollAction StopWith(string reason)
    {
        if (!Stopped) { Stopped = true; StopReason = reason; }
        return SubmissionPollAction.Stop;
    }
}
