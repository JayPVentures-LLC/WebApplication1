using System.Security.Cryptography;
using System.Text;

namespace JPVOS.Services.ClaimsEvidence;

public sealed class TrackingCredentialService : ITrackingCredentialService
{
    public TrackingCredentialIssue Issue()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return new TrackingCredentialIssue(token, ComputeVerifier(token));
    }

    public bool Verify(string candidate, string verifier)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(verifier)) return false;

        try
        {
            var expected = Convert.FromBase64String(verifier);
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(candidate));
            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ComputeVerifier(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
