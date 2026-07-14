# ──────────────────────────────────────────────────────────────────────────────
# PLATFORM-APP stack — run by the platform operator (Azure RBAC only, no Entra
# directory privileges). Provisions the API's cloud host and datastore:
#
#   - Azure Container App running the API image, with a system-assigned managed
#     identity (this is the DefaultAzureCredential identity the API uses at
#     runtime — the one that needs Key Vault + Automation access).
#   - Role assignments for that identity: Key Vault Crypto Officer (DataProtection
#     key wrap/unwrap), Key Vault Secrets User (platform secret store), Key Vault
#     Certificate User (tenant PFXs), and Automation Contributor (trigger +
#     auto-publish the runbook).
#   - Azure Database for PostgreSQL Flexible Server with automated backups.
#
# Depends on the Key Vault and Automation account already existing (create the
# vault out-of-band and the Automation account via the platform-azure stack);
# their resource IDs are passed in as variables.
#
# Single instance by design: the API keeps its work queues in-memory with DB
# rehydration, which is correct for ONE replica but double-processes with more.
# The Container App is pinned to 1/1 replicas; override var.replica_count only
# after the queues move to a shared broker.
# ──────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.5"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
}

resource "azurerm_resource_group" "app" {
  count    = var.create_resource_group ? 1 : 0
  name     = var.resource_group_name
  location = var.location
}

data "azurerm_resource_group" "app" {
  count      = var.create_resource_group ? 0 : 1
  name       = var.resource_group_name
  depends_on = [azurerm_resource_group.app]
}

locals {
  rg_name     = var.create_resource_group ? azurerm_resource_group.app[0].name : data.azurerm_resource_group.app[0].name
  rg_location = var.create_resource_group ? azurerm_resource_group.app[0].location : data.azurerm_resource_group.app[0].location

  db_connection_string = join(";", [
    "Host=${azurerm_postgresql_flexible_server.main.fqdn}",
    "Port=5432",
    "Database=${azurerm_postgresql_flexible_server_database.main.name}",
    "Username=${var.postgres_admin_login}",
    "Password=${random_password.postgres.result}",
    "SslMode=Require",
  ])
}

# ── PostgreSQL Flexible Server ───────────────────────────────────────────────

resource "random_password" "postgres" {
  length           = 32
  special          = true
  override_special = "!#$%*()-_=+[]{}"
}

resource "azurerm_postgresql_flexible_server" "main" {
  name                          = var.postgres_server_name
  resource_group_name           = local.rg_name
  location                      = local.rg_location
  version                       = "16"
  administrator_login           = var.postgres_admin_login
  administrator_password        = random_password.postgres.result
  sku_name                      = var.postgres_sku_name
  storage_mb                    = var.postgres_storage_mb
  backup_retention_days         = var.backup_retention_days
  geo_redundant_backup_enabled  = var.geo_redundant_backup
  public_network_access_enabled = true # simplification; VNet integration is the hardened option

  lifecycle {
    # The admin password lives only in state/secret; ignore drift if rotated
    # out-of-band. Remove this to let Terraform manage rotation.
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "main" {
  name      = "migration_platform"
  server_id = azurerm_postgresql_flexible_server.main.id
  collation = "en_US.utf8"
  charset   = "utf8"
}

# Allow other Azure services (the Container App's outbound) to reach the server.
# 0.0.0.0/0.0.0.0 is the documented "Allow Azure services" rule, NOT public
# internet. Prefer VNet integration + private endpoint for production isolation.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# ── Container App environment ────────────────────────────────────────────────

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${var.app_name}-logs"
  resource_group_name = local.rg_name
  location            = local.rg_location
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_container_app_environment" "main" {
  name                       = "${var.app_name}-env"
  resource_group_name        = local.rg_name
  location                   = local.rg_location
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
}

# ── Container App (the API) ──────────────────────────────────────────────────

resource "azurerm_container_app" "api" {
  name                         = var.app_name
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = local.rg_name
  revision_mode                = "Single"

  identity {
    type = "SystemAssigned"
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  secret {
    name  = "db-connection"
    value = local.db_connection_string
  }

  dynamic "secret" {
    for_each = var.azure_ad_client_id != "" ? [1] : []
    content {
      name  = "azuread-clientid"
      value = var.azure_ad_client_id
    }
  }

  template {
    # 1/1 — see the single-instance note at the top of this file.
    min_replicas = var.replica_count
    max_replicas = var.replica_count

    container {
      name   = "api"
      image  = var.api_image
      cpu    = var.container_cpu
      memory = var.container_memory

      env {
        name        = "ConnectionStrings__DefaultConnection"
        secret_name = "db-connection"
      }
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }
      env {
        name  = "Platform__DevMode"
        value = "false"
      }
      env {
        name  = "Database__AutoMigrate"
        value = "true"
      }
      env {
        name  = "KeyVault__Enabled"
        value = "true"
      }
      env {
        name  = "KeyVault__VaultUri"
        value = var.key_vault_uri
      }
      env {
        name  = "AzureAd__TenantId"
        value = var.azure_ad_tenant_id
      }
      dynamic "env" {
        for_each = var.azure_ad_client_id != "" ? [1] : []
        content {
          name        = "AzureAd__ClientId"
          secret_name = "azuread-clientid"
        }
      }
      env {
        name  = "Azure__Automation__SubscriptionId"
        value = var.subscription_id
      }
      env {
        name  = "Azure__Automation__ResourceGroup"
        value = var.automation_resource_group
      }
      env {
        name  = "Azure__Automation__AccountName"
        value = var.automation_account_name
      }

      liveness_probe {
        transport     = "HTTP"
        port          = 8080
        path          = "/health/live"
        initial_delay = 20
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health/ready"
      }
    }
  }
}

# ── Role assignments for the Container App's managed identity ────────────────

locals {
  api_identity_principal_id = azurerm_container_app.api.identity[0].principal_id
}

# DataProtection key wrap/unwrap (the keys plane — currently the missing role).
resource "azurerm_role_assignment" "kv_crypto_officer" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Crypto Officer"
  principal_id         = local.api_identity_principal_id
}

# Platform secret store reads/writes.
resource "azurerm_role_assignment" "kv_secrets_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = local.api_identity_principal_id
}

# Tenant PFX certificates.
resource "azurerm_role_assignment" "kv_certificate_user" {
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Certificate User"
  principal_id         = local.api_identity_principal_id
}

# Trigger + auto-publish the Automation runbook.
resource "azurerm_role_assignment" "automation_contributor" {
  scope                = var.automation_account_id
  role_definition_name = "Automation Contributor"
  principal_id         = local.api_identity_principal_id
}

# Pull the image from ACR when var.acr_id is set (public images need no role).
resource "azurerm_role_assignment" "acr_pull" {
  count                = var.acr_id != "" ? 1 : 0
  scope                = var.acr_id
  role_definition_name = "AcrPull"
  principal_id         = local.api_identity_principal_id
}
