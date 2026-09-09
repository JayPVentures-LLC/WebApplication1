using System.Text.RegularExpressions;

namespace JPVOS.Services.Outbound;

public sealed class PrincipalSmsBindingResolver : IPrincipalSmsBindingResolver
{
    public const string ConnorPrincipalId = "github:jaypventuresllc-admin";
    private readonly IReadOnlyDictionary<string, string?> _configuration;

    private PrincipalSmsBindingResolver(IReadOnlyDictionary<string, string?> configuration) => _configuration = configuration;

    public static PrincipalSmsBindingResolver FromDictionary(IReadOnlyDictionary<string, string?> configuration) => new(configuration);

    public PrincipalSmsBindingResult Resolve(string principalId, DateTimeOffset now)
    {
        if (!string.Equals(principalId, ConnorPrincipalId, StringComparison.Ordinal))
            return PrincipalSmsBindingResult.Denied("principal_mismatch");

        var endpoint = Get("JPV_PRINCIPAL_CONNOR_SMS_E164");
        if (string.IsNullOrWhiteSpace(endpoint)) return PrincipalSmsBindingResult.Denied("principal_binding_missing");
        if (!Regex.IsMatch(endpoint, "^\\+[1-9][0-9]{7,14}$")) return PrincipalSmsBindingResult.Denied("principal_binding_unverified");

        var verifiedAtRaw = Get("JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT");
        if (!DateTimeOffset.TryParse(verifiedAtRaw, out var verifiedAt)) return PrincipalSmsBindingResult.Denied("principal_binding_unverified");
        if (verifiedAt > now) return PrincipalSmsBindingResult.Denied("principal_binding_unverified");
        var version = Get("JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION");
        if (string.IsNullOrWhiteSpace(version)) return PrincipalSmsBindingResult.Denied("principal_binding_unverified");

        var revokedRaw = Get("JPV_PRINCIPAL_CONNOR_SMS_REVOKED");
        var revoked = false;
        if (!string.IsNullOrWhiteSpace(revokedRaw) && !bool.TryParse(revokedRaw, out revoked))
            return PrincipalSmsBindingResult.Denied("principal_binding_unverified");
        if (revoked) return PrincipalSmsBindingResult.Denied("principal_binding_revoked");

        DateTimeOffset? expiresAt = null;
        var expiresRaw = Get("JPV_PRINCIPAL_CONNOR_SMS_EXPIRES_AT");
        if (!string.IsNullOrWhiteSpace(expiresRaw))
        {
            if (!DateTimeOffset.TryParse(expiresRaw, out var parsedExpiry)) return PrincipalSmsBindingResult.Denied("principal_binding_unverified");
            expiresAt = parsedExpiry;
            if (parsedExpiry <= now) return PrincipalSmsBindingResult.Denied("principal_binding_expired");
        }

        return PrincipalSmsBindingResult.Ok(new PrincipalSmsBinding(ConnorPrincipalId, "sms", endpoint, version, verifiedAt, false, expiresAt));
    }

    private string? Get(string key) => _configuration.TryGetValue(key, out var value) ? value : null;
}

public sealed class ConfigurationPrincipalSmsBindingResolver : IPrincipalSmsBindingResolver
{
    private readonly IConfiguration _configuration;
    public ConfigurationPrincipalSmsBindingResolver(IConfiguration configuration) => _configuration = configuration;

    public PrincipalSmsBindingResult Resolve(string principalId, DateTimeOffset now)
    {
        var keys = new[]
        {
            "JPV_PRINCIPAL_CONNOR_SMS_E164", "JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT", "JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION",
            "JPV_PRINCIPAL_CONNOR_SMS_REVOKED", "JPV_PRINCIPAL_CONNOR_SMS_EXPIRES_AT"
        };
        var values = keys.ToDictionary(k => k, k => _configuration[k]);
        return PrincipalSmsBindingResolver.FromDictionary(values).Resolve(principalId, now);
    }
}
