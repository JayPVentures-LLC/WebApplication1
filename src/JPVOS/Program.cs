using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;
using Stripe;

using JPVOS.Components;
using JPVOS.Services;
using JPVOS.Services.SystemicAccess;
using JPVOS.Services.PrivilegedActions;
using JPVOS.Services.GitHubOrgMutation;
using JPVOS.Services.Attention;
using JPVOS.Services.Outbound;
using JPVOS.Infrastructure.Stripe;
using JPVOS.Infrastructure.Twilio;

var builder = WebApplication.CreateBuilder(args);

var systemicAccessPolicyPath = Path.Combine(builder.Environment.ContentRootPath, ".jpv", "governance", "systemic-access-hygiene.json");
var systemicAccessPolicy = SystemicAccessPolicyLoader.LoadAndValidate(systemicAccessPolicyPath);

var privilegedActionPolicyPath = Path.Combine(
    builder.Environment.ContentRootPath,
    ".jpv",
    "governance",
    "privileged-action-governance.json");
var privilegedActionPolicy = PrivilegedActionPolicyLoader.LoadAndValidate(privilegedActionPolicyPath);

var githubAppOptions = GitHubAppAuthenticationOptions.FromConfiguration(builder.Configuration);
var outboundProvider = builder.Configuration["JPV_OUTBOUND_SMS_PROVIDER"]?.Trim().ToLowerInvariant() ?? "disabled";
var outboundEnabled = outboundProvider == "twilio";
if (outboundProvider is not "disabled" and not "twilio") throw new InvalidOperationException($"Unsupported JPV_OUTBOUND_SMS_PROVIDER: {outboundProvider}");

var outboundDataDir = builder.Configuration["JPV_OUTBOUND_DATA_DIR"];
if (string.IsNullOrWhiteSpace(outboundDataDir))
{
    if (outboundEnabled && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("JPV_OUTBOUND_DATA_DIR is required when outbound SMS is enabled outside Development and must point to writable persistent storage.");
    outboundDataDir = Path.Combine(Path.GetTempPath(), "jpv-os-outbound");
}
Directory.CreateDirectory(outboundDataDir);

StripeConfiguration.ApiKey = builder.Configuration["STRIPE_SECRET_KEY"];

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "__Host-JPV.Auth"; options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict; options.SlidingExpiration = true; options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.LoginPath = "/login"; options.AccessDeniedPath = "/login?denied=1";
});
builder.Services.AddAuthorization(options => options.AddPolicy("FounderOnly", policy => policy.RequireRole("Founder")));
builder.Services.AddRateLimiter(options => options.AddPolicy("FounderLogin", httpContext => RateLimitPartition.GetFixedWindowLimiter(httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true })));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState(); builder.Services.AddControllers();
if (builder.Environment.IsDevelopment()) builder.Services.AddSingleton<IEntitlementService, InMemoryEntitlementService>();
else
{
    var dbPath = Path.Combine(AppContext.BaseDirectory, "entitlements.db");
    builder.Services.AddSingleton<IEntitlementRepository>(new SqliteEntitlementRepository(dbPath));
    builder.Services.AddSingleton<IEntitlementService, PersistentEntitlementService>();
    builder.Services.AddSingleton<EntitlementAccessProvider>();
    builder.Services.AddSingleton<ISystemicAccessInventorySource>(sp => sp.GetRequiredService<EntitlementAccessProvider>());
    builder.Services.AddSingleton<ISystemicAccessActionProvider>(sp => sp.GetRequiredService<EntitlementAccessProvider>());
}
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DiscordService>();
builder.Services.AddSingleton<StripePricingLoader>();
builder.Services.AddSingleton<StripeCheckoutService>();
builder.Services.AddSingleton<StripeWebhookEventStore>();
builder.Services.AddSingleton<StripeSubscriptionAuditStore>();
builder.Services.AddSingleton<JPVOS.Infrastructure.Discord.DiscordRoleSyncAuditStore>();
builder.Services.AddSingleton<ProductionAttentionAdmissionService>();

builder.Services.AddSingleton(systemicAccessPolicy);
builder.Services.AddSingleton<SystemicAccessClassifier>();
builder.Services.AddSingleton<SystemicAccessRuntimeState>();
builder.Services.AddSingleton(sp => new SystemicAccessAuditStore(
    Path.Combine(AppContext.BaseDirectory, "audit", "systemic-access-receipts.jsonl")));
builder.Services.AddSingleton<SystemicAccessReconciler>();
builder.Services.AddHostedService<SystemicAccessReconciliationService>();

builder.Services.AddSingleton(privilegedActionPolicy);
builder.Services.AddSingleton<PrivilegedActionAuthorizer>();
builder.Services.AddSingleton<BreakGlassAuthorizationService>();
builder.Services.AddSingleton(sp => new PrivilegedActionAuditStore(
    Path.Combine(AppContext.BaseDirectory, "audit", "privileged-action-receipts.jsonl")));
builder.Services.AddSingleton<PrivilegedActionExecutionService>();

builder.Services.AddSingleton(githubAppOptions);
builder.Services.AddHttpClient<IGitHubAppTokenProvider, GitHubAppTokenProvider>();
builder.Services.AddHttpClient<IGitHubOrganizationClient, GitHubOrganizationClient>();
builder.Services.AddHttpClient<IGitHubCanonicalTopologySource, GitHubCanonicalTopologyLoader>();
builder.Services.AddSingleton(sp => new GitHubOrgMutationReceiptStore(
    Path.Combine(AppContext.BaseDirectory, "audit", "github-org-mutation-receipts.jsonl")));
builder.Services.AddSingleton<GitHubOrganizationReconciler>();
builder.Services.AddSingleton<GitHubOrgMutationRuntimeState>();
builder.Services.AddHostedService<GitHubOrgMutationHostedService>();

builder.Services.AddSingleton(systemicAccessPolicy); builder.Services.AddSingleton<SystemicAccessClassifier>(); builder.Services.AddSingleton<SystemicAccessRuntimeState>();
builder.Services.AddSingleton(sp => new SystemicAccessAuditStore(Path.Combine(AppContext.BaseDirectory, "audit", "systemic-access-receipts.jsonl"))); builder.Services.AddSingleton<SystemicAccessReconciler>(); builder.Services.AddHostedService<SystemicAccessReconciliationService>();

builder.Services.AddSingleton(githubAppOptions); builder.Services.AddHttpClient<IGitHubAppTokenProvider, GitHubAppTokenProvider>(); builder.Services.AddHttpClient<IGitHubOrganizationClient, GitHubOrganizationClient>(); builder.Services.AddHttpClient<IGitHubCanonicalTopologySource, GitHubCanonicalTopologyLoader>();
builder.Services.AddSingleton(sp => new GitHubOrgMutationReceiptStore(Path.Combine(AppContext.BaseDirectory, "audit", "github-org-mutation-receipts.jsonl"))); builder.Services.AddSingleton<GitHubOrganizationReconciler>(); builder.Services.AddSingleton<GitHubOrgMutationRuntimeState>(); builder.Services.AddHostedService<GitHubOrgMutationHostedService>();

builder.Services.AddSingleton<IPrincipalSmsBindingResolver, ConfigurationPrincipalSmsBindingResolver>();
builder.Services.AddSingleton<IOutboundReceiptStore>(_ => new JsonlOutboundReceiptStore(Path.Combine(outboundDataDir, "outbound-message-receipts.jsonl")));
builder.Services.AddSingleton<IDirectConversationStore>(_ => new JsonlDirectConversationStore(Path.Combine(outboundDataDir, "connor-direct-conversation.jsonl")));
builder.Services.AddHttpClient<TwilioSmsTransport>();
if (outboundEnabled) builder.Services.AddTransient<ISmsTransport>(sp => sp.GetRequiredService<TwilioSmsTransport>());
else builder.Services.AddTransient<ISmsTransport, DisabledSmsTransport>();
builder.Services.AddHttpClient<IGitHubExactHeadReader, GitHubExactHeadReader>(); builder.Services.AddTransient<OutboundTransportService>(); builder.Services.AddTransient<DirectConversationService>(); builder.Services.AddTransient<ReviewAcknowledgmentService>();

var app = builder.Build(); PeopleProtectionStartupGuard.Verify(app); app.Services.GetRequiredService<SystemicAccessRuntimeState>().MarkPolicyLoaded(); _ = app.Services.GetRequiredService<ProductionAttentionAdmissionService>();
if (!app.Environment.IsDevelopment()) { app.UseExceptionHandler("/Error", createScopeForErrors: true); app.UseHsts(); app.UseHttpsRedirection(); }
app.UseStaticFiles(); app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization(); app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(); app.MapControllers();
app.MapGet("/health", (IConfiguration config, SystemicAccessRuntimeState systemicState, GitHubOrgMutationRuntimeState githubState, ProductionAttentionAdmissionService attentionGate) => Results.Ok(new
{
    status = systemicState.LastError is null && githubState.LastError is null ? "healthy" : "degraded",
    identity = new
    {
        founderProvisioned = !string.IsNullOrWhiteSpace(config["JPV_FOUNDER_ID"]) &&
                             !string.IsNullOrWhiteSpace(config["JPV_FOUNDER_ACCESS_KEY_SHA256"]),
        session = "cookie",
        founderProfile = "/profile",
        founderWorkspace = "/workspace"
    },
    outbound = new { provider = outboundProvider, enabled = outboundEnabled, persistentStorageRequired = outboundEnabled },
    privilegedActions = new
    {
        policyLoaded = true,
        phishingResistantStepUpRequired = privilegedActionPolicy.Invariants.PhishingResistantStepUpRequired,
        voiceOnlyPermitted = privilegedActionPolicy.Invariants.VoiceOnlyPermitted,
        providerReadbackRequired = privilegedActionPolicy.Invariants.ProviderReadbackRequired,
        breakGlassMaxTtlMinutes = privilegedActionPolicy.Invariants.BreakGlassMaxTtlMinutes
    },
    systemicAccess = new
    {
        policyLoaded = systemicState.PolicyLoaded,
        lastEvaluated = systemicState.LastSummary?.Evaluated,
        lastActionsApplied = systemicState.LastSummary?.ActionsApplied,
        lastFailures = systemicState.LastSummary?.Failures,
        lastCompletedAtUtc = systemicState.LastSummary?.CompletedAtUtc,
        lastError = systemicState.LastError
    },
    githubOrganizationMutation = new
    {
        configured = githubState.Configured,
        canonicalPolicyLoaded = githubState.CanonicalPolicyLoaded,
        lastReconciliationState = githubState.LastReconciliationState?.ToString(),
        lastReceiptId = githubState.LastReceiptId,
        lastError = githubState.LastError
    },
    productionAttentionAdmission = new
    {
        registered = attentionGate is not null,
        mode = "fail-closed"
    },
    timestamp = DateTime.UtcNow
}));
app.Run();
