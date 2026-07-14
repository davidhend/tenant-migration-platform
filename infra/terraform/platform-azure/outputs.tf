output "automation_account_id" {
  description = "Resource ID of the Automation account (matches Azure:Automation settings in the platform)."
  value       = azurerm_automation_account.main.id
}

output "automation_managed_identity_principal_id" {
  description = "Principal ID of the Automation account's system-assigned managed identity (empty when disabled)."
  value       = var.enable_automation_managed_identity ? azurerm_automation_account.main.identity[0].principal_id : ""
}

output "platform_settings" {
  description = "Values for Settings → Pre-Setup → Azure Automation (or appsettings.json Azure:Automation)."
  value = {
    SubscriptionId = var.subscription_id
    ResourceGroup  = var.resource_group_name
    AccountName    = azurerm_automation_account.main.name
    RunbookName    = var.runbook_name
  }
}
