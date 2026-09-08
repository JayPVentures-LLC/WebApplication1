namespace JPVOS.Services.Outbound;

public sealed class OutboundTransportService
{
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly ISmsTransport _transport;
    private readonly IOutboundReceiptStore _receipts;
    private readonly IGitHubExactHeadReader _github;

    public OutboundTransportService(IPrincipalSmsBindingResolver bindings, ISmsTransport transport, IOutboundReceiptStore receipts, IGitHubExactHeadReader github)
    {
        _bindings = bindings;
        _transport = transport;
        _receipts = receipts;
        _github = github;
    }

    public async Task<OutboundSendResult> SendGithubReviewAsync(GithubExactHeadReviewRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.TargetPrincipalId, PrincipalSmsBindingResolver.ConnorPrincipalId, StringComparison.Ordinal))
            return OutboundSendResult.Denied("principal_mismatch");
        if (string.IsNullOrWhiteSpace(request.RequestingAuthority) || !request.RequestingAuthority.StartsWith("founder:", StringComparison.Ordinal))
            return OutboundSendResult.Denied("authority_denied");
        if (string.IsNullOrWhiteSpace(request.RepositoryFullName) || request.PullRequestNumber <= 0 || string.IsNullOrWhiteSpace(request.ExactHeadSha))
            return OutboundSendResult.Denied("purpose_not_supported");

        var liveHead = await _github.GetHeadShaAsync(request.RepositoryFullName, request.PullRequestNumber, cancellationToken);
        if (!string.Equals(liveHead, request.ExactHeadSha, StringComparison.OrdinalIgnoreCase))
            return OutboundSendResult.Denied("exact_head_stale");

        var binding = _bindings.Resolve(request.TargetPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success) return OutboundSendResult.Denied(binding.ErrorCode ?? "principal_binding_unverified");

        var messageId = Guid.NewGuid().ToString("N");
        var acknowledgment = ReviewAcknowledgmentToken.Create(messageId);
        var body = $"JPV governance review required: {request.RepositoryFullName}#{request.PullRequestNumber} at {request.ExactHeadSha}. Review: {request.PullRequestUrl} Reply ACK {acknowledgment.Code} to acknowledge this exact request. SMS acknowledgment does not approve the PR.";
        var send = await _transport.SendAsync(new SmsSendCommand(binding.Binding!.EndpointE164, body, messageId), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var receipt = new OutboundMessageReceipt(
            messageId,
            request.TargetPrincipalId,
            "github_exact_head_review_request",
            request.RepositoryFullName,
            request.PullRequestNumber,
            request.ExactHeadSha,
            _transport.GetType().Name,
            send.ProviderMessageId,
            send.State,
            now,
            send.Success ? now : null,
            FailedAtUtc: send.Success ? null : now,
            AcknowledgmentCode: acknowledgment.Code);
        await _receipts.SaveAsync(receipt, cancellationToken);

        return send.Success ? OutboundSendResult.Ok(messageId) : OutboundSendResult.Denied(send.ErrorCode ?? "provider_rejected");
    }
}
