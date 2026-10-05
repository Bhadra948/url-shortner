using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shortener.Api.Orchestration;

public sealed class AuditTrail
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<AuditEvent>> _events = new();
    private long _sequence;
    private string _lastHash = string.Empty;

    public AuditTrail(IWebHostEnvironment environment) : this(environment.ContentRootPath)
    {
    }

    public AuditTrail(string contentRootPath)
    {
        var directory = Path.Combine(contentRootPath, "data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "audit.jsonl");

        if (!File.Exists(_path))
        {
            return;
        }

        foreach (var line in File.ReadLines(_path))
        {
            try
            {
                var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line);
                if (auditEvent is null)
                {
                    continue;
                }

                _sequence = Math.Max(_sequence, auditEvent.Sequence);
                _lastHash = auditEvent.Hash;
                _events.GetOrAdd(auditEvent.RunId, _ => new ConcurrentQueue<AuditEvent>()).Enqueue(auditEvent);
            }
            catch (JsonException)
            {
                break;
            }
        }
    }

    public async Task<AuditEvent> AppendAsync(
        Guid runId,
        int planVersion,
        string actor,
        string action,
        string? stageId,
        string detail,
        CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var sequence = _sequence + 1;
            var atUtc = DateTimeOffset.UtcNow;
            var previousHash = _lastHash;
            var hash = ComputeHash(sequence, atUtc, runId, planVersion, actor, action, stageId, detail, previousHash);
            var auditEvent = new AuditEvent(
                sequence, atUtc, runId, planVersion, actor, action, stageId, detail, previousHash, hash);
            var line = JsonSerializer.SerializeToUtf8Bytes(auditEvent);

            await using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await stream.WriteAsync(line, cancellationToken);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);

            _sequence = sequence;
            _lastHash = hash;
            _events.GetOrAdd(runId, _ => new ConcurrentQueue<AuditEvent>()).Enqueue(auditEvent);
            return auditEvent;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public IReadOnlyList<AuditEvent> GetRunEvents(Guid runId) =>
        _events.TryGetValue(runId, out var events) ? events.ToArray() : [];

    public bool VerifyHashChain()
    {
        if (!File.Exists(_path))
        {
            return true;
        }

        var previousHash = string.Empty;
        long expectedSequence = 0;
        foreach (var line in File.ReadLines(_path))
        {
            AuditEvent? auditEvent;
            try
            {
                auditEvent = JsonSerializer.Deserialize<AuditEvent>(line);
            }
            catch (JsonException)
            {
                return false;
            }

            if (auditEvent is null
                || auditEvent.Sequence != ++expectedSequence
                || auditEvent.PreviousHash != previousHash
                || auditEvent.Hash != ComputeHash(
                    auditEvent.Sequence, auditEvent.AtUtc, auditEvent.RunId, auditEvent.PlanVersion,
                    auditEvent.Actor, auditEvent.Action, auditEvent.StageId, auditEvent.Detail,
                    auditEvent.PreviousHash))
            {
                return false;
            }

            previousHash = auditEvent.Hash;
        }

        return true;
    }

    private static string ComputeHash(
        long sequence,
        DateTimeOffset atUtc,
        Guid runId,
        int planVersion,
        string actor,
        string action,
        string? stageId,
        string detail,
        string previousHash)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sequence,
            atUtc,
            runId,
            planVersion,
            actor,
            action,
            stageId,
            detail,
            previousHash
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}