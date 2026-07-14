# Changelog

All notable changes to the M365 tenant-to-tenant migration platform are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
The Azure Automation runbook (`apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1`)
is versioned in lockstep with the API — see its `RUNBOOK_VERSION` marker and
`GET /api/version`.

## [Unreleased]

## [0.9.0] - 2026-07-05

First tagged release. The platform orchestrates Microsoft-native cross-tenant
migration (mailbox via MRS, OneDrive/SharePoint via the SPO cross-tenant MnA
flow, Entra cross-tenant user sync) with a Next.js UI and a .NET 8 API.

> **Maturity note.** Migration paths are validated end-to-end against real
> tenants *up to Microsoft-side boundaries* — e.g. the mailbox setup chain and
> MRS submission are exercised live, but a full mailbox move currently stops at
> a Microsoft cross-tenant authorization step; SharePoint site moves reach
> `StartSite` and require the tenant `MnASiteMove` feature. This is a `0.x`
> release: the orchestration, setup automation, and platform hardening are
> solid; a proven full end-to-end content move is not yet claimed.

### Added
- **Setup wizard** (`/projects/{id}/setup`) that renders every remaining manual
  prerequisite with the real tenant values filled in: admin-consent links,
  per-tenant EXO bootstrap scripts, SharePoint app-permission grants, the manual
  migration-endpoint command, and the interactive OneDrive pre-provisioning step.
- **Per-persona Terraform** pre-setup (`infra/terraform/`): separate
  `target-tenant`, `source-tenant`, and `platform-azure`/`platform-app` stacks so
  no single operator needs rights in both tenants — provisions the migration app +
  dual-tenant consent, Graph + SharePoint + Exchange app permissions, Exchange
  Administrator roles, the Automation account, and the API's cloud host (Container
  App + managed identity + Key Vault roles + PostgreSQL Flexible Server).
- **Domain-cutover UI**: a full workflow surface (phase stepper, Start, and the
  DNS/MX pause-phase Continue steps that show the exact records to add) — the last
  workload that had no GUI is now drivable.
- **System status**: a live health indicator in the app header (Healthy /
  Degraded / Unhealthy / Backend-unreachable) plus a `/status` page, backed by
  `/health/live` and `/health/ready`.
- **Startup ergonomics**: one-command `start.sh` / `start.ps1` / `start.bat`,
  `stop`/`status`/`logs`/`dev` scripts, and a `Makefile`.
- **Observability**: correlation-ID middleware; config-gated OpenTelemetry /
  Application Insights (no-op unless configured); a `PlatformMetrics` meter
  (active batches, worker poll failures, stuck jobs); an opt-in retention worker.
- **Cross-tenant access & license automation**: auto-establish of the SPO MnA
  relationship, identity-map upload, and Cross Tenant User Data Migration license
  auto-assignment at migration start.

### Changed
- **Persistence** moved from an in-memory store to **PostgreSQL via EF Core**
  with real migrations (`apps/api/Migrations/`); existing databases auto-baseline
  at `InitialCreate` on first startup.
- **Secrets** moved out of `settings.override.json` into **Azure Key Vault**
  (config-value-wins-else-vault resolution), with a startup migrator that relocates
  any pre-existing plaintext secrets. DataProtection keys are Key-Vault-encrypted
  when the identity holds Crypto Officer.
- **Containers**: the API image is multi-stage, non-root, with a `/health/live`
  HEALTHCHECK; compose runs a self-contained local stack.
- **Runbook auto-publish**: the API keeps the deployed Azure Automation runbook in
  sync with the repo copy at startup (Automation Contributor required).

### Fixed
Live validation against real tenants surfaced and fixed a series of defects in the
never-before-exercised migration paths:
- **Mailbox / MRS setup chain** (resolved the historical `0x80070057`): correct
  `New-MailUser` parameter set with a stub password; read-after-write provisioning
  poll; migration-batch CSV uses target (not source) addresses; failed moves are no
  longer masked as "awaiting cutover"; endpoint stale-secret detection surfaced as
  an actionable error (`AADSTS7000215`); clear guidance that the migration endpoint
  is a one-time manual step (PSCredential can't be created over EXO REST).
- **OneDrive / SharePoint content path**: content jobs accept OneDrive items
  without SharePoint URLs; the `Request-SPOPersonalSite` runbook `-UserEmails`
  binding fixed; the provisioning worker now surfaces a failed Automation job after
  bounded retries instead of looping silently.
- **License auto-assign** moved to after target-MailUser provisioning so it no
  longer fails "User not found".
- A logging `FormatException` that aborted Native MRS setup on endpoint reuse.

### Security
- **Entra ID authentication** (dual-scheme, MSAL frontend, `Operator` role policy),
  with the real signed-in user recorded in the audit trail via
  `ICurrentUserService`. A security review caught and closed an auth-scheme bypass
  (a forgeable local JWT when Entra was enabled); base `DevMode` now defaults to
  `false` and a placeholder signing key hard-fails startup outside Development.
- **Single-instance guard** (Postgres advisory lock) prevents a second instance
  from double-processing the in-memory queues.

### Known limitations
- A full end-to-end content move (mailbox and OneDrive/SharePoint) is gated on
  Microsoft-side steps: mailbox MRS cross-tenant authorization, OneDrive personal-
  site provisioning timing, and the `MnASiteMove` tenant feature for site moves.
- Single-replica by design (in-memory queues); scale-out needs a
  Postgres/Service-Bus-backed queue first.
- Distribution model is self-hosted (single operator per deployment); no
  multi-customer tenancy.
