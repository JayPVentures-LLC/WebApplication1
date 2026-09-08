# Connor Direct Outbound Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a governed provider-neutral two-way SMS line in JPV so Jay can message Connor, Connor can reply, both directions persist in one protected founder conversation, and exact-head GitHub review requests retain independent approval semantics.

**Architecture:** The existing .NET 8 access gateway contains provider-neutral outbound contracts, a secret-backed Connor principal binding, Twilio adapter, durable review receipts, durable direct-conversation storage, inbound callback handling, exact-head GitHub review routing, and a protected interactive founder page at `/workspace/connor`. Twilio is an adapter only; conversation and governance logic do not depend on Twilio types.

**Tech Stack:** .NET 8, ASP.NET Core, Blazor Interactive Server, HttpClient, JSONL persistence, System.Text.Json, System.Security.Cryptography, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-08-connor-direct-outbound-transport-design.md`

## Global Constraints

- Canonical target principal: `github:jaypventuresllc-admin`.
- No raw destination phone number in any founder send surface.
- Phone number and provider credentials are runtime secrets only.
- Fail closed on missing/unverified/revoked/expired/mismatched binding or authority.
- Twilio remains behind the provider-neutral `ISmsTransport` interface.
- Webhook mutations require authenticated, replay-resistant provider evidence.
- Direct inbound replies are accepted only from Connor's verified bound endpoint.
- Review acknowledgments are message-specific (`ACK <code>`) and cannot satisfy GitHub approval.
- Exact-head GitHub review routing re-reads the live PR head before dispatch.
- Protected founder UI exposes transcript, compose/send, refresh, inbound/outbound attribution, and delivery state.

---

### Task 1: Transport contracts and principal binding

- [x] Add failing tests for verified/missing/malformed/mismatched bindings.
- [x] Record RED CI evidence.
- [x] Implement provider-neutral contracts and fail-closed secret-backed Connor binding.
- [x] Verify tests/build.

### Task 2: Governed exact-head review routing

- [x] Add stale-head and receipt tests.
- [x] Implement live GitHub App exact-head reader.
- [x] Implement structured review sends with message-specific acknowledgment code.
- [x] Persist durable review receipts without destination numbers.

### Task 3: Twilio provider adapter and callback security

- [x] Add signature/status tests.
- [x] Implement Twilio send adapter behind `ISmsTransport`.
- [x] Validate callback signatures against configured public webhook URL.
- [x] Normalize provider status and protect monotonic receipt state.
- [x] Locate receipts before claiming callback event IDs.

### Task 4: Two-way Connor conversation

- [x] Add RED tests for freeform founder send, attributable inbound reply, unbound-number rejection, duplicate inbound event, and review ACK correlation.
- [x] Implement `DirectConversationService`.
- [x] Implement durable `JsonlDirectConversationStore`.
- [x] Route authenticated Twilio inbound replies into the canonical Connor conversation.
- [x] Keep review ACK processing isolated from GitHub approval mutation.

### Task 5: Founder-facing direct line

- [x] Add protected `/workspace/connor` interactive page.
- [x] Display durable inbound/outbound transcript.
- [x] Add freeform compose/send action.
- [x] Add explicit refresh and delivery-state display without background polling.
- [x] Add direct-line entry to the founder workspace.
- [x] Fix Interactive Server render-mode compile binding.

### Task 6: Review-defect remediation

- [x] Replace weak ACK/approval enum assertion with real acknowledgment workflow regression test.
- [x] Correlate review ACKs to exact messages.
- [x] Validate Twilio signatures against public callback URL.
- [x] Prevent early callback event loss.
- [x] Prevent late failure callbacks from overwriting `Acknowledged`.
- [x] Apply code-quality cleanup for JSONL filtering and temp-file construction.
- [x] Reply to and resolve addressed review threads.

### Task 7: Exact-head release gates

- [ ] Final exact-head CI Build PASS.
- [ ] Final exact-head Authority Accountability PASS.
- [ ] Final exact-head Completion-Bounded Governance PASS.
- [ ] Final exact-head JPV Security Inheritance PASS.
- [ ] Final exact-head Stripe/Azure validation PASS.
- [ ] Final exact-head Container Build PASS.
- [ ] Fresh automated review on final exact head has no unresolved substantive defects.
- [ ] `jaypventuresllc-admin` submits independent `APPROVED` review on final exact head.
- [ ] Repository signing requirements are satisfied by an environment capable of signed commits.

### Task 8: Production activation and live end-to-end proof

- [ ] Deploy the approved final container revision to the production gateway.
- [ ] Provision production secret `JPV_PRINCIPAL_CONNOR_SMS_E164` with Connor's verified E.164 endpoint plus verification metadata.
- [ ] Provision Twilio account/auth and sender/messaging-service secrets.
- [ ] Set `JPV_OUTBOUND_WEBHOOK_BASE_URL` to the deployed public HTTPS gateway URL and configure Twilio status/inbound callbacks to the canonical endpoints.
- [ ] Send an authorized live message through `/workspace/connor` and record the provider message ID.
- [ ] Verify provider delivery callback reaches JPV and updates delivery state.
- [ ] Verify an attributable Connor reply reaches the same conversation transcript.
- [ ] Verify a second Jay message can be sent in the same direct line after Connor's reply.

No production-activation step may be represented as complete without provider/deployment evidence. No phone number or provider secret may be committed to this repository.
