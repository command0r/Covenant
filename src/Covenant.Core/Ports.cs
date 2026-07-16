namespace Covenant.Core;

/// <summary>
/// Sink for audit entries. Implementations must persist append-only and tamper-evidently, and must
/// do so off the hot path — <see cref="EnqueueAsync"/> hands the entry off and returns immediately,
/// it must never block the request on durable I/O.
/// </summary>
public interface IAuditSink
{
    ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
