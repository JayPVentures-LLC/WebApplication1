# Connor Direct Outbound Transport Design

**Status:** Approved and expanded to two-way direct communication
**Date:** 2026-09-08

## Goal

Add a governed direct messaging capability to JPV so Jay and Connor can exchange SMS messages through Connor's verified bound principal endpoint with attributable delivery, inbound reply capture, durable conversation history, and review-request acknowledgment where applicable. GitHub-review routing remains a governed use case, but the transport is also a direct two-way founder communication line. SMS delivery or acknowledgment never substitutes for GitHub approval.

## Existing Runtime Context

The implementation belongs in the existing `JayPVentures-LLC/jpv-os-access-gateway` .NET gateway and follows its controller/service/infrastructure separation and xUnit test project under `tests/JPVOS.Tests`.

The repository's provider-neutral deployment boundary remains authoritative. Twilio is the first SMS provider implementation, but JPV domain and conversation code depend only on provider-neutral transport interfaces.

## Governing Constraints

1. The bound principal is `github:jaypventuresllc-admin`; callers cannot supply an arbitrary destination phone number.
2. Connor's phone number, provider credentials, webhook secrets, and equivalent sensitive values remain runtime secrets and are never committed.
3. Sending and inbound attribution fail closed when the principal binding is absent, malformed, unverified, expired, revoked, or mismatched.
4. Founder-originated sends require authorized founder context.
5. Direct conversation messages are freeform between the authorized founder surface and Connor's verified endpoint; arbitrary-recipient or bulk messaging is not permitted.
6. Provider callbacks and inbound messages must be authenticated, idempotent, replay-resistant, and attributable before durable state mutation.
7. Provider-specific statuses are normalized into JPV states.
8. The transport preserves vendor portability: provider replacement must not change principal resolution, conversation semantics, receipt semantics, review routing, or acknowledgment rules.
9. SMS delivery or acknowledgment proves communication state only. It can never satisfy or promote an independent GitHub review requirement.
10. GitHub approval for consequential governance changes remains attributable only to the bound GitHub reviewer account and exact reviewed head.

## Communication Model

JPV maintains one canonical direct conversation with Connor: `direct:github:jaypventuresllc-admin`.

Each conversation message records:

- JPV message ID;
- canonical conversation ID;
- principal ID;
- direction (`Outbound` or `Inbound`);
- message body;
- timestamp;
- provider message ID where available;
- normalized delivery state where applicable.

Outbound freeform messages are sent only to the secret-backed Connor binding. Attributable inbound SMS from that same verified endpoint is persisted to the same conversation. Duplicate provider message IDs are idempotent.

## Review Request Model

Governed GitHub review requests remain structured and distinct from ordinary direct messages. Before dispatch, JPV re-reads the live GitHub PR head and refuses stale exact-head instructions.

Each review request generates a message-specific acknowledgment code tied to the JPV message ID. The SMS instructs Connor to reply `ACK <code>`. Inbound acknowledgment resolution uses principal + acknowledgment code, not the latest message for the principal, so an older request cannot acknowledge a different PR or head.

`ACKNOWLEDGED` is receipt evidence only. The acknowledgment service has no GitHub approval dependency or mutation path.

## State Model

Review/delivery receipts use:

- `QUEUED` — provider accepted the outbound message;
- `SENT` — provider reports send progression;
- `DELIVERED` — provider reports delivery where supported;
- `FAILED` — terminal provider failure before stronger attributable acknowledgment;
- `ACKNOWLEDGED` — attributable inbound acknowledgment for the specific message.

State transitions do not regress. Once a receipt is `ACKNOWLEDGED`, later delayed `failed` or `undelivered` callbacks cannot overwrite that stronger state.

## Components

### Principal Endpoint Binding

The runtime resolver maps `github:jaypventuresllc-admin` to a secret-backed E.164 SMS endpoint with verification metadata, revocation status, binding version, and optional expiry.

### Provider-Neutral SMS Transport

`ISmsTransport` accepts normalized send commands and returns provider message references plus normalized state. Twilio-specific code remains under `Infrastructure/Twilio`.

### Twilio Adapter

The Twilio adapter supports outbound SMS, provider message ID capture, status normalization, and HMAC-SHA1 callback signature validation. Callback validation uses the configured public `JPV_OUTBOUND_WEBHOOK_BASE_URL`, not proxy-internal request scheme/host values.

### Durable Review Receipt Store

The JSONL receipt store persists review message correlation, provider IDs, normalized state, timestamps, acknowledgment code/evidence, and processed provider-event IDs. It never persists Connor's destination phone number.

Status callbacks locate the receipt before claiming the provider event ID, preventing valid early callbacks from being permanently consumed before receipt persistence.

### Durable Direct Conversation Store

The JSONL conversation store persists inbound and outbound conversation messages for the canonical Connor conversation. It deduplicates attributable inbound provider message IDs and exposes an authenticated transcript readback surface.

### Direct Conversation Service

The direct conversation service handles:

`authorized founder message -> Connor binding resolution -> provider send -> durable outbound conversation message`

and:

`authenticated inbound provider callback -> Connor endpoint attribution -> duplicate check -> durable inbound conversation message`

### Review Acknowledgment Service

The review acknowledgment service resolves only attributable `ACK <code>` replies against exact receipt correlation and mutates only receipt acknowledgment state. It does not depend on any GitHub approval client or reviewer mutation service.

## API Surface

Authenticated founder endpoints:

- `POST /api/outbound/conversation/connor/messages` — send a freeform direct message to Connor's verified bound endpoint.
- `GET /api/outbound/conversation/connor/messages` — read the durable two-way Connor conversation transcript.
- `POST /api/outbound/github-review` — send a structured exact-head GitHub review request.
- `GET /api/outbound/receipts/{messageId}` — read a delivery/review receipt.

Provider callback endpoints:

- `POST /api/outbound/providers/twilio/status` — authenticated Twilio delivery/status callback.
- `POST /api/outbound/providers/twilio/inbound` — authenticated Twilio inbound SMS callback for freeform replies and exact review acknowledgments.

No send endpoint accepts a raw destination phone number.

## Configuration and Secrets

Runtime configuration includes:

- `JPV_OUTBOUND_SMS_PROVIDER=twilio`
- `JPV_PRINCIPAL_CONNOR_SMS_E164`
- `JPV_PRINCIPAL_CONNOR_SMS_VERIFIED_AT`
- `JPV_PRINCIPAL_CONNOR_SMS_BINDING_VERSION`
- optional revocation/expiry metadata
- `TWILIO_ACCOUNT_SID`
- `TWILIO_AUTH_TOKEN`
- `TWILIO_MESSAGING_SERVICE_SID` or `TWILIO_FROM_NUMBER`
- `JPV_OUTBOUND_WEBHOOK_BASE_URL`

No real secret value belongs in repository files, test fixtures, logs, receipts, conversation storage, or API responses.

## Security and Privacy

- Verify Twilio signatures against the configured public callback URL before any callback mutation.
- Compare principal IDs and endpoint bindings using canonical normalized values.
- Never log complete phone numbers or provider authentication material.
- Reject inbound SMS from any endpoint other than Connor's verified binding.
- Reject arbitrary-recipient sends.
- Preserve provider-event idempotency and replay resistance.
- Keep freeform communication state separate from governance approval state.
- Preserve the distinction between communication receipt and decision authority.

## Testing

The xUnit suite covers:

1. verified Connor binding resolution and failure on missing/malformed/mismatched binding;
2. no arbitrary destination number input;
3. unauthorized send denial;
4. stale exact-head review rejection before provider send;
5. outbound review receipt persistence without destination number;
6. message-specific review acknowledgment correlation;
7. actual acknowledgment workflow mutating only receipt state with no GitHub approval dependency;
8. durable receipt event idempotency;
9. valid/invalid Twilio signature behavior;
10. provider status normalization;
11. freeform outbound direct-message persistence;
12. attributable freeform inbound reply persistence;
13. rejection of inbound messages from unbound phone numbers;
14. duplicate inbound provider message idempotency;
15. provider-neutral deployment-boundary validation.

## Operational Acceptance Criteria

Implementation is ready for governed release only when all of the following are true:

- repository tests and applicable governance/security checks pass on the exact head;
- no committed file contains Connor's real phone number or provider secrets;
- provider-specific code remains behind the provider-neutral transport boundary;
- the direct conversation path supports outbound send, inbound reply capture, transcript readback, and duplicate protection;
- production configuration resolves Connor's verified binding without exposing it in source control;
- production provider configuration supplies a working sending identity and callback URL;
- a live provider test produces a provider message ID and receives an authenticated inbound reply/delivery callback;
- exact-head review routing performs a live head read before dispatch;
- SMS acknowledgment remains technically incapable of satisfying the GitHub independent-review gate;
- PR changes are merged only through the repository's required review/check path.

## Non-Goals

This design does not create a bulk SMS platform, marketing system, arbitrary-recipient messaging API, or mechanism for approving GitHub changes by text message. It does not infer Connor's decisions or review outcome. The freeform direct-message capability is intentionally limited to the verified Connor principal binding and authorized founder surface.
