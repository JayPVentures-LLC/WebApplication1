using System.Security.Cryptography;
using System.Text;

namespace JPVOS.Services.Outbound;

public enum ConversationDirection
{
    Outbound,
    Inbound
}

public sealed record ConversationMessage(
    string MessageId,
    string ConversationId,
    string PrincipalId,
    ConversationDirection Direction,
    string Body,
    DateTimeOffset CreatedAtUtc,
    string? ProviderMessageId = null,
    OutboundMessageState? DeliveryState = null);

public sealed record DirectConversationSendRequest(string TargetPrincipalId, string RequestingAuthority, string Body);
public sealed record InboundDirectMessage(string FromE164, string ProviderMessageId, string Body);
public sealed record DirectConversationResult(bool Success, string? MessageId, string? ErrorCode)
{
    public static DirectConversationResult Ok(string messageId) => new(true, messageId, null);
    public static DirectConversationResult Denied(string errorCode) => new(false, null, errorCode);
}

public interface IDirectConversationStore
{
    Task SaveAsync(ConversationMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken);
    Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken);
}

public sealed class InMemoryDirectConversationStore : IDirectConversationStore
{
    private readonly Dictionary<string, ConversationMessage> _messages = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task SaveAsync(ConversationMessage message, CancellationToken cancellationToken)
    {
        lock (_gate) _messages[message.MessageId] = message;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationMessage>> GetConversationAsync(string conversationId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ConversationMessage> result = _messages.Values
                .Where(x => string.Equals(x.ConversationId, conversationId, StringComparison.Ordinal))
                .OrderBy(x => x.CreatedAtUtc)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<bool> ContainsProviderMessageAsync(string providerMessageId, CancellationToken cancellationToken)
    {
        lock (_gate) return Task.FromResult(_messages.Values.Any(x => string.Equals(x.ProviderMessageId, providerMessageId, StringComparison.Ordinal)));
    }
}

public sealed class DirectConversationService
{
    public const string ConnorConversationId = "direct:github:jaypventuresllc-admin";
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly ISmsTransport _transport;
    private readonly IDirectConversationStore _store;

    public DirectConversationService(IPrincipalSmsBindingResolver bindings, ISmsTransport transport, IDirectConversationStore store)
    {
        _bindings = bindings;
        _transport = transport;
        _store = store;
    }

    public async Task<DirectConversationResult> SendAsync(DirectConversationSendRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TargetPrincipalId, PrincipalSmsBindingResolver.ConnorPrincipalId, StringComparison.Ordinal))
            return DirectConversationResult.Denied("principal_mismatch");
        if (string.IsNullOrWhiteSpace(request.RequestingAuthority) || !request.RequestingAuthority.StartsWith("founder:", StringComparison.Ordinal))
            return DirectConversationResult.Denied("authority_denied");
        if (string.IsNullOrWhiteSpace(request.Body)) return DirectConversationResult.Denied("message_empty");

        var binding = _bindings.Resolve(request.TargetPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success) return DirectConversationResult.Denied(binding.ErrorCode ?? "principal_binding_unverified");

        var messageId = Guid.NewGuid().ToString("N");
        var send = await _transport.SendAsync(new SmsSendCommand(binding.Binding!.EndpointE164, request.Body.Trim(), messageId), cancellationToken);
        if (!send.Success) return DirectConversationResult.Denied(send.ErrorCode ?? "provider_rejected");

        await _store.SaveAsync(new ConversationMessage(
            messageId,
            ConnorConversationId,
            request.TargetPrincipalId,
            ConversationDirection.Outbound,
            request.Body.Trim(),
            DateTimeOffset.UtcNow,
            send.ProviderMessageId,
            send.State), cancellationToken);
        return DirectConversationResult.Ok(messageId);
    }

    public async Task<DirectConversationResult> RecordInboundAsync(InboundDirectMessage inbound, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inbound.ProviderMessageId) || string.IsNullOrWhiteSpace(inbound.Body))
            return DirectConversationResult.Denied("message_invalid");
        if (await _store.ContainsProviderMessageAsync(inbound.ProviderMessageId, cancellationToken))
            return DirectConversationResult.Ok(inbound.ProviderMessageId);

        var binding = _bindings.Resolve(PrincipalSmsBindingResolver.ConnorPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success || !string.Equals(binding.Binding!.EndpointE164, inbound.FromE164, StringComparison.Ordinal))
            return DirectConversationResult.Denied("principal_mismatch");

        var messageId = Guid.NewGuid().ToString("N");
        await _store.SaveAsync(new ConversationMessage(
            messageId,
            ConnorConversationId,
            PrincipalSmsBindingResolver.ConnorPrincipalId,
            ConversationDirection.Inbound,
            inbound.Body.Trim(),
            DateTimeOffset.UtcNow,
            inbound.ProviderMessageId,
            OutboundMessageState.Delivered), cancellationToken);
        return DirectConversationResult.Ok(messageId);
    }

    public Task<bool> ParseReviewAcknowledgmentAsync(string body, ReviewAcknowledgmentToken token, CancellationToken cancellationToken)
    {
        var normalized = body.Trim();
        return Task.FromResult(string.Equals(normalized, $"ACK {token.Code}", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record ReviewAcknowledgmentToken(string MessageId, string Code)
{
    public static ReviewAcknowledgmentToken Create(string messageId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        var code = Convert.ToHexString(hash)[..8];
        return new ReviewAcknowledgmentToken(messageId, code);
    }
}
