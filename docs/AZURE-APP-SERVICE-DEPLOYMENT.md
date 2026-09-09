# Azure App Service Deployment — Retired

Status: RETIRED

This document is a non-operational historical reference. GitHub Actions, GitHub OIDC deployment, Azure publish-profile deployment, and `.github/workflows/deploy-appservice.yml` are not admitted production deployment authority for this repository.

The current deployment authority is the provider-neutral JPV-OS boundary defined in [`../governance/PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md`](../governance/PROVIDER-NEUTRAL-DEPLOYMENT-BOUNDARY.md). Runtime deployment must enter through that governed boundary, preserve provider independence, fail closed when execution capacity is unavailable, and produce terminal deployment evidence before any completion claim.

For container/runtime packaging reference, use [`CONTAINER-DEPLOYMENT.md`](./CONTAINER-DEPLOYMENT.md) only where it remains consistent with the provider-neutral boundary.

Historical Azure/App Service instructions remain available through repository history but must not be used as current execution instructions.
