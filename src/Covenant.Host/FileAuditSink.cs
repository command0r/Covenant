using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Covenant.Core;
using Microsoft.Extensions.Hosting;

namespace Covenant.Host;

/// <summary>Off-hot-path audit sink: hash-chains entries to an append-only file (tamper-evident).
/// With anchoring configured (ADR-0007), every Nth entry's head hash is appended to a separate anchor
/// file — its value is placement on an independent storage/attacker domain.</summary>
public sealed class FileAuditSink(string path, string? anchorPath = null, int anchorEvery = 0) : IAuditSink, IHostedService
{
    private readonly Channel<AuditEntry> _channel =
        Channel.CreateUnbounded<AuditEntry>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private Task? _drain;
    private string _previousHash = AuditChain.GenesisHash;
    private long _count;

    public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(entry, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resume the chain across restarts: a fresh genesis mid-file would (correctly) fail verification.
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _count++;
                var parts = line.Split(AuditChain.Separator, 3);
                if (parts.Length == 3) _previousHash = parts[1];
            }
        }

        _drain = Task.Run(DrainAsync, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // TryComplete, not Complete: StopAsync can run more than once across host disposal
        // (Complete throws ChannelClosedException on an already-completed channel). Idempotent.
        _channel.Writer.TryComplete();
        if (_drain is not null) await _drain;
    }

    private async Task DrainAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync())
        {
            await _fileLock.WaitAsync();
            try
            {
                var content = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEntry);
                var entryHash = AuditChain.Hash(_previousHash, content);
                try
                {
                    await File.AppendAllTextAsync(path,
                        $"{_previousHash}{AuditChain.Separator}{entryHash}{AuditChain.Separator}{content}{Environment.NewLine}");
                }
                catch (Exception ex)
                {
                    // Fail-closed: an appliance that cannot write evidence must not keep serving.
                    Environment.FailFast($"covenant: audit log write failed ({ex.Message}) — halting rather than serving unaudited.");
                }
                _previousHash = entryHash;
                _count++;

                if (anchorEvery > 0 && anchorPath is not null && _count % anchorEvery == 0)
                {
                    try
                    {
                        await File.AppendAllTextAsync(anchorPath,
                            $"{_count}{AuditChain.Separator}{entryHash}{AuditChain.Separator}{DateTimeOffset.UtcNow:o}{Environment.NewLine}");
                    }
                    catch (Exception ex)
                    {
                        // A missed anchor only widens exposure by one cadence (verification stays sound);
                        // surface loudly, never let the anchor volume kill the primary drain.
                        Console.Error.WriteLine($"covenant: WARNING — anchor write to '{anchorPath}' failed ({ex.Message}); exposure widened by one cadence.");
                    }
                }
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>Archives log AND anchor file together (rename, never delete); fresh chain, fresh anchors.</summary>
    public async Task<string?> RotateAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string? archive = null;
            if (File.Exists(path))
            {
                archive = $"{path}.{stamp}.archived";
                File.Move(path, archive);
            }
            // Archive anchors even when the log is absent (out-of-band deletion): otherwise stale
            // anchors would fail verification forever with no sanctioned recovery path.
            if (anchorPath is not null && File.Exists(anchorPath))
                File.Move(anchorPath, $"{anchorPath}.{stamp}.archived");
            _previousHash = AuditChain.GenesisHash;
            _count = 0;
            return archive;
        }
        finally
        {
            _fileLock.Release();
        }
    }
}

[JsonSourceGenerationOptions(Converters = [typeof(JsonStringEnumConverter<DataClassification>), typeof(JsonStringEnumConverter<PolicyEffect>)])]
[JsonSerializable(typeof(AuditEntry))]
public partial class AuditJsonContext : JsonSerializerContext;
