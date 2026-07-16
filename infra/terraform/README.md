# Pre-setup via Terraform — separated by persona

Real tenant-to-tenant migrations rarely have one person with deployment rights
in both tenants. The pre-setup is therefore split into **three independent
stacks** — each with its own state, its own single-tenant credentials, and a
different owner:

| Stack | Run by | Credentials needed | Provisions |
|---|---|---|---|
| `target-tenant/` | Target-tenant admin | Entra directory roles in **target only** | Migration app (multitenant, `Mailbox.Migration`, office.com redirect) + client secret + target consent; target platform-app Graph grants (`Policy.Read.All`, `User.ReadWrite.All`, `Organization.Read.All`) + **SharePoint grants** (`Sites.FullControl.All`, `User.ReadWrite.All`, `OneDrive.Provision.All`, `SharePointCrossTenantMigration.Manage.All`, `Migration.ReadWrite.All`) + `Exchange.ManageAsApp` + Exchange Administrator role |
| `source-tenant/` | Source-tenant admin | Entra directory roles in **source only** | Migration-app consent in source (SP + `Mailbox.Migration` grant — the programmatic adminconsent); source platform-app Graph grants (`Synchronization.ReadWrite.All`, `Application.Read.All`, `User.ReadWrite.All`, `Organization.Read.All`) + **SharePoint grants** (`Sites.FullControl.All`, `User.ReadWrite.All`, `SharePointCrossTenantMigration.Manage.All`, `Migration.ReadWrite.All`) + `Exchange.ManageAsApp` + Exchange Administrator role |
| `platform-azure/` | Platform operator (target org **or** third party/MSP) | Azure RBAC on one subscription — **no** Entra directory privileges | Automation account, SPO module, Az modules (optional), runbook seed, Automation Contributor for the API identity, managed identity + Key Vault access (optional) |
| `platform-app/` | Platform operator | Azure RBAC on one subscription — **no** Entra directory privileges | API host: Azure Container App (system-assigned managed identity, pinned to 1 replica) + Container Apps environment + Log Analytics; PostgreSQL Flexible Server with automated backups; role assignments for the API identity — **Key Vault Crypto Officer** (DataProtection), Key Vault Secrets User, Key Vault Certificate User, Automation Contributor |

Shared grant logic lives in `modules/platform-grants/`.

### `platform-app/` — the API's cloud host

Provisions where the API actually runs. Its Container App gets a **system-assigned managed identity**, which is the runtime `DefaultAzureCredential` identity — this is the principal that needs Key Vault and Automation access (locally that role is your `az login` user; in the cloud it's this identity). The stack grants it Key Vault **Crypto Officer** (DataProtection key wrap/unwrap), **Secrets User** (platform secret store), **Certificate User** (tenant PFXs), and **Automation Contributor** (trigger + auto-publish the runbook).

Depends on the Key Vault and Automation account already existing (pass their resource IDs as variables; the Automation account comes from `platform-azure/`). Build the API image from `apps/api/Dockerfile`, push it to a registry the Container App can pull (public, or ACR with `acr_id` set), then:

```bash
cd platform-app
cp terraform.tfvars.example terraform.tfvars   # set api_image, key_vault_id/uri, automation_account_id
terraform init && terraform apply
terraform output api_url
terraform output -raw postgres_admin_password
```

**Single instance:** the Container App is pinned to 1/1 replicas because the API holds its work queues in memory (with DB rehydration) — correct for one replica, double-processing with more. Keep `replica_count = 1` until the queues move to a shared broker.

## Order of operations & the single handoff

```
1. target-tenant admin:   terraform apply
       → hand `migration_app_client_id` (a non-secret GUID) to the source admin
       → put client ID + `-raw migration_app_client_secret` into
         Settings → Pre-Setup → Cross-Tenant Mailbox Migration App
2. source-tenant admin:   set migration_app_client_id in tfvars → terraform apply
3. platform operator:     terraform apply   (any time; independent)
```

That GUID is the **only** cross-stack value, and it isn't sensitive. The secret
never leaves the target admin/platform operator.

If the source admin won't run Terraform at all: the target stack's
`source_admin_consent_url` output is a one-click fallback for the migration-app
consent, and the platform's Setup wizard (`/projects/{id}/setup`) renders
bootstrap scripts covering the source platform-app grants.

**Pick ONE path — the URL or the source-tenant stack, never both.** Clicking
the consent URL creates the migration app's service principal in the source
tenant; the source-tenant stack then fails on apply with
`A resource with the ID "<sp-object-id>" already exists`
(`azuread_service_principal.migration`). Recovery if you already clicked it:
either delete the consent-created SP and let Terraform own it
(`az ad sp delete --id <sp-object-id>`, then re-apply — consent is recreated
seconds later), or import it
(`terraform import azuread_service_principal.migration /servicePrincipals/<sp-object-id>`).

## Authentication (per stack, single tenant)

```bash
# target-tenant/ or source-tenant/  (no Azure subscription needed):
az login --tenant <that-tenant>.onmicrosoft.com --allow-no-subscriptions

# platform-azure/:
az login   # any account with Owner/User Access Administrator on the subscription
```

Entra stacks need Global Administrator, or Application Administrator +
Privileged Role Administrator (the directory-role assignment needs the latter).
Service-principal auth for CI works too — add `client_id`/`client_secret` to the
stack's `provider "azuread"` block or use `ARM_*` environment variables.

## Runbook content: deliberately NOT managed by Terraform

`platform-azure/` seeds the runbook once; day-to-day content is pushed by the
API's `RunbookAutoPublisher` at startup (compares the repo script vs deployed,
republishes on drift). The runbook resource carries
`lifecycle { ignore_changes = [content] }` so the two never fight. Rationale:
runbook logic changes with application code — new operations ship in the same
commit as the C# that calls them — so tying content to infra-cadence applies
would reintroduce the drift problem the auto-publisher solved.

## Adopting the existing Automation account

If your Automation account was created by hand, import it (in `platform-azure/`)
before the first apply so Terraform doesn't try to recreate it (greenfield
deployments skip this — the stack creates the account):

```bash
cd platform-azure
terraform init
terraform import azurerm_automation_account.main \
  /subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.Automation/automationAccounts/<automation-account>
terraform import azurerm_automation_runbook.spo_cross_tenant \
  .../automationAccounts/<automation-account>/runbooks/Invoke-SpoCrossTenantOperation
terraform import azurerm_automation_module.spo \
  .../automationAccounts/<automation-account>/modules/Microsoft.Online.SharePoint.PowerShell
```

(Skip any import whose resource doesn't exist yet — e.g. the Az.* modules.)

Likewise, grants that already exist in a tenant (e.g. `Exchange.ManageAsApp`
consented long ago) will error with "already exists" on apply — either
`terraform import` them or leave `grant_exchange_manage_as_app = false`.

## Usage (each stack)

```bash
cd <stack>
cp terraform.tfvars.example terraform.tfvars   # then edit
terraform init && terraform plan && terraform apply
```

## What Terraform cannot do

The Setup wizard (`/projects/{id}/setup`) walks the rest:

1. **EXO-internal service-principal registration** per tenant
   (`New-ServicePrincipal -AppId <platform-app> -ObjectId <sp-object-id>`) —
   each stack outputs `platform_sp_object_id` to feed the wizard's pre-filled
   script.
2. **The cross-tenant migration endpoint** (target-tenant EXO PowerShell:
   `New-MigrationEndpoint -ExchangeRemoteMove ... -ApplicationId <migration-app>
   -Credentials <PSCredential of AppId + secret>`). There is no ARM/Graph surface
   for it, and it cannot be created over EXO REST either (the PSCredential
   doesn't serialize through InvokeCommand), so the platform can't self-create
   or refresh it. The Setup wizard renders the exact script with real values.
   **Every rotation of the migration app secret requires recreating the endpoint
   manually** — a stale endpoint secret fails deep in the move with AADSTS7000215.
3. **App management policy exemption for the migration-app secret.** Tenants
   with Entra's secure-by-default app management policy reject
   `azuread_application_password` with *"Credential type not allowed as per
   assigned policy"*, and the `azuread` provider has no appManagementPolicy
   resource. One-time pre-step in the target tenant (Global Admin):
   ```bash
   POLICY=$(az rest --method POST \
     --url https://graph.microsoft.com/v1.0/policies/appManagementPolicies \
     --body '{"displayName":"Allow client secret - cross-tenant mailbox migration app",
              "isEnabled":true,
              "restrictions":{"passwordCredentials":[
                {"restrictionType":"passwordAddition","state":"disabled"}]}}' \
     --query id -o tsv)
   az rest --method POST \
     --url "https://graph.microsoft.com/v1.0/applications/<migration-app-OBJECT-id>/appManagementPolicies/\$ref" \
     --body "{\"@odata.id\":\"https://graph.microsoft.com/v1.0/policies/appManagementPolicies/$POLICY\"}"
   ```
   (A secret is unavoidable — the migration endpoint only supports PSCredential
   auth, not certificates.)
4. **License purchase** (Cross Tenant User Data Migration seats — billing).
   Assignment to users is automated by the platform at migration start.
5. **Custom-domain DNS verification**.
6. **Cross-tenant access settings** (both tenants — needed only for the Entra
   cross-tenant sync user-migration strategy): partner configuration for the
   other tenant, inbound *user sync allowed* + automatic invitation redemption
   on the target, outbound automatic redemption on the source. Deliberately
   manual: the `azuread` provider has no `crossTenantAccessPolicy` support
   (hashicorp/terraform-provider-azuread#1713); it could be automated with the
   `microsoft/msgraph` provider if that trade-off is ever wanted. The Setup
   wizard lists both steps with the pair's actual tenant IDs.
7. **Interactive OneDrive personal-site provisioning** (standalone provisioning
   only — **not** part of OneDrive content moves, which create the target drive
   themselves and *fail* if it already exists). Where standalone provisioning is
   wanted, `Request-SPOPersonalSite` does **not** work with app-only auth —
   confirmed by live validation: even with `OneDrive.Provision.All` +
   `User.ReadWrite.All` + `Sites.FullControl.All` granted, app-only calls fail
   "Attempted to perform an unauthorized operation"; the same command run
   interactively by a SharePoint admin succeeds. The SharePoint app grants above
   are still required for the *other* content operations (compatibility check,
   identity-map upload, MnA relationship, the actual move) — they just don't
   enable app-only personal-site provisioning.

## Notes & gotchas

- **App-role grants don't show under "API permissions".** Terraform consents by
  creating app-role assignments directly on the resource service principals —
  effective immediately, visible under *Enterprise applications → (app) →
  Permissions*; the app registration's *API permissions* blade only mirrors
  `requiredResourceAccess`, which is not managed for the pre-existing platform
  apps.
- **Token caching:** the API caches Graph/EXO tokens up to ~1 h. After apply,
  restart the API before trusting permission-dependent checks.
- **Secret rotation:** the migration-app secret rotates on the first apply after
  `migration_app_secret_rotation_days`; every rotation must be re-entered in
  Settings → Pre-Setup.
- **State contains secrets** in `target-tenant/` (`migration_app_client_secret`).
  Use a protected backend (Azure Storage + RBAC); the `.gitignore` here excludes
  `*.tfstate*` and `terraform.tfvars`, and `.terraform.lock.hcl` files should be
  committed.
