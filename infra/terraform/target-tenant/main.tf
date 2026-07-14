# ──────────────────────────────────────────────────────────────────────────────
# TARGET-tenant stack — run by a target-tenant admin, with target credentials
# only (az login --tenant <target> --allow-no-subscriptions).
#
# Creates the cross-tenant Mailbox Migration app (multitenant, homed here per
# Microsoft's doc), consents it in this tenant, issues its client secret, and
# grants the target platform app its Graph roles + Exchange Administrator role.
#
# Hand the migration_app_client_id output (a non-secret GUID) to the SOURCE
# tenant admin — it is the only cross-stack input their stack needs.
# ──────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.5"

  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
  }
}

provider "azuread" {
  tenant_id = var.target_tenant_id
}

# ── Migration app ────────────────────────────────────────────────────────────

resource "azuread_application" "migration" {
  display_name     = var.migration_app_display_name
  sign_in_audience = "AzureADMultipleOrgs"

  web {
    # The office.com reply URL is required — its absence surfaces as
    # AADSTS500113 during the source-tenant consent flow. The provider insists
    # on a trailing slash for host-only URIs; Entra treats it as equivalent to
    # https://office.com in the adminconsent redirect_uri.
    redirect_uris = ["https://office.com/"]
  }

  required_resource_access {
    resource_app_id = data.azuread_application_published_app_ids.well_known.result["Office365ExchangeOnline"]

    resource_access {
      id   = module.platform_grants.exchange_sp_app_role_ids["Mailbox.Migration"]
      type = "Role"
    }
  }
}

data "azuread_application_published_app_ids" "well_known" {}

resource "azuread_service_principal" "migration" {
  client_id = azuread_application.migration.client_id
}

# Client secret for the migration endpoint's PSCredential. Rotates on apply
# after the rotation window elapses; every rotation must be re-entered in
# Settings → Pre-Setup → Cross-Tenant Mailbox Migration App.
#
# Tenants with Entra's secure-by-default app management policy reject this
# resource with "Credential type not allowed as per assigned policy"
# (confirmed live 2026-07-13). The azuread provider has no appManagementPolicy
# resource (same gap as crossTenantAccessPolicy), so the scoped exemption is a
# one-time manual pre-step — see "App management policy" in ../README.md. A
# secret is unavoidable: the migration endpoint only supports PSCredential auth.
resource "time_rotating" "migration_secret" {
  rotation_days = var.migration_app_secret_rotation_days
}

resource "azuread_application_password" "migration" {
  application_id = azuread_application.migration.id
  display_name   = "migration-platform"

  rotate_when_changed = {
    rotation = time_rotating.migration_secret.id
  }
}

# Admin consent in this (target) tenant: app-role assignment == consent.
resource "azuread_app_role_assignment" "migration_consent" {
  app_role_id         = module.platform_grants.exchange_sp_app_role_ids["Mailbox.Migration"]
  principal_object_id = azuread_service_principal.migration.object_id
  resource_object_id  = module.platform_grants.exchange_sp_object_id
}

# ── Platform app grants (target side) ────────────────────────────────────────

module "platform_grants" {
  source = "../modules/platform-grants"

  platform_app_client_id             = var.platform_app_client_id
  graph_roles                        = var.platform_graph_roles
  sharepoint_roles                   = var.platform_sharepoint_roles
  grant_exchange_manage_as_app       = var.grant_exchange_manage_as_app
  assign_exchange_administrator_role = var.assign_exchange_administrator_role
}
