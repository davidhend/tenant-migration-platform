# M365 Tenant Migration Platform

A full-stack orchestration platform for Microsoft 365 tenant-to-tenant migrations. Rather than copying content directly, it drives Microsoft-native migration mechanisms (Exchange cross-tenant mailbox migration, Entra cross-tenant sync, SharePoint/OneDrive cross-tenant migration) and provides a unified interface for planning, executing, and validating multi-workload migrations.

Built with a Next.js frontend and .NET 8 backend, backed by PostgreSQL and integrated with Microsoft Graph, Exchange Online, SharePoint Online, Azure Automation, and Azure Key Vault.

## Features

- **Tenant Management** — Register source and target tenants with certificate or secret-based authentication. Credentials are stored in Azure Key Vault.
- **Guided Setup Wizard** — Per-project checklist that renders every manual prerequisite with your real tenant values filled in: consent links, copy-paste bootstrap scripts, config badges, and live verification via dependency checks.
- **Discovery Scans** — Inventory users, groups, mailboxes, SharePoint sites, OneDrive accounts, and domains. Detects blockers and warnings with a readiness score.
- **Identity Mapping** — Map source users to target users with configurable domain transformation rules. Auto-mapping, manual edits, and CSV import.
- **Entra User Sync** — Provision users in the target tenant via Microsoft Graph cross-tenant synchronization with on-demand provisioning, UPN rename, and Exchange MailUser enablement.
- **Mailbox Migration** — Orchestrate Exchange Online cross-tenant mailbox moves (native MRS) with per-mailbox status tracking, explicit cutover, and error reporting.
- **Content Migration** — Drive SharePoint and OneDrive cross-tenant moves via the SPO cross-tenant cmdlets, executed on a Microsoft-managed Windows sandbox through an Azure Automation runbook the API publishes and updates automatically.
- **Migration Waves** — Group user sync batches, mailbox batches, and content jobs into ordered waves with scheduled execution.
- **Domain Cutover** — Multi-phase workflow to move custom domains from source to target, pausing for the DNS TXT and MX changes only an admin can make.
- **Post-Migration Validation** — Verify migrated users, mailboxes, and content exist in the target tenant.
- **Audit Trail** — Every state-changing action is logged with the signed-in actor, timestamp, resource, and outcome.
- **Real-Time Progress** — SignalR pushes scan progress, migration status, and validation results to the UI live.

## Quickstart (Docker)

Get the whole stack (database + API + web UI) running in one command.

### Prerequisites

- **[Docker Desktop](https://www.docker.com/products/docker-desktop/)** — the only local install you need.
  - **Windows:** enable **WSL integration** (Docker Desktop → Settings → Resources → WSL Integration).
  - **macOS / Linux:** Docker Desktop, or Docker Engine + Compose v2.

### Start it

From the repo root:

```bash
./start.sh          # macOS / Linux / WSL
```

```powershell
.\start.ps1         # Windows PowerShell (or double-click start.bat)
```

> First run builds the images and can take a few minutes. Later runs start in seconds.

The script checks Docker is running, brings up all three services, waits until the API is healthy, and prints:

| Service | URL |
|---------|-----|
| **Web UI** | http://localhost:3000 |
| **API** | http://localhost:5000 (Swagger at `/swagger`) |
| **PostgreSQL** | localhost:5432 |

### First sign-in

Sign in with the built-in local account: **`admin` / `MigrationAdmin123!`**

On first login you are prompted to **set a new password** (minimum 12 characters — you can skip and change it later, but don't leave the default in place on anything reachable by others). The password is stored as a salted hash in PostgreSQL; the default can also be pre-empted entirely by setting `Auth:LocalAdmin:InitialPassword` (e.g. as an environment variable) before first startup.

For production use, configure **Microsoft Entra ID sign-in** instead — see [Authentication](#authentication).

### Everyday commands

| Command | What it does |
|---------|--------------|
| `./start.sh` / `.\start.ps1` / `make up` | Start everything |
| `./status.sh` / `.\status.ps1` / `make status` | Container + API health at a glance |
| `./logs.sh api` / `make logs s=api` | Tail logs (omit the service for all) |
| `./stop.sh` / `.\stop.ps1` / `make stop` | Stop (keeps the database) |
| `./stop.sh --clean` / `.\stop.ps1 -Clean` / `make clean` | Stop **and wipe the database** |
| `./start.sh --build` / `make rebuild` | Rebuild images after code changes |

Run `make` on its own to see all targets.

### Try it without any Microsoft credentials

Set `Platform:MockGraphCalls=true` (backend) and every scanner generates realistic synthetic data — the full UI flow works with no tenants, no Azure, no consent. Ideal for evaluating the platform before wiring up real tenants.

## From zero to a real migration

1. **Run the stack** (above) and sign in.
2. **Set up the Azure side once** — an Automation account for SharePoint/OneDrive moves and (recommended) a Key Vault for credentials. See [Azure environment setup](#azure-environment-setup). Terraform stacks under `infra/terraform/` can provision all of it.
3. **Register your tenants** (*Tenants* page) with each tenant's app registration + certificate, then **create a project** pairing source → target.
4. **Open the project's Setup wizard** (`Setup` button). It walks every remaining prerequisite — consent clicks, the Exchange migration endpoint, licenses, cross-tenant features — with your real tenant values baked into every command, and verifies them live. Work it until the checks are green.
5. **Scan, map, migrate, validate** — see [Migration workflow](#migration-workflow).

The wizard is the authoritative, always-current list of manual prerequisites. The deep reference lives in [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md) (operations) and [CLAUDE.md](CLAUDE.md) (full technical detail per workload); [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) maps every known live-fire error to its fix.

## Authentication

Two schemes, selected automatically at startup:

- **Microsoft Entra ID** (production) — active when `AzureAd:TenantId` + `AzureAd:ClientId` are set. Create a single-tenant app registration in *your* tenant: SPA platform with your frontend origin(s) as redirect URIs, *Expose an API* → `api://{clientId}` with delegated scope `access_as_user`, and app roles `Admin` / `Operator` / `Reader` assigned to users or groups. The frontend then shows Microsoft sign-in (MSAL), and roles come from the token.
- **Local** (dev / small self-hosted) — active only when `Platform:DevMode=true` and Entra is not configured (outside the Development environment, the two never coexist). Single `admin` account, hashed credential in PostgreSQL, change-password prompt at first login, `POST /api/auth/change-password` any time after.

If neither is configured, every endpoint returns 401 — the API fails closed, never open.

Authorization: any authenticated user can read; every state-changing endpoint requires the `Admin` or `Operator` role.

## Configuration

### Backend (`apps/api/appsettings.json` / environment variables)

Values can also be edited from the **Settings** page in the web UI, which writes to `apps/api/settings.override.json` (gitignored, hot-reloaded — no restart). Secret values go to Key Vault when it's enabled.

| Setting | Description | Default |
|---------|-------------|---------|
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string | (must be set) |
| `Platform:MockGraphCalls` | Synthetic data instead of Microsoft APIs | `false` |
| `Platform:DevMode` | Local login + Swagger + relaxed logging. **Never set `true` in a deployed environment** | `false` (`true` in Development) |
| `Auth:LocalAdmin:InitialPassword` | Seed the local admin with this password instead of the well-known default | (empty) |
| `AzureAd:TenantId` / `AzureAd:ClientId` | Entra ID sign-in (production auth) | (empty) |
| `Jwt:SecretKey` | Symmetric key for local-scheme JWTs | (must be set for local auth) |
| `Database:AutoMigrate` | Run EF migrations + seeding at startup | `true` |
| `KeyVault:Enabled` / `KeyVault:VaultUri` | Azure Key Vault for tenant certs + platform secrets | `false` |
| `Azure:Automation:SubscriptionId` / `ResourceGroup` / `AccountName` | Automation account that runs the SPO runbook | (set to enable content migration) |
| `Azure:Automation:RunbookName` | Runbook name | `Invoke-SpoCrossTenantOperation` |
| `Azure:Automation:AutoPublishRunbook` | API auto-publishes/updates the runbook at startup | `true` |
| `Azure:Automation:UseKeyVaultCertificate` | Runbook fetches tenant PFX from Key Vault itself (keeps it out of job params) | `false` |
| `Platform:CrossTenantMigration:AppId` / `ClientSecret` | Cross-tenant Mailbox Migration app (see wizard) | (empty) |
| `Workers:Enabled` | Background workers on/off | `true` |

### Frontend (`apps/web/.env.local` / Docker build args)

| Variable | Description | Default |
|----------|-------------|---------|
| `NEXT_PUBLIC_API_URL` | Backend API base URL | `http://localhost:5000/api` |

## Azure environment setup

One-time, per deployment (not per migration). Everything here can be provisioned by the Terraform stacks in **`infra/terraform/`** (see its README) — the manual steps are below for portal/CLI users. The web UI's **Settings → Pre-Setup** page shows live deployment badges and an environment scan so you can verify each piece.

### 1. Azure Automation account (required for SharePoint/OneDrive migration)

The SPO cross-tenant cmdlets ship in a **Windows-only** PowerShell module, so the Linux API offloads them to an Azure Automation runbook (a Microsoft-managed Windows sandbox).

```bash
az group create --name migration-automation-rg --location eastus
az automation account create \
  --name migration-automation \
  --resource-group migration-automation-rg \
  --location eastus
```

Then in the account: **Modules → Browse gallery** → import `Microsoft.Online.SharePoint.PowerShell` into the **PowerShell 5.1** runtime (wait for *Available*).

You do **not** import the runbook by hand: the API publishes and updates `apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1` automatically at startup whenever the deployed copy drifts.

### 2. API Azure identity

The API needs an Azure identity to run Automation jobs and (with auto-publish) manage the runbook. Locally that's your `az login` user or an app registration via `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` env vars (the repo-root `.env` feeds docker-compose); in Azure it's the Container App's managed identity — `DefaultAzureCredential` resolves whichever exists.

Grant it **Automation Contributor** on the Automation account:

```bash
az role assignment create \
  --assignee <identity-object-or-client-id> \
  --role "Automation Contributor" \
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Automation/automationAccounts/<account>
```

(*Automation Job Operator* is enough to run jobs, but then runbook updates must be imported manually — the startup log will tell you.)

### 3. Azure Key Vault (recommended)

With `KeyVault:Enabled=true`, tenant certificates and platform secrets live in the vault instead of local files. The API identity needs **Key Vault Secrets User** + **Certificate User**, and **Key Vault Crypto Officer** if you want DataProtection keys encrypted at rest. `KeyVault:Enabled=false` keeps a self-contained file-based store — fine for evaluation, not for production.

### 4. Per-tenant app registrations

Each migrating tenant needs one app registration with an **uploaded certificate** (app-only `Connect-SPOService` and EXO require cert auth) and the workload permissions the Setup wizard lists (Graph, SharePoint, `Exchange.ManageAsApp` + Exchange Administrator role, EXO service-principal registration). The wizard renders per-tenant bootstrap scripts for all of it; the `infra/terraform/source-tenant` and `target-tenant` stacks automate the same.

## Per-tenant-pair prerequisites (the Setup wizard's job)

These are the things Microsoft requires humans or tenant admins to do, once per source→target pair. **Don't work from this list — open the Setup wizard**, which renders each step with your tenant values and verifies it live. Summary of what to expect:

- **Cross-tenant Mailbox Migration app** — created in the target tenant, consented in both (the wizard gives the exact consent URLs), plus a client secret saved in Pre-Setup. The platform then stamps org relationships automatically.
- **Exchange migration endpoint** — one manual `New-MigrationEndpoint` in target EXO PowerShell (a PSCredential can't travel over EXO's REST surface). Rotating the migration app's secret requires refreshing this endpoint's credential too.
- **Cross Tenant User Data Migration licenses** — purchase seats; the platform auto-assigns them at batch/job start.
- **Target OneDrives must NOT exist** — the OneDrive move creates the target personal site itself; the start preflight blocks with removal guidance if one is already there. Don't pre-provision.
- **SPO cross-tenant relationship** — established automatically at first content-migration start (`ContentMigration:AutoEstablishRelationship`, default on).
- **MnASiteMove feature** (SharePoint *site* moves only) — must be enabled by Microsoft support on both tenants; the wizard includes a copy-paste support-request email. OneDrive and mailbox migrations don't need it.
- **Domain verification / DNS** — custom domain verification, and TXT + MX changes during domain cutover (the cutover job pauses and shows the exact records).

## Migration workflow

1. **Register tenants** → **create project** → **work the Setup wizard** until green
2. **Discovery scan** — inventory and readiness score
3. **Domain rules + identity mapping** — define UPN transforms, auto-map users
4. **Provision users** — Entra user sync batches
5. **Migrate mailboxes** — batches sync, park at *Synced (awaiting cutover)*, then explicit **Complete** cuts over
6. **Migrate content** — OneDrive/SharePoint jobs (preflight handles relationship, identity map, licenses)
7. **Domain cutover** — move custom domains, pausing for your DNS changes
8. **Validate** — post-migration verification
9. Steps 4–6 can be grouped into **Waves** for phased rollout

## Architecture

```
┌─────────────────────┐     ┌──────────────────────┐     ┌──────────────┐
│   Next.js Frontend  │────▶│   .NET 8 Web API     │────▶│  PostgreSQL  │
│   (React 19 / SPA)  │     │   (REST + SignalR)   │     │              │
│   Port 3000         │     │   Port 5000          │     │  Port 5432   │
└─────────────────────┘     └──────────┬───────────┘     └──────────────┘
                                       │─────▶ Azure Key Vault
                                       │       (secrets, tenant certs)
                            ┌──────────▼───────────┐
                            │  Background workers   │
                            │  (scan, user sync,    │
                            │   mailbox, content,   │
                            │   domain cutover,     │
                            │   validation, waves)  │
                            └──────────┬───────────┘
                                       │
                    ┌──────────────────┼──────────────────┐
                    ▼                  ▼                  ▼
             Microsoft Graph    Exchange Online     Azure Automation
             (identity: users,  (native cross-      (SPO cross-tenant
              groups, domains;   tenant MRS          move runbook,
              Entra cross-       mailbox moves)      Windows sandbox)
              tenant sync;                                │
              Graph fallbacks)                            ▼
                                                   SharePoint Online

  Native Microsoft mechanisms are the primary transports (MRS mailbox moves,
  SPO cross-tenant moves, Entra cross-tenant sync — the latter driven via
  Graph); direct Graph copies/POSTs are the explicit fallbacks.
```

- **Frontend:** Next.js App Router, React Query data layer (`src/lib/api.ts`), handwritten Radix-based UI primitives, SignalR live updates.
- **Backend:** controller → repository → EF Core/PostgreSQL; long-running work flows through in-memory `Channel<Guid>` queues into `BackgroundService` workers that rehydrate from the database on restart. **Single-instance by design** — a Postgres advisory lock makes any second instance serve HTTP but suppress its workers.
- **Health & ops:** `/health/live` + `/health/ready`, correlation-ID middleware, opt-in OpenTelemetry/App Insights. `GET /api/version` reports platform + expected runbook version.
- Swagger UI at `http://localhost:5000/swagger` (Development only).

## Development

```bash
./dev.sh            # Postgres in Docker; run API + web locally:
# terminal 1
cd apps/api && dotnet watch run          # http://localhost:5000
# terminal 2
cd apps/web && npm install && npm run dev # http://localhost:3000
```

- Tests: `dotnet test` (xUnit, `apps/api` test project)
- Lint/typecheck: `npm run lint` / `npx tsc --noEmit` in `apps/web`
- Schema changes: `dotnet dotnet-ef migrations add <Name> --project apps/api` (local tool manifest in `.config/`)
- Contributor guide with full architectural detail: [CLAUDE.md](CLAUDE.md)

## Documentation

| Doc | What's in it |
|-----|--------------|
| [docs/ADMIN_GUIDE.md](docs/ADMIN_GUIDE.md) | Operator handbook: deployment, prerequisites checklist, running migrations, operations |
| [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) | Every known live-fire error mapped to its cause and fix |
| [docs/RELEASING.md](docs/RELEASING.md) | Versioning and release process |
| [infra/terraform/README.md](infra/terraform/README.md) | The four Terraform stacks (platform Azure infra, app hosting, per-tenant grants) |
| [CLAUDE.md](CLAUDE.md) | Deep technical reference per workload (also used by AI coding assistants) |

## Tech stack

| Layer | Technology |
|-------|-----------|
| Frontend | Next.js 15, React 19, TypeScript, Tailwind CSS, Radix UI, React Query, SignalR |
| Backend | .NET 8, ASP.NET Core, Entity Framework Core, SignalR |
| Database | PostgreSQL 16 |
| Microsoft APIs | Graph SDK v5, Exchange Online REST (adminapi), SPO PowerShell via Azure Automation |
| Secrets | Azure Key Vault |
| Infrastructure | Docker, Docker Compose, Terraform, Azure Container Apps |

## License

[MIT](LICENSE).
