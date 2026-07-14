# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository Overview

M365 tenant-to-tenant migration orchestration platform. Acts as an orchestrator over Microsoft-native migration mechanisms (cross-tenant mailbox migration, SharePoint/OneDrive cross-tenant migration, Entra cross-tenant sync) rather than a raw content copier.

Two apps:
- `apps/web` — Next.js 14 frontend
- `apps/api` — .NET 8 Web API backend

## Development Commands

### Frontend (`apps/web`)
```bash
npm install          # first time only
npm run dev          # dev server → http://localhost:3000
npm run build        # production build
npm run lint         # ESLint
```

### Backend (`apps/api`)
```bash
dotnet run           # dev server → http://localhost:5000
dotnet build         # compile check
dotnet watch run     # hot reload
```
> `dotnet` lives at `~/.dotnet/dotnet` in WSL (not on PATH) — use the full path, or run backend commands from a Windows terminal.

**Background workers** are gated solely by `Workers:Enabled` (default `true`). `Database:AutoMigrate` controls only whether **EF Core migrations** run at startup (plus seeding) and no longer disables workers. Schema is managed by real migrations in `apps/api/Migrations/` (no more `EnsureCreated` + raw SQL patches): add one with `dotnet dotnet-ef migrations add <Name> --project apps/api` (local tool manifest in `.config/`). Databases created before the conversion are auto-baselined at `InitialCreate` on first startup (history row inserted, no schema touched).

### Exchange cross-tenant mailbox migration prerequisites

Mailbox moves run via Microsoft's native cross-tenant Mailbox Replication Service (MRS), driven by the EXO REST API. The platform calls EXO directly from the Linux container — no PowerShell module needed (it uses the same `adminapi/beta/{orgId}/InvokeCommand` endpoint that EXO V3 PowerShell wraps).

CTIM (Cross-Tenant Identity Mapping) is **not used**. It is documented as a preview feature with state-machine edge cases that broke repeatedly during validation; the supported path is manual MailUser provisioning which the platform now automates per user.

**Manual one-time setup per tenant pair** (admin must do; the platform cannot, due to interactive consent / licensing requirements). Most of it can instead be provisioned with **Terraform — see `infra/terraform/`** (migration app + dual-tenant consent, platform-app Graph grants, Exchange Administrator roles, the whole Automation account; runbook *content* stays owned by the API's auto-publisher via `ignore_changes`). The **Setup wizard** (`/projects/{id}/setup` in the UI, backed by `GET /api/setup/{projectId}`) renders all of these as guided steps with the real tenant values filled in: consent links, per-tenant copy-paste bootstrap scripts (covering items 3 and 4 below), config completeness badges, and live verification via the tenant-prereqs diagnostics. Items 6 (license *assignment*) and 7 (endpoint) plus the org relationships are now platform-automated; what remains manual is consent clicks, one bootstrap script per tenant, license *purchase*, domain verification, and — when the CrossTenantSync user-migration strategy is used — the Entra **cross-tenant access settings** in both tenants (partner config; inbound user-sync-allowed + auto-redemption on target, outbound auto-redemption on source; deliberately manual since the azuread Terraform provider lacks crossTenantAccessPolicy support).
1. **Register a cross-tenant Mailbox Migration app and consent it on both tenants.** Direct `adminconsent` URLs against the well-known AppId `879f1d6d-c0b7-4543-a2dd-dfa812c5179d` **do not work** in current M365 tenants (the app isn't pre-installed — `AADSTS700016` on target, `AADSTS500113` on source). Per Microsoft's current doc (verified 2026-07-02 against the Learn page, updated 2026-05-29), the app is created in the **TARGET** tenant and the target is configured first: (a) in **target** tenant, Entra → App registrations → New: **multitenant**, redirect URI type **Web** = `https://office.com` (the exact reply URL is required — its absence is `AADSTS500113`); API permissions → `Office 365 Exchange Online` → application permission `Mailbox.Migration` → grant admin consent on target (the default `User.Read` delegated permission can be removed); create a **client secret** (note the expiry — endpoint auth dies silently when it lapses). (b) source admin consents via `https://login.microsoftonline.com/<source>.onmicrosoft.com/adminconsent?client_id=<YOUR_APPID>&redirect_uri=https://office.com`. (c) save the AppId + secret value via **Settings → Pre-Setup → Cross-Tenant Mailbox Migration App** in the UI (persists to `settings.override.json`, hot-reloaded — no restart) or directly as `Platform:CrossTenantMigration:AppId`/`ClientSecret` in appsettings.json — the platform then stamps both org relationships (source: `RemoteOutbound` + `OAuthApplicationId` + `MailboxMovePublishedScopes`; target: `Inbound` — Microsoft's script uses `Inbound`, not `RemoteInbound`, on the target side) and creates the migration endpoint automatically per tenant pair. **The app MUST be homed in the target tenant — this is functional, not cosmetic.** A source-homed multitenant app with target-side consent looks equivalent (token roles/consents verify identically) but MRS's source-side ProxyService rejects it with a bare "Access is denied" during the move; re-homing the registration to the target tenant fixed it live (2026-07-13, after six repros with a provably-correct source-homed config). Also: tenants with Entra's secure-by-default app management policy block client-secret creation (`Credential type not allowed as per assigned policy`) — create a scoped `appManagementPolicy` exempting just this app (`restrictions.passwordCredentials[restrictionType=passwordAddition].state=disabled`) and assign it to the app before creating the secret.
2. **App registration with cert-based auth + `Exchange.ManageAsApp` admin-consented** in each tenant (same app reg as the rest of the platform — no second one needed). PFX in Key Vault, thumbprint stored on the `Tenant` row.
3. **Microsoft Entra directory role on the app's service principal in EACH tenant.** `Exchange.ManageAsApp` is *impersonation only* — it gets you in the door but grants no cmdlet access. EXO derives effective RBAC from the access token's `wids` claim, populated by Microsoft Entra directory roles assigned via Entra → Roles and administrators (NOT via Exchange RBAC roles). Assign **Exchange Administrator** (covers all needed cmdlets) — Exchange Recipient Administrator is documented to cover the same cmdlets but in practice has been observed to leave `New-DistributionGroup` 403'd, so Exchange Administrator is the safer choice. **Do NOT** rely on `New-ManagementRoleAssignment -App <appid> -Role "<management role>"` — that cmdlet only accepts "Application *" prefix roles (Graph/EWS-only) for cmdlet authorization; it silently accepts other roles but they are inert. Microsoft Learn quote: *"If you use the App parameter, you can't specify admin or user roles; you can only specify application roles (for example, 'Application Mail.Read')."*
4. **Register the app's service principal inside Exchange Online**: `Connect-ExchangeOnline` then `New-ServicePrincipal -AppId <yourAppId> -ObjectId <spObjectId>` on each tenant. Without this, EXO returns 401 on every InvokeCommand call regardless of AAD consent state.
5. **Verified domains and accepted-domain status** for any custom domains used by source UPNs / target routing. Source mailboxes must have non-zero `ExchangeGuid` and populated `LegacyExchangeDN` (true for any normal cloud mailbox).
6. **Cross Tenant User Data Migration license** assigned to each migrating user (one-time per-user fee; assignable on source OR target; also covers OneDrive migration). Without this, EXO emits a "needs approval" warning and the move stalls. **The platform now auto-assigns it at batch start** (`MailboxMigration:AutoAssignLicense`, default true, `LicenseAssignmentSide` default `target`; the OneDrive start preflight does the same via `ContentMigration:AutoAssignLicense`) — only *purchasing* seats stays manual. Assignment requires the assigning-side app registration to hold Graph application permissions **`User.ReadWrite.All`** (assignLicense + usageLocation patch) and **`Organization.Read.All`** (read subscribedSkus). Assignment failures never block the start; they surface in the start response (`licenseAssignment`) and per-user `NeedsApproval` detection remains the backstop.
7. **Cross-tenant migration endpoint must use `-Credentials` (client secret), not `-AppSecretKeyVaultUrl`.** Microsoft's current cross-tenant migration doc (Microsoft Learn, 2026-04-29 update) creates the endpoint with a `PSCredential` built from a client secret value — no Key Vault, no certificate path. The `-AppSecretKeyVaultUrl` parameter is undocumented (`{{ Fill AppSecretKeyVaultUrl Description }}` in cmdlet reference) and is residue from the deprecated `microsoft/cross-tenant` v1 setup script; it fails with `AADSTS70011: scope https://outlook.office.com is not valid` against current MS infra. Add a client secret to the migration app reg, then on **target tenant** EXO PowerShell: `New-MigrationEndpoint -Name CrossTenantEndpoint -ExchangeRemoteMove -RemoteTenant <source>.onmicrosoft.com -RemoteServer outlook.office.com -ApplicationId <AppId> -Credentials (New-Object PSCredential -ArgumentList <AppId>, (ConvertTo-SecureString <secret> -AsPlainText -Force))`. **Use `outlook.office.com`, NOT `outlook.office365.com`** — the docs and EXO both want the former. The platform auto-creates this endpoint per tenant pair when `Platform:CrossTenantMigration:ClientSecret` is set in `appsettings.json`; without it, batch start fails fast with instructions to create the endpoint manually (the PSCredential-over-REST shape is unvalidated against a live tenant — if EXO rejects it, the error includes the manual command above).

**Platform-automated per migration** (`MailboxMigrationWorker.PrepareNativeMrsEntriesAsync` and `IExoRestClient` extensions):
- Mail-enabled scope DG `CTMS-{targetOnMicrosoftDomain}` on source + adding each migrating UPN as a member
- Capture of source mailbox attributes via `Get-Mailbox`
- Target MailUser provisioning: target-routing UPN + source-routing `ExternalEmailAddress` + stamped `ExchangeGuid` + `LegacyExchangeDN`-derived `x500:` proxy
- Both org relationships: source `RemoteOutbound` with `OAuthApplicationId` + `MailboxMovePublishedScopes` populated; target `Inbound` (per Microsoft's script — not `RemoteInbound`)
- Migration endpoint on target (`ExchangeRemoteMove`, created with client-secret `Credentials`, matched by `RemoteTenant`)
- `New-MigrationBatch` + polling. Batches park at status **Synced** ("awaiting cutover") once initial sync completes; cutover is explicit via `POST /api/projects/{projectId}/mailbox-batches/{batchId}/complete` (calls `Complete-MigrationBatch`), then polling continues to the true terminal state. Retry of a failed NativeMrs batch removes the stale EXO batch and creates a fresh one. A per-user `NeedsApproval` status is surfaced as a Failed entry naming the missing Cross Tenant User Data Migration license.

**Known blocker — stale soft-deleted MailUsers on target.** If a prior migration attempt (especially CTIM) left soft-deleted MailUsers carrying the source SMTP, the auto-provisioning step fails with "existing identity found". Manual cleanup: `Get-MailUser -SoftDeletedMailUser | Remove-MailUser -PermanentlyDelete`. If EXO refuses with "AAD user backing it", delete the underlying AAD user from Entra and hard-purge from the deleted-users recycle bin first.

### SPO cross-tenant OneDrive migration prerequisites
The OneDrive content migration flow drives the `Microsoft.Online.SharePoint.PowerShell` module (`Start-SPOCrossTenantUserContentMove`) — Microsoft does not expose a public CSOM/REST surface for the cross-tenant MnA flow, and the module is **Windows-only**, so it cannot load inside the Linux API container. Execution is offloaded to an **Azure Automation runbook** (a Microsoft-managed Windows sandbox) that the API triggers via the Azure REST API.

One-time setup:
1. Create an Azure Automation account in any region/subscription.
2. Import the `Microsoft.Online.SharePoint.PowerShell` module from the PowerShell Gallery into the account (Modules → Browse gallery).
3. ~~Import the runbook manually~~ **The API now auto-publishes the runbook** (`RunbookAutoPublisher` hosted service, `Azure:Automation:AutoPublishRunbook` default true): at startup it compares the local `apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1` against the deployed runbook content and creates/updates/publishes it on drift — including creating the runbook from scratch. Requires the API identity to hold **Automation Contributor** (not just Job Operator) on the Automation account; without it, a warning tells you to re-import manually. Current operations: batch state polling (`GetStateBatch`/`GetSiteStateBatch` — one Automation job per content job per poll cycle), `GetCrossTenantHostUrl`, `SetCrossTenantRelationship`, and chunked (≤200 UPN) `Request-SPOPersonalSite`.
4. Grant the API's Azure identity (the one `DefaultAzureCredential` resolves to — managed identity in prod, `az login` / env vars locally) the **Automation Contributor** role on the Automation account (Contributor covers running jobs AND lets the API auto-publish the runbook; with only Job Operator, jobs run but runbook updates must be re-imported manually).
5. Fill in `Azure:Automation` in `appsettings.json` — `SubscriptionId`, `ResourceGroup`, `AccountName`, `RunbookName` (default `Invoke-SpoCrossTenantOperation`).
6. Ensure each tenant has an app registration with an uploaded certificate; the PFX must be stored in Key Vault (client-secret auth is not supported by `Connect-SPOService` for app-only).
7. *(Optional, recommended)* Set `Azure:Automation:UseKeyVaultCertificate=true` so the runbook fetches the PFX from Key Vault itself instead of receiving base64 PFX + password as job parameters (which are visible in Automation job history). Requires: system-assigned managed identity on the Automation account, **Key Vault Secrets User** on the vault for that identity, and `Az.Accounts` + `Az.KeyVault` modules imported into the account.
8. *(SharePoint **site** migrations only — not OneDrive)* The tenant-level **`MnASiteMove`** feature must be enabled by **Microsoft** (request via Microsoft support / cross-tenant SharePoint migration onboarding). It is separate from the OneDrive cross-tenant move (enabled by default) and from mailbox migration. Confirmed live: `StartSite` fails with *"The Cross-Tenant content move [MnASiteMove] feature is not enabled for this tenant."* until it is on. OneDrive-only and mailbox-only migrations do not need it.

Per-migration behavior: the Start preflight now also **auto-establishes the SPO MnA cross-tenant relationship** when `Get-SPOCrossTenantCompatibilityStatus` says it's missing (`ContentMigration:AutoEstablishRelationship`, default true): target side first (`Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Source -PartnerCrossTenantHostUrl <source>-my host`), then source side (`-PartnerRole Target`, target's -my host), verified with `Test-SPOCrossTenantRelationship` (expect `GoodToProceed`) — one-time per tenant pair, ~5-15 min of runbook jobs. Before submitting OneDrive moves, the platform builds Microsoft's 6-column identity-map CSV (project IdentityMaps ∪ job item pairs; **UserType column must be `RegularUser`** — `Add-SPOTenantIdentityMap` silently rejects other values like `Member`, reporting the rejection on the console *without* a terminating error, so the runbook verifies the upload with `Get-SPOTenantIdentityMappingUser` and throws if the map didn't take) and uploads it to the **target** tenant via `Add-SPOTenantIdentityMap` (each upload replaces the whole map, so the full project set is sent), and auto-assigns Cross Tenant User Data Migration licenses to target owners. **Target OneDrives must NOT exist** (confirmed live 2026-07-09): `Start-SPOCrossTenantUserContentMove` creates the target personal site itself and fails with "The target tenant has a conflict for the site provided" when one is present — the Start preflight blocks with a 422 listing conflicting users and the removal commands (`Remove-SPOSite` then `Remove-SPODeletedSite`; both needed, a soft-deleted site still conflicts). Do NOT pre-provision with `Request-SPOPersonalSite` (the old `Provisioning` flow is retired for OneDrive moves; `OneDriveProvisioningWorker` remains only as legacy).

### Swagger UI
`http://localhost:5000/swagger` — available in Development environment only.

## Environment Configuration

**Frontend** (`apps/web/.env.local`):
- `NEXT_PUBLIC_API_URL=http://localhost:5000/api` — backend base URL. (The old `NEXT_PUBLIC_USE_MOCK` frontend mock layer and `src/lib/mock-data.ts` were removed — the frontend always talks to the real backend; use `Platform:MockGraphCalls` on the backend for credential-free development.)

**Backend** (`apps/api/appsettings.json`):
- `Platform:MockGraphCalls=true` — all scanners generate synthetic data instead of calling Microsoft Graph
- `Platform:DevMode` — **defaults to `false`** (secure default); `appsettings.Development.json` sets it `true` so local dev is unchanged. Enables Swagger, relaxed logging, and the local username/password login scheme. **Do not set `true` in a deployed environment** — it activates a dev token path.

### Authentication (Tier 1)
Dual-scheme JWT with graceful degradation, selected at startup:
- **Entra ID** — active when `AzureAd:TenantId` + `AzureAd:ClientId` are set. Tenant-pinned issuer/audience validation; roles from the token's `roles` claim (Entra app roles). This is the production path.
- **Local** — the symmetric-key dev login (`POST /api/auth/token`, `admin`/`MigrationAdmin123!`). Active **only** when `Platform:DevMode=true` AND (Entra is not configured OR the environment is Development). Once Entra is configured outside Development, the local scheme is not registered — the two never coexist as parallel admin paths.
- **Neither configured** → every `[Authorize]` endpoint returns 401 (an inert scheme with a random per-process key; a committed placeholder `Jwt:SecretKey` cannot forge tokens). Startup hard-fails if a known placeholder key is used with `DevMode=true` outside Development.
- `GET /api/auth/config` (anonymous) reports the active mode so the frontend picks MSAL sign-in vs the dev form; `GET /api/auth/me` returns the caller's identity + roles.
- Authorization: default `[Authorize]` = any authenticated user (Reader); `[Authorize(Policy="Operator")]` (roles Admin or Operator) gates every state-changing endpoint. Audit events record the real signed-in user via `ICurrentUserService`.

**Entra sign-in app registration** (operator creates in their own tenant, single-tenant): SPA platform → redirect URIs for the frontend origin(s); Expose an API → `api://{clientId}` + delegated scope `access_as_user`; App roles `Admin`/`Operator`/`Reader` assigned to users/groups. Then set `AzureAd:TenantId` + `AzureAd:ClientId`.

### Secrets & DataProtection
Platform secrets (`Platform:CrossTenantMigration:ClientSecret`, `Azure:Identity:*` secret/cert) are stored in **Key Vault** when `KeyVault:Enabled=true`, not in `settings.override.json`. The Settings UI writes secret values to the vault (secret names `platform-*`); a startup migrator moves any pre-existing plaintext secrets out of the file into the vault and leaves a `kv:` marker. Readers resolve config-value-wins-else-vault via `IPlatformSecretResolver` (5-min cache). DataProtection keys persist to `apps/api/keys/` (gitignored) and are encrypted with a Key Vault **key** (`platform-dataprotection`) when the API identity has **Key Vault Crypto Officer**; without that role it degrades to filesystem-only with a warning. `KeyVault:Enabled=false` keeps the file-based dev behavior.

## Deployment & Operations (Tier 2)

**Containers.** `apps/api/Dockerfile` is multi-stage (restore → publish → `aspnet:8.0` runtime), runs as non-root `app` (uid 1654), and has a `HEALTHCHECK` curling `/health/live`. `docker-compose.yml` brings up the full stack (Postgres + API + web); the API service runs `KeyVault:Enabled=false` for a self-contained local run (file-based secret store, no Azure creds). `.dockerignore` keeps `settings.override.json`, `keys/`, and `appsettings.Development.json` out of the image. `apps/web/Dockerfile` is Next.js standalone, non-root.

**Cloud IaC.** `infra/terraform/platform-app/` (persona = platform operator) provisions the API's runtime: an Azure **Container App** (system-assigned managed identity, **min=max=1 replica** — see single-instance below) whose identity gets **Key Vault Crypto Officer** (DataProtection), **Key Vault Secrets User** + **Certificate User** (secret store + tenant PFXs), and **Automation Contributor**; plus an Azure **PostgreSQL Flexible Server** (v16, 14-day backups). Depends on the KV + Automation account from `platform-azure/`. `terraform validate`-clean; needs an ACR image + the existing vault/automation IDs (in `terraform.tfvars.example`).

**Health.** `GET /health/live` (liveness, always 200 when up) and `GET /health/ready` (readiness) — both `[AllowAnonymous]`, mapped after auth so they're never gated. Ready checks Postgres (Unhealthy → 503) and, when configured, Key Vault + Azure Automation (**Degraded**, not Unhealthy — a missing optional dependency returns 200 so the container isn't killed). JSON: `{status, totalDurationMs, checks:[{name,status,description,durationMs,error}]}`.

**Observability.** A correlation-ID middleware reads/generates `X-Correlation-ID`, scopes it into logs, and echoes it back. OpenTelemetry (traces/metrics/logs, ASP.NET+HttpClient+EF instrumentation) is **off by default** and a complete no-op unless `OpenTelemetry:OtlpEndpoint` or `ApplicationInsights:ConnectionString` (or env `APPLICATIONINSIGHTS_CONNECTION_STRING`) is set. A `PlatformMetrics` Meter exposes `migration.active_batches` and `migration.worker_poll_failures` (instruments registered; wiring the counters into workers is still pending).

**Single-instance constraint.** The in-memory `Channel` queues + DB rehydration are correct for exactly ONE running instance — two would double-poll and create duplicate EXO/SPO batches. `SingleInstanceGuard` (hosted service) takes a Postgres session advisory lock at startup; the **primary** holds it and runs workers, a **secondary** logs CRITICAL and every worker self-suppresses (`SingleInstanceState.IsPrimary`) while still serving HTTP. Gated by `SingleInstance:Enforce` (default true); degrades to primary if the DB is unreachable. This is why the Container App is pinned to 1 replica — revisit (Postgres-backed queue or Service Bus) before scaling out.

## Architecture

### Frontend Data Flow
Every page is a client component that fetches via **React Query**. The API client (`src/lib/api.ts`) exports namespaced objects (`tenantsApi`, `scansApi`, etc.); every method calls the real backend through the shared `request()` helper (which attaches the auth token and unwraps errors). When adding a new API call, add it to `api.ts` and its entity type to `src/types/index.ts`.

All TypeScript entity types live in `src/types/index.ts` and must stay in sync with the backend models.

shadcn/ui components are written manually in `src/components/ui/` (the CLI was not used). When adding a new primitive, write it there following the existing pattern (Radix primitive + `cva` + `cn`).

### Backend Request Lifecycle
`HTTP request → Controller → Repository → AppDbContext (PostgreSQL) → return`

State lives in **PostgreSQL via EF Core** (`Data/AppDbContext.cs`, `DbSet<>` per entity), accessed through the repositories in `Data/Repositories/` (`ITenantRepository`, `IScanRepository`, `IAuditRepository`, …) injected into controllers and workers. Schema is managed by real EF migrations in `apps/api/Migrations/`; `DatabaseSeeder` seeds baseline data at startup when `Database:AutoMigrate=true`. State survives restarts (the original in-memory `InMemoryStore` singleton was replaced with PostgreSQL).

Scans and long-running migrations are the exception to the synchronous read/write: `POST /api/scans → Controller writes Scan + Job via the repositories → enqueues `scanId` to the in-memory `ScanJobQueue` (a singleton `Channel<Guid>`) → ScanWorker (BackgroundService) dequeues → DiscoveryEngine.RunScanAsync`. Each workload has its own singleton `Channel<Guid>` queue + BackgroundService worker (mailbox, content, user, domain-cutover, validation, OneDrive provisioning), and every worker re-hydrates its active rows from the database on startup so in-flight work survives a restart. These in-memory queues are why the app is single-instance (see the single-instance constraint above).

### Discovery Engine Pipeline
`DiscoveryEngine` runs scanners sequentially, persisting results via the repositories and updating `Scan.Progress` between each step:

```
UserScanner (0→25%) → GroupScanner (→40%) → MailboxScanner (→55%)
→ SharePointScanner (→70%) → OneDriveScanner (→82%) → DomainScanner (→90%)
→ IssueDetector → ReadinessAnalyzer → Scan.Status = Completed (100%)
```

Each scanner checks `Platform:MockGraphCalls`. When true it returns synthetic data. When false it calls the real Microsoft Graph/EXO APIs via the injected `IGraphClientFactory` (`Services/Graph/`) and maps results to the scan model — the real-scan path is wired (scanners no longer throw `NotImplementedException`).

`IssueDetector` inspects scan results and emits `ScanIssue` records (Blocker / Warning / Info). `ReadinessAnalyzer` deducts points from 100 based on blockers, warnings, large mailboxes, and unverified domains.

### Adding a New Workload Scanner
1. Create `Services/Discovery/Scanners/MyScanner.cs` following the existing pattern
2. Register it as `AddScoped<MyScanner>()` in `Program.cs`
3. Inject it into `DiscoveryEngine` and call it in `RunScanAsync` with a progress step
4. Add corresponding model properties to `Scan.cs` / `ScanSummary` if needed (add an EF migration if the schema changes: `dotnet dotnet-ef migrations add <Name> --project apps/api`)
5. Add the matching entity type to `apps/web/src/types/index.ts` and any new API method to `apps/web/src/lib/api.ts`

### JSON Serialization Contract
The API serializes all enums as camelCase strings (e.g., `"connected"`, `"running"`). Frontend `types/index.ts` uses string union types matching these values exactly. When adding enum members, update both sides.

### Audit Trail
Every significant controller action should write an `AuditEvent` via the injected `IAuditRepository` (`_audit.AddAsync(...)` then `_audit.SaveAsync(...)`). There is no middleware for this — it is done manually per-controller, and the actor is the real signed-in user from `ICurrentUserService` (not a hardcoded value). See `TenantsController` for the pattern.

## Key Design Constraints

- **Orchestrator, not a copier.** The platform should drive Microsoft-native migration APIs (cross-tenant mailbox move, SPO cross-tenant migration tasks) rather than streaming content itself.
- **One enterprise app registration per tenant.** App-only Graph permissions; workload-specific scoping by adapter. Exchange and SharePoint have their own migration flows distinct from Graph.
- **Least privilege.** When wiring real Graph calls, scope permissions per workload rather than granting all permissions to one app.
- **Idempotent operations.** All scan and migration operations should be safe to retry. The retry path in `JobsController` resets state and re-enqueues — new operations must follow this contract.
- **Mailbox before user migration (enforced).** For users whose mailbox migrates, the mailbox flow provisions the target identity itself (`New-MailUser` creates the AAD user + MailUser stub); a pre-created member account at the same UPN breaks it, and after the move the account already exists. User migration is only for users whose mailbox is NOT migrating. Enforced server-side: user-migration start/retry 422s when any batch user overlaps a mailbox entry (`UserMigrationController.FindMailboxOverlaps`; Skipped mailbox entries don't count), and the Create User Batch dialog excludes mailbox-covered users up front.
- **Dev/prod parity via flags.** `Platform:MockGraphCalls` lets the backend generate synthetic scan/discovery data so the full stack runs without real tenant credentials. Do not remove this abstraction layer. (The separate frontend `NEXT_PUBLIC_USE_MOCK` mock layer has been removed — the UI always calls the real backend.)
