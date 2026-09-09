using System.Text.Json;

namespace JPVOS.Services.Outbound;

public sealed class JsonlDirectConversationStore : IDirectConversationStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public JsonlDirectConversationStore(string path)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    public async Task SaveAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { var all = await ReadAllUnsafeAsync(cancellationToken); all[message.MessageId] = message; await RewriteUnsafeAsync(all.Values, cancellationToken); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.Any(x => x.ProviderMessageId == providerMessageId); }
        finally { _gate.Release(); }
    }

    public async Task<bool> TryInsertInboundIfNewAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllUnsafeAsync(cancellationToken);
            if (all.Values.Any(x => x.ProviderMessageId == message.ProviderMessageId)) return false;
            all[message.MessageId] = message;
            await RewriteUnsafeAsync(all.Values, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<ConversationStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllUnsafeAsync(cancellationToken);
            var message = all.Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId);
            if (message is null) return new ConversationStatusApplyResult(ProviderStatusApplyDisposition.NotFound);
            var processed = new HashSet<string>(message.ProcessedProviderEventIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (!processed.Add(providerEventId)) return new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Duplicate, message);
            var current = message.DeliveryState ?? OutboundMessageState.Queued;
if (current == OutboundMessageState.Acknowledged ||
    (current == OutboundMessageState.Delivered && nextState == OutboundMessageState.Failed) ||
    (Rank(nextState) < Rank(current) && nextState != OutboundMessageState.Failed))
    return new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Ignored, message);
            var updated = message with { DeliveryState = nextState, ProcessedProviderEventIds = processed.OrderBy(x => x).ToArray() };
            all[updated.MessageId] = updated;
            await RewriteUnsafeAsync(all.Values, cancellationToken);
            return new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Updated, updated);
        }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, ConversationMessage>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ConversationMessage>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;
        foreach (var line in (await File.ReadAllLinesAsync(_path, cancellationToken)).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(line, _json);
            if (message is not null) result[message.MessageId] = message;
        }
        return result;
    }

    private async Task RewriteUnsafeAsync(IEnumerable<ConversationMessage> messages, CancellationToken cancellationToken)
    {
        var temp = _path + ".tmp";
        await File.WriteAllLinesAsync(temp, messages.OrderBy(x => x.CreatedAtUtc).Select(x => JsonSerializer.Serialize(x, _json)), cancellationToken);
        File.Move(temp, _path, true);
    }

    private static int Rank(OutboundMessageState state) => state switch { OutboundMessageState.Queued => 0, OutboundMessageState.Sent => 1, OutboundMessageState.Delivered => 2, OutboundMessageState.Failed => 3, OutboundMessageState.Acknowledged => 4, _ => 0 };
}
