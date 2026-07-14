variable "platform_app_client_id" {
  description = "Application (client) ID of the platform's existing app registration in this tenant."
  type        = string
}

variable "graph_roles" {
  description = "Microsoft Graph application roles to admin-consent on the platform app."
  type        = list(string)
}

variable "sharepoint_roles" {
  description = "Office 365 SharePoint Online application roles to admin-consent on the platform app (for cross-tenant OneDrive/SharePoint content migration)."
  type        = list(string)
  default     = []
}

variable "grant_exchange_manage_as_app" {
  description = "Grant Office 365 Exchange Online → Exchange.ManageAsApp (application) on the platform app."
  type        = bool
  default     = true
}

variable "assign_exchange_administrator_role" {
  description = "Assign the Exchange Administrator directory role to the platform app's service principal (EXO derives cmdlet RBAC from the token's wids claim)."
  type        = bool
  default     = true
}
