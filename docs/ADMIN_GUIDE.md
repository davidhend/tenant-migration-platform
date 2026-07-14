# Administrator & Operator Guide

This guide is for the IT admin who **deploys** the M365 Tenant Migration Platform and **runs migrations** with it. For a fast local start see the [README Quickstart](../README.md#quickstart); for symptom→fix help see [TROUBLESHOOTING.md](./TROUBLESHOOTING.md); for deep architecture/prerequisite detail see [CLAUDE.md](../CLAUDE.md).

---

## 1. What it is

An **orchestrator** over Microsoft-native cross-tenant migration mechanisms — it drives Microsoft's own APIs rather than copying content itself:

| Workload | Mechanism |
|---|---|
| Mailboxes | Cross-tenant **Mailbox Replication Service (MRS)** via the EXO REST API |
| OneDrive / SharePoint | **SPO cross-tenant (MnA)** content move, run from an Azure Automation runbook |
| Users | **Entra cross-tenant synchronization** (or direct Graph provisioning) |
| Domains | Multi-phase **domain cutover** (release from source → verify on target → reassign) |

**Model:** one operator runs the platform; work is organized into **projects**, each a **source→target tenant pair**. A project holds scans, identity maps, and the migration batches/jobs for every workload.

> **Single-instance constraint.** The platform is designed to run as **exactly one instance**. Its work queues are in-memory (rehydrated from PostgreSQL on restart) and a Postgres advisory lock elects the primary. Running two replicas would double-poll and create duplicate EXO/SPO batches. The cloud Terraform pins the Container App to 1 replica for this reason. Do **not** scale it out.

---

## 2. Deploy

Two supported shapes:

### (a) Local / self-contained — Docker Compose
The fastest path. See the **[README Quickstart](../README.md#quickstart)**: `./start.sh` (or `make up`, or double-click `start.bat`) brings up PostgreSQL + API + web, waits for health, and prints the URLs. `KeyVault:Enabled=false` in this mode, so secrets use the local file store — good for evaluation, not for production.

### (b) Cloud — Terraform
`infra/terraform/platform-app/` provisions the production runtime: an Azure **Container App** (system-assigned managed identity, **min=max=1 replica**), an **Azure Database for PostgreSQL Flexible Server** (v16, 14-day backups), and the identity's role assignments on Key Vault (**Crypto Officer** for DataProtection, **Secrets User**, **Certificate User**) plus **Automation Contributor**. It depends on the Key Vault + Automation account from `infra/terraform/platform-azure/`. Supply an ACR image reference and the existing vault/automation IDs (see `terraform.tfvars.example`).

**Prerequisites**

| Component | Local | Cloud |
|---|---|---|
| Container host | Docker Desktop (WSL integration) | Azure Container Apps |
| Database | Postgres container | Azure DB for PostgreSQL Flexible Server |
| Key Vault | optional (file store) | **recommended** (secrets + tenant certs + DataProtection) |
| Azure Automation | required for OneDrive/SharePoint | required for OneDrive/SharePoint |

---

## 3. Configure

Backend settings live in `apps/api/appsettings.json` and can be overridden by environment variables (`__` for nesting, e.g. `Platform__DevMode=false`). **Secrets are not stored in files** — with `KeyVault:Enabled=true` the Settings UI writes secret values to Key Vault (names `platform-*`); a startup migrator moves any pre-existing plaintext secrets out of `settings.override.json` into the vault.

| Key | Default | Purpose |
|---|---|---|
| `Platform:DevMode` | `false` | Enables Swagger + the local dev login. **Must stay `false` in production** (it activates a dev token path). |
| `AzureAd:TenantId` / `:ClientId` / `:Audience` | empty | Entra ID sign-in app (production auth). When set outside Development, the dev login is not registered. |
| `Workers:Enabled` | `true` | Master switch for all background workers. `false` = serve HTTP only (no processing). |
| `SingleInstance:Enforce` | `true` | Advisory-lock primary election. Leave on. |
| `KeyVault:Enabled` / `:VaultUri` | `false` / empty | Secret store + tenant PFX source + DataProtection key wrapping. |
| `Platform:CrossTenantMigration:AppId` / `:ClientSecret` | app id set / empty | The cross-tenant Mailbox Migration app (see §4). Secret belongs in Key Vault / Settings UI. |
| `Azure:Automation:SubscriptionId` / `:ResourceGroup` / `:AccountName` | empty | Automation account for the SPO runbook. Required for OneDrive/SharePoint. |
| `Azure:Automation:RunbookName` | `Invoke-SpoCrossTenantOperation` | Runbook name; API auto-publishes on drift. |
| `Azure:Automation:AutoPublishRunbook` | `true` | API keeps the deployed runbook in sync with the repo (needs Automation Contributor). |
| `Azure:Automation:UseKeyVaultCertificate` | `false` | Runbook fetches tenant PFX from Key Vault itself instead of receiving it as a job parameter. |
| `MailboxMigration:AutoAssignLicense` | `true` | Auto-assign the Cross Tenant User Data Migration license at batch start (in the worker, after MailUser provisioning). |
| `MailboxMigration:LicenseAssignmentSide` | `target` | Which side to license (`target` or `source`). |
| `MailboxMigration:DefaultUsageLocation` | `US` | `usageLocation` stamped before licensing if missing. |
| `ContentMigration:AutoEstablishRelationship` | `true` | Auto-establish the SPO MnA relationship on first content start. |
| `ContentMigration:AutoAssignLicense` | `true` | License OneDrive owners at start. |
| `ContentMigration:MaxConcurrentJobs` | `3` | Parallel content jobs. |
| `OneDriveProvisioning:MaxProvisionAttempts` | `5` | Bounded retries before a failed pre-provision surfaces as Failed. |
| `OneDriveProvisioning:TimeoutMinutes` / `:MaxHours` | `60` / `24` | Provisioning wait budget. |
| `Retention:Enabled` | `false` | Opt-in audit-event pruning (destructive). |
| `Retention:AuditEventRetentionDays` / `:SweepIntervalHours` | `365` / `24` | Retention window + sweep cadence. |
| `Monitoring:StuckJobThresholdHours` | `6` | A non-terminal job older than this flags the `stuck-jobs` health check (excludes legit human-wait states). |
| `OpenTelemetry:OtlpEndpoint` / `ApplicationInsights:ConnectionString` | empty | Observability exporters — **off (no-op) unless set**. |
| `Cors:AllowedOrigins` | localhost:3000/3001 | Allowed frontend origins. |
| `Http:EnforceHttps` | `true` | HSTS + HTTPS redirect outside Development. |
| `Database:AutoMigrate` | (env-set) | Apply EF migrations + seed at startup. Controls schema bootstrap **only** — not the workers. |
| `ConnectionStrings:DefaultConnection` | — | PostgreSQL connection string. |

Frontend: `apps/web/.env.local` → `NEXT_PUBLIC_API_URL` (backend base URL).

---

## 4. Pre-setup (per tenant pair)

Most of the pre-setup is **automated** two ways:
- **Terraform** (`infra/terraform/target-tenant/`, `source-tenant/`, `platform-azure/`, `platform-app/`) — the migration app + dual-tenant consent, platform-app Graph **and SharePoint** grants, Exchange Administrator roles, and the Automation account. Split by persona so a source-tenant admin and target-tenant admin each run only their own stack.
- **Setup wizard** (`/projects/{id}/setup` in the UI) — renders every remaining step with your real tenant values filled in: consent links, copy-paste bootstrap scripts, config badges, and live verification via the tenant-prereqs diagnostics.

**What stays irreducibly manual** (the wizard/Terraform surface these; Microsoft or interactive consent requires them):

- [ ] **Admin-consent clicks** — consent the cross-tenant Mailbox Migration app in both tenants.
- [ ] **EXO service-principal registration** — `New-ServicePrincipal -AppId … -ObjectId …` inside Exchange Online, per tenant (an app cannot do this to itself; the wizard renders the script).
- [ ] **Cross-tenant migration endpoint** — `New-MigrationEndpoint -ExchangeRemoteMove … -Credentials <PSCredential>` on the target, once, with the **current** client secret value. Cannot be created over EXO REST. If a mailbox move later fails `AADSTS7000215`, the endpoint's secret is stale — recreate it.
- [ ] **License purchase** — buy Cross Tenant User Data Migration seats (assignment is automated; purchasing is not).
- [ ] **Domain verification** — verify any custom domains used by source UPNs / target routing.
- [ ] **Cross-tenant access settings** — only for the CrossTenantSync user strategy: partner config + inbound user-sync/auto-redemption on target, outbound auto-redemption on source.
- [ ] **Target OneDrives must NOT exist** — the cross-tenant move **creates** each user's target personal site itself and fails with a site conflict if one already exists (do **not** pre-provision with `Request-SPOPersonalSite`). Remove any existing target OneDrive first: `Remove-SPOSite` then `Remove-SPODeletedSite` (both — a soft-deleted site still conflicts). The start preflight blocks and lists conflicting users; the wizard renders the removal script.
- [ ] **MnASiteMove feature** — for SharePoint **site** migration only, Microsoft must enable the tenant-level `MnASiteMove` feature (separate from the OneDrive move). File a Microsoft support request — the Setup wizard's MnASiteMove step includes a copy-paste example email with your tenant values filled in. Symptom if missing: `StartSite` fails "the Cross-Tenant content move [MnASiteMove] feature is not enabled for this tenant."

See CLAUDE.md for the full prerequisite detail and the exact commands.

---

## 5. Running a migration end-to-end

The operator drives everything from the web UI. Project sub-pages are organized as tabs.

1. **Add tenants** — *Tenants* page: add the **source** and **target** tenants with their app credentials (cert in Key Vault). Use **Verify** to confirm connectivity.
2. **Create a project** — pair source→target.
3. **Run the Setup wizard** — *Setup* button on the project → work the checklist (§4) until the prereq checks are green.
4. **Discovery scan** — *Scans* tab: run a scan. Review **readiness score** and **issues** (blockers/warnings) on *Overview*.
5. **Identity maps & domain rules** — *Identity* and *Domain Rules* tabs: auto-map source→target UPNs, apply domain transforms, resolve conflicts/unmapped users.
6. **(Optional) Waves** — *Waves* tab: batch users/sites into scheduled waves.
7. **Run each workload:**
   - **Mailboxes** (*Mailboxes* tab): create a batch (strategy NativeMrs) → **Start**. The platform provisions the target MailUser, stamps the org relationships, and creates the MRS batch. It syncs, then **parks at "Synced" (awaiting cutover)**. Click **Complete** to cut over. Failed batches offer **Retry**; **Reset Target** clears a stale target MailUser.
   - **OneDrive / SharePoint** (*Content* tab): create a job (jobType OneDrive **or** SharePoint site) → **Start**. Preflight auto-establishes the MnA relationship, uploads + verifies the identity map, auto-assigns migration licenses, and blocks with removal guidance if a target OneDrive already exists (§4 — the move creates the target site itself); SharePoint site jobs need the MnASiteMove feature. Pause/Resume/Cancel available.
   - **Users** (*Users* tab): create → start → monitor provisioning; retry failures.
   - **Domain cutover** (*Domain Cutover* tab): create with the domain name → **Start**. The worker auto-advances until a **pause phase**: at **Awaiting DNS verification** it shows the **TXT record** to add, at **Awaiting MX update** it shows the **MX record** to point at the target — add the record, then click **Continue**. Ends at Completed or Failed.
   - **Validation** (*Validation* tab): create a post-migration validation run to verify migrated objects exist on the target.
8. **Audit** (*Audit* tab) records every state-changing action with the signed-in operator as actor.

---

## 6. Operations

- **Health** — `GET /health/live` (liveness, always 200 when up) and `GET /health/ready` (Postgres → 503 if down; Key Vault / Automation / stuck-jobs → Degraded but 200). The GUI **System Status** pill (header) polls readiness and shows Healthy / Degraded / Unhealthy / **Backend unreachable**, with a per-dependency dropdown and a `/status` page. `GET /api/version` reports the running version.
- **Backups** — back up PostgreSQL (the cloud Flexible Server keeps 14-day automated backups; for local, back up the `postgres_data` volume / `pg_dump`). All platform state is in Postgres.
- **Single instance** — never run two replicas (see §1). A secondary self-suppresses its workers and logs CRITICAL.
- **Workers kill-switch** — `Workers:Enabled=false` stops all processing (e.g. a maintenance window) while HTTP keeps serving.
- **Secret rotation** — rotate the migration-app client secret periodically: new secret on the app reg → update **Settings → Pre-Setup → Cross-Tenant Mailbox Migration App** → recreate the migration endpoint with the new value (§4).
- **Observability** — OpenTelemetry / Application Insights are **off by default** and a complete no-op until an OTLP endpoint or App Insights connection string is set. The `stuck-jobs` health check + `migration.stuck_jobs` metric are the alerting hooks.
- **Data retention** — opt-in (`Retention:Enabled=true`), audit events only; project deletion is intentionally not automated.

---

## 7. Security notes

- The platform holds **app-only credentials to both tenants** (certificates/secrets in Key Vault) — treat the instance and its vault as highly sensitive; a compromise implies admin-level access to the connected M365 tenants. Keep it self-hosted and isolated.
- **Least privilege** — scope app permissions per workload (see the Terraform grant stacks); the Automation identity needs Automation Contributor, the API identity needs Key Vault Crypto Officer/Secrets/Certs.
- **`Platform:DevMode` must be `false`** outside Development — startup hard-fails on a known placeholder JWT key in a non-Development environment, and the local login scheme is not registered once Entra is configured.
- **Auth** — production uses Entra ID; state-changing endpoints require the **Operator** role (Admin or Operator); read endpoints require any authenticated user.
- Run a security review / pen-test before any external exposure.
