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
        try
        {
            var all = await ReadAllUnsafeAsync(cancellationToken);
            all[message.MessageId] = message;
            await File.WriteAllLinesAsync(_path, all.Values.OrderBy(x => x.CreatedAtUtc).Select(x => JsonSerializer.Serialize(x, _json)), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadAllUnsafeAsync(cancellationToken)).Values
                .Where(x => string.Equals(x.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderBy(x => x.CreatedAtUtc)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllUnsafeAsync(cancellationToken)).Values.Any(x => string.Equals(x.ProviderMessageId, providerMessageId, StringComparison.Ordinal)); }
        finally { _gate.Release(); }
    }

    private async Task<Dictionary<string, ConversationMessage>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ConversationMessage>(StringComparer.Ordinal);
        if (!File.Exists(_path)) return result;
        var lines = await File.ReadAllLinesAsync(_path, cancellationToken);
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(line, _json);
            if (message is not null) result[message.MessageId] = message;
        }
        return result;
    }
}
