namespace JPVOS.Services.Outbound;

public enum OutboundMessageState
{
    Queued,
    Sent,
    Delivered,
    Failed,
    Acknowledged
}

public enum ProviderStatusApplyDisposition
{
    NotFound,
    Duplicate,
    Ignored,
    Updated
}

public sealed record ProviderStatusApplyResult(ProviderStatusApplyDisposition Disposition, OutboundMessageReceipt? Receipt = null);

public sealed record PrincipalSmsBinding(string PrincipalId, string Channel, string EndpointE164, string Version, DateTimeOffset VerifiedAt, bool Revoked, DateTimeOffset? ExpiresAt);

public sealed record PrincipalSmsBindingResult(bool Success, PrincipalSmsBinding? Binding, string? ErrorCode)
{
    public static PrincipalSmsBindingResult Ok(PrincipalSmsBinding binding) => new(true, binding, null);
    public static PrincipalSmsBindingResult Denied(string errorCode) => new(false, null, errorCode);
}

public interface IPrincipalSmsBindingResolver { PrincipalSmsBindingResult Resolve(string principalId, DateTimeOffset now); }

public sealed record GithubExactHeadReviewRequest(string RepositoryFullName, int PullRequestNumber, string ExactHeadSha, string PullRequestUrl, string TargetPrincipalId, string RequestingAuthority);
public sealed record SmsSendCommand(string DestinationE164, string Body, string CorrelationId);
public sealed record SmsSendResult(bool Success, string? ProviderMessageId, OutboundMessageState State, string? ErrorCode)
{
    public static SmsSendResult Accepted(string providerMessageId, OutboundMessageState state) => new(true, providerMessageId, state, null);
    public static SmsSendResult Failed(string errorCode, string? providerMessageId = null) => new(false, providerMessageId, OutboundMessageState.Failed, errorCode);
}
public interface ISmsTransport { Task<SmsSendResult> SendAsync(SmsSendCommand command, CancellationToken cancellationToken); }
public interface IGitHubExactHeadReader { Task<string> GetHeadShaAsync(string repositoryFullName, int pullRequestNumber, CancellationToken cancellationToken); }

public sealed record OutboundMessageReceipt(
    string MessageId, string TargetPrincipalId, string Purpose, string RepositoryFullName, int PullRequestNumber, string ExactHeadSha,
    string ProviderName, string? ProviderMessageId, OutboundMessageState State, DateTimeOffset AdmittedAtUtc,
    DateTimeOffset? SentAtUtc = null, DateTimeOffset? DeliveredAtUtc = null, DateTimeOffset? FailedAtUtc = null,
    DateTimeOffset? AcknowledgedAtUtc = null, string? AcknowledgmentEvidenceType = null, string? AcknowledgmentEvidenceReference = null,
    string? AcknowledgmentCode = null, IReadOnlyList<string>? ProcessedProviderEventIds = null);

public interface IOutboundReceiptStore
{
    Task SaveAsync(OutboundMessageReceipt receipt, CancellationToken cancellationToken);
    Task<OutboundMessageReceipt?> GetAsync(string messageId, CancellationToken cancellationToken);
    Task<OutboundMessageReceipt?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken);
    Task<OutboundMessageReceipt?> FindLatestForPrincipalAsync(string principalId, CancellationToken cancellationToken);
    Task<OutboundMessageReceipt?> FindByAcknowledgmentCodeAsync(string principalId, string acknowledgmentCode, CancellationToken cancellationToken);
    Task<bool> TryMarkProviderEventProcessedAsync(string providerEventId, CancellationToken cancellationToken);
    Task<ProviderStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class InMemoryOutboundReceiptStore : IOutboundReceiptStore
{
    private readonly Dictionary<string, OutboundMessageReceipt> _receipts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _providerEvents = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public Task SaveAsync(OutboundMessageReceipt receipt, CancellationToken cancellationToken) { lock (_gate) _receipts[receipt.MessageId] = receipt; return Task.CompletedTask; }
    public Task<OutboundMessageReceipt?> GetAsync(string messageId, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_receipts.GetValueOrDefault(messageId)); }
    public Task<OutboundMessageReceipt?> FindByProviderMessageIdAsync(string providerMessageId, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_receipts.Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId)); }
    public Task<OutboundMessageReceipt?> FindLatestForPrincipalAsync(string principalId, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_receipts.Values.Where(x => x.TargetPrincipalId == principalId).OrderByDescending(x => x.AdmittedAtUtc).FirstOrDefault()); }
    public Task<OutboundMessageReceipt?> FindByAcknowledgmentCodeAsync(string principalId, string acknowledgmentCode, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_receipts.Values.FirstOrDefault(x => x.TargetPrincipalId == principalId && string.Equals(x.AcknowledgmentCode, acknowledgmentCode, StringComparison.OrdinalIgnoreCase))); }
    public Task<bool> TryMarkProviderEventProcessedAsync(string providerEventId, CancellationToken cancellationToken) { lock (_gate) return Task.FromResult(_providerEvents.Add(providerEventId)); }

    public Task<ProviderStatusApplyResult> ApplyProviderStatusAsync(string providerMessageId, string providerEventId, OutboundMessageState nextState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var receipt = _receipts.Values.FirstOrDefault(x => x.ProviderMessageId == providerMessageId);
            if (receipt is null) return Task.FromResult(new ProviderStatusApplyResult(ProviderStatusApplyDisposition.NotFound));
            var processed = new HashSet<string>(receipt.ProcessedProviderEventIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            if (!processed.Add(providerEventId)) return Task.FromResult(new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Duplicate, receipt));
            if (receipt.State == OutboundMessageState.Acknowledged || (Rank(nextState) < Rank(receipt.State) && nextState != OutboundMessageState.Failed)) return Task.FromResult(new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Ignored, receipt));
            var updated = receipt with { State = nextState, SentAtUtc = nextState == OutboundMessageState.Sent && receipt.SentAtUtc is null ? now : receipt.SentAtUtc, DeliveredAtUtc = nextState == OutboundMessageState.Delivered ? now : receipt.DeliveredAtUtc, FailedAtUtc = nextState == OutboundMessageState.Failed ? now : receipt.FailedAtUtc, ProcessedProviderEventIds = processed.OrderBy(x => x).ToArray() };
            _receipts[updated.MessageId] = updated;
            return Task.FromResult(new ProviderStatusApplyResult(ProviderStatusApplyDisposition.Updated, updated));
        }
    }
    private static int Rank(OutboundMessageState state) => state switch { OutboundMessageState.Queued => 0, OutboundMessageState.Sent => 1, OutboundMessageState.Delivered => 2, OutboundMessageState.Failed => 3, OutboundMessageState.Acknowledged => 4, _ => 0 };
}

public sealed record OutboundSendResult(bool Success, string? MessageId, string? ErrorCode)
{
    public static OutboundSendResult Ok(string messageId) => new(true, messageId, null);
    public static OutboundSendResult Denied(string errorCode) => new(false, null, errorCode);
}
