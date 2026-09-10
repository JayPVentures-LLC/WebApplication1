# Claims & Evidence Case Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved JPV claims/evidence case engine behind the existing ASP.NET access gateway with append-only persistence, protected submitter tracking, idempotent intake, safe status projection, and fail-closed governance boundaries.

**Architecture:** Add a focused `Services/ClaimsEvidence` domain boundary to the existing JPVOS application. Persist canonical case events transactionally in SQLite, derive current status from immutable events, and expose only three narrowly scoped public API operations through the existing gateway. Binary evidence remains disabled unless an approved private evidence store is configured.

**Tech Stack:** .NET / C#, ASP.NET Core controllers, Microsoft.Data.Sqlite, xUnit, existing JPVOS dependency-injection and rate-limiter conventions.

**Spec:** `docs/superpowers/specs/2026-09-09-claims-evidence-case-engine-design.md`

## Global Constraints

- The existing `jpv-os-access-gateway` remains the sole external entry point.
- Canonical executable case history is append-only and reconstructable.
- Initial production event persistence uses transactional SQLite.
- Identity modes are exactly `anonymous`, `pseudonymous`, and `verified-confidential`; identity affects verification, not truth.
- `POST` operations require `Idempotency-Key` and must retry deterministically.
- Plaintext tracking credentials, raw allegations, evidence content, and confidential identity material must not enter ordinary telemetry.
- Cases are private by default; no unauthenticated publication endpoint is added.
- No automated transition may create criminal guilt, civil liability, regulatory violation, publication approval, abuse sanction, or compulsory remedy.
- Binary evidence ingestion fails closed unless an approved private `IEvidenceBlobStore` is configured.
- GitHub Actions is not an execution dependency.

---

## File Structure

Create focused files under `src/JPVOS/Services/ClaimsEvidence/`:

- `ClaimsEvidenceTypes.cs` — enums and immutable domain records.
- `ClaimsEvidenceContracts.cs` — command/result records plus service/store interfaces.
- `TrackingCredentialService.cs` — issue and verify high-entropy one-time tracking credentials.
- `ClaimsEvidenceProjector.cs` — deterministic event-stream-to-safe-case-state projection.
- `SqliteClaimsEvidenceEventStore.cs` — transactional append, sequence allocation, idempotency replay, stream reads.
- `ClaimsEvidenceService.cs` — case creation, evidence append, authorization, and safe status operations.
- `DisabledEvidenceBlobStore.cs` — explicit fail-closed binary-storage implementation.

Create API and tests:

- `src/JPVOS/Api/ClaimsEvidenceController.cs` — minimal public adapter for create/add/status.
- `tests/JPVOS.Tests/ClaimsEvidenceEventStoreTests.cs`
- `tests/JPVOS.Tests/ClaimsEvidenceServiceTests.cs`
- `tests/JPVOS.Tests/ClaimsEvidenceControllerTests.cs`

Modify:

- `src/JPVOS/Program.cs` — DI registration plus claims/evidence rate-limiter policy.

### Task 1: Domain contracts and tracking credentials

**Files:**
- Create: `src/JPVOS/Services/ClaimsEvidence/ClaimsEvidenceTypes.cs`
- Create: `src/JPVOS/Services/ClaimsEvidence/ClaimsEvidenceContracts.cs`
- Create: `src/JPVOS/Services/ClaimsEvidence/TrackingCredentialService.cs`
- Test: `tests/JPVOS.Tests/ClaimsEvidenceServiceTests.cs`

**Interfaces:**
- Produces: `ClaimsIdentityMode`, `ClaimsEvidenceEventType`, `CasePublicStatus`, `ClaimsEvidenceEvent`, `CreateCaseCommand`, `AddEvidenceCommand`, `CreateCaseResult`, `AddEvidenceResult`, `CaseStatusResult`, `IClaimsEvidenceEventStore`, `IClaimsEvidenceService`, `ITrackingCredentialService`.

- [ ] **Step 1: Write failing credential tests**

Add tests asserting `TrackingCredentialService.Issue()` returns a non-empty high-entropy token, returns only a verifier for persistence, and `Verify(candidate, verifier)` accepts the original token but rejects a different one. Add a test asserting the three and only three identity modes serialize to `anonymous`, `pseudonymous`, and `verified-confidential` through explicit mapping helpers.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceServiceTests"`
Expected: FAIL because claims/evidence domain types and credential service do not exist.

- [ ] **Step 3: Implement minimal domain contracts**

Define enums/records with explicit string conversion methods rather than relying on enum-name serialization. Define `ClaimsEvidenceEvent` with `EventId`, `CaseId`, `Sequence`, `OccurredAtUtc`, `Type`, `ActorClass`, `IdempotencyKey`, `Sensitivity`, and `PayloadJson`. Define store methods for atomic create/idempotency replay, append, stream read, and credential-verifier lookup. Define service methods:

```csharp
Task<CreateCaseResult> CreateCaseAsync(CreateCaseCommand command, string idempotencyKey, CancellationToken ct);
Task<AddEvidenceResult> AddEvidenceAsync(string caseId, AddEvidenceCommand command, string idempotencyKey, string trackingCredential, CancellationToken ct);
Task<CaseStatusResult?> GetStatusAsync(string caseId, string trackingCredential, CancellationToken ct);
```

- [ ] **Step 4: Implement tracking credentials**

Use `RandomNumberGenerator.GetBytes(32)` and Base64Url encoding for the returned credential. Persist only `SHA256(credential)` bytes/base64 as verifier. Verify with `CryptographicOperations.FixedTimeEquals`.

- [ ] **Step 5: Run tests and verify pass**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceServiceTests"`
Expected: PASS for contract/credential tests.

- [ ] **Step 6: Commit**

```bash
git add src/JPVOS/Services/ClaimsEvidence tests/JPVOS.Tests/ClaimsEvidenceServiceTests.cs
git commit -m "feat: add claims evidence domain contracts"
```

### Task 2: Transactional append-only SQLite event store

**Files:**
- Create: `src/JPVOS/Services/ClaimsEvidence/SqliteClaimsEvidenceEventStore.cs`
- Test: `tests/JPVOS.Tests/ClaimsEvidenceEventStoreTests.cs`

**Interfaces:**
- Consumes: `ClaimsEvidenceEvent`, `IClaimsEvidenceEventStore`.
- Produces: transactional `SqliteClaimsEvidenceEventStore` supporting canonical event streams, unique per-case sequence, credential verifier storage, and idempotent result replay.

- [ ] **Step 1: Write failing persistence tests**

Cover: first `CaseReceived` is sequence 1; two appends become sequences 2 and 3; duplicate create with same idempotency key and same request hash returns original case/result without second event; same key with different request hash throws an idempotency conflict; concurrent appends produce unique contiguous sequences; original events cannot be updated through the store API.

- [ ] **Step 2: Run event-store tests and verify failure**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceEventStoreTests"`
Expected: FAIL because `SqliteClaimsEvidenceEventStore` does not exist.

- [ ] **Step 3: Implement schema initialization**

Create tables inside the configured SQLite database:

```sql
CREATE TABLE IF NOT EXISTS claims_case_events (
  event_id TEXT PRIMARY KEY,
  case_id TEXT NOT NULL,
  sequence INTEGER NOT NULL,
  occurred_at_utc TEXT NOT NULL,
  event_type TEXT NOT NULL,
  actor_class TEXT NOT NULL,
  idempotency_key TEXT NULL,
  sensitivity TEXT NOT NULL,
  payload_json TEXT NOT NULL,
  UNIQUE(case_id, sequence)
);
CREATE TABLE IF NOT EXISTS claims_case_access (
  case_id TEXT PRIMARY KEY,
  tracking_verifier TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS claims_idempotency (
  operation_scope TEXT NOT NULL,
  idempotency_key TEXT NOT NULL,
  request_hash TEXT NOT NULL,
  result_json TEXT NOT NULL,
  PRIMARY KEY(operation_scope, idempotency_key)
);
```

Use transactions with `BEGIN IMMEDIATE` semantics for sequence allocation and idempotency insertion. Never expose update/delete methods for canonical events.

- [ ] **Step 4: Implement deterministic request hashing and replay**

Hash a canonical JSON representation of the normalized command with SHA-256. Same scope/key/hash returns stored result. Same scope/key/different hash raises `ClaimsEvidenceIdempotencyConflictException`.

- [ ] **Step 5: Run event-store tests and verify pass**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceEventStoreTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/JPVOS/Services/ClaimsEvidence/SqliteClaimsEvidenceEventStore.cs tests/JPVOS.Tests/ClaimsEvidenceEventStoreTests.cs
git commit -m "feat: add append only claims event store"
```

### Task 3: Deterministic projection and case service

**Files:**
- Create: `src/JPVOS/Services/ClaimsEvidence/ClaimsEvidenceProjector.cs`
- Create: `src/JPVOS/Services/ClaimsEvidence/ClaimsEvidenceService.cs`
- Create: `src/JPVOS/Services/ClaimsEvidence/DisabledEvidenceBlobStore.cs`
- Modify/Test: `tests/JPVOS.Tests/ClaimsEvidenceServiceTests.cs`

**Interfaces:**
- Consumes: `IClaimsEvidenceEventStore`, `ITrackingCredentialService`, canonical events.
- Produces: `ClaimsEvidenceService` and `ClaimsEvidenceProjector` used by the API adapter.

- [ ] **Step 1: Write failing service tests**

Cover successful anonymous/pseudonymous/verified-confidential create; invalid identity rejection; explicit allowance of unknown dates/jurisdiction; supplemental metadata append with correct credential; invalid credential rejection; safe status projection; append-only correction history; external-authority routing represented as routing rather than JPV guilt; good-faith unproven reports not classified as abuse; stewardship does not offset unrelated required remediation; unsupported binary evidence request rejected when only disabled blob store exists.

- [ ] **Step 2: Run service tests and verify failure**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceServiceTests"`
Expected: FAIL for missing service/projector behaviors.

- [ ] **Step 3: Implement projector**

Fold events in ascending sequence into an internal projection. Only map approved case statuses into the public result. Never include raw payloads, identity details, reviewer notes, conflict details, or evidence contents in `CaseStatusResult`.

- [ ] **Step 4: Implement create and supplement flows**

`CreateCaseAsync` validates identity mode and minimum claim/subject input, issues a tracking credential, creates one `CaseReceived` event plus any metadata-only `EvidenceAdded` events transactionally as a single idempotent operation, and returns the credential once. `AddEvidenceAsync` validates the tracking credential and appends metadata-only evidence. Binary content flags route to `IEvidenceBlobStore` and fail closed when disabled.

- [ ] **Step 5: Implement safe authority and classification semantics**

Keep allegation/evidence/verification/authority-determination/founder-commentary event meanings distinct. No public service method creates `PublicationReviewed`, `FounderCommentaryRecorded`, legal-guilt, abuse-sanction, or compulsory-remedy outcomes.

- [ ] **Step 6: Run service tests and verify pass**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceServiceTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/JPVOS/Services/ClaimsEvidence tests/JPVOS.Tests/ClaimsEvidenceServiceTests.cs
git commit -m "feat: implement claims evidence case service"
```

### Task 4: Public API adapter and gateway admission

**Files:**
- Create: `src/JPVOS/Api/ClaimsEvidenceController.cs`
- Modify: `src/JPVOS/Program.cs`
- Create: `tests/JPVOS.Tests/ClaimsEvidenceControllerTests.cs`

**Interfaces:**
- Consumes: `IClaimsEvidenceService`.
- Produces: `POST /api/claims-evidence/cases`, `POST /api/claims-evidence/cases/{caseId}/evidence`, `GET /api/claims-evidence/cases/{caseId}/status`.

- [ ] **Step 1: Write failing controller/admission tests**

Cover missing `Idempotency-Key` => 400; malformed body => 400; create success => 201 without echoing raw claim; supplement requires tracking credential => 403 on missing/invalid; status requires tracking credential and returns only safe status fields; idempotency conflict => 409; oversized body is rejected by configured request limit; routes use the claims/evidence rate-limit policy.

- [ ] **Step 2: Run controller tests and verify failure**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceControllerTests"`
Expected: FAIL because controller and registrations do not exist.

- [ ] **Step 3: Implement controller**

Use `[ApiController]`, route `api/claims-evidence`, explicit request DTOs, `[EnableRateLimiting("ClaimsEvidencePublic")]`, and request-size limits. Read tracking credential from `X-JPV-Case-Tracking`. Never log it. Map validation errors to 400, credential failures to 403, idempotency conflicts to 409, unsupported binary ingestion to 415/422, and persistence failure to 503 without claiming success.

- [ ] **Step 4: Register services and rate limiter in `Program.cs`**

Add a fixed-window or token-bucket policy named `ClaimsEvidencePublic` keyed by remote address and appropriate for public intake. Register one persistent SQLite case database path configurable by `JPV_CLAIMS_DATA_DIR`, with persistent-storage requirement outside Development, `IClaimsEvidenceEventStore`, `ITrackingCredentialService`, disabled `IEvidenceBlobStore`, projector, and `IClaimsEvidenceService`.

- [ ] **Step 5: Run controller tests and verify pass**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidenceControllerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/JPVOS/Api/ClaimsEvidenceController.cs src/JPVOS/Program.cs tests/JPVOS.Tests/ClaimsEvidenceControllerTests.cs
git commit -m "feat: expose governed claims evidence intake api"
```

### Task 5: Full verification and regression gate

**Files:**
- Modify only if defects are discovered by verification.

**Interfaces:**
- Consumes: completed case-engine implementation.
- Produces: verified repository state suitable for PR review.

- [ ] **Step 1: Build**

Run: `dotnet build src/JPVOS/JPVOS.csproj --configuration Release`
Expected: exit 0 with no compile errors.

- [ ] **Step 2: Run focused test suite**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --filter "ClaimsEvidence" --configuration Release`
Expected: all claims/evidence tests pass.

- [ ] **Step 3: Run full test suite**

Run: `dotnet test tests/JPVOS.Tests/JPVOS.Tests.csproj --configuration Release`
Expected: all existing and new tests pass.

- [ ] **Step 4: Verify prohibited public surfaces are absent**

Search for public routes exposing publication, founder commentary, conflict notes, remediation mutations, stewardship mutations, or case search. Expected: none added by this feature.

- [ ] **Step 5: Verify sensitive logging is absent**

Search changed code for logging of claim statement, evidence payload, `X-JPV-Case-Tracking`, or plaintext credential variables. Expected: no ordinary telemetry containing these values.

- [ ] **Step 6: Review diff against spec**

Check every acceptance criterion in `docs/superpowers/specs/2026-09-09-claims-evidence-case-engine-design.md` against code/tests. Fix any discrepancy before completion.

- [ ] **Step 7: Commit verification fixes if needed**

```bash
git add src tests
git commit -m "test: complete claims evidence verification"
```

- [ ] **Step 8: Open implementation PR**

Open a PR from the implementation branch to `main` containing the spec reference, test receipt, and explicit statement that binary evidence ingestion remains disabled without an approved private store.
