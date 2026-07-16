using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Covenant.Core;
using Microsoft.Extensions.Hosting;

namespace Covenant.Host;

/// <summary>
/// Off-hot-path audit sink. Stages enqueue to an in-memory channel; a single background reader drains
/// it, hash-chains each entry (EntryHash = SHA256(PreviousHash || content)), and appends one line to an
/// append-only file. Tamper-evidence comes from the chain: altering any past line breaks every hash after it.
///
/// This is the first-slice implementation. Production swaps the file for WORM-capable storage and may
/// anchor the chain externally — see deploy/CLAUDE.md. The chaining logic stays the same.
/// </summary>
public sealed class FileAuditSink(string path) : IAuditSink, IHostedService
{
    private readonly Channel<AuditEntry> _channel =
        Channel.CreateUnbounded<AuditEntry>(new UnboundedChannelOptions { SingleReader = true });

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
            var content = JsonSerializer.Serialize(entry, AuditJsonContext.Default.AuditEntry);
            var entryHash = AuditChain.Hash(_previousHash, content);
            await File.AppendAllTextAsync(path,
                $"{_previousHash}{AuditChain.Separator}{entryHash}{AuditChain.Separator}{content}{Environment.NewLine}");
            _previousHash = entryHash;
        }
    }
}

[JsonSourceGenerationOptions(Converters = [typeof(JsonStringEnumConverter<DataClassification>), typeof(JsonStringEnumConverter<PolicyEffect>)])]
[JsonSerializable(typeof(AuditEntry))]
public partial class AuditJsonContext : JsonSerializerContext;
