# Grants on ONE tenant's existing platform app registration. The app itself is
# not managed here (it carries certificates and state created by the platform);
# consent is effected directly as app-role assignments on the Graph / Exchange
# Online resource service principals. Effective immediately, but note these do
# NOT appear in the app registration's "API permissions" blade (that mirrors
# requiredResourceAccess, which is cosmetic) — see Enterprise applications →
# (app) → Permissions instead.

terraform {
  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
  }
}

data "azuread_application_published_app_ids" "well_known" {}

data "azuread_service_principal" "graph" {
  client_id = data.azuread_application_published_app_ids.well_known.result["MicrosoftGraph"]
}

data "azuread_service_principal" "exchange" {
  client_id = data.azuread_application_published_app_ids.well_known.result["Office365ExchangeOnline"]
}

# Office 365 SharePoint Online (fixed well-known resource appId). Cross-tenant
# OneDrive/SharePoint content migration authenticates to SPO app-only and needs
# these application permissions — confirmed by live validation (missing them
# surfaces as "Access is denied ... profile information" / "unauthorized
# operation"). CAVEAT: these grants do NOT make app-only Request-SPOPersonalSite
# work — OneDrive pre-provisioning must still be run interactively by a
# SharePoint admin (the platform's Setup wizard surfaces that command). The
# grants are required for the other content operations: compatibility check,
# identity-map upload, MnA relationship, and the actual content move.
data "azuread_service_principal" "sharepoint" {
  client_id = "00000003-0000-0ff1-ce00-000000000000"
}

data "azuread_service_principal" "platform" {
  client_id = var.platform_app_client_id
}

resource "azuread_app_role_assignment" "graph" {
  for_each = toset(var.graph_roles)

  app_role_id         = data.azuread_service_principal.graph.app_role_ids[each.value]
  principal_object_id = data.azuread_service_principal.platform.object_id
  resource_object_id  = data.azuread_service_principal.graph.object_id
}

resource "azuread_app_role_assignment" "sharepoint" {
  for_each = toset(var.sharepoint_roles)

  app_role_id         = data.azuread_service_principal.sharepoint.app_role_ids[each.value]
  principal_object_id = data.azuread_service_principal.platform.object_id
  resource_object_id  = data.azuread_service_principal.sharepoint.object_id
}

resource "azuread_app_role_assignment" "exchange_manage_as_app" {
  count = var.grant_exchange_manage_as_app ? 1 : 0

  app_role_id         = data.azuread_service_principal.exchange.app_role_ids["Exchange.ManageAsApp"]
  principal_object_id = data.azuread_service_principal.platform.object_id
  resource_object_id  = data.azuread_service_principal.exchange.object_id
}

# Exchange Administrator template ID (fixed across all tenants).
locals {
  exchange_administrator_template_id = "29232cdf-9323-42fd-ade2-1d097af3e4de"
}

resource "azuread_directory_role" "exchange_admin" {
  count       = var.assign_exchange_administrator_role ? 1 : 0
  template_id = local.exchange_administrator_template_id
}

resource "azuread_directory_role_assignment" "platform_exchange_admin" {
  count               = var.assign_exchange_administrator_role ? 1 : 0
  role_id             = azuread_directory_role.exchange_admin[0].template_id
  principal_object_id = data.azuread_service_principal.platform.object_id
}
