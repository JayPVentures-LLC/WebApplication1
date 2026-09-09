using System.Net.Http.Headers;
using System.Text.Json;
using JPVOS.Services.GitHubOrgMutation;

namespace JPVOS.Services.Outbound;

public sealed class GitHubExactHeadReader : IGitHubExactHeadReader
{
    private readonly HttpClient _http;
    private readonly IGitHubAppTokenProvider _tokens;
    private readonly GitHubAppAuthenticationOptions _options;

    public GitHubExactHeadReader(HttpClient http, IGitHubAppTokenProvider tokens, GitHubAppAuthenticationOptions options)
    {
        _http = http;
        _tokens = tokens;
        _options = options;
        _http.BaseAddress ??= new Uri("https://api.github.com/");
    }

    public async Task<string> GetHeadShaAsync(string repositoryFullName, int pullRequestNumber, CancellationToken cancellationToken)
    {
        var parts = repositoryFullName.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || pullRequestNumber <= 0) throw new InvalidOperationException("Invalid GitHub PR correlation target.");
        var installation = _options.InstallationFor(parts[0]);
        var token = await _tokens.GetInstallationTokenAsync(installation, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{Uri.EscapeDataString(parts[0])}/{Uri.EscapeDataString(parts[1])}/pulls/{pullRequestNumber}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.UserAgent.ParseAdd("JPV-OS-Access-Gateway/1.0");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub exact-head read failed with HTTP {(int)response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("head", out var head) || !head.TryGetProperty("sha", out var shaNode) || string.IsNullOrWhiteSpace(shaNode.GetString()))
            throw new InvalidOperationException("GitHub exact-head response did not contain head.sha.");
        return shaNode.GetString()!;
    }
}
