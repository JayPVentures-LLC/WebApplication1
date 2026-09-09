namespace JPVOS.Services.Outbound;

public sealed record ReviewAcknowledgmentApplyResult(bool Matched, string? MessageId, string? ErrorCode)
{
    public static ReviewAcknowledgmentApplyResult NoMatch() => new(false, null, null);
    public static ReviewAcknowledgmentApplyResult Denied(string errorCode) => new(false, null, errorCode);
    public static ReviewAcknowledgmentApplyResult Applied(string messageId) => new(true, messageId, null);
}

public sealed class ReviewAcknowledgmentService
{
    private readonly IPrincipalSmsBindingResolver _bindings;
    private readonly IOutboundReceiptStore _receipts;

    public ReviewAcknowledgmentService(IPrincipalSmsBindingResolver bindings, IOutboundReceiptStore receipts)
    {
        _bindings = bindings;
        _receipts = receipts;
    }

    public async Task<ReviewAcknowledgmentApplyResult> ApplyAsync(string fromE164, string providerMessageId, string body, CancellationToken cancellationToken)
    {
        var binding = _bindings.Resolve(PrincipalSmsBindingResolver.ConnorPrincipalId, DateTimeOffset.UtcNow);
        if (!binding.Success || !string.Equals(binding.Binding!.EndpointE164, fromE164, StringComparison.Ordinal))
            return ReviewAcknowledgmentApplyResult.Denied("acknowledgment_not_attributable");

        var parts = body.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !string.Equals(parts[0], "ACK", StringComparison.OrdinalIgnoreCase))
            return ReviewAcknowledgmentApplyResult.NoMatch();

        var receipt = await _receipts.ApplyAcknowledgmentAsync(
            PrincipalSmsBindingResolver.ConnorPrincipalId,
            parts[1],
            providerMessageId,
            DateTimeOffset.UtcNow,
            cancellationToken);
        return receipt is null ? ReviewAcknowledgmentApplyResult.NoMatch() : ReviewAcknowledgmentApplyResult.Applied(receipt.MessageId);
    }
}
