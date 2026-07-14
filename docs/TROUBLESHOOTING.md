# Troubleshooting Guide

Symptom → cause → fix reference for the M365 tenant-to-tenant migration platform.
Entries are grouped by workload; the **bold lead** is the error string or behaviour
you'll actually see, so Ctrl-F the message you got.

Most of these were found during live validation against real tenants. Where a fix
already shipped in the platform, the symptom is still listed because you may hit it
on a *manual* EXO/SPO command or an older instance.

---

## How to get diagnostics first

- **Platform health:** `GET /health/ready` (anonymous) — per-dependency status
  (`postgresql`, `keyvault`, `automation`, `stuck-jobs`). `GET /health/live` = process up.
  The GUI header pill shows the same at a glance.
- **Version:** `GET /api/version` → `{ version, runbookVersion, environment }`. Compare
  `runbookVersion` against the `# RUNBOOK_VERSION:` marker at the top of the deployed
  Automation runbook.
- **Correlation ID:** every response carries `X-Correlation-ID`; send/grep it to tie a
  request to its logs.
- **Auth mode:** `GET /api/auth/config` → `{ mode: entraId | local | none }`.
- **MRS move detail (mailbox):** in **target** EXO PowerShell,
  `Get-MigrationUserStatistics -Identity <targetUpn> -IncludeReport | fl` — the `Error`
  field has the *real* (untruncated) reason behind an EXO batch "SyncedWithErrors".
- **Runbook failure detail (OneDrive/SharePoint):** the API logs the failed Automation
  job ID. Read its exception/streams via the Azure portal (Automation account → Jobs)
  or `az rest .../automationAccounts/<acct>/jobs/<id>?api-version=2023-11-01` → `properties.exception`.
- **API logs:** container logs (`./logs.sh api` / `docker compose logs -f api`) or the
  local `dotnet run` console. OpenTelemetry/App Insights export is off unless configured.

---

## Mailbox / cross-tenant MRS

### **`AmbiguousParameterSetException` / "Parameter set cannot be resolved using the specified named parameters"** during prep
- **Cause:** `New-MailUser` was called with a parameter combination that satisfies no
  parameter set (e.g. `-MicrosoftOnlineServicesID` + `-ExternalEmailAddress` but no
  `-Password`). Cloud `New-MailUser` needs `-MicrosoftOnlineServicesID` **and**
  `-Password` to create the backing directory object.
- **Fix:** Fixed in the platform (it now sends a stub `-Password`). If you hit it on a
  **manual** `New-MailUser`, include both. A plain-string password *does* work over the
  EXO REST `InvokeCommand` surface (it's coerced to `SecureString`).

### **"Target MailUser '…' could not be re-read after provisioning"**
- **Cause:** directory → EXO replication lag — a freshly created MailUser isn't visible
  to `Get-MailUser` for several seconds to a minute.
- **Fix:** The platform now polls with backoff (~90s) before failing. If a batch fails
  with this, just **retry** it — replication usually completes shortly.

### **"The migration user type for '…' is not correct. Please ensure it has RecipientTypeDetails:MailUser"**
- **Cause:** the migration-batch CSV referenced the **source** address. MRS onboarding
  resolves each CSV row in the **target** org and requires a `MailUser`; a source address
  makes it follow the MailUser's `ExternalEmailAddress` back to the source `UserMailbox`
  and reject it.
- **Fix:** Fixed in the platform (the CSV now uses target addresses). On a **manual**
  `New-MigrationBatch`, put the **target** MailUser's primary SMTP in the CSV, not the
  source UPN.

### **`AADSTS7000215: Invalid client secret provided`** (deep in the move, surfaced via `Get-MigrationUserStatistics`)
- **Cause:** the cross-tenant migration **endpoint** (`CrossTenantEndpoint`) carries a
  **stale** client secret. The move authenticates through the endpoint; a rotated/expired
  secret fails here even though the platform's configured secret is valid. The endpoint's
  credential **cannot be created or refreshed over EXO REST** — it's a manual step.
- **Fix:** In **target** tenant EXO PowerShell, remove and recreate the endpoint with the
  **current secret VALUE** (not the secret ID):
  ```powershell
  Get-MigrationBatch | ? { $_.SourceEndpoint -eq 'CrossTenantEndpoint' } | Remove-MigrationBatch -Confirm:$false
  Remove-MigrationEndpoint -Identity CrossTenantEndpoint
  $appId  = '<migration-app-client-id>'
  $secret = ConvertTo-SecureString '<CURRENT-CLIENT-SECRET-VALUE>' -AsPlainText -Force
  $cred   = New-Object System.Management.Automation.PSCredential($appId, $secret)
  New-MigrationEndpoint -Name CrossTenantEndpoint -ExchangeRemoteMove `
    -RemoteTenant <source>.onmicrosoft.com -RemoteServer outlook.office.com `
    -ApplicationId $appId -Credentials $cred
  ```
  Use **`outlook.office.com`**, not `outlook.office365.com`. Then retry the batch.

### **"The call to …/MailboxReplicationService.ProxyService/OAuth failed. --> Access is denied."**
- **Cause:** the move authenticated successfully but failed **authorization** at the
  source MRS proxy — the cross-tenant OAuth trust hasn't fully propagated, or (rarely) a
  genuine Microsoft-side cross-tenant-auth issue.
- **Fix:** First verify all config is correct: source org relationship
  `MailboxMoveCapability=RemoteOutbound` + `OAuthApplicationId` + `MailboxMovePublishedScopes`
  (the CTMS scope group), the user is a member of that scope group, target org relationship
  `Inbound`, endpoint auth works (no `AADSTS7000215`). If everything checks out, **wait**
  — cross-tenant OAuth authorization can take **hours** to propagate — then retry
  unchanged. If it persists for many hours with config verified correct, open a **Microsoft
  support ticket** (this is the one boundary the platform can't resolve).

### **`0x80070057` / `MigrationCSVRowValidationException`** (legacy, "unexpected error")
- **Cause:** historically mysterious; root-caused to the `New-MailUser` parameter-set bug
  above (the target MailUser was mis-provisioned).
- **Fix:** Fixed in the platform. If you still see it, it's almost certainly the endpoint
  **stale-secret** (`AADSTS7000215`) or a config gap — work the two entries above. Do
  **not** assume it's a Microsoft-side issue; the last occurrence was platform code.

### Per-user status **`NeedsApproval`** / the move stalls with no error
- **Cause:** the migrating user lacks the **Cross Tenant User Data Migration** license on
  the assigning side. EXO parks the move awaiting approval.
- **Fix:** The platform auto-assigns the license at migration start (target side by
  default) — but **purchasing seats** is manual. Buy seats
  (M365 admin → Billing → Purchase services → "Cross Tenant User Data Migration"), then
  retry. The license also covers OneDrive. If auto-assign reported a failure, check the
  assigning-side app has Graph `User.ReadWrite.All` + `Organization.Read.All`.

### **"existing identity found"** / provisioning fails on a re-run
- **Cause:** a prior attempt (especially CTIM) left **soft-deleted MailUsers** on the
  target carrying the source SMTP.
- **Fix:** In target EXO PowerShell:
  ```powershell
  Get-MailUser -SoftDeletedMailUser | Remove-MailUser -PermanentlyDelete
  ```
  If EXO refuses with "AAD user backing it", delete the underlying AAD user in Entra and
  hard-purge it from the deleted-users recycle bin first, then retry.

### **EXO returns 401 on every `InvokeCommand`** (regardless of consent)
- **Cause:** the app's service principal is not registered **inside** Exchange Online, or
  it lacks the Exchange Administrator directory role (AAD consent alone is insufficient —
  EXO derives cmdlet RBAC from the token's `wids` claim).
- **Fix:** Per tenant: `Connect-ExchangeOnline` then
  `New-ServicePrincipal -AppId <appId> -ObjectId <spObjectId>`, and assign the
  **Exchange Administrator** Entra directory role to the app's SP (via Entra → Roles, NOT
  Exchange RBAC). The Setup wizard renders both, pre-filled.

### **`504 Gateway Timeout`** on `Get-Mailbox` (or another EXO call) during prep
- **Cause:** transient EXO service blip.
- **Fix:** Just **retry** the batch. Not a config problem.

---

## OneDrive / SharePoint content

### OneDrive move fails: **"The target tenant has a conflict for the site provided"**
- **Cause:** the target user **already has a OneDrive**. `Start-SPOCrossTenantUserContentMove`
  **creates the target personal site itself** and refuses to run when one exists
  (confirmed live 2026-07-09). Do **not** pre-provision target OneDrives.
- **Fix:** Remove the existing (empty) target OneDrive as a SharePoint admin — **both**
  commands, a soft-deleted site in the recycle bin still conflicts:
  ```powershell
  Connect-SPOService -Url https://<target>-admin.sharepoint.com
  Remove-SPOSite        -Identity https://<target>-my.sharepoint.com/personal/<user_target_com> -Confirm:$false
  Remove-SPODeletedSite -Identity https://<target>-my.sharepoint.com/personal/<user_target_com> -Confirm:$false
  ```
  The start preflight blocks with this guidance (and the exact site URLs) when it detects
  an existing target drive. The user still needs their licenses — they just must not have
  a OneDrive yet. Source content is untouched by the removal.

### OneDrive move fails: **"Identity map entry for source UPN […] does not exist on the target tenant"**
- **Cause:** the uploaded identity map never took effect. `Add-SPOTenantIdentityMap`
  **silently rejects malformed rows** (console message, no terminating error, exit
  success) — most notably when the UserType column is anything but **`RegularUser`**
  (the platform historically wrote `Member`; fixed).
- **Fix:** Fixed in the platform (CSV writes `RegularUser`; the runbook now verifies the
  upload with `Get-SPOTenantIdentityMappingUser` and fails loudly if the map didn't take).
  If you see it with a current build, inspect the map on the target:
  ```powershell
  Get-SPOTenantIdentityMappingUser -Field SourceUserKey -Value user@source.com
  ```

### OneDrive job stuck in legacy **`provisioning`** status (jobs from older platform versions)
- **Cause:** the retired pre-provisioning flow (`Request-SPOPersonalSite` app-only, which
  SharePoint rejects — it requires an interactive admin context; confirmed live).
- **Fix:** Delete the job and recreate it on a current build — Start now goes directly to
  the content move (which provisions the target site itself, see the conflict entry above).

### SharePoint **site** move: **"The Cross-Tenant content move [MnASiteMove] feature is not enabled for this tenant."** (StartSite fails)
- **Cause:** cross-tenant SharePoint **site** migration requires the tenant-level
  **`MnASiteMove`** feature, which **Microsoft must enable**. It is separate from the
  OneDrive cross-tenant move (enabled by default) and from mailbox migration.
- **Fix:** Request `MnASiteMove` enablement via **Microsoft support** (Microsoft 365 admin
  center → Support) for **both** tenants. The Setup wizard's MnASiteMove step renders a
  copy-paste example support-request email with your tenant names/IDs filled in.
  OneDrive-only and mailbox-only migrations don't need it.

### Compatibility check reports **"Incompatible"**, or the cross-tenant relationship is missing
- **Cause:** the SPO **MnA cross-tenant relationship** isn't established between the pair.
- **Fix:** The platform auto-establishes it on the first content-migration start
  (`ContentMigration:AutoEstablishRelationship`, default on). If that fails, run it
  manually — target side first, then source (each connects to its own admin URL):
  ```powershell
  # Target admin:
  Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Source -PartnerCrossTenantHostUrl https://<source>-my.sharepoint.com
  # Source admin:
  Set-SPOCrossTenantRelationship -Scenario MnA -PartnerRole Target -PartnerCrossTenantHostUrl https://<target>-my.sharepoint.com
  # Verify both: Test-SPOCrossTenantRelationship … → expect GoodToProceed
  ```

### Runbook not updating / startup warning **"grant the API identity Automation Contributor"** (403)
- **Cause:** the API's Azure identity has only **Automation Job Operator** — enough to run
  jobs, not to auto-publish the runbook.
- **Fix:** Grant **Automation Contributor** on the Automation account
  (`az role assignment create --assignee <apiIdentity> --role "Automation Contributor"
  --scope <automationAccountId>`), or re-import/publish the runbook manually after changes.

### SharePoint content operations fail with permission errors even after granting `Sites.FullControl.All`
- **Cause:** the platform apps need the full SharePoint app-permission set on **both**
  tenants: `Sites.FullControl.All`, `User.ReadWrite.All`,
  `SharePointCrossTenantMigration.Manage.All`, `Migration.ReadWrite.All`, and (target)
  `OneDrive.Provision.All`.
- **Fix:** The `infra/terraform/{source,target}-tenant` stacks grant these; or consent
  them manually (Entra → app → API permissions → Office 365 SharePoint Online). Note this
  still does **not** enable app-only `Request-SPOPersonalSite` (see the interactive
  pre-provision entry above).

---

## Authentication / platform / infrastructure

### Every `[Authorize]` endpoint returns **401**
- **Cause:** no auth scheme is active — `Platform:DevMode=false` **and** `AzureAd:TenantId`/
  `ClientId` unset (the inert fallback rejects all tokens), or a token from the wrong scheme.
- **Fix:** Check `GET /api/auth/config`. For production, configure `AzureAd:TenantId` +
  `AzureAd:ClientId` (Entra sign-in app). For local dev, set `Platform:DevMode=true`
  (Development environment) and use `POST /api/auth/token` (`admin` / `MigrationAdmin123!`).

### Startup **fails**: "Jwt:SecretKey is a known development placeholder but the environment is 'Production'…"
- **Cause:** a deliberate guard — running outside Development with `DevMode=true` and the
  committed placeholder signing key would allow forged Admin tokens.
- **Fix:** Configure `AzureAd` for Entra auth and set `Platform:DevMode=false`, **or**
  supply a real `Jwt:SecretKey`. (Base `appsettings.json` ships `DevMode=false`; only
  `appsettings.Development.json` turns it on.)

### **"Failed to connect to 127.0.0.1:5432"** / "Database unreachable at startup"
- **Cause:** PostgreSQL isn't running. In dev this is the `migration-postgres` Docker
  container; if Docker Desktop isn't up or the container is stopped, the API can't connect.
- **Fix:** Start the stack — `./start.sh` (or `make up`), which brings up Postgres and
  waits for health. The API starts **degraded** (non-DB endpoints work, DB endpoints 500)
  rather than crashing, and recovers once Postgres is reachable and it's restarted.

### Startup warning: DataProtection keys are **"filesystem-only" / not encrypted at rest**
- **Cause:** the API identity lacks **Key Vault Crypto Officer**, so it can't wrap the
  DataProtection keys with the vault key.
- **Fix:** Grant it (production identity via the `platform-app` Terraform; locally:
  `az role assignment create --assignee <objectId> --role "Key Vault Crypto Officer"
  --scope <keyVaultId>`). Harmless to ignore in dev — keys just persist unencrypted on disk.

### Workers aren't processing anything (batches sit Queued/Draft, no progress)
- **Cause:** either `Workers:Enabled=false`, or this instance is the **secondary** under
  the single-instance guard (only the primary runs workers — two instances would double-poll
  and create duplicate EXO/SPO batches).
- **Fix:** Check the logs. `"…worker disabled via Workers:Enabled"` → set `Workers:Enabled=true`.
  `"not the primary instance — background processing suppressed"` → another instance holds
  the Postgres advisory lock; run exactly **one** instance (the platform is pinned to 1
  replica by design). If the DB is unreachable the guard degrades to primary.

### Azure CLI **"AADSTS70043: The refresh token has expired"** while running ops/`az` commands
- **Cause:** conditional-access sign-in-frequency limit — the CLI token lasts ~2 hours.
- **Fix:** Re-run `az login` (device code works from WSL:
  `az login --use-device-code --tenant <tenantId>`). This also re-primes
  `DefaultAzureCredential`, so restart the API afterward if its Azure calls (Key Vault,
  Automation) were failing.

### GUI shows **"Backend unreachable"** in the status pill
- **Cause:** the API process/container isn't running (the pill couldn't reach
  `/health/ready`).
- **Fix:** Start the stack — `./start.sh` / `make up` — then the pill turns green. If it
  shows **Unhealthy** (red) instead, the backend is up but a dependency failed; open the
  pill dropdown to see which (usually `postgresql`).

---

*Found something not listed here? Capture the exact error string, the correlation ID, and
(for mailbox) the `Get-MigrationUserStatistics -IncludeReport` output or (for content) the
Automation job exception — those three make any new issue diagnosable.*
