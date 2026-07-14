output "migration_app_consented" {
  description = "Confirms the migration app is consented in the source tenant."
  value       = "Mailbox.Migration granted to ${var.migration_app_client_id} (SP ${azuread_service_principal.migration.object_id})"
}

output "platform_sp_object_id" {
  description = "Source platform app's SP object ID — needed for the EXO New-ServicePrincipal registration (Setup wizard renders the script pre-filled)."
  value       = module.platform_grants.platform_sp_object_id
}
