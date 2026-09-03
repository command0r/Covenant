using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Covenant.Core;
using Microsoft.Extensions.Hosting;

namespace Covenant.Host;

/// <summary>Off-hot-path audit sink: hash-chains entries to an append-only file (tamper-evident).
/// First slice; WORM store is the audit-store ADR (deploy/CLAUDE.md).</summary>
public sealed class FileAuditSink(string path) : IAuditSink, IHostedService
{
    private readonly Channel<AuditEntry> _channel =
        Channel.CreateUnbounded<AuditEntry>(new UnboundedChannelOptions { SingleReader = true });

    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private Task? _drain;
    private string _previousHash = AuditChain.GenesisHash;

    public ValueTask EnqueueAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        => _channel.Writer.WriteAsync(entry, cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Resume the chain across restarts: a fresh genesis mid-file would (correctly) fail verification.
        if (File.Exists(path))
        {
            var last = File.ReadLines(path).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
            var parts = last?.Split(AuditChain.Separator, 3);
            if (parts is { Length: 3 }) _previousHash = parts[1];
        }

        _drain = Task.Run(DrainAsync, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.Complete();
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
                await File.AppendAllTextAsync(path,
                    $"{_previousHash}{AuditChain.Separator}{entryHash}{AuditChain.Separator}{content}{Environment.NewLine}");
                _previousHash = entryHash;
            }
            finally
            {
                _fileLock.Release();
            }
        }
    }

    /// <summary>Archives the current log (rename, never delete — evidence retires, it doesn't die) and
    /// restarts the chain at genesis. Returns the archive path, or null if nothing to archive.</summary>
    public async Task<string?> RotateAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            if (!File.Exists(path)) return null;
            var archive = $"{path}.{DateTime.UtcNow:yyyyMMdd-HHmmss}.archived";
            File.Move(path, archive);
            _previousHash = AuditChain.GenesisHash;
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
