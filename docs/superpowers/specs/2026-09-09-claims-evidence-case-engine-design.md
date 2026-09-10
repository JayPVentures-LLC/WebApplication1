# Claims & Evidence Case Engine Design

Status: Approved architecture; written-spec confirmation required before implementation
Authority domain: JPV-OS / enterprise governance

## Purpose

Turn the approved JPV Claims & Evidence Operating Layer into executable infrastructure without creating a parallel public gateway or giving JPV legal authority it does not possess.

The existing `jpv-os-access-gateway` remains the sole external entry point. A logically isolated claims/evidence module inside the current .NET application owns the private case engine. The implementation SHALL consume JPV governance as authoritative policy and SHALL NOT redefine legal guilt, liability, regulatory findings, or compulsory remedies.

Canonical governance authority resides in `JayPVentures-LLC/jpv-governance`, including:

- `governance/procedures/CLAIMS-EVIDENCE-OPERATING-PROCEDURE.md`
- `governance/templates/CLAIM-EVIDENCE-INTAKE.md`
- `governance/templates/CASE-VERIFICATION-REVIEW.md`
- `governance/templates/CASE-STATUS-ROUTING-RECORD.md`
- `governance/templates/PUBLICATION-COMMENTARY-REVIEW.md`
- `governance/templates/REMEDIATION-STEWARDSHIP-AUDIT.md`
- `governance/policies/REPAIR-HARM-REWARD-STEWARDSHIP-STANDARD.md`
- `governance/policies/EVIDENCE-BASED-ESCALATING-SCRUTINY-STANDARD.md`

## Architectural decision

The system SHALL use the existing ASP.NET JPV-OS access gateway as the public edge and implement the case engine as an internally isolated service boundary within this repository.

This is preferred over a second public intake service because it preserves one external trust boundary. It is preferred over placing all case logic directly in controllers because the case lifecycle, evidence integrity, and audit rules require an independently testable domain boundary.

The module MAY later be extracted into a separately deployed service without changing the public API contract or event semantics. Extraction is not required for this phase.

## System flow

`external submission → access-gateway admission → normalized intake command → private case engine → append-only case events → deterministic case projection → authorized status/routing/remediation/publication outputs`

The public edge SHALL fail closed. Malformed, unauthorized, oversized, or admission-rejected requests SHALL NOT create case-engine events.

## Component boundaries

### 1. Public API adapter

A claims/evidence API controller under `src/JPVOS/Api` SHALL expose only the minimum public surface required for this phase:

- `POST /api/claims-evidence/cases` — create a case submission;
- `POST /api/claims-evidence/cases/{caseId}/evidence` — append supplemental evidence metadata to an existing case;
- `GET /api/claims-evidence/cases/{caseId}/status` — return submitter-safe case status.

Internal verification, conflict review, routing, remediation, stewardship, publication approval, and founder-commentary mutations SHALL NOT be exposed as unauthenticated public endpoints in this phase.

### 2. Gateway admission layer

The existing access gateway SHALL perform edge responsibilities before dispatching to the case engine:

- request-size limits;
- rate limiting;
- schema/input validation;
- identity-mode normalization;
- idempotency validation;
- tracking-credential validation for case-scoped follow-up;
- basic abuse and threat admission screening that does not make truth determinations;
- upload/reference authorization where applicable; and
- removal of unnecessary edge metadata before private storage.

Abuse screening SHALL distinguish transport or safety abuse from factual credibility. It SHALL NOT label a report false merely because it is anonymous, controversial, uncorroborated, or ultimately incorrect in good faith.

### 3. Claims/evidence domain service

A focused module under `src/JPVOS/Services/ClaimsEvidence` SHALL own case semantics and expose internal interfaces for commands and projections.

It SHALL generate immutable case IDs and append immutable case events. It SHALL enforce valid lifecycle transitions and ensure that corrections, supplements, and later findings add history rather than overwrite it.

### 4. Append-only event store

The canonical executable case history SHALL be an append-only event stream, not a mutable current-state record.

Each event SHALL contain at minimum:

- `EventId`;
- `CaseId`;
- per-case monotonically increasing `Sequence`;
- `OccurredAtUtc`;
- event type;
- actor class or protected actor reference;
- idempotency/causation reference where applicable;
- sensitivity classification;
- structured payload; and
- integrity metadata sufficient to detect accidental or unauthorized mutation.

The initial production implementation SHALL use a transactional SQLite event table because the application already uses SQLite-backed persistence, and transactional append semantics provide deterministic sequencing and idempotency without adding a new infrastructure vendor dependency. The storage contract SHALL remain interface-based so another durable backend can replace SQLite without changing domain semantics.

Evidence binary content SHALL NOT be embedded in the event table. The event stream stores evidence metadata, cryptographic digest when available, provenance, and an authorized opaque storage reference.

### 5. Evidence object storage abstraction

`IEvidenceBlobStore` SHALL separate binary evidence storage from case events.

The public API SHALL NOT accept arbitrary permanent public URLs as a substitute for controlled evidence storage. Production binary ingestion requires a private authorized storage implementation with access logging, size/type limits, integrity hashing, and non-public object identifiers.

Binary ingestion is not enabled in this phase unless an approved private `IEvidenceBlobStore` implementation is already configured. When no approved private binary backend is configured, the service SHALL reject binary upload attempts rather than silently fall back to public or local ephemeral storage. Metadata-only evidence submissions remain valid.

### 6. Case projection

Current case state SHALL be derived deterministically from the event stream. The projection MAY be cached or persisted for performance, but it SHALL be reproducible from canonical events.

Corrections alter the current projection while preserving the original proposition and correction history. Supplemental evidence adds new evidence entries. Status transitions do not delete earlier states.

## Event model

The domain SHALL support at least these event meanings:

- `CaseReceived`
- `EvidenceAdded`
- `VerificationUpdated`
- `CorrectionRequested`
- `CorrectionResolved`
- `StatusChanged`
- `ConflictRecorded`
- `Routed`
- `RemediationRecorded`
- `StewardshipReviewed`
- `PublicationReviewed`
- `FounderCommentaryRecorded`
- `EvidenceDispositionRecorded`
- `CaseClosed`
- `CaseReopened`

Public endpoints in this phase generate only events they are authorized to generate. Internal-only event types remain available through internal service interfaces for later authorized JPV workflows.

## Identity and tracking model

The executable intake contract SHALL support exactly these identity modes:

- `anonymous`;
- `pseudonymous`;
- `verified-confidential`.

Identity mode affects authentication and verification methods, not evidentiary truth.

Every newly accepted public case SHALL receive a high-entropy tracking credential. The plaintext credential is returned only to the submitter at creation time and SHALL NOT be persisted or logged in plaintext. The server stores a one-way cryptographic verifier or equivalent protected representation.

Anonymous users use the tracking credential as their continuing case channel. Pseudonymous and verified-confidential cases MAY additionally bind to an authenticated principal, but public status and supplemental-evidence access SHALL still be authorized explicitly rather than inferred from identity alone.

## Idempotency

`POST` operations SHALL require an `Idempotency-Key` header and SHALL provide deterministic retry behavior.

For a given operation scope and idempotency key, the service SHALL persist enough information to return the original successful result on a legitimate retry without creating a second case or duplicate evidence event.

Reusing the same idempotency key with materially different request content SHALL be rejected.

## Intake schema

A create-case request SHALL include at minimum:

- identity mode;
- claim statement;
- subject/person/entity description;
- relevant dates or `unknown` where appropriate;
- relevant jurisdictions if known;
- affected parties if known;
- confidentiality request;
- urgency indicators;
- evidence metadata collection, which may be empty; and
- explicit acknowledgment that submission is not an official legal finding or automatic publication authorization.

The schema SHALL permit uncertainty. It SHALL NOT require a submitter to invent facts merely to satisfy validation.

## Evidence metadata

Each evidence item SHALL include:

- evidence ID generated by the system;
- case ID;
- received timestamp;
- submitter identity mode or protected source classification;
- description;
- source/provenance description;
- date created if known;
- cryptographic digest if content is ingested or otherwise verifiably hashed;
- opaque storage reference if content is stored;
- confidentiality/sensitivity classification;
- known authenticity limitations; and
- relationship to the claim or existing evidence.

Evidence IDs and original metadata are immutable. Reviewer annotations and later verification findings are separate events.

## Status read model

The public status endpoint SHALL expose only submitter-safe states:

- `received`
- `verification-in-progress`
- `additional-evidence-requested`
- `routed`
- `action-in-progress`
- `closed`
- `resolved`
- `reopened`

It SHALL NOT return protected identities, confidential evidence content, privileged material, internal reviewer notes, protected investigative details, or information that could compromise rights, safety, evidence integrity, or active proceedings.

## Authority boundary

The software SHALL encode the distinction among allegation, submitted evidence, evidence-supported proposition, verified fact, unresolved dispute, inference, competent-authority determination, and founder commentary.

No automated transition SHALL convert an allegation directly into criminal guilt, civil liability, regulatory violation, or any other compulsory legal determination.

JPV-internal decisions MAY be recorded only where JPV has actual authority. Matters requiring external authority are represented as routing actions and external determination references rather than JPV legal findings.

## Publication boundary

Cases are private by default.

There SHALL be no unauthenticated public publication endpoint in this phase. A case, allegation, evidence item, identity, or status SHALL NOT become public merely because it was submitted.

Future publication integrations, including any adapter to `jpv-public-records`, SHALL consume only an explicitly approved publication artifact generated after the governance publication review. They SHALL NOT read the private case event store directly for public rendering.

Founder commentary is a separate record class and SHALL NOT mutate evidentiary classifications or bypass publication review.

## Remediation and stewardship boundary

The domain model SHALL preserve the distinction between:

- enforceable or authorized remediation/obligations; and
- voluntary stewardship/public-benefit activity.

A stewardship event SHALL NOT reduce or mark an unrelated enforceable obligation satisfied unless an authorized legal rule explicitly permits that treatment.

## Security and privacy

The implementation SHALL:

- minimize personal data collected at the edge;
- avoid logging claim bodies, evidence contents, tracking credentials, or confidential identity material in ordinary request logs;
- keep secrets outside the repository;
- store tracking credentials only as protected verifiers;
- use constant-time credential comparison where applicable;
- use private evidence references rather than public object URLs;
- enforce request-size limits before expensive processing;
- rate-limit public intake and follow-up routes;
- reject unsupported binary ingestion when no authorized private backend exists;
- preserve a legal-hold indicator that prevents normal retention cleanup; and
- expose internal mutation operations only through authenticated/authorized JPV pathways.

Retention rules SHALL be classification-driven and SHALL preserve active cases, legal holds, and the minimum auditable historical record required by governance and applicable law. Retention cleanup, when authorized, SHALL operate on eligible stored content without rewriting the immutable event history; an `EvidenceDispositionRecorded` event records what was lawfully removed and why.

## Failure semantics

The system SHALL fail closed when admission, identity authorization, idempotency validation, event persistence, or evidence-storage integrity cannot be established.

A case-creation response SHALL not report success until the canonical `CaseReceived` event is durably committed.

An evidence-add response SHALL not report success until its `EvidenceAdded` event is durably committed and, when binary content is accepted, the content digest and private storage reference are durably associated.

If event persistence fails after evidence content has been staged, the storage implementation SHALL either clean up the uncommitted object or mark it unreachable for deterministic reconciliation. It SHALL NOT create an untracked public object.

## Concurrency

The event store SHALL enforce unique `(CaseId, Sequence)` and operation/idempotency constraints transactionally. Concurrent supplements to the same case SHALL serialize at append time or retry safely without losing an event.

## Automation and AI boundary

Automation MAY assist with normalization, duplicate detection, metadata completeness, safe routing suggestions, and reviewer support.

Automation SHALL NOT independently make final high-impact determinations of truth, legal guilt, liability, publication approval, abuse sanctions, or compulsory remedies.

## Observability and audit

Operational telemetry SHALL record request IDs, outcome classes, latency, status codes, and non-sensitive event identifiers. It SHALL NOT place raw allegations, evidence, credentials, or confidential identities into general-purpose logs.

Each accepted mutation SHALL have a correlation path from gateway request ID to case event ID without requiring sensitive content in telemetry.

## Compatibility with JPV infrastructure

The implementation SHALL use the repository's current .NET/C# application and xUnit test conventions. It SHALL not introduce a second web framework or require GitHub Actions as an execution dependency.

The design SHALL remain provider-neutral at the domain and storage interfaces. A provider-specific implementation MAY sit behind an interface only when its failure cannot redefine JPV governance semantics or become the sole unrecoverable source of case truth.

## Test requirements

The implementation SHALL include unit and integration coverage for at least:

1. successful anonymous intake and one-time tracking credential issuance;
2. pseudonymous intake;
3. verified-confidential intake without identity leakage;
4. invalid identity mode rejection;
5. required-field validation while permitting `unknown` factual fields;
6. duplicate create retry returning the original case without creating a second `CaseReceived` event;
7. conflicting reuse of an idempotency key rejection;
8. authenticated supplemental evidence append;
9. invalid tracking credential rejection;
10. tampered or mismatched evidence digest rejection when content is ingested;
11. append-only correction history;
12. concurrent event sequencing without lost events;
13. submitter-safe status redaction;
14. conflict/high-severity state remaining internal;
15. unauthorized publication attempts failing closed;
16. external-authority routing represented without a JPV guilt finding;
17. good-faith unproven reports not automatically classified as abuse;
18. stewardship unable to offset unrelated required remediation;
19. binary upload rejection when no approved private evidence store is configured;
20. plaintext tracking credentials and raw evidence content absent from ordinary telemetry; and
21. lawful evidence disposition creating `EvidenceDispositionRecorded` without rewriting earlier case events.

## Acceptance criteria

The phase is accepted when:

- all public claims/evidence traffic enters through the existing access gateway;
- accepted case creation is transactional and idempotent;
- the canonical case history is append-only and reconstructable;
- submitters can add evidence and retrieve a safe status using protected tracking credentials;
- identity modes are supported without treating identity as truth;
- evidence metadata preserves provenance and integrity references;
- private evidence cannot silently become public;
- JPV's legal-authority boundary is enforced in code semantics;
- remediation and stewardship cannot be conflated;
- sensitive content is excluded from ordinary telemetry;
- the module can be tested independently from UI components; and
- the implementation passes the repository's applicable build and test gates.

## Out of scope for this phase

The following are intentionally excluded rather than left ambiguous:

- a public case-search portal;
- public display of allegations or case files;
- automated legal adjudication;
- autonomous publication decisions;
- autonomous abuse sanctions;
- a full investigator/reviewer user interface;
- a new external gateway or new web framework;
- migration of existing historical investigations into the event store;
- extraction of the module into a separately deployed repository/service; and
- activation of binary evidence ingestion without an approved private evidence-storage implementation.
