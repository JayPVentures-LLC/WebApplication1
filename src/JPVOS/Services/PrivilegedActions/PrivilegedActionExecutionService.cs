using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace JPVOS.Services.PrivilegedActions;

public sealed record PrivilegedProviderResult(bool Success, string ResultCode, string State);

public interface IPrivilegedActionProvider
{
    Task<PrivilegedProviderResult> ExecuteAsync(PrivilegedActionRequest request, CancellationToken cancellationToken);
    Task<PrivilegedProviderResult> ReadBackAsync(PrivilegedActionRequest request, CancellationToken cancellationToken);
}

public sealed class PrivilegedActionAuditStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string? _previousReceiptHash;

    public PrivilegedActionAuditStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _previousReceiptHash = LoadExistingChainHead(path);
    }

    public async Task AppendAsync(PrivilegedActionReceipt receipt, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var linked = receipt with { PreviousReceiptHash = _previousReceiptHash };
            var json = JsonSerializer.Serialize(linked);
            await File.AppendAllTextAsync(_path, json + Environment.NewLine, cancellationToken);
            _previousReceiptHash = Hash(json);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static string? LoadExistingChainHead(string path)
    {
        if (!File.Exists(path)) return null;
        var last = File.ReadLines(path).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        return last is null ? null : Hash(last);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record PrivilegedExecutionOutcome(string TerminalStatus, string ReasonCode, PrivilegedActionReceipt? Receipt);

public sealed class PrivilegedActionExecutionService
{
    private readonly PrivilegedActionAuthorizer _authorizer;
    private readonly PrivilegedActionAuditStore _auditStore;

    public PrivilegedActionExecutionService(
        PrivilegedActionAuthorizer authorizer,
        PrivilegedActionAuditStore auditStore)
    {
        _authorizer = authorizer;
        _auditStore = auditStore;
    }

    public async Task<PrivilegedExecutionOutcome> ExecuteAsync(
        PrivilegedActionRequest request,
        AuthenticationEvidence authentication,
        IPrivilegedActionProvider provider,
        DateTimeOffset nowUtc,
        BreakGlassGrant? breakGlassGrant = null,
        CancellationToken cancellationToken = default)
    {
        var decision = _authorizer.Authorize(request, authentication, nowUtc, breakGlassGrant);
        if (decision.Decision != PrivilegedDecisionKind.Allow)
            return new PrivilegedExecutionOutcome("DENIED", decision.ReasonCode, null);

        if (string.IsNullOrWhiteSpace(request.DesiredState))
        {
            var missingDesired = BuildReceipt(
                request,
                decision.RiskClass,
                authentication,
                nowUtc,
                desiredState: string.Empty,
                providerExecutionResult: "NOT_EXECUTED",
                observedState: string.Empty,
                terminalStatus: "DEGRADED");
            await _auditStore.AppendAsync(missingDesired, cancellationToken);
            return new PrivilegedExecutionOutcome("DEGRADED", "DESIRED_STATE_REQUIRED", missingDesired);
        }

        PrivilegedProviderResult execution;
        PrivilegedProviderResult observed;
        try
        {
            execution = await provider.ExecuteAsync(request, cancellationToken);
            observed = execution.Success
                ? await provider.ReadBackAsync(request, cancellationToken)
                : new PrivilegedProviderResult(false, "EXECUTION_FAILED", string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var failed = BuildReceipt(
                request,
                decision.RiskClass,
                authentication,
                nowUtc,
                request.DesiredState,
                "PROVIDER_EXCEPTION",
                string.Empty,
                "FAILED");
            await _auditStore.AppendAsync(failed, CancellationToken.None);
            return new PrivilegedExecutionOutcome("FAILED", "PROVIDER_EXCEPTION", failed);
        }

        var terminal = !execution.Success
            ? "FAILED"
            : !observed.Success || !string.Equals(observed.State, request.DesiredState, StringComparison.Ordinal)
                ? "DEGRADED"
                : "PASS";

        var reasonCode = terminal switch
        {
            "PASS" => "PASS",
            "FAILED" => execution.ResultCode,
            _ => !observed.Success ? observed.ResultCode : "READBACK_MISMATCH"
        };

        var receipt = BuildReceipt(
            request,
            decision.RiskClass,
            authentication,
            nowUtc,
            request.DesiredState,
            execution.ResultCode,
            observed.State,
            terminal);

        await _auditStore.AppendAsync(receipt, cancellationToken);
        return new PrivilegedExecutionOutcome(terminal, reasonCode, receipt);
    }

    private static PrivilegedActionReceipt BuildReceipt(
        PrivilegedActionRequest request,
        PrivilegedRiskClass effectiveRiskClass,
        AuthenticationEvidence authentication,
        DateTimeOffset nowUtc,
        string desiredState,
        string providerExecutionResult,
        string observedState,
        string terminalStatus) =>
        new(
            Guid.NewGuid().ToString("N"),
            request.ActorSubject,
            request.Action,
            request.Resource,
            effectiveRiskClass,
            authentication.PhishingResistant ? "PHISHING_RESISTANT" : "SESSION",
            nowUtc,
            FingerprintState(desiredState),
            providerExecutionResult,
            FingerprintState(observedState),
            terminalStatus,
            null);

    private static string FingerprintState(string? state)
    {
        if (string.IsNullOrEmpty(state)) return string.Empty;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(state));
        return $"SHA256:{Convert.ToHexString(digest)}";
    }
}
