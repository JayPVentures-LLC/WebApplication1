using System.Security.Cryptography;
using System.Text;

namespace JPVOS.Services.Outbound;

public enum ConversationDirection { Outbound, Inbound }

public sealed record ConversationMessage(
    string MessageId, string ConversationId, string PrincipalId, ConversationDirection Direction, string Body,
    DateTimeOffset CreatedAtUtc, string? ProviderMessageId = null, OutboundMessageState? DeliveryState = null,
    IReadOnlyList<string>? ProcessedProviderEventIds = null);

public sealed record DirectConversationSendRequest(string TargetPrincipalId, string RequestingAuthority, string Body);
public sealed record InboundDirectMessage(string FromE164, string ProviderMessageId, string Body);
public sealed record DirectConversationResult(bool Success, string? MessageId, string? ErrorCode)
{
    public static DirectConversationResult Ok(string messageId) => new(true, messageId, null);
    public static DirectConversationResult Denied(string errorCode) => new(false, null, errorCode);
}
public sealed record ConversationStatusApplyResult(ProviderStatusApplyDisposition Disposition, ConversationMessage? Message = null);

public interface IDirectConversationStore
{
    Task SaveAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken);
    Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken);
    Task<bool> TryInsertInboundIfNewAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<ConversationStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class InMemoryDirectConversationStore : IDirectConversationStore
{
    private readonly Dictionary<string, ConversationMessage> _messages = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public Task SaveAsync(ConversationMessage message, CancellationToken cancellationToken) { lock (_gate) _messages[message.MessageId] = message; return Task.CompletedTask; }
    public Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ConversationMessage>>(_messages.Values.Where(x => x.ConversationId == conversationId).OrderBy(x => x.CreatedAtUtc).ToArray());
    }
    public Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_messages.Values.Any(x => x.ProviderMessageId == providerMessageId)); }
    public Task<bool> TryInsertInboundIfNewAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_messages.Values.Any(x => x.ProviderMessageId == message.ProviderMessageId)) return Task.FromResult(false);
            _messages[message.MessageId] = message;
            return Task.FromResult(true);
        }
    }
    public Task<ConversationStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var message = _messages.Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId);
            if (message is null) return Task.FromResult(new ConversationStatusApplyResult(ProviderStatusApplyDisposition.NotFound));
            var processed = new HashSet<string>(message.ProcessedProviderEventIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (!processed.Add(providerEventId)) return Task.FromResult(new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Duplicate, message));
            var current = message.DeliveryState ?? OutboundMessageState.Queued;
            if (current == OutboundMessageState.Acknowledged ||
                (current == OutboundMessageState.Delivered && nextState == OutboundMessageState.Failed) ||
                (Rank(nextState) < Rank(current) && nextState != OutboundMessageState.Failed)) return Task.FromResult(new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Ignored, message));
            var updated = message with { DeliveryState = nextState, ProcessedProviderEventIds = processed.OrderBy(x => x).ToArray() };
            _messages[updated.MessageId] = updated;
            return Task.FromResult(new ConversationStatusApplyResult(ProviderStatusApplyDisposition.Updated, updated));
        }
    }
    private static int Rank(OutboundMessageState state) => state switch { OutboundMessageState.Queued => 0, OutboundMessageState.Sent => 1, OutboundMessageState.Delivered => 2, OutboundMessageState.Failed => 3, OutboundMessageState.Acknowledged => 4, _ => 0 };
}

public sealed class DirectConversationService
{
    public const string ConnorConversationId = "direct:github:jaypventuresllc-admin";
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly ISmsTransport _transport;
    private readonly IDirectConversationStore _store;
    public DirectConversationService(IPrincipalSmsBindingResolver bindings, ISmsTransport transport, IDirectConversationStore store) { _bindings = bindings; _transport = transport; _store = store; }

    public async Task<DirectConversationResult> SendAsync(DirectConversationSendRequest request, CancellationToken cancellationToken)
    {
        if (request.TargetPrincipalId != PrincipalSmsBindingResolver.ConnorPrincipalId) return DirectConversationResult.Denied("principal_mismatch");
        if (string.IsNullOrWhiteSpace(request.RequestingAuthority) || !request.RequestingAuthority.StartsWith("founder:", StringComparison.Ordinal)) return DirectConversationResult.Denied("authority_denied");
        if (string.IsNullOrWhiteSpace(request.Body)) return DirectConversationResult.Denied("message_empty");
        var binding = _bindings.Resolve(request.TargetPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success) return DirectConversationResult.Denied(binding.ErrorCode ?? "principal_binding_unverified");
        var messageId = Guid.NewGuid().ToString("N");
        var send = await _transport.SendAsync(new SmsSendCommand(binding.Binding!.EndpointE164, request.Body.Trim(), messageId), cancellationToken);
        if (!send.Success) return DirectConversationResult.Denied(send.ErrorCode ?? "provider_rejected");
        await _store.SaveAsync(new ConversationMessage(messageId, ConnorConversationId, request.TargetPrincipalId, ConversationDirection.Outbound, request.Body.Trim(), DateTimeOffset.UtcNow, send.ProviderMessageId, send.State), cancellationToken);
        return DirectConversationResult.Ok(messageId);
    }

    public async Task<DirectConversationResult> RecordInboundAsync(InboundDirectMessage inbound, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inbound.ProviderMessageId) || string.IsNullOrWhiteSpace(inbound.Body)) return DirectConversationResult.Denied("message_invalid");
        var binding = _bindings.Resolve(PrincipalSmsBindingResolver.ConnorPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success || binding.Binding!.EndpointE164 != inbound.FromE164) return DirectConversationResult.Denied("principal_mismatch");
        var messageId = Guid.NewGuid().ToString("N");
        var inserted = await _store.TryInsertInboundIfNewAsync(new ConversationMessage(messageId, ConnorConversationId, PrincipalSmsBindingResolver.ConnorPrincipalId, ConversationDirection.Inbound, inbound.Body.Trim(), DateTimeOffset.UtcNow, inbound.ProviderMessageId, OutboundMessageState.Delivered), cancellationToken);
        return DirectConversationResult.Ok(inserted ? messageId : inbound.ProviderMessageId);
    }

    public Task<bool> ParseReviewAcknowledgmentAsync(string body, ReviewAcknowledgmentToken token, CancellationToken cancellationToken) => Task.FromResult(string.Equals(body.Trim(), $"ACK {token.Code}", StringComparison.OrdinalIgnoreCase));
}

public sealed record ReviewAcknowledgmentToken(string MessageId, string Code)
{
    public static ReviewAcknowledgmentToken Create(string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return new ReviewAcknowledgmentToken(messageId, Convert.ToHexString(hash)[..32]);
    }
}
