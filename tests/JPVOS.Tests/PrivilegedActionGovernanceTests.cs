using System.Text.Json;
using JPVOS.Services.PrivilegedActions;

namespace JPVOS.Tests;

public sealed class PrivilegedActionGovernanceTests
{
    private static PrivilegedActionPolicy Policy() => new()
    {
        Id = "JPV-GOV-PRIVILEGED-ACTION-001",
        RiskClasses = ["ROUTINE", "ELEVATED", "PRIVILEGED", "BREAK_GLASS"],
        PrivilegedActions = ["credential_change", "break_glass_activation"],
        Invariants = new PrivilegedActionInvariants
        {
            PhishingResistantStepUpRequired = true,
            VoiceOnlyPermitted = false,
            ProviderReadbackRequired = true,
            UnknownStateDecision = "BLOCK",
            BreakGlassMaxTtlMinutes = 30,
            DurableReceiptRequired = true
        }
    };

    [Fact]
    public void Policy_rejects_voice_only_authorization()
    {
        var policy = Policy();
        var weakened = new PrivilegedActionPolicy
        {
            Id = policy.Id,
            RiskClasses = policy.RiskClasses,
            PrivilegedActions = policy.PrivilegedActions,
            Invariants = new PrivilegedActionInvariants
            {
                PhishingResistantStepUpRequired = true,
                VoiceOnlyPermitted = true,
                ProviderReadbackRequired = true,
                UnknownStateDecision = "BLOCK",
                BreakGlassMaxTtlMinutes = 30,
                DurableReceiptRequired = true
            }
        };

        Assert.Throws<InvalidOperationException>(() => PrivilegedActionPolicyLoader.Validate(weakened));
    }

    [Fact]
    public void Privileged_action_requires_phishing_resistant_step_up()
    {
        var authorizer = new PrivilegedActionAuthorizer(Policy());
        var now = DateTimeOffset.UtcNow;
        var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true);
        var authentication = new AuthenticationEvidence(true, false, false, now, TimeSpan.FromMinutes(5));
        var decision = authorizer.Authorize(request, authentication, now);
        Assert.Equal(PrivilegedDecisionKind.Deny, decision.Decision);
        Assert.Equal("PHISHING_RESISTANT_STEP_UP_REQUIRED", decision.ReasonCode);
    }

    [Fact]
    public void Policy_listed_action_cannot_self_classify_as_routine()
    {
        var authorizer = new PrivilegedActionAuthorizer(Policy());
        var now = DateTimeOffset.UtcNow;
        var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Routine, true);
        var authentication = new AuthenticationEvidence(true, false, false, now, TimeSpan.FromMinutes(5));
        var decision = authorizer.Authorize(request, authentication, now);
        Assert.Equal(PrivilegedDecisionKind.Deny, decision.Decision);
        Assert.Equal(PrivilegedRiskClass.Privileged, decision.RiskClass);
    }

    [Fact]
    public void Voice_signal_cannot_replace_phishing_resistant_step_up()
    {
        var authorizer = new PrivilegedActionAuthorizer(Policy());
        var now = DateTimeOffset.UtcNow;
        var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true);
        var authentication = new AuthenticationEvidence(true, false, true, now, TimeSpan.FromMinutes(5));
        var decision = authorizer.Authorize(request, authentication, now);
        Assert.Equal(PrivilegedDecisionKind.Deny, decision.Decision);
        Assert.Equal("VOICE_ONLY_INSUFFICIENT", decision.ReasonCode);
    }

    [Fact]
    public void Ambiguous_entitlement_routes_to_review()
    {
        var authorizer = new PrivilegedActionAuthorizer(Policy());
        var now = DateTimeOffset.UtcNow;
        var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true, true);
        var authentication = new AuthenticationEvidence(true, true, false, now, TimeSpan.FromMinutes(5));
        Assert.Equal(PrivilegedDecisionKind.Review, authorizer.Authorize(request, authentication, now).Decision);
    }

    [Fact]
    public void Break_glass_rejects_ttl_over_thirty_minutes()
    {
        var service = new BreakGlassAuthorizationService(Policy());
        Assert.Throws<InvalidOperationException>(() => service.Issue("founder", "emergency production recovery", "prod/runtime", TimeSpan.FromMinutes(31), DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Persisted_break_glass_grant_is_revalidated_against_policy_ttl()
    {
        var now = DateTimeOffset.UtcNow;
        var authorizer = new PrivilegedActionAuthorizer(Policy());
        var request = new PrivilegedActionRequest("founder", "break_glass_activation", "prod/runtime", PrivilegedRiskClass.BreakGlass, true);
        var authentication = new AuthenticationEvidence(true, true, false, now, TimeSpan.FromMinutes(5));
        var forgedLongLived = new BreakGlassGrant("grant-1", "founder", "recovery", "prod/runtime", now.AddMinutes(-1), now.AddHours(12));

        var decision = authorizer.Authorize(request, authentication, now, forgedLongLived);

        Assert.Equal(PrivilegedDecisionKind.Deny, decision.Decision);
        Assert.Equal("BREAK_GLASS_GRANT_INVALID", decision.ReasonCode);
    }

    [Fact]
    public async Task Provider_readback_mismatch_never_returns_pass()
    {
        var auditPath = Path.Join(Path.GetTempPath(), $"jpv-privileged-{Guid.NewGuid():N}.jsonl");
        try
        {
            var execution = new PrivilegedActionExecutionService(new PrivilegedActionAuthorizer(Policy()), new PrivilegedActionAuditStore(auditPath));
            var now = DateTimeOffset.UtcNow;
            var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true, false, "rotated");
            var authentication = new AuthenticationEvidence(true, true, false, now, TimeSpan.FromMinutes(5));
            var outcome = await execution.ExecuteAsync(request, authentication, new MismatchProvider(), now);
            Assert.Equal("DEGRADED", outcome.TerminalStatus);
        }
        finally { if (File.Exists(auditPath)) File.Delete(auditPath); }
    }

    [Fact]
    public async Task Cancellation_after_provider_execution_begins_persists_uncertain_receipt()
    {
        var auditPath = Path.Join(Path.GetTempPath(), $"jpv-privileged-{Guid.NewGuid():N}.jsonl");
        using var cts = new CancellationTokenSource();
        try
        {
            var execution = new PrivilegedActionExecutionService(new PrivilegedActionAuthorizer(Policy()), new PrivilegedActionAuditStore(auditPath));
            var now = DateTimeOffset.UtcNow;
            var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true, false, "rotated");
            var authentication = new AuthenticationEvidence(true, true, false, now, TimeSpan.FromMinutes(5));
            var provider = new CancelAfterMutationProvider(cts);

            await Assert.ThrowsAsync<OperationCanceledException>(() => execution.ExecuteAsync(request, authentication, provider, now, cancellationToken: cts.Token));

            var persisted = await File.ReadAllTextAsync(auditPath);
            Assert.Contains("UNCERTAIN", persisted, StringComparison.Ordinal);
            Assert.Contains("CANCELED_AFTER_EXECUTION_STARTED", persisted, StringComparison.Ordinal);
        }
        finally { if (File.Exists(auditPath)) File.Delete(auditPath); }
    }

    [Fact]
    public async Task Audit_receipt_never_persists_raw_state_values()
    {
        const string secret = "super-secret-credential-value";
        var auditPath = Path.Join(Path.GetTempPath(), $"jpv-privileged-{Guid.NewGuid():N}.jsonl");
        try
        {
            var execution = new PrivilegedActionExecutionService(new PrivilegedActionAuthorizer(Policy()), new PrivilegedActionAuditStore(auditPath));
            var now = DateTimeOffset.UtcNow;
            var request = new PrivilegedActionRequest("founder", "credential_change", "secret-store", PrivilegedRiskClass.Privileged, true, false, secret);
            var authentication = new AuthenticationEvidence(true, true, false, now, TimeSpan.FromMinutes(5));
            var outcome = await execution.ExecuteAsync(request, authentication, new MatchProvider(secret), now);
            var persisted = await File.ReadAllTextAsync(auditPath);
            Assert.Equal("PASS", outcome.TerminalStatus);
            Assert.DoesNotContain(secret, persisted, StringComparison.Ordinal);
            Assert.Contains("SHA256:", persisted, StringComparison.Ordinal);
        }
        finally { if (File.Exists(auditPath)) File.Delete(auditPath); }
    }

    [Fact]
    public async Task Audit_hash_chain_resumes_after_restart()
    {
        var auditPath = Path.Join(Path.GetTempPath(), $"jpv-privileged-{Guid.NewGuid():N}.jsonl");
        try
        {
            var first = new PrivilegedActionAuditStore(auditPath);
            await first.AppendAsync(TestReceipt("one"), CancellationToken.None);
            var second = new PrivilegedActionAuditStore(auditPath);
            await second.AppendAsync(TestReceipt("two"), CancellationToken.None);
            var lines = File.ReadAllLines(auditPath);
            Assert.Equal(2, lines.Length);
            using var document = JsonDocument.Parse(lines[1]);
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("PreviousReceiptHash").GetString()));
        }
        finally { if (File.Exists(auditPath)) File.Delete(auditPath); }
    }

    private static PrivilegedActionReceipt TestReceipt(string id) => new(id, "system", "test_action", "test/resource", PrivilegedRiskClass.Privileged, "PHISHING_RESISTANT", DateTimeOffset.UtcNow, "SHA256:DESIRED", "OK", "SHA256:OBSERVED", "PASS", null);

    private sealed class MismatchProvider : IPrivilegedActionProvider
    {
        public Task<PrivilegedProviderResult> ExecuteAsync(PrivilegedActionRequest request, CancellationToken cancellationToken) => Task.FromResult(new PrivilegedProviderResult(true, "ACCEPTED", "rotated"));
        public Task<PrivilegedProviderResult> ReadBackAsync(PrivilegedActionRequest request, CancellationToken cancellationToken) => Task.FromResult(new PrivilegedProviderResult(true, "OBSERVED", "stale"));
    }

    private sealed class MatchProvider(string state) : IPrivilegedActionProvider
    {
        public Task<PrivilegedProviderResult> ExecuteAsync(PrivilegedActionRequest request, CancellationToken cancellationToken) => Task.FromResult(new PrivilegedProviderResult(true, "ACCEPTED", state));
        public Task<PrivilegedProviderResult> ReadBackAsync(PrivilegedActionRequest request, CancellationToken cancellationToken) => Task.FromResult(new PrivilegedProviderResult(true, "OBSERVED", state));
    }

    private sealed class CancelAfterMutationProvider(CancellationTokenSource cts) : IPrivilegedActionProvider
    {
        public Task<PrivilegedProviderResult> ExecuteAsync(PrivilegedActionRequest request, CancellationToken cancellationToken)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PrivilegedProviderResult(true, "ACCEPTED", request.DesiredState ?? string.Empty));
        }

        public Task<PrivilegedProviderResult> ReadBackAsync(PrivilegedActionRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new PrivilegedProviderResult(true, "OBSERVED", request.DesiredState ?? string.Empty));
    }
}
