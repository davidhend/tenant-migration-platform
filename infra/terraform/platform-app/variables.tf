# ── Subscription / resource group ────────────────────────────────────────────

variable "subscription_id" {
  description = "Subscription hosting the API Container App and PostgreSQL server."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group for the API host + database."
  type        = string
  default     = "migration-platform-app-rg"
}

variable "create_resource_group" {
  description = "Create the resource group (false = it already exists and is imported/unmanaged)."
  type        = bool
  default     = true
}

variable "location" {
  description = "Azure region."
  type        = string
  default     = "eastus"
}

variable "app_name" {
  description = "Base name for the Container App + its environment/logs."
  type        = string
  default     = "migration-platform-api"
}

# ── Container image ───────────────────────────────────────────────────────────

variable "api_image" {
  description = "Fully-qualified API image reference (e.g. myacr.azurecr.io/migration-api:1.0.0 or a public registry tag). Built from apps/api/Dockerfile."
  type        = string
}

variable "acr_id" {
  description = "Resource ID of the Azure Container Registry hosting the image. When set, the Container App identity is granted AcrPull. Leave empty for public images."
  type        = string
  default     = ""
}

variable "container_cpu" {
  description = "vCPU for the API container."
  type        = number
  default     = 0.5
}

variable "container_memory" {
  description = "Memory for the API container (must pair with CPU per Container Apps allocation rules)."
  type        = string
  default     = "1Gi"
}

variable "replica_count" {
  description = "Fixed replica count. MUST stay 1 until the in-memory work queues move to a shared broker — more than one replica double-processes jobs."
  type        = number
  default     = 1
}

# ── Existing dependencies (from platform-azure / out-of-band) ────────────────

variable "key_vault_id" {
  description = "Resource ID of the existing Key Vault (tenant certs + platform secrets + DataProtection key)."
  type        = string
}

variable "key_vault_uri" {
  description = "Vault URI, e.g. https://<your-kv-name>.vault.azure.net/"
  type        = string
}

variable "automation_account_id" {
  description = "Resource ID of the Automation account (from the platform-azure stack)."
  type        = string
}

variable "automation_account_name" {
  description = "Automation account name (for the API's Azure:Automation:AccountName setting)."
  type        = string
  default     = "migration-automation"
}

variable "automation_resource_group" {
  description = "Resource group of the Automation account (for Azure:Automation:ResourceGroup)."
  type        = string
  default     = "migration-automation-rg"
}

# ── Entra ID auth (the platform sign-in app registration) ────────────────────

variable "azure_ad_tenant_id" {
  description = "Tenant ID of the platform sign-in app registration (AzureAd:TenantId). Empty leaves the API in no-auth lockdown until configured."
  type        = string
  default     = ""
}

variable "azure_ad_client_id" {
  description = "Client ID of the platform sign-in app registration (AzureAd:ClientId)."
  type        = string
  default     = ""
}

# ── PostgreSQL ────────────────────────────────────────────────────────────────

variable "postgres_server_name" {
  description = "Globally-unique name for the PostgreSQL Flexible Server."
  type        = string
  default     = "migration-platform-db"
}

variable "postgres_admin_login" {
  description = "Administrator login for the PostgreSQL server."
  type        = string
  default     = "migration_admin"
}

variable "postgres_sku_name" {
  description = "Flexible Server SKU (e.g. B_Standard_B1ms burstable, or GP_Standard_D2s_v3 for production)."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  description = "Storage in MB (min 32768)."
  type        = number
  default     = 32768
}

variable "backup_retention_days" {
  description = "Automated backup retention (7-35 days)."
  type        = number
  default     = 14
}

variable "geo_redundant_backup" {
  description = "Enable geo-redundant backups (higher cost; cross-region restore)."
  type        = bool
  default     = false
}
