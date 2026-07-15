variable "subscription_id" {
  description = "Subscription hosting the Automation account."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group for the Automation account."
  type        = string
  default     = "migration-automation-rg"
}

variable "create_resource_group" {
  description = "Create the resource group (false = it already exists and is imported/unmanaged)."
  type        = bool
  default     = false
}

variable "location" {
  description = "Azure region for the Automation account / resource group."
  type        = string
  default     = "eastus"
}

variable "automation_account_name" {
  description = "Name of the Automation account."
  type        = string
  default     = "migration-automation"
}

variable "runbook_name" {
  description = "Runbook name — must match Azure:Automation:RunbookName in appsettings.json."
  type        = string
  default     = "Invoke-SpoCrossTenantOperation"
}

variable "runbook_source_path" {
  description = "Path to the runbook script used to seed the runbook on first create. Day-to-day content updates are pushed by the API's RunbookAutoPublisher, not Terraform."
  type        = string
  default     = "../../../apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1"
}

variable "api_principal_object_id" {
  description = <<-EOT
    Object ID of the security principal the API authenticates to ARM as
    (DefaultAzureCredential). Granted Automation Contributor so the API can
    run jobs AND auto-publish the runbook.

    Pick the ONE case that matches how you run the API:

    1. Docker compose stack (./start.sh — the default clone-to-running
       path), or any API using Settings → Azure Identity credentials.
       You must CREATE a dedicated app registration first — no Terraform
       stack creates it (it needs no Graph/EXO permissions, only the Azure
       RBAC this stack assigns):
         az ad app create --display-name migration-platform-api --query appId -o tsv
         az ad sp create --id <appId>
         az ad app credential reset --id <appId> --append --query password -o tsv
       Put the appId + secret in Settings → Azure Identity, and supply the
       app's SERVICE PRINCIPAL object id here (the Enterprise Application
       object id, NOT the app registration's object id):
         az ad sp show --id <appId> --query id -o tsv

    2. Azure Container App with system-assigned managed identity (the
       platform-app stack): that identity's principalId.

    3. API run directly on your machine (dotnet run) with az login and
       NO Azure Identity configured in Settings: your user object id —
         az ad signed-in-user show --query id -o tsv
       This case does NOT apply to the Docker stack — the container has
       no az login session.
  EOT
  type        = string
}

variable "install_az_keyvault_modules" {
  description = "Import Az.Accounts + Az.KeyVault into the Automation account (needed only when Azure:Automation:UseKeyVaultCertificate=true)."
  type        = bool
  default     = true
}

variable "enable_automation_managed_identity" {
  description = "Enable a system-assigned managed identity on the Automation account (used by the runbook's Key Vault certificate fetch path)."
  type        = bool
  default     = true
}

variable "key_vault_id" {
  description = "Resource ID of the Key Vault holding tenant certificates. When set (and the managed identity is enabled), the Automation identity is granted Key Vault Secrets User on it."
  type        = string
  default     = ""
}
