# Connor Direct Outbound Transport Design

**Status:** Approved design; implementation not yet admitted
**Date:** 2026-09-08

## Goal

Add a governed direct outbound messaging capability to JPV so an authorized workflow can send an SMS to Connor's bound principal endpoint and return attributable delivery and acknowledgment state. The initial operational use is exact-head GitHub review routing, including PR #437, without treating SMS delivery or acknowledgment as GitHub approval.

## Existing Runtime Context

The implementation belongs in the existing `JayPVentures-LLC/jpv-os-access-gateway` .NET gateway. It will follow the existing controller/service/infrastructure separation used by the gateway and the existing xUnit test project under `tests/JPVOS.Tests`.

The repository's provider-neutral deployment boundary remains authoritative. Twilio may be the first SMS provider implementation, but JPV domain and routing code must depend only on a provider-neutral transport interface.

## Governing Constraints

1. The bound principal is `github:jaypventuresllc-admin`; SMS routing must resolve from that principal identity rather than from an unbound phone-number argument supplied by a caller.
2. The phone number, provider credentials, webhook secrets, and equivalent sensitive values must never be committed to source control. Runtime secrets/configuration hold those values.
3. Sending fails closed when the target principal binding is absent, malformed, unverified, expired, revoked, or does not match the requested principal.
4. Sending fails closed when the request lacks authorized JPV purpose/scope.
5. SMS delivery or acknowledgment proves only delivery/receipt state. It can never satisfy or promote an independent GitHub review requirement.
6. GitHub approval for consequential governance changes remains attributable only to the bound GitHub reviewer account and exact reviewed head.
7. Provider callbacks and inbound messages must be authenticated, idempotent, replay-resistant, and attributable before they can mutate receipt state.
8. Provider-specific status values must be normalized to the JPV state model.
9. The transport must preserve vendor portability: replacing Twilio must not require changing principal resolution, authorization, receipt semantics, PR-review routing, or acknowledgment rules.
10. No generic marketing, bulk-notification, or arbitrary-recipient SMS surface is in scope.

## State Model

A message receipt has one of these JPV states:

- `QUEUED` — JPV admitted the request and the provider accepted it for processing.
- `SENT` — provider evidence indicates the message left the provider's sending path.
- `DELIVERED` — provider evidence indicates carrier/device delivery where supported.
- `FAILED` — sending or delivery reached a terminal failure.
- `ACKNOWLEDGED` — an attributable inbound response or valid one-time acknowledgment token proves the recipient acted on the delivery request.

State transitions are monotonic except that a terminal provider failure may be recorded after a nonterminal provider state. Duplicate or stale callbacks must not regress a stronger state.

## Components

### 1. Principal Endpoint Binding

Create a provider-neutral principal binding contract that maps a canonical principal identifier to an SMS endpoint reference without exposing the phone number to ordinary callers.

The Connor binding resolves the canonical principal `github:jaypventuresllc-admin`. The runtime resolver reads the E.164 endpoint from a deployment secret/configuration value and returns a binding only when all required verification metadata is valid.

Required binding semantics:

- canonical principal ID
- channel type `sms`
- secret-backed endpoint reference
- verification status
- verification timestamp/version
- revocation status
- optional expiry

### 2. Authorization Admission

Every send request carries an explicit purpose and authority context. The outbound service admits only recognized governed purposes. The first supported purpose is `github_exact_head_review_request`.

For that purpose, the request must include:

- repository full name
- pull request number
- exact head SHA
- GitHub URL
- target principal ID
- requesting authority identity/context

The service rejects arbitrary message bodies for this governed route. Message text is built from structured fields to prevent callers from bypassing routing semantics.

### 3. Provider-Neutral Transport Interface

Define an interface that accepts a normalized SMS send command and returns a provider message reference plus normalized initial status.

The interface owns no JPV approval logic. It is responsible only for provider transmission and provider-callback validation/parsing.

### 4. Twilio Adapter

Implement the first transport adapter using Twilio behind the provider-neutral interface.

The adapter reads credentials and sender configuration from runtime secrets. It must support:

- outbound SMS send
- status callback validation and normalization
- inbound SMS webhook validation
- provider message ID capture
- deterministic error mapping without leaking secrets

No Twilio type may appear in the JPV domain contracts or PR-routing service interfaces.

### 5. Durable Receipt Store

Persist message receipts and processed provider event IDs so delivery and acknowledgment state survives process restart and duplicate callbacks cannot create duplicate transitions.

Use the gateway's existing persistence conventions. Receipt data includes:

- JPV message ID
- target principal ID
- governed purpose
- correlation data for repository/PR/exact head
- provider adapter name
- provider message ID
- normalized state
- timestamps for admitted, sent, delivered, failed, acknowledged
- acknowledgment evidence type/reference
- processed callback/event identifiers

The persisted receipt must not contain the target phone number or provider credential material.

### 6. Outbound Transport Service

The service orchestrates:

`authorized request -> principal binding resolution -> admission validation -> structured message construction -> provider send -> receipt persistence -> normalized result`

It returns the JPV message ID and current normalized status. Provider failure returns a deterministic failure result and persists terminal evidence when a provider message reference exists.

### 7. Webhook Intake

Expose provider webhook endpoints through the gateway API for:

- outbound delivery/status callbacks
- inbound SMS replies

Webhook processing must validate provider authenticity before parsing or mutating state. Duplicate event delivery is idempotent. Unknown provider message IDs, mismatched sender endpoints, malformed payloads, invalid signatures, stale/replayed acknowledgment tokens, or principal-binding mismatch fail closed.

### 8. Acknowledgment

The first acknowledgment mechanisms are:

- an attributable inbound SMS reply from Connor's verified bound endpoint; or
- a one-time acknowledgment token tied to one JPV message ID and target principal, if a future provider/channel requires token-based acknowledgment.

An acknowledgment transition records evidence and timestamp. It does not produce, imply, or synthesize a GitHub review.

### 9. GitHub Exact-Head Review Routing

Add a routing helper for governed GitHub review requests. It accepts only structured exact-head review metadata and builds a concise SMS containing:

- JPV governance review required
- repository and PR number
- exact head SHA or an unambiguous shortened display plus full SHA in the receipt correlation record
- GitHub PR link
- acknowledgment instruction

For PR #437 the correlation target is `JayPVentures-LLC/jpv-governance#437` at exact head `711d306e4cfe484ce6dfe9b1d23b2308e74bc380` unless that head changes before actual send. The runtime must re-read the live PR head before dispatch and refuse to send stale exact-head review instructions.

## API Surface

Internal/governed API endpoints:

- `POST /api/outbound/github-review` — authenticated governed request to send an exact-head review notification.
- `GET /api/outbound/receipts/{messageId}` — authenticated status/readback for a JPV message receipt.
- `POST /api/outbound/providers/twilio/status` — Twilio delivery/status webhook.
- `POST /api/outbound/providers/twilio/inbound` — Twilio inbound SMS webhook.

The public API must not accept a raw destination phone number.

## Configuration and Secrets

Configuration names may include:

- `JPV_OUTBOUND_SMS_PROVIDER=twilio`
- `JPV_PRINCIPAL_CONNOR_SMS_E164`
- `JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT`
- `JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION`
- `TWILIO_ACCOUNT_SID`
- `TWILIO_AUTH_TOKEN`
- `TWILIO_MESSAGING_SERVICE_SID` or `TWILIO_FROM_NUMBER`
- `JPV_OUTBOUND_WEBHOOK_BASE_URL`
- acknowledgment-signing material if token acknowledgment is enabled

No real secret value belongs in repository files, test fixtures, logs, receipts, or API responses.

## Security and Privacy

- Verify provider webhook signatures before any state mutation.
- Compare principal IDs and endpoint bindings using canonical normalized values.
- Never log complete phone numbers or provider authentication material.
- Mask provider diagnostic data before persistence or response.
- Protect internal send/readback routes using the gateway's existing founder/authority authentication pattern and explicit purpose admission.
- Use single-use event/acknowledgment identifiers to reject replay.
- Treat unknown or ambiguous identity state as denial, not fallback.
- Preserve the distinction between communication receipt and decision authority.

## Error Semantics

The service returns explicit machine-readable denial/failure reasons, including:

- `principal_binding_missing`
- `principal_binding_unverified`
- `principal_binding_revoked`
- `principal_binding_expired`
- `principal_mismatch`
- `authority_denied`
- `purpose_not_supported`
- `exact_head_stale`
- `provider_unavailable`
- `provider_rejected`
- `webhook_auth_invalid`
- `webhook_replay`
- `receipt_not_found`
- `acknowledgment_not_attributable`

Provider-specific codes may be retained as redacted evidence but do not replace JPV error semantics.

## Testing

The xUnit suite must cover at minimum:

1. verified Connor binding resolves and raw arbitrary destinations cannot be supplied;
2. missing, malformed, expired, or revoked bindings deny sending;
3. mismatched target principal denies sending;
4. unauthorized or unsupported-purpose requests deny sending;
5. exact-head review routing rejects a stale head;
6. successful provider send persists provider reference and `QUEUED`/`SENT` state;
7. provider rejection records deterministic failure without leaking credentials;
8. valid delivery callback promotes state to `DELIVERED`;
9. duplicate callback is idempotent;
10. invalid webhook signature cannot mutate state;
11. replayed callback or acknowledgment cannot create a second transition;
12. attributable inbound reply promotes the matching receipt to `ACKNOWLEDGED`;
13. inbound reply from an unbound/mismatched endpoint cannot acknowledge;
14. acknowledgment for one message cannot acknowledge another;
15. SMS `ACKNOWLEDGED` never satisfies or fabricates GitHub `APPROVED` state;
16. provider adapter replacement can be tested through the same transport interface without changing domain behavior.

## Operational Acceptance Criteria

Implementation is ready for governed release only when all of the following are true:

- repository tests and applicable governance/security checks pass on the exact head;
- no committed file contains Connor's phone number or provider secrets;
- the provider-neutral interface is the only dependency used by domain/routing code;
- a test-provider end-to-end path proves send -> status callback -> delivered -> attributable acknowledgment;
- production configuration can resolve Connor's verified binding without exposing it in source control;
- a live provider test, when explicitly authorized and configured, produces a provider message ID and delivery receipt;
- PR-review routing performs a live exact-head read before dispatch;
- SMS acknowledgment remains technically incapable of satisfying the GitHub independent-review gate;
- PR changes are merged only through the repository's required review/check path.

## Non-Goals

This design does not create a bulk SMS platform, marketing system, arbitrary-recipient messaging API, or mechanism for approving GitHub changes by text message. It does not infer Connor's consent, decisions, or review outcome. It creates only a governed delivery and acknowledgment transport to his verified bound endpoint.
