variable "source_tenant_id" {
  description = "Entra tenant ID of the SOURCE (losing) tenant."
  type        = string
}

variable "migration_app_client_id" {
  description = "Application (client) ID of the cross-tenant Mailbox Migration app — the migration_app_client_id output of the target-tenant stack (non-secret; get it from the target admin)."
  type        = string
}

variable "platform_app_client_id" {
  description = "Application (client) ID of the platform's existing app registration in the SOURCE tenant."
  type        = string
}

variable "platform_graph_roles" {
  description = "Microsoft Graph application roles to admin-consent on the SOURCE platform app."
  type        = list(string)
  default = [
    "Synchronization.ReadWrite.All", # cross-tenant sync discovery (jobs/list)
    "Application.Read.All",          # cross-tenant sync discovery (SP scan)
    "User.ReadWrite.All",            # license auto-assign when LicenseAssignmentSide=source; covers the user scanner's reads
    "Organization.Read.All",         # read subscribedSkus for license auto-assign
    # Discovery scanners (read-only):
    "Group.Read.All",                # group scanner (/groups + member counts)
    "Sites.Read.All",                # SharePoint scanner (getAllSites) + OneDrive scanner (/users/{id}/drive)
    "Domain.Read.All",               # domain scanner (/domains)
  ]
}

variable "platform_sharepoint_roles" {
  description = "Office 365 SharePoint Online application roles to admin-consent on the SOURCE platform app (cross-tenant OneDrive/SharePoint content migration)."
  type        = list(string)
  default = [
    "Sites.FullControl.All",                    # site + content operations
    "User.ReadWrite.All",                       # User Profile Service (profile access)
    "SharePointCrossTenantMigration.Manage.All", # the cross-tenant content move (source initiates)
    "Migration.ReadWrite.All",                  # SPO migration API
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
