# Microsoft Teams Deployment Notification Setup — Retired

Status: RETIRED

This document is a non-operational historical reference. The former `.github/workflows/teams-notify.yml` and `deploy-appservice` notification chain is retired with the repository's GitHub Actions deployment architecture and must not be recreated as an operational dependency.

Current deployment and notification behavior must inherit the provider-neutral JPV-OS boundary in [`../governance/PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md`](../governance/PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md). Notifications may report verified runtime outcomes, but they do not establish deployment authority or completion by themselves.

Historical Teams webhook and GitHub Actions setup instructions remain available through repository history for audit purposes only.
