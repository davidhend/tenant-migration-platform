variable "target_tenant_id" {
  description = "Entra tenant ID of the TARGET (gaining) tenant."
  type        = string
}

variable "source_tenant_id" {
  description = "Entra tenant ID of the SOURCE tenant — used only to render the adminconsent URL output for the source admin."
  type        = string
}

variable "migration_app_display_name" {
  description = "Display name for the cross-tenant Mailbox Migration app registration."
  type        = string
  default     = "Cross-Tenant Mailbox Migration"
}

variable "migration_app_secret_rotation_days" {
  description = "How often a `terraform apply` ROTATES the migration app client secret (a new secret is issued once this many days have passed, on the next apply). This does NOT set the secret's expiry — Entra issues it with the provider default (~2 years); keep this comfortably below that so rotation happens first. Every rotation must be re-entered in Settings → Pre-Setup AND the migration endpoint credential must be recreated manually."
  type        = number
  default     = 365
}

variable "platform_app_client_id" {
  description = "Application (client) ID of the platform's existing app registration in the TARGET tenant."
  type        = string
}

variable "platform_graph_roles" {
  description = "Microsoft Graph application roles to admin-consent on the TARGET platform app."
  type        = list(string)
  default = [
    "Policy.Read.All",       # inbound partner-policy check (cross-tenant sync discovery)
    "User.ReadWrite.All",    # license auto-assign (target is the default side); covers the user scanner's reads
    "Organization.Read.All", # read subscribedSkus for license auto-assign
    # Discovery scanners (read-only) — scans can run against either tenant:
    "Group.Read.All",        # group scanner (/groups + member counts)
    "Sites.Read.All",        # SharePoint scanner (getAllSites) + OneDrive scanner (/users/{id}/drive)
    "Domain.Read.All",       # domain scanner (/domains)
  ]
}

variable "platform_sharepoint_roles" {
  description = "Office 365 SharePoint Online application roles to admin-consent on the TARGET platform app (cross-tenant OneDrive/SharePoint content migration)."
  type        = list(string)
  default = [
    "Sites.FullControl.All",                    # site + content operations
    "User.ReadWrite.All",                       # User Profile Service (profile access)
    "OneDrive.Provision.All",                   # OneDrive pre-provisioning happens target-side
    "SharePointCrossTenantMigration.Manage.All", # the cross-tenant content move
    "Migration.ReadWrite.All",                  # SPO migration API (identity-map upload etc.)
  ]
}

variable "grant_exchange_manage_as_app" {
  description = "Grant Exchange.ManageAsApp on the platform app (harmless if already consented)."
  type        = bool
  default     = true
}

variable "assign_exchange_administrator_role" {
  description = "Assign the Exchange Administrator directory role to the platform app's SP."
  type        = bool
  default     = true
}
