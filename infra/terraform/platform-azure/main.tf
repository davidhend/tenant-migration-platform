# ──────────────────────────────────────────────────────────────────────────────
# PLATFORM-AZURE stack — run by whoever operates the migration platform (which
# may be the target org or a third party/MSP whose Azure subscription belongs
# to neither migrating tenant). No Entra directory privileges needed — only
# Azure RBAC on the subscription/resource group.
#
# Provisions the Azure Automation account that executes the Windows-only
# SharePoint Online PowerShell module for cross-tenant OneDrive/SharePoint
# operations.
#
# Division of labor (deliberate):
#   - Terraform owns the INFRASTRUCTURE: account, PowerShell modules, RBAC,
#     managed identity, Key Vault access, and the initial runbook.
#   - The API owns runbook CONTENT: RunbookAutoPublisher compares the deployed
#     runbook against apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1 at
#     startup and republishes on drift, so runbook logic ships with the code
#     that calls it. lifecycle.ignore_changes below keeps Terraform from
#     fighting that.
# ──────────────────────────────────────────────────────────────────────────────

terraform {
  required_version = ">= 1.5"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
  subscription_id = var.subscription_id
}

resource "azurerm_resource_group" "automation" {
  count    = var.create_resource_group ? 1 : 0
  name     = var.resource_group_name
  location = var.location
}

data "azurerm_resource_group" "automation" {
  count      = var.create_resource_group ? 0 : 1
  name       = var.resource_group_name
  depends_on = [azurerm_resource_group.automation]
}

locals {
  rg_name     = var.create_resource_group ? azurerm_resource_group.automation[0].name : data.azurerm_resource_group.automation[0].name
  rg_location = var.create_resource_group ? azurerm_resource_group.automation[0].location : data.azurerm_resource_group.automation[0].location
}

resource "azurerm_automation_account" "main" {
  name                = var.automation_account_name
  location            = local.rg_location
  resource_group_name = local.rg_name
  sku_name            = "Basic"

  dynamic "identity" {
    for_each = var.enable_automation_managed_identity ? [1] : []
    content {
      type = "SystemAssigned"
    }
  }
}

# ── PowerShell modules ───────────────────────────────────────────────────────

resource "azurerm_automation_module" "spo" {
  name                    = "Microsoft.Online.SharePoint.PowerShell"
  resource_group_name     = local.rg_name
  automation_account_name = azurerm_automation_account.main.name

  module_link {
    uri = "https://www.powershellgallery.com/api/v2/package/Microsoft.Online.SharePoint.PowerShell"
  }
}

# Needed only for the UseKeyVaultCertificate runbook path
# (Connect-AzAccount -Identity + Get-AzKeyVaultSecret).
resource "azurerm_automation_module" "az_accounts" {
  count                   = var.install_az_keyvault_modules ? 1 : 0
  name                    = "Az.Accounts"
  resource_group_name     = local.rg_name
  automation_account_name = azurerm_automation_account.main.name

  module_link {
    uri = "https://www.powershellgallery.com/api/v2/package/Az.Accounts"
  }
}

resource "azurerm_automation_module" "az_keyvault" {
  count                   = var.install_az_keyvault_modules ? 1 : 0
  name                    = "Az.KeyVault"
  resource_group_name     = local.rg_name
  automation_account_name = azurerm_automation_account.main.name

  module_link {
    uri = "https://www.powershellgallery.com/api/v2/package/Az.KeyVault"
  }

  # Az.KeyVault depends on Az.Accounts being extracted first.
  depends_on = [azurerm_automation_module.az_accounts]
}

# ── Runbook (seeded once; content thereafter owned by the API) ──────────────

resource "azurerm_automation_runbook" "spo_cross_tenant" {
  name                    = var.runbook_name
  location                = local.rg_location
  resource_group_name     = local.rg_name
  automation_account_name = azurerm_automation_account.main.name
  runbook_type            = "PowerShell"
  log_verbose             = false
  log_progress            = false
  description             = "Cross-tenant SPO/OneDrive operations. Content is auto-published by the migration platform API on startup (Azure:Automation:AutoPublishRunbook) — do not manage content here."
  content                 = file(var.runbook_source_path)

  lifecycle {
    # The API's RunbookAutoPublisher keeps content in sync with the repo;
    # Terraform must not revert it on the next apply.
    ignore_changes = [content]
  }
}

# ── RBAC ─────────────────────────────────────────────────────────────────────

# Automation Contributor (not just Job Operator): lets the API start jobs AND
# auto-publish the runbook.
resource "azurerm_role_assignment" "api_automation_contributor" {
  scope                = azurerm_automation_account.main.id
  role_definition_name = "Automation Contributor"
  principal_id         = var.api_principal_object_id
}

# Let the Automation account's managed identity read tenant PFX secrets when
# the runbook fetches certificates from Key Vault itself
# (Azure:Automation:UseKeyVaultCertificate=true).
resource "azurerm_role_assignment" "automation_kv_secrets" {
  count                = var.enable_automation_managed_identity && var.key_vault_id != "" ? 1 : 0
  scope                = var.key_vault_id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_automation_account.main.identity[0].principal_id
}
