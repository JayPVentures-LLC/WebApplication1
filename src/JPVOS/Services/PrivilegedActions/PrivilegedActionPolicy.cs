using System.Text.Json;
using System.Text.Json.Serialization;

namespace JPVOS.Services.PrivilegedActions;

public sealed class PrivilegedActionPolicy
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("risk_classes")]
    public string[] RiskClasses { get; init; } = [];

    [JsonPropertyName("privileged_actions")]
    public string[] PrivilegedActions { get; init; } = [];

    [JsonPropertyName("invariants")]
    public PrivilegedActionInvariants Invariants { get; init; } = new();
}

public sealed class PrivilegedActionInvariants
{
    [JsonPropertyName("phishing_resistant_step_up_required")]
    public bool PhishingResistantStepUpRequired { get; init; }

    [JsonPropertyName("voice_only_permitted")]
    public bool VoiceOnlyPermitted { get; init; }

    [JsonPropertyName("provider_readback_required")]
    public bool ProviderReadbackRequired { get; init; }

    [JsonPropertyName("unknown_state_decision")]
    public string UnknownStateDecision { get; init; } = string.Empty;

    [JsonPropertyName("break_glass_max_ttl_minutes")]
    public int BreakGlassMaxTtlMinutes { get; init; }

    [JsonPropertyName("durable_receipt_required")]
    public bool DurableReceiptRequired { get; init; }
}

public static class PrivilegedActionPolicyLoader
{
    public static PrivilegedActionPolicy LoadAndValidate(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Privileged-action policy missing: {path}");

        var policy = JsonSerializer.Deserialize<PrivilegedActionPolicy>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Privileged-action policy could not be parsed.");

        Validate(policy);
        return policy;
    }

    public static void Validate(PrivilegedActionPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Id))
            throw new InvalidOperationException("Privileged-action policy id is required.");
        if (!policy.Invariants.PhishingResistantStepUpRequired)
            throw new InvalidOperationException("Privileged actions must require phishing-resistant step-up.");
        if (policy.Invariants.VoiceOnlyPermitted)
            throw new InvalidOperationException("Voice-only privileged authorization is prohibited.");
        if (!policy.Invariants.ProviderReadbackRequired)
            throw new InvalidOperationException("Provider readback is required.");
        if (!policy.Invariants.DurableReceiptRequired)
            throw new InvalidOperationException("Durable privileged-action receipts are required.");
        if (!string.Equals(policy.Invariants.UnknownStateDecision, "BLOCK", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unknown privileged-action state must BLOCK.");
        if (policy.Invariants.BreakGlassMaxTtlMinutes is <= 0 or > 30)
            throw new InvalidOperationException("Break-glass TTL must be between 1 and 30 minutes.");
        if (policy.PrivilegedActions.Length == 0)
            throw new InvalidOperationException("At least one privileged action must be declared.");
    }
}
