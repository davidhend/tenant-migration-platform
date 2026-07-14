output "platform_sp_object_id" {
  description = "Object ID of the platform app's service principal — needed for the EXO New-ServicePrincipal registration (the one remaining manual Exchange step)."
  value       = data.azuread_service_principal.platform.object_id
}

output "exchange_sp_app_role_ids" {
  description = "App-role name → ID map of the Office 365 Exchange Online service principal in this tenant."
  value       = data.azuread_service_principal.exchange.app_role_ids
}

output "exchange_sp_object_id" {
  description = "Object ID of the Office 365 Exchange Online service principal in this tenant."
  value       = data.azuread_service_principal.exchange.object_id
}

output "granted_sharepoint_roles" {
  description = "Office 365 SharePoint Online application roles granted to the platform app in this tenant."
  value       = sort(var.sharepoint_roles)
}
