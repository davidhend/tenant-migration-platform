# ──────────────────────────────────────────────────────────────────────────────
# SOURCE-tenant stack — run by a source-tenant admin, with source credentials
# only (az login --tenant <source> --allow-no-subscriptions).
#
# Needs exactly one value from the target side: the migration app's client ID
# (a non-secret GUID, output of the target-tenant stack). Consents that app
# here — the programmatic equivalent of clicking the adminconsent URL — and
# grants the source platform app its Graph roles + Exchange Administrator role.
#
# Run AFTER the target-tenant stack (the multitenant app must exist first).
# ──────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.5"

  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }
}

provider "azuread" {
  tenant_id = var.source_tenant_id
}

# Instantiate the multitenant migration app's service principal in this tenant
# and grant Mailbox.Migration — together these ARE the admin consent.
resource "azuread_service_principal" "migration" {
  client_id = var.migration_app_client_id
}

resource "azuread_app_role_assignment" "migration_consent" {
  app_role_id         = module.platform_grants.exchange_sp_app_role_ids["Mailbox.Migration"]
  principal_object_id = azuread_service_principal.migration.object_id
  resource_object_id  = module.platform_grants.exchange_sp_object_id
}

# ── Platform app grants (source side) ────────────────────────────────────────

module "platform_grants" {
  source = "../modules/platform-grants"

  platform_app_client_id             = var.platform_app_client_id
  graph_roles                        = var.platform_graph_roles
  sharepoint_roles                   = var.platform_sharepoint_roles
  grant_exchange_manage_as_app       = var.grant_exchange_manage_as_app
  assign_exchange_administrator_role = var.assign_exchange_administrator_role
}
