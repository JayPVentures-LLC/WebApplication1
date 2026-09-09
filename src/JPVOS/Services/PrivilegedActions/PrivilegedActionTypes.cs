namespace JPVOS.Services.PrivilegedActions;

public enum PrivilegedRiskClass
{
    Routine,
    Elevated,
    Privileged,
    BreakGlass
}

public enum PrivilegedDecisionKind
{
    Allow,
    Deny,
    Review,
    BreakGlassRequired
}

public sealed record PrivilegedActionRequest(
    string ActorSubject,
    string Action,
    string Resource,
    PrivilegedRiskClass RiskClass,
    bool EntitlementValid,
    bool EntitlementAmbiguous = false,
    string? DesiredState = null);

public sealed record AuthenticationEvidence(
    bool IdentityVerified,
    bool PhishingResistant,
    bool VoiceSignalPresent,
    DateTimeOffset AuthenticatedAtUtc,
    TimeSpan MaxAge)
{
    public bool IsFresh(DateTimeOffset nowUtc) => nowUtc - AuthenticatedAtUtc <= MaxAge;
}

public sealed record BreakGlassGrant(
    string GrantId,
    string ActorSubject,
    string Reason,
    string Scope,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool PostEventReviewRequired = true)
{
    public bool IsActive(DateTimeOffset nowUtc) => nowUtc >= IssuedAtUtc && nowUtc < ExpiresAtUtc;
}

public sealed record PrivilegedActionDecision(
    PrivilegedDecisionKind Decision,
    string ReasonCode,
    PrivilegedRiskClass RiskClass);

public sealed record PrivilegedActionReceipt(
    string ReceiptId,
    string ActorSubject,
    string Action,
    string Resource,
    PrivilegedRiskClass RiskClass,
    string StepUpMethod,
    DateTimeOffset DecisionAtUtc,
    string DesiredState,
    string ProviderExecutionResult,
    string ObservedState,
    string TerminalStatus,
    string? PreviousReceiptHash);
