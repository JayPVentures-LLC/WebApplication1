using System.Text.Json;

namespace JPVOS.Services.Outbound;

public sealed class JsonlOutboundReceiptStore : IOutboundReceiptStore
{
    private readonly string _path;
    private readonly string _eventsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public JsonlOutboundReceiptStore(string path)
    {
        _path = path;
        _eventsPath = path + ".events";
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    public async Task SaveAsync(OutboundMessageReceipt receipt, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { var all = await ReadAllUnsafeAsync(cancellationToken); all[receipt.MessageId] = receipt; await RewriteUnsafeAsync(all.Values, cancellationToken); }
        finally { _gate.Release(); }
    }
    public async Task<OutboundMessageReceipt?> GetAsync(string messageId, CancellationToken cancellationToken) { await _gate.WaitAsync(cancellationToken); try { return (await ReadAllUnsafeAsync(cancellationToken)).GetValueOrDefault(messageId); } finally { _gate.Release(); } }
    public async Task<OutboundMessageReceipt?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) { await _gate.WaitAsync(cancellationToken); try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId); } finally { _gate.Release(); } }
    public async Task<OutboundMessageReceipt?> FindLatestForPrincipalAsync(string principalId, CancellationToken cancellationToken) { await _gate.WaitAsync(cancellationToken); try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.Where(x => x.TargetPrincipalId == principalId).OrderByDescending(x => x.AdmittedAtUtc).FirstOrDefault(); } finally { _gate.Release(); } }
    public async Task<OutboundMessageReceipt?> FindByAcknowledgmentCodeAsync(string principalId, string acknowledgmentCode, CancellationToken cancellationToken) { await _gate.WaitAsync(cancellationToken); try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.FirstOrDefault(x => x.TargetPrincipalId == principalId && string.Equals(x.AcknowledgmentCode, acknowledgmentCode, StringComparison.OrdinalIgnoreCase)); } finally { _gate.Release(); } }

    public async Task<bool> TryMarkProviderEventProcessedAsync(string providerEventId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerEventId)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = File.Exists(_eventsPath) ? new HashSet<string>((await File.ReadAllLinesAsync(_eventsPath, cancellationToken)).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            if (!existing.Add(providerEventId)) return false;
            var temp = _eventsPath + ".tmp";
            await File.WriteAllLinesAsync(temp, existing.OrderBy(x => x), cancellationToken);
            File.Move(temp, _eventsPath, true);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ProviderStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllUnsafeAsync(cancellationToken);
            var receipt = all.Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId);
            if (receipt is null) return new ProviderStatusApplyResult(ProviderStatusApplyDisposition.NotFound);
            var processed = new HashSet<string>(receipt.ProcessedProviderEventIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (!processed.Add(providerEventId)) return new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Duplicate, receipt);
            if (receipt.State == OutboundMessageState.Acknowledged ||
                (receipt.State == OutboundMessageState.Delivered && nextState == OutboundMessageState.Failed) ||
                (Rank(nextState) < Rank(receipt.State) && nextState != OutboundMessageState.Failed)) return new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Ignored, receipt);
            var updated = receipt with { State = nextState, SentAtUtc = nextState == OutboundMessageState.Sent && receipt.SentAtUtc is null ? now : receipt.SentAtUtc, DeliveredAtUtc = nextState == OutboundMessageState.Delivered ? now : receipt.DeliveredAtUtc, FailedAtUtc = nextState == OutboundMessageState.Failed ? now : receipt.FailedAtUtc, ProcessedProviderEventIds = processed.OrderBy(x => x).ToArray() };
            all[updated.MessageId] = updated;
            await RewriteUnsafeAsync(all.Values, cancellationToken);
            return new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Updated, updated);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, OutboundMessageReceipt>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, OutboundMessageReceipt>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;
        foreach (var line in (await File.ReadAllLinesAsync(_path, cancellationToken)).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var receipt = JsonSerializer.Deserialize<OutboundMessageReceipt>(line, _json);
            if (receipt is not null) result[receipt.MessageId] = receipt;
        }
        return result;
    }
    private async Task RewriteUnsafeAsync(IEnumerable<OutboundMessageReceipt> receipts, CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp";
        await File.WriteAllLinesAsync(temp, receipts.OrderBy(x => x.AdmittedAtUtc).Select(x => JsonSerializer.Serialize(x, _json)), cancellationToken);
        File.Move(temp, _path, true);
    }
    private static int Rank(OutboundMessageState state) => state switch { OutboundMessageState.Queued => 0, OutboundMessageState.Sent => 1, OutboundMessageState.Delivered => 2, OutboundMessageState.Failed => 3, OutboundMessageState.Acknowledged => 4, _ => 0 };
}
