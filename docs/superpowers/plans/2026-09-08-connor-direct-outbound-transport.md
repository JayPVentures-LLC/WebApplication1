# Connor Direct Outbound Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a governed provider-neutral SMS transport in JPV that routes exact-head GitHub review requests to Connor's verified principal binding and records attributable delivery/acknowledgment without ever promoting SMS state into GitHub approval.

**Architecture:** Add a focused `Services/Outbound` domain/service layer, a Twilio adapter under `Infrastructure/Twilio`, durable JSONL receipt persistence, and an authenticated API controller. Reuse the existing GitHub App installation-token provider to verify the live PR head immediately before dispatch. Domain code depends only on provider-neutral interfaces.

**Tech Stack:** .NET 8, ASP.NET Core controllers/DI, `HttpClient`, `System.Text.Json`, `System.Security.Cryptography`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-08-connor-direct-outbound-transport-design.md`

## Global Constraints

- Canonical target principal: `github:jaypventuresllc-admin`.
- No raw destination phone number in the governed public send API.
- Phone number and provider credentials are runtime secrets only.
- Fail closed on missing/unverified/revoked/expired/mismatched binding or authority.
- Twilio remains behind a provider-neutral interface.
- Webhook mutations require authenticated, replay-resistant provider evidence.
- SMS delivery/acknowledgment must remain technically incapable of satisfying GitHub approval.
- Exact-head GitHub review routing must re-read the live PR head immediately before dispatch.

---

### Task 1: Define transport contracts and binding admission

**Files:**
- Create: `tests/JPVOS.Tests/OutboundTransportTests.cs`
- Create: `src/JPVOS/Services/Outbound/OutboundContracts.cs`
- Create: `src/JPVOS/Services/Outbound/PrincipalSmsBindingResolver.cs`

**Interfaces:**
- Produces `IPrincipalSmsBindingResolver`, `ISmsTransport`, `IOutboundReceiptStore`, `IGitHubExactHeadReader`, `OutboundMessageReceipt`, `GithubExactHeadReviewRequest`, and normalized state/error contracts.

- [ ] **Step 1:** Add xUnit tests proving verified Connor binding resolves and missing/unverified/revoked/expired/mismatched bindings deny.
- [ ] **Step 2:** Run `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter OutboundTransportTests` and verify RED because outbound types do not exist.
- [ ] **Step 3:** Add minimal contracts and configuration-backed binding resolver.
- [ ] **Step 4:** Re-run the filtered tests and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 2: Implement durable receipts and provider-neutral outbound orchestration

**Files:**
- Modify: `tests/JPVOS.Tests/OutboundTransportTests.cs`
- Create: `src/JPVOS/Services/Outbound/JsonlOutboundReceiptStore.cs`
- Create: `src/JPVOS/Services/Outbound/OutboundTransportService.cs`

**Interfaces:**
- Consumes `IPrincipalSmsBindingResolver`, `ISmsTransport`, `IOutboundReceiptStore`, `IGitHubExactHeadReader`.
- Produces `OutboundTransportService.SendGithubReviewAsync(...)` and idempotent receipt transitions.

- [ ] **Step 1:** Add tests for authority denial, unsupported purpose, stale exact head, successful send persistence, deterministic provider failure, monotonic/idempotent transitions, and acknowledgment isolation.
- [ ] **Step 2:** Run filtered tests and verify RED.
- [ ] **Step 3:** Implement minimal orchestrator and JSONL receipt store.
- [ ] **Step 4:** Re-run tests and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 3: Implement GitHub exact-head reader

**Files:**
- Modify: `tests/JPVOS.Tests/OutboundTransportTests.cs`
- Create: `src/JPVOS/Services/Outbound/GitHubExactHeadReader.cs`

**Interfaces:**
- Implements `IGitHubExactHeadReader.GetHeadShaAsync(repositoryFullName, pullRequestNumber, cancellationToken)` using the existing `IGitHubAppTokenProvider` and installation allowlist.

- [ ] **Step 1:** Add a test around parsing/normalizing the GitHub PR head response using a deterministic fake HTTP handler.
- [ ] **Step 2:** Run and verify RED.
- [ ] **Step 3:** Implement the reader with GitHub App installation token authentication and no token logging.
- [ ] **Step 4:** Re-run and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 4: Implement Twilio adapter and webhook authenticity

**Files:**
- Modify: `tests/JPVOS.Tests/OutboundTransportTests.cs`
- Create: `src/JPVOS/Infrastructure/Twilio/TwilioSmsTransport.cs`

**Interfaces:**
- Implements `ISmsTransport.SendAsync(...)`, `ValidateAndParseStatusAsync(...)`, and `ValidateAndParseInboundAsync(...)`.
- Uses Twilio REST with `HttpClient` and validates callbacks via Twilio's HMAC-SHA1 request signature algorithm.

- [ ] **Step 1:** Add tests for provider send normalization, signature validation, invalid-signature denial, callback normalization, and inbound sender attribution.
- [ ] **Step 2:** Run and verify RED.
- [ ] **Step 3:** Implement adapter without introducing Twilio domain types.
- [ ] **Step 4:** Re-run and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 5: Add governed API endpoints and DI registration

**Files:**
- Create: `src/JPVOS/Api/OutboundTransportController.cs`
- Modify: `src/JPVOS/Program.cs`

**Interfaces:**
- `POST /api/outbound/github-review` requires `FounderOnly` authorization and structured request fields only.
- `GET /api/outbound/receipts/{messageId}` requires `FounderOnly` authorization.
- Twilio callback endpoints validate provider authenticity before state mutation and do not require founder cookies.

- [ ] **Step 1:** Add controller/service tests for structured request-only routing and denial semantics.
- [ ] **Step 2:** Run and verify RED.
- [ ] **Step 3:** Add controller and DI registrations.
- [ ] **Step 4:** Run full `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj` and verify GREEN.
- [ ] **Step 5:** Commit.

### Task 6: Security and release verification

**Files:**
- Modify: `docs/superpowers/specs/2026-09-08-connor-direct-outbound-transport-design.md` only if implementation evidence requires clarifying operational status; do not change approved semantics.

- [ ] **Step 1:** Run full tests and applicable repository governance/security checks.
- [ ] **Step 2:** Scan changed files for phone numbers, Twilio credentials, auth tokens, and raw destination-number API parameters.
- [ ] **Step 3:** Verify exact-head PR routing cannot dispatch when live head differs from requested head.
- [ ] **Step 4:** Verify `ACKNOWLEDGED` state has no code path to GitHub review mutation.
- [ ] **Step 5:** Open PR with exact-head verification evidence and request independent review.
