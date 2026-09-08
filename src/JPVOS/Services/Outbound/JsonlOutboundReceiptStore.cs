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
        try
        {
            var all = await ReadAllUnsafeAsync(cancellationToken);
            all[receipt.MessageId] = receipt;
            await RewriteUnsafeAsync(all.Values.OrderBy(x => x.AdmittedAtUtc), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<OutboundMessageReceipt?> GetAsync(string messageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllUnsafeAsync(cancellationToken)).GetValueOrDefault(messageId); }
        finally { _gate.Release(); }
    }

    public async Task<OutboundMessageReceipt?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId); }
        finally { _gate.Release(); }
    }

    public async Task<OutboundMessageReceipt?> FindLatestForPrincipalAsync(string principalId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAllUnsafeAsync(cancellationToken)).Values
                .Where(x => string.Equals(x.TargetPrincipalId, principalId, StringComparison.Ordinal))
                .OrderByDescending(x => x.AdmittedAtUtc)
                .FirstOrDefault();
        }
        finally { _gate.Release(); }
    }

    public async Task<OutboundMessageReceipt?> FindByAcknowledgmentCodeAsync(string principalId, string acknowledgmentCode, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAllUnsafeAsync(cancellationToken)).Values.FirstOrDefault(x =>
                string.Equals(x.TargetPrincipalId, principalId, StringComparison.Ordinal) &&
                string.Equals(x.AcknowledgmentCode, acknowledgmentCode, StringComparison.OrdinalIgnoreCase));
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryMarkProviderEventProcessedAsync(string providerEventId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerEventId)) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = File.Exists(_eventsPath)
                ? new HashSet<string>((await File.ReadAllLinesAsync(_eventsPath, cancellationToken)).Where(line => !string.IsNullOrWhiteSpace(line)), StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            if (!existing.Add(providerEventId)) return false;
            await File.WriteAllLinesAsync(_eventsPath, existing.OrderBy(x => x, StringComparer.Ordinal), cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, OutboundMessageReceipt>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, OutboundMessageReceipt>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;
        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var receipt = JsonSerializer.Deserialize<OutboundMessageReceipt>(line, _json);
            if (receipt is not null) result[receipt.MessageId] = receipt;
        }
        return result;
    }

    private async Task RewriteUnsafeAsync(IEnumerable<OutboundMessageReceipt> receipts, CancellationToken cancellationToken)
    {
        var lines = receipts.Select(r => JsonSerializer.Serialize(r, _json)).ToArray();
        await File.WriteAllLinesAsync(_path, lines, cancellationToken);
    }
}
