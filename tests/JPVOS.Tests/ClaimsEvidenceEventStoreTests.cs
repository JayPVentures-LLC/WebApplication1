using System.Text.Json;
using JPVOS.Services.ClaimsEvidence;
using Microsoft.AspNetCore.DataProtection;

namespace JPVOS.Tests;

public sealed class ClaimsEvidenceEventStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"jpv-claims-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateCase_IsAtomicAndReplayable()
    {
        var store = CreateStore();
        var caseId = $"jpv_case_{Guid.NewGuid():N}";
        var resultJson = JsonSerializer.Serialize(new { caseId, trackingCredential = "secret-token" });
        var events = new[] { Event(caseId, ClaimsEvidenceEventType.CaseReceived) };

        var first = await store.CreateCaseAtomicallyAsync("create-case", "idem-key-0001", "hash-a", caseId, "verifier", events, resultJson, default);
        var replay = await store.CreateCaseAtomicallyAsync("create-case", "idem-key-0001", "hash-a", $"jpv_case_{Guid.NewGuid():N}", "different", events, "different", default);
        var stream = await store.ReadStreamAsync(caseId, default);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(resultJson, replay.ResultJson);
        Assert.Single(stream);
        Assert.Equal(1, stream[0].Sequence);
    }

    [Fact]
    public async Task CreateCase_RejectsIdempotencyKeyReuseWithDifferentContent()
    {
        var store = CreateStore();
        var caseId = $"jpv_case_{Guid.NewGuid():N}";
        var events = new[] { Event(caseId, ClaimsEvidenceEventType.CaseReceived) };
        await store.CreateCaseAtomicallyAsync("create-case", "idem-key-0002", "hash-a", caseId, "verifier", events, "{}", default);

        await Assert.ThrowsAsync<ClaimsEvidenceIdempotencyConflictException>(() =>
            store.CreateCaseAtomicallyAsync("create-case", "idem-key-0002", "hash-b", $"jpv_case_{Guid.NewGuid():N}", "verifier", events, "{}", default));
    }

    [Fact]
    public async Task Append_ProducesContiguousUniqueSequences()
    {
        var store = CreateStore();
        var caseId = $"jpv_case_{Guid.NewGuid():N}";
        await store.CreateCaseAtomicallyAsync("create-case", "idem-key-0003", "hash", caseId, "verifier", new[] { Event(caseId, ClaimsEvidenceEventType.CaseReceived) }, "{}", default);

        var tasks = Enumerable.Range(0, 8).Select(i => store.AppendAtomicallyAsync(
            $"add-evidence:{caseId}", $"idem-append-{i:D2}", $"hash-{i}", Event(caseId, ClaimsEvidenceEventType.EvidenceAdded), "{}", default));
        await Task.WhenAll(tasks);

        var stream = await store.ReadStreamAsync(caseId, default);
        Assert.Equal(9, stream.Count);
        Assert.Equal(Enumerable.Range(1, 9).Select(i => (long)i), stream.Select(e => e.Sequence));
        Assert.Equal(stream.Count, stream.Select(e => e.EventId).Distinct().Count());
    }

    [Fact]
    public async Task IdempotentResult_IsProtectedAtRest()
    {
        var store = CreateStore();
        var caseId = $"jpv_case_{Guid.NewGuid():N}";
        const string secret = "plaintext-tracking-credential";
        await store.CreateCaseAtomicallyAsync("create-case", "idem-key-0004", "hash", caseId, "verifier", new[] { Event(caseId, ClaimsEvidenceEventType.CaseReceived) }, $"{{\"trackingCredential\":\"{secret}\"}}", default);

        var databaseBytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "claims.db"));
        var databaseText = System.Text.Encoding.UTF8.GetString(databaseBytes);
        Assert.DoesNotContain(secret, databaseText, StringComparison.Ordinal);
    }

    private SqliteClaimsEvidenceEventStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        var provider = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_directory, "keys")));
        return new SqliteClaimsEvidenceEventStore(Path.Combine(_directory, "claims.db"), provider);
    }

    private static ClaimsEvidenceEvent Event(string caseId, ClaimsEvidenceEventType type) =>
        new($"jpv_evt_{Guid.NewGuid():N}", caseId, 0, DateTime.UtcNow, type, "test", null, "restricted", "{}");

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch { }
    }
}
