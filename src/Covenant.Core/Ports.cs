namespace Covenant.Core;

/// <summary>Sink for audit entries: append-only, tamper-evident, off the hot path —
/// <see cref="EnqueueAsync"/> hands off and returns; it must never block the request on durable I/O.</summary>
public interface IAuditSink
{
    ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
