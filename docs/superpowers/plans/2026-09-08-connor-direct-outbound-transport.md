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

### Implemented

- [x] Provider-neutral transport contracts and fail-closed secret-backed Connor principal binding.
- [x] Twilio send adapter and authenticated public-URL webhook validation.
- [x] Durable provider-event/review receipt storage with replay protection and monotonic states.
- [x] Live exact-head GitHub review routing with message-specific review acknowledgment tokens.
- [x] Two-way freeform founder-to-Connor and Connor-to-founder conversation service.
- [x] Durable direct-conversation transcript and duplicate inbound-message protection.
- [x] Isolation of SMS acknowledgment from GitHub approval mutation.
- [x] Protected `/workspace/connor` founder UI with transcript, compose/send, refresh, direction labels, and delivery state.
- [x] Founder workspace entry for the direct line.
- [x] Review defects remediated: public callback URL, exact ACK correlation, early status retry safety, acknowledgment monotonicity, actual ACK/approval regression test, JSONL filtering, and temp-file path safety.
- [x] Addressed review threads replied to and resolved.

### Required exact-head release gates

- [ ] CI Build PASS on the final exact head.
- [ ] Authority Accountability PASS on the final exact head.
- [ ] Completion-Bounded Governance PASS on the final exact head.
- [ ] JPV Security Inheritance PASS on the final exact head.
- [ ] Stripe/Azure validation PASS on the final exact head.
- [ ] Container Build PASS on the final exact head.
- [ ] Fresh automated review on the final exact head has no unresolved substantive defects.
- [ ] `jaypventuresllc-admin` submits an independent `APPROVED` review on the final exact head.
- [ ] Repository signing requirements are satisfied from an execution environment capable of signed commits.

### Production activation and live end-to-end proof

- [ ] Deploy the approved final container revision to the production gateway.
- [ ] Provision production secret `JPV_PRINCIPAL_CONNOR_SMS_E164` with Connor's verified E.164 endpoint plus verification metadata.
- [ ] Provision Twilio account/auth and sender or messaging-service secrets.
- [ ] Set `JPV_OUTBOUND_WEBHOOK_BASE_URL` to the deployed public HTTPS gateway URL and configure Twilio status/inbound callbacks to the canonical endpoints.
- [ ] Send an authorized live message through `/workspace/connor` and record the provider message ID.
- [ ] Verify provider delivery callback reaches JPV and updates delivery state.
- [ ] Verify an attributable Connor reply reaches the same conversation transcript.
- [ ] Verify a second Jay message can be sent in the same direct line after Connor's reply.

No production-activation step may be represented as complete without provider/deployment evidence. No phone number or provider secret may be committed to this repository.
