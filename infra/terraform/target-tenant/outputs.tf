output "migration_app_client_id" {
  description = "HAND THIS TO THE SOURCE ADMIN (non-secret). Also save in Settings → Pre-Setup → Cross-Tenant Mailbox Migration App."
  value       = azuread_application.migration.client_id
}

output "migration_app_client_secret" {
  description = "Client secret VALUE for the migration app — save alongside the client ID in Settings → Pre-Setup. Read with `terraform output -raw migration_app_client_secret`. Never give this to the source admin; they don't need it."
  value       = azuread_application_password.migration.value
  sensitive   = true
}

output "migration_app_secret_expires" {
  description = "Expiry of the migration app client secret. Re-apply before this date to rotate, then update Settings → Pre-Setup."
  value       = azuread_application_password.migration.end_date
}

output "source_admin_consent_url" {
  description = "Fallback for the source admin if they don't run the source-tenant stack: one click on this URL consents the migration app in the source tenant (covers ONLY that step, not the source platform-app grants)."
  value       = "https://login.microsoftonline.com/${var.source_tenant_id}/adminconsent?client_id=${azuread_application.migration.client_id}&redirect_uri=https://office.com"
}

output "platform_sp_object_id" {
  description = "Target platform app's SP object ID — needed for the EXO New-ServicePrincipal registration (Setup wizard renders the script pre-filled)."
  value       = module.platform_grants.platform_sp_object_id
}
