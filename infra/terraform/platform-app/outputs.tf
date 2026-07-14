output "api_identity_principal_id" {
  description = "Principal ID of the Container App's system-assigned managed identity — the runtime DefaultAzureCredential identity granted the Key Vault + Automation roles."
  value       = azurerm_container_app.api.identity[0].principal_id
}

output "api_fqdn" {
  description = "Public FQDN of the API Container App."
  value       = azurerm_container_app.api.ingress[0].fqdn
}

output "api_url" {
  description = "Base URL of the API (set the frontend's NEXT_PUBLIC_API_URL to this + /api)."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

output "postgres_fqdn" {
  description = "FQDN of the PostgreSQL Flexible Server."
  value       = azurerm_postgresql_flexible_server.main.fqdn
}

output "postgres_admin_password" {
  description = "Generated PostgreSQL administrator password. Read with: terraform output -raw postgres_admin_password"
  value       = random_password.postgres.result
  sensitive   = true
}
