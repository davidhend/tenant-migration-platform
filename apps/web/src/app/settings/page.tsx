"use client";

import { useEffect, useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Save, Loader2, ExternalLink, Trash2, CheckCircle2, XCircle, ChevronDown, ChevronRight, AlertTriangle, HelpCircle, RefreshCw } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import {
  settingsApi,
  type AutomationEnvironmentReport,
  type AzureAutomationSettings,
  type AzureIdentityRequest,
  type AzureIdentityResponse,
  type CrossTenantMigrationRequest,
} from "@/lib/api";
import type { DiagCheck } from "@/types";

function Step({ n, title, body }: { n: number; title: string; body: React.ReactNode }) {
  return (
    <div className="flex gap-4">
      <div className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary text-xs font-semibold text-primary-foreground">
        {n}
      </div>
      <div className="flex-1 space-y-2">
        <h3 className="font-medium leading-7">{title}</h3>
        <div className="space-y-2 text-muted-foreground">{body}</div>
      </div>
    </div>
  );
}

function Cmd({ children }: { children: string }) {
  return (
    <pre className="overflow-x-auto rounded-md bg-muted p-3 text-xs text-foreground">
      <code>{children}</code>
    </pre>
  );
}

function LinkOut({ href, children }: { href: string; children: React.ReactNode }) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noreferrer"
      className="inline-flex items-center gap-1 text-sm text-primary hover:underline"
    >
      {children}
      <ExternalLink className="h-3 w-3" />
    </a>
  );
}

function CheckStatusIcon({ status }: { status: DiagCheck["status"] }) {
  switch (status) {
    case "pass":
      return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" />;
    case "fail":
      return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />;
    case "warn":
      return <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />;
    default:
      return <HelpCircle className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />;
  }
}

function EnvironmentCheckList({ report }: { report: AutomationEnvironmentReport }) {
  return (
    <div className="space-y-2 rounded-md border p-3">
      {report.checks.map((c) => (
        <div key={c.id} className="flex gap-2 text-sm">
          <CheckStatusIcon status={c.status} />
          <div className="min-w-0 flex-1">
            <p className="font-medium">{c.description}</p>
            <p className="break-words text-xs text-muted-foreground">{c.message}</p>
            {c.remediation && c.status !== "pass" && (
              <p className="mt-1 break-words text-xs text-amber-700 dark:text-amber-400">
                Fix: {c.remediation}
              </p>
            )}
          </div>
        </div>
      ))}
      <p className="pt-1 text-xs text-muted-foreground">
        {report.summary} — scanned {new Date(report.generatedAt).toLocaleString()}
      </p>
    </div>
  );
}

function DeploymentBadge({
  checking,
  deployed,
  labels = ["Deployed", "Not deployed"],
}: {
  checking: boolean;
  deployed: boolean | undefined;
  labels?: [string, string];
}) {
  if (checking)
    return (
      <Badge variant="secondary">
        <Loader2 className="mr-1 h-3 w-3 animate-spin" /> Checking…
      </Badge>
    );
  if (deployed === undefined) return null;
  return (
    <Badge variant={deployed ? "default" : "secondary"}>
      {deployed ? (
        <><CheckCircle2 className="mr-1 h-3 w-3" /> {labels[0]}</>
      ) : (
        <><XCircle className="mr-1 h-3 w-3" /> {labels[1]}</>
      )}
    </Badge>
  );
}

const EMPTY: AzureAutomationSettings = {
  subscriptionId: "",
  resourceGroup: "",
  accountName: "",
  runbookName: "Invoke-SpoCrossTenantOperation",
  jobPollIntervalSeconds: 10,
  jobTimeoutMinutes: 15,
};

export default function SettingsPage() {
  const qc = useQueryClient();
  const [form, setForm] = useState<AzureAutomationSettings>(EMPTY);
  // Terraform is the primary deployment path; manual how-to blocks are hidden
  // behind this single toggle.
  const [showManual, setShowManual] = useState(false);

  const { data, isLoading } = useQuery({
    queryKey: ["settings", "azure-automation"],
    queryFn: () => settingsApi.getAzureAutomation(),
  });

  useEffect(() => {
    if (data) setForm(data);
  }, [data]);

  // Live ARM scan of the Automation environment (account, RBAC, runbook,
  // modules) — the deployed-state complement to the saved-settings check.
  const automationConfigured = !!(
    data?.subscriptionId && data?.resourceGroup && data?.accountName
  );

  // ARM resource IDs for the Terraform import instructions, derived from the
  // operator's own Azure Automation settings (placeholders until configured).
  const automationAccountArmId = `/subscriptions/${
    form.subscriptionId || "<subscription-id>"
  }/resourceGroups/${form.resourceGroup || "<resource-group>"}/providers/Microsoft.Automation/automationAccounts/${
    form.accountName || "<automation-account>"
  }`;
  const runbookNameForImport = form.runbookName || "Invoke-SpoCrossTenantOperation";
  const verify = useQuery({
    queryKey: ["settings", "azure-automation", "verify"],
    queryFn: () => settingsApi.verifyAzureAutomation(),
    enabled: automationConfigured,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });

  const save = useMutation({
    mutationFn: (s: AzureAutomationSettings) => settingsApi.updateAzureAutomation(s),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "azure-automation"] });
      toast.success("Azure Automation settings saved.");
    },
    onError: (err: Error) => toast.error(err.message),
  });

  const set = <K extends keyof AzureAutomationSettings>(
    key: K, value: AzureAutomationSettings[K]
  ) => setForm((f) => ({ ...f, [key]: value }));

  // ── Azure Identity (service principal) ──────────────────────────────────
  const [idForm, setIdForm] = useState<AzureIdentityRequest>({
    tenantId: "", clientId: "", authMethod: "secret",
  });

  const identity = useQuery({
    queryKey: ["settings", "azure-identity"],
    queryFn: () => settingsApi.getAzureIdentity(),
  });

  useEffect(() => {
    if (identity.data) {
      setIdForm((f) => ({
        ...f,
        tenantId: identity.data.tenantId,
        clientId: identity.data.clientId,
        authMethod: identity.data.authMethod || "secret",
        certificateThumbprint: identity.data.certificateThumbprint || "",
      }));
    }
  }, [identity.data]);

  const saveIdentity = useMutation({
    mutationFn: (s: AzureIdentityRequest) => settingsApi.updateAzureIdentity(s),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "azure-identity"] });
      setIdForm((f) => ({ ...f, clientSecret: undefined, certificateBase64: undefined, certificatePassword: undefined }));
      toast.success("Service principal credentials saved.");
    },
    onError: (err: Error) => toast.error(err.message),
  });

  const deleteIdentity = useMutation({
    mutationFn: () => settingsApi.deleteAzureIdentity(),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "azure-identity"] });
      setIdForm({ tenantId: "", clientId: "", authMethod: "secret" });
      toast.success("Service principal credentials removed.");
    },
    onError: (err: Error) => toast.error(err.message),
  });

  const setId = (key: keyof AzureIdentityRequest, value: string) =>
    setIdForm((f) => ({ ...f, [key]: value }));

  const handleCertFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = () => {
      const base64 = (reader.result as string).split(",")[1] ?? "";
      setIdForm((f) => ({ ...f, certificateBase64: base64 }));
      toast.success(`Certificate loaded: ${file.name}`);
    };
    reader.readAsDataURL(file);
  };

  // ── Cross-tenant Mailbox Migration app (Platform:CrossTenantMigration) ────
  const [ctmForm, setCtmForm] = useState<CrossTenantMigrationRequest>({ appId: "" });
  const ctm = useQuery({
    queryKey: ["settings", "cross-tenant-migration"],
    queryFn: () => settingsApi.getCrossTenantMigration(),
  });
  useEffect(() => {
    if (ctm.data) setCtmForm((f) => ({ ...f, appId: ctm.data.appId }));
  }, [ctm.data]);
  const saveCtm = useMutation({
    mutationFn: (s: CrossTenantMigrationRequest) => settingsApi.updateCrossTenantMigration(s),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["settings", "cross-tenant-migration"] });
      setCtmForm((f) => ({ ...f, clientSecret: undefined }));
      toast.success("Cross-tenant migration app settings saved. Applied immediately — no restart needed.");
    },
    onError: (err: Error) => toast.error(err.message),
  });

  return (
    <div className="space-y-6 p-6">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Pre-Setup</h1>
        <p className="text-sm text-muted-foreground">
          Prerequisites and configuration required before running user, email,
          OneDrive, or SharePoint cross-tenant migrations.
        </p>
      </div>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between gap-2">
            <CardTitle>Deploy with Terraform (recommended)</CardTitle>
            <DeploymentBadge
              checking={
                ctm.isLoading || identity.isLoading || isLoading ||
                (automationConfigured && verify.isFetching && !verify.data)
              }
              deployed={
                !!ctm.data?.isConfigured &&
                !!identity.data?.isConfigured &&
                !!verify.data?.isDeployed
              }
              labels={["Deployed", "Incomplete"]}
            />
          </div>
          <CardDescription>
            The pre-setup is codified as three independent Terraform stacks under{" "}
            <code className="rounded bg-muted px-1">infra/terraform/</code>,
            separated by persona — one admin rarely has deployment rights in
            both tenants. Each stack has its own state and plain single-tenant
            credentials.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          <div className="space-y-2 rounded-md border p-3">
            <p className="font-medium text-foreground">Deployment status</p>
            <div className="flex gap-2">
              <CheckStatusIcon status={ctm.data?.isConfigured ? "pass" : "fail"} />
              <p className="min-w-0 flex-1">
                <code className="rounded bg-muted px-1">target-tenant/</code>{" "}
                <span className="text-muted-foreground">
                  {ctm.data?.isConfigured
                    ? "Migration app ID + secret are saved in the platform (card below)."
                    : "Migration app ID + secret not saved yet — apply the stack, then save its outputs in the card below."}
                </span>
              </p>
            </div>
            <div className="flex gap-2">
              <CheckStatusIcon status="unknown" />
              <p className="min-w-0 flex-1">
                <code className="rounded bg-muted px-1">source-tenant/</code>{" "}
                <span className="text-muted-foreground">
                  Consent lives in the source tenant — verified per project via the
                  Setup wizard&apos;s tenant-prerequisite diagnostics.
                </span>
              </p>
            </div>
            <div className="flex gap-2">
              <CheckStatusIcon
                status={
                  !automationConfigured || verify.isError
                    ? "fail"
                    : verify.data
                      ? verify.data.isDeployed
                        ? (verify.data.warnCount > 0 ? "warn" : "pass")
                        : "fail"
                      : "unknown"
                }
              />
              <p className="min-w-0 flex-1">
                <code className="rounded bg-muted px-1">platform-azure/</code>{" "}
                <span className="text-muted-foreground">
                  {!automationConfigured
                    ? "Azure Automation settings not configured yet (card below)."
                    : verify.isFetching && !verify.data
                      ? "Scanning the Azure environment…"
                      : verify.isError
                        ? "Environment scan failed — see the Azure Automation card below."
                        : verify.data
                          ? verify.data.isDeployed
                            ? verify.data.warnCount > 0
                              ? `Deployed — ${verify.data.warnCount} warning(s); details in the Azure Automation card below.`
                              : "Automation account, RBAC, runbook, and modules verified live in Azure."
                            : `Environment scan found problems (${verify.data.failCount} failing) — details in the Azure Automation card below.`
                          : "Environment not scanned yet."}
                </span>
              </p>
            </div>
          </div>

          <ul className="space-y-2">
            <li>
              <code className="rounded bg-muted px-1">target-tenant/</code> —{" "}
              <strong>target admin</strong>: creates the migration app
              (multitenant, Mailbox.Migration, office.com redirect) + client
              secret + target consent; target platform-app Graph grants +
              Exchange Administrator role.
            </li>
            <li>
              <code className="rounded bg-muted px-1">source-tenant/</code> —{" "}
              <strong>source admin</strong>: consents the migration app using
              only the client-ID GUID from the target run; source platform-app
              Graph grants + Exchange Administrator role.
            </li>
            <li>
              <code className="rounded bg-muted px-1">platform-azure/</code> —{" "}
              <strong>platform operator</strong>: Automation account, SPO/Az
              modules, runbook seed, Automation Contributor for the API
              identity. Azure RBAC only — no Entra directory privileges.
            </li>
          </ul>

          <p className="font-medium text-foreground">
            Order of operations — one non-secret handoff
          </p>
          <ol className="list-decimal space-y-1 pl-5 text-muted-foreground">
            <li>
              Target admin: <code className="rounded bg-muted px-1">terraform apply</code>{" "}
              → hand <code className="rounded bg-muted px-1">migration_app_client_id</code>{" "}
              (a non-secret GUID — the only cross-stack value) to the source
              admin, and save the client ID +{" "}
              <code className="rounded bg-muted px-1">terraform output -raw migration_app_client_secret</code>{" "}
              in the <strong>Cross-Tenant Mailbox Migration App</strong> card
              below. The secret never goes to the source admin.
            </li>
            <li>
              Source admin: set{" "}
              <code className="rounded bg-muted px-1">migration_app_client_id</code>{" "}
              in tfvars → <code className="rounded bg-muted px-1">terraform apply</code>.
            </li>
            <li>
              Platform operator:{" "}
              <code className="rounded bg-muted px-1">terraform apply</code>{" "}
              (any time; independent).
            </li>
          </ol>

          <p className="font-medium text-foreground">
            Authenticate (per stack, single tenant)
          </p>
          <Cmd>{`# target-tenant/ or source-tenant/  (no Azure subscription needed):
az login --tenant <that-tenant>.onmicrosoft.com --allow-no-subscriptions

# platform-azure/:
az login   # any account with Owner / User Access Administrator on the subscription`}</Cmd>

          <p className="font-medium text-foreground">
            Import a pre-existing Automation account first (only if created by hand)
          </p>
          <p className="text-muted-foreground">
            If your Automation account already exists (created manually rather
            than by Terraform), import it in{" "}
            <code className="rounded bg-muted px-1">platform-azure/</code>{" "}
            before the first apply so Terraform adopts it instead of trying to
            recreate it. Skip any import whose resource doesn&apos;t exist yet
            (e.g. the Az.* modules). Greenfield deployments (no existing
            account) skip this entirely. The resource IDs below are built from
            the <strong>Azure Automation</strong> card&apos;s saved settings —
            fill that in first.
          </p>
          <Cmd>{`cd infra/terraform/platform-azure
terraform init
terraform import azurerm_automation_account.main \\
  ${automationAccountArmId}
terraform import azurerm_automation_runbook.spo_cross_tenant \\
  ${automationAccountArmId}/runbooks/${runbookNameForImport}
terraform import azurerm_automation_module.spo \\
  ${automationAccountArmId}/modules/Microsoft.Online.SharePoint.PowerShell`}</Cmd>

          <p className="font-medium text-foreground">Deploy each stack</p>
          <Cmd>{`cd infra/terraform/<stack>
cp terraform.tfvars.example terraform.tfvars   # then edit
terraform init && terraform plan && terraform apply`}</Cmd>

          <p className="font-medium text-foreground">Where the outputs go</p>
          <ul className="list-disc space-y-1 pl-5 text-muted-foreground">
            <li>
              <code className="rounded bg-muted px-1">migration_app_client_id</code>{" "}
              + <code className="rounded bg-muted px-1">terraform output -raw migration_app_client_secret</code>{" "}
              → <strong>Cross-Tenant Mailbox Migration App</strong> card below.
            </li>
            <li>
              <code className="rounded bg-muted px-1">platform_settings</code>{" "}
              → <strong>Azure Automation</strong> card below.
            </li>
            <li>
              <code className="rounded bg-muted px-1">platform_sp_object_id</code>{" "}
              → the EXO{" "}
              <code className="rounded bg-muted px-1">New-ServicePrincipal</code>{" "}
              script in the project <strong>Setup</strong> wizard.
            </li>
          </ul>

          <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-700 dark:bg-amber-950">
            <p className="font-medium text-amber-800 dark:text-amber-300">
              Remaining manual steps (not covered by Terraform)
            </p>
            <ul className="mt-1 list-disc space-y-1 pl-5 text-amber-700 dark:text-amber-400">
              <li>
                <strong>EXO service-principal registration</strong> per tenant
                (<code>New-ServicePrincipal -AppId &lt;platform-app&gt; -ObjectId &lt;sp-object-id&gt;</code>)
                — the project <strong>Setup</strong> wizard renders this script
                pre-filled per tenant.
              </li>
              <li>
                <strong>Cross Tenant User Data Migration license purchase</strong>{" "}
                (billing) — assignment to users is automated by the platform at
                migration start.
              </li>
              <li>
                <strong>Cross-tenant access settings</strong> in BOTH tenants
                (needed for the Entra cross-tenant sync user-migration
                strategy; not used by mailbox or SharePoint/OneDrive moves).
                Entra → External Identities → Cross-tenant access settings →
                Add organization (the other tenant&apos;s ID), then: target
                side — Inbound access → allow{" "}
                <strong>users sync into this tenant</strong> + Trust settings →
                automatic invitation redemption; source side — Outbound access
                → Trust settings → automatic invitation redemption. The project{" "}
                <strong>Setup</strong> wizard lists both steps with your actual
                tenant IDs. (Kept manual: the azuread Terraform provider has no
                cross-tenant access policy support.)
              </li>
              <li>
                <strong>Custom-domain DNS verification</strong> in each tenant.
              </li>
            </ul>
          </div>
        </CardContent>
      </Card>

      <div>
        <Button
          variant="outline"
          size="sm"
          onClick={() => setShowManual((v) => !v)}
        >
          {showManual ? (
            <ChevronDown className="mr-2 h-4 w-4" />
          ) : (
            <ChevronRight className="mr-2 h-4 w-4" />
          )}
          Manual Deployment Instructions
        </Button>
      </div>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <CardTitle>Cross-Tenant Mailbox Migration App</CardTitle>
            {ctm.data && (
              <Badge variant={ctm.data.isConfigured ? "default" : "secondary"}>
                {ctm.data.isConfigured ? (
                  <><CheckCircle2 className="mr-1 h-3 w-3" /> Configured</>
                ) : (
                  <><XCircle className="mr-1 h-3 w-3" /> Not configured</>
                )}
              </Badge>
            )}
          </div>
          <CardDescription>
            The multitenant app registration (created in the <strong>target</strong>{" "}
            tenant, consented in both) that authorizes cross-tenant mailbox moves.
            It needs exactly one API permission:{" "}
            <strong>Office 365 Exchange Online → Mailbox.Migration</strong>{" "}
            (application) — admin-consented in the target directly and in the
            source via the adminconsent link on the project Setup page. No
            Microsoft Graph permissions are required on this app. With these
            values set, the platform stamps both organization relationships and
            creates the migration endpoint automatically at batch start. Changes
            apply immediately — no restart needed.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {ctm.isLoading ? (
            <Skeleton className="h-10 w-full" />
          ) : (
            <>
              <div className="grid gap-4 sm:grid-cols-2">
                <div className="space-y-2">
                  <Label htmlFor="ctm-appid">Application (client) ID</Label>
                  <Input
                    id="ctm-appid"
                    placeholder="00000000-0000-0000-0000-000000000000"
                    value={ctmForm.appId}
                    onChange={(e) => setCtmForm((f) => ({ ...f, appId: e.target.value }))}
                  />
                  <p className="text-xs text-muted-foreground">
                    From the app registration&apos;s <strong>Overview</strong> blade —
                    NOT the enterprise-app object ID.
                  </p>
                </div>
                <div className="space-y-2">
                  <Label htmlFor="ctm-secret">Client secret</Label>
                  <Input
                    id="ctm-secret"
                    type="password"
                    placeholder={
                      ctm.data?.clientSecretHint
                        ? `Stored (${ctm.data.clientSecretHint}) — leave blank to keep`
                        : "Secret VALUE from Certificates & secrets"
                    }
                    value={ctmForm.clientSecret ?? ""}
                    onChange={(e) => setCtmForm((f) => ({ ...f, clientSecret: e.target.value }))}
                  />
                  <p className="text-xs text-muted-foreground">
                    Write-only; note the expiry date in Entra — endpoint auth fails
                    when it lapses.
                  </p>
                </div>
              </div>
              <Button
                onClick={() => saveCtm.mutate(ctmForm)}
                disabled={saveCtm.isPending || !ctmForm.appId.trim()}
              >
                {saveCtm.isPending
                  ? <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  : <Save className="mr-2 h-4 w-4" />}
                Save
              </Button>
            </>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between gap-2">
            <CardTitle>Azure Automation</CardTitle>
            <div className="flex items-center gap-2">
              {automationConfigured && (
                <Button
                  variant="ghost"
                  size="sm"
                  onClick={() => verify.refetch()}
                  disabled={verify.isFetching}
                  title="Re-scan the Azure environment"
                >
                  <RefreshCw className={`h-4 w-4 ${verify.isFetching ? "animate-spin" : ""}`} />
                </Button>
              )}
              {automationConfigured ? (
                <DeploymentBadge
                  checking={verify.isFetching}
                  deployed={verify.data?.isDeployed}
                />
              ) : (
                !isLoading && (
                  <Badge variant="secondary">
                    <XCircle className="mr-1 h-3 w-3" /> Not configured
                  </Badge>
                )
              )}
            </div>
          </div>
          <CardDescription>
            OneDrive cross-tenant migrations are executed by the{" "}
            <code className="rounded bg-muted px-1">Invoke-SpoCrossTenantOperation</code>{" "}
            runbook hosted in an Azure Automation account (a Microsoft-managed
            Windows sandbox). The SharePoint Online PowerShell module is
            Windows-only and cannot load inside the Linux API container.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {isLoading ? (
            <div className="space-y-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : (
            <>
              <div className="grid gap-2">
                <Label htmlFor="subscriptionId">Subscription ID</Label>
                <Input
                  id="subscriptionId"
                  placeholder="00000000-0000-0000-0000-000000000000"
                  value={form.subscriptionId}
                  onChange={(e) => set("subscriptionId", e.target.value)}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="resourceGroup">Resource Group</Label>
                <Input
                  id="resourceGroup"
                  placeholder="migration-automation-rg"
                  value={form.resourceGroup}
                  onChange={(e) => set("resourceGroup", e.target.value)}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="accountName">Automation Account Name</Label>
                <Input
                  id="accountName"
                  placeholder="migration-automation"
                  value={form.accountName}
                  onChange={(e) => set("accountName", e.target.value)}
                />
              </div>
              <div className="grid gap-2">
                <Label htmlFor="runbookName">Runbook Name</Label>
                <Input
                  id="runbookName"
                  value={form.runbookName}
                  onChange={(e) => set("runbookName", e.target.value)}
                />
              </div>
              <div className="grid grid-cols-2 gap-4">
                <div className="grid gap-2">
                  <Label htmlFor="pollInterval">Poll Interval (seconds)</Label>
                  <Input
                    id="pollInterval"
                    type="number"
                    min={1}
                    value={form.jobPollIntervalSeconds}
                    onChange={(e) => set("jobPollIntervalSeconds", Number(e.target.value))}
                  />
                </div>
                <div className="grid gap-2">
                  <Label htmlFor="jobTimeout">Job Timeout (minutes)</Label>
                  <Input
                    id="jobTimeout"
                    type="number"
                    min={1}
                    value={form.jobTimeoutMinutes}
                    onChange={(e) => set("jobTimeoutMinutes", Number(e.target.value))}
                  />
                </div>
              </div>

              <div className="flex justify-end pt-2">
                <Button
                  onClick={() => save.mutate(form)}
                  disabled={save.isPending}
                >
                  {save.isPending ? (
                    <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                  ) : (
                    <Save className="mr-2 h-4 w-4" />
                  )}
                  Save
                </Button>
              </div>

              {verify.error instanceof Error && (
                <p className="text-sm text-destructive">
                  Environment scan failed: {verify.error.message}
                </p>
              )}
              {verify.data && <EnvironmentCheckList report={verify.data} />}
            </>
          )}
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <div className="flex items-center justify-between">
            <CardTitle>Azure Identity (Service Principal)</CardTitle>
            {identity.data && (
              <Badge variant={identity.data.isConfigured ? "default" : "secondary"}>
                {identity.data.isConfigured ? (
                  <><CheckCircle2 className="mr-1 h-3 w-3" /> Configured</>
                ) : (
                  <><XCircle className="mr-1 h-3 w-3" /> Not configured</>
                )}
              </Badge>
            )}
          </div>
          <CardDescription>
            The API needs an Azure identity to trigger Automation runbooks via
            Azure Resource Manager. Create a dedicated Entra app registration
            (it does NOT need Graph or SPO permissions) and enter its
            credentials here. This replaces the need for{" "}
            <code className="rounded bg-muted px-1">az login</code> and
            survives token expiration.{" "}
            <strong>Terraform does not create this identity</strong> — it must
            exist first; its object ID goes into the{" "}
            <code className="rounded bg-muted px-1">api_principal_object_id</code>{" "}
            tfvar of the <code className="rounded bg-muted px-1">platform-azure</code>{" "}
            stack, which then grants it the Automation Contributor role.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {identity.isLoading ? (
            <div className="space-y-2">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
            </div>
          ) : (
            <>
              {showManual && (
              <div className="rounded-md border bg-muted/50 p-4 text-sm">
                <p className="font-medium">How to create the service principal:</p>
                <ol className="mt-2 list-decimal space-y-1 pl-5 text-muted-foreground">
                  <li>
                    Go to{" "}
                    <strong>Azure Portal → Entra ID → App registrations → New registration</strong>.
                    Name it e.g.{" "}
                    <code className="rounded bg-muted px-1">migration-platform-api</code>{" "}
                    (single tenant).
                  </li>
                  <li>
                    Copy the <strong>Application (client) ID</strong> from the
                    Overview page.
                  </li>
                  <li>
                    Choose an authentication method:
                    <ul className="mt-1 list-disc space-y-1 pl-5">
                      <li>
                        <strong>Client secret</strong> (simpler): Go to{" "}
                        <strong>Certificates &amp; secrets → New client secret</strong>.
                        Copy the value immediately.
                      </li>
                      <li>
                        <strong>Certificate</strong> (more secure): Go to{" "}
                        <strong>Certificates &amp; secrets → Certificates → Upload certificate</strong>.
                        Upload the public key (.cer). Keep the PFX file — you will
                        upload it below.
                      </li>
                    </ul>
                  </li>
                  <li>
                    Grant this app the{" "}
                    <strong>Automation Contributor</strong> role on your
                    Automation account (Contributor lets the API auto-publish
                    the runbook at startup; Job Operator alone runs jobs but
                    requires manual runbook re-imports):
                  </li>
                </ol>
                <Cmd>{`az role assignment create \\
  --assignee <app-client-id> \\
  --role "Automation Contributor" \\
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Automation/automationAccounts/<account>`}</Cmd>
                <p className="mt-2 text-xs text-muted-foreground">
                  Or in the portal: Automation account → Access control (IAM)
                  → Add role assignment → Automation Job Operator → select
                  the app.
                </p>
              </div>
              )}

              <div className="grid gap-2">
                <Label htmlFor="idTenantId">Azure Tenant ID</Label>
                <Input
                  id="idTenantId"
                  placeholder="00000000-0000-0000-0000-000000000000"
                  value={idForm.tenantId}
                  onChange={(e) => setId("tenantId", e.target.value)}
                />
                <p className="text-xs text-muted-foreground">
                  The Entra tenant where the service principal app registration lives
                  (typically the same tenant as your Azure subscription).
                </p>
              </div>
              <div className="grid gap-2">
                <Label htmlFor="idClientId">Application (Client) ID</Label>
                <Input
                  id="idClientId"
                  placeholder="00000000-0000-0000-0000-000000000000"
                  value={idForm.clientId}
                  onChange={(e) => setId("clientId", e.target.value)}
                />
              </div>

              <div className="grid gap-2">
                <Label>Authentication Method</Label>
                <Select
                  value={idForm.authMethod}
                  onValueChange={(v) => setIdForm((f) => ({
                    ...f,
                    authMethod: v as "secret" | "certificate",
                  }))}
                >
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="secret">Client Secret</SelectItem>
                    <SelectItem value="certificate">Certificate (PFX)</SelectItem>
                  </SelectContent>
                </Select>
              </div>

              {idForm.authMethod === "secret" ? (
                <div className="grid gap-2">
                  <Label htmlFor="idClientSecret">Client Secret</Label>
                  <Input
                    id="idClientSecret"
                    type="password"
                    placeholder={
                      identity.data?.isConfigured && identity.data.authMethod === "secret"
                        ? `Current: ${identity.data.clientSecretHint} — enter new value to replace`
                        : "Paste the secret value from Azure"
                    }
                    value={idForm.clientSecret ?? ""}
                    onChange={(e) => setId("clientSecret", e.target.value)}
                  />
                  {identity.data?.isConfigured && identity.data.authMethod === "secret" && !idForm.clientSecret && (
                    <p className="text-xs text-muted-foreground">
                      A secret is already stored (ending in{" "}
                      <code className="rounded bg-muted px-1">
                        {identity.data.clientSecretHint}
                      </code>
                      ). Leave blank to keep it, or enter a new value to replace.
                    </p>
                  )}
                </div>
              ) : (
                <div className="space-y-3">
                  <div className="grid gap-2">
                    <Label htmlFor="idCertFile">PFX Certificate File</Label>
                    <Input
                      id="idCertFile"
                      type="file"
                      accept=".pfx,.p12"
                      onChange={handleCertFileChange}
                    />
                    {identity.data?.hasCertificate && !idForm.certificateBase64 && (
                      <p className="text-xs text-muted-foreground">
                        A certificate is already stored
                        {identity.data.certificateThumbprint && (
                          <> (thumbprint:{" "}
                            <code className="rounded bg-muted px-1">
                              {identity.data.certificateThumbprint}
                            </code>)
                          </>
                        )}
                        . Upload a new PFX to replace it.
                      </p>
                    )}
                    {idForm.certificateBase64 && (
                      <p className="text-xs text-green-600">
                        New certificate loaded and ready to save.
                      </p>
                    )}
                  </div>
                  <div className="grid gap-2">
                    <Label htmlFor="idCertPassword">PFX Password (optional)</Label>
                    <Input
                      id="idCertPassword"
                      type="password"
                      placeholder="Leave blank if the PFX has no password"
                      value={idForm.certificatePassword ?? ""}
                      onChange={(e) => setId("certificatePassword", e.target.value)}
                    />
                  </div>
                  <div className="grid gap-2">
                    <Label htmlFor="idCertThumb">Certificate Thumbprint (optional, for display)</Label>
                    <Input
                      id="idCertThumb"
                      placeholder="AB12CD34..."
                      value={idForm.certificateThumbprint ?? ""}
                      onChange={(e) => setId("certificateThumbprint", e.target.value)}
                    />
                  </div>
                </div>
              )}

              <div className="flex justify-between pt-2">
                {identity.data?.isConfigured && (
                  <Button
                    variant="destructive"
                    size="sm"
                    onClick={() => deleteIdentity.mutate()}
                    disabled={deleteIdentity.isPending}
                  >
                    {deleteIdentity.isPending ? (
                      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                      <Trash2 className="mr-2 h-4 w-4" />
                    )}
                    Remove credentials
                  </Button>
                )}
                <div className="ml-auto">
                  <Button
                    onClick={() => {
                      if (idForm.authMethod === "secret" && !idForm.clientSecret &&
                          !(identity.data?.isConfigured && identity.data.authMethod === "secret")) {
                        toast.error("Enter a client secret.");
                        return;
                      }
                      if (idForm.authMethod === "certificate" && !idForm.certificateBase64 &&
                          !(identity.data?.hasCertificate)) {
                        toast.error("Upload a PFX certificate file.");
                        return;
                      }
                      saveIdentity.mutate(idForm);
                    }}
                    disabled={saveIdentity.isPending || !idForm.tenantId || !idForm.clientId}
                  >
                    {saveIdentity.isPending ? (
                      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                    ) : (
                      <Save className="mr-2 h-4 w-4" />
                    )}
                    Save
                  </Button>
                </div>
              </div>
            </>
          )}
        </CardContent>
      </Card>

      {showManual && (
      <Card>
        <CardHeader>
          <CardTitle>Setup guide: SharePoint / OneDrive cross-tenant execution</CardTitle>
          <CardDescription>
            Follow these steps once per environment. Azure does not expose a
            public REST or CSOM API for cross-tenant OneDrive migration, and
            the only supported PowerShell module is Windows-only, so the API
            offloads execution to an Azure Automation runbook that runs on a
            Microsoft-managed Windows sandbox.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-6 text-sm">
          <Step
            n={1}
            title="Create an Azure Automation account"
            body={
              <>
                <p>
                  Any subscription and region works. Pick a region close to
                  your tenants for lower latency.
                </p>
                <Cmd>{`az group create --name migration-automation-rg --location eastus
az automation account create \\
  --name migration-automation \\
  --resource-group migration-automation-rg \\
  --location eastus`}</Cmd>
                <LinkOut href="https://portal.azure.com/#create/Microsoft.AutomationAccount">
                  Create Automation Account (portal)
                </LinkOut>
              </>
            }
          />

          <Step
            n={2}
            title="Import the SharePoint Online PowerShell module"
            body={
              <>
                <p>
                  In the Automation account, go to{" "}
                  <strong>Shared Resources → Modules → Browse gallery</strong>,
                  search for{" "}
                  <code className="rounded bg-muted px-1">
                    Microsoft.Online.SharePoint.PowerShell
                  </code>
                  , and import it. Wait until the status shows{" "}
                  <strong>Available</strong> (usually 1–5 minutes).
                </p>
                <p className="text-muted-foreground">
                  You must import into the <strong>PowerShell 5.1</strong>{" "}
                  runtime — this module is not compatible with PowerShell 7.
                </p>
              </>
            }
          />

          <Step
            n={3}
            title="Import and publish the runbook"
            body={
              <>
                <p>
                  In the Automation account, go to{" "}
                  <strong>Process Automation → Runbooks → Import a runbook</strong>.
                  Upload this file from the repo:
                </p>
                <Cmd>apps/api/scripts/Invoke-SpoCrossTenantOperation.ps1</Cmd>
                <p>
                  Set the runbook type to <strong>PowerShell</strong> and
                  runtime version to <strong>5.1</strong>. After import, open
                  the runbook and click <strong>Publish</strong>. The name on
                  this page must match the{" "}
                  <strong>Runbook Name</strong> field above (default{" "}
                  <code className="rounded bg-muted px-1">
                    Invoke-SpoCrossTenantOperation
                  </code>
                  ).
                </p>
              </>
            }
          />

          <Step
            n={4}
            title="Create an API service principal and grant it the Automation Job Operator role"
            body={
              <>
                <p>
                  The API needs an Azure identity to trigger runbooks. Create a{" "}
                  <strong>dedicated Entra app registration</strong> for this
                  purpose — it does NOT need Graph or SPO permissions (those
                  come from the per-tenant app registrations).
                </p>
                <ol className="list-decimal space-y-1 pl-5">
                  <li>
                    <strong>Entra ID → App registrations → New registration</strong>{" "}
                    — name it e.g.{" "}
                    <code className="rounded bg-muted px-1">migration-platform-api</code>{" "}
                    (single tenant).
                  </li>
                  <li>
                    <strong>Certificates &amp; secrets → New client secret</strong>{" "}
                    — copy the value immediately.
                  </li>
                  <li>
                    Copy the <strong>Application (client) ID</strong> from the
                    Overview page.
                  </li>
                </ol>
                <p>
                  Grant the <strong>Automation Job Operator</strong> role on the
                  Automation account:
                </p>
                <Cmd>{`az role assignment create \\
  --assignee <app-client-id> \\
  --role "Automation Job Operator" \\
  --scope /subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.Automation/automationAccounts/<account>`}</Cmd>
                <p>
                  Then set the credentials. For <strong>Docker</strong>, fill in
                  the <code className="rounded bg-muted px-1">.env</code> file
                  at the repo root:
                </p>
                <Cmd>{`AZURE_TENANT_ID=<your-tenant-id>
AZURE_CLIENT_ID=<app-client-id>
AZURE_CLIENT_SECRET=<secret-value>`}</Cmd>
                <p className="text-muted-foreground">
                  For local dev without Docker, export the same variables in
                  your shell before running{" "}
                  <code className="rounded bg-muted px-1">dotnet run</code>.
                  In production on Azure App Service / Container Apps, you can
                  use a managed identity instead — assign the same role and
                  omit the env vars.
                </p>
              </>
            }
          />

          <Step
            n={5}
            title="Fill in the form above and save"
            body={
              <p>
                Enter the subscription ID, resource group, Automation account
                name, and runbook name from steps 1–3. Click{" "}
                <strong>Save</strong>. The values are written to{" "}
                <code className="rounded bg-muted px-1">
                  settings.override.json
                </code>{" "}
                on the API host and take effect immediately — no restart
                required.
              </p>
            }
          />

          <Step
            n={6}
            title="Per-tenant app registration and certificate"
            body={
              <>
                <p>
                  Each tenant involved in a OneDrive migration must have an
                  Entra app registration with an uploaded certificate (not a
                  client secret —{" "}
                  <code className="rounded bg-muted px-1">Connect-SPOService</code>{" "}
                  requires cert auth for app-only). Required SPO application
                  permissions:
                </p>
                <p className="font-medium text-foreground">
                  Microsoft Graph (8)
                </p>
                <ul className="list-disc space-y-1 pl-5">
                  <li><code className="rounded bg-muted px-1">Application.Read.All</code> — Read all applications</li>
                  <li><code className="rounded bg-muted px-1">Domain.Read.All</code> — Read domains</li>
                  <li><code className="rounded bg-muted px-1">Files.Read.All</code> — Read files in all site collections</li>
                  <li><code className="rounded bg-muted px-1">Group.Read.All</code> — Read all groups</li>
                  <li><code className="rounded bg-muted px-1">MailboxSettings.Read</code> — Read all user mailbox settings</li>
                  <li><code className="rounded bg-muted px-1">Sites.Read.All</code> — Read items in all site collections</li>
                  <li><code className="rounded bg-muted px-1">Synchronization.ReadWrite.All</code> — Read and write all Azure AD synchronization data</li>
                  <li><code className="rounded bg-muted px-1">User.Read.All</code> — Read all users&apos; full profiles</li>
                </ul>
                <p className="font-medium text-foreground">
                  Office 365 Exchange Online (2)
                </p>
                <ul className="list-disc space-y-1 pl-5">
                  <li><code className="rounded bg-muted px-1">Exchange.ManageAsApp</code> — Manage Exchange As Application</li>
                  <li><code className="rounded bg-muted px-1">Mailbox.Migration</code> — Move mailboxes between organizations</li>
                </ul>
                <p className="font-medium text-foreground">
                  SharePoint (2)
                </p>
                <ul className="list-disc space-y-1 pl-5">
                  <li><code className="rounded bg-muted px-1">Migration.ReadWrite.All</code> — Read and write access to migration data</li>
                  <li><code className="rounded bg-muted px-1">Sites.FullControl.All</code> — Have full control of all site collections</li>
                </ul>
                <p className="text-muted-foreground">
                  All permissions are <strong>Application</strong> type and require
                  admin consent granted for each tenant.
                </p>
                <p>
                  Upload each tenant&apos;s PFX via the{" "}
                  <strong>Tenants → ⋯ → Re-configure credentials</strong>{" "}
                  dialog. When Key Vault is enabled, the PFX is stored there
                  and the runbook loads it at execution time.
                </p>
              </>
            }
          />

          <Step
            n={7}
            title="Add the Cross-Tenant Synchronization Preview app (source tenant)"
            body={
              <>
                <p>
                  In the <strong>source</strong> tenant, add the{" "}
                  <strong>Cross-Tenant Synchronization Preview</strong>{" "}
                  enterprise application from the Azure AD app gallery. This
                  creates the provisioning service principal that Entra
                  cross-tenant sync uses to provision identities into the target
                  tenant.
                </p>
                <ol className="list-decimal space-y-1 pl-5">
                  <li>
                    Go to{" "}
                    <strong>
                      Azure Portal → Enterprise applications → New application
                    </strong>.
                  </li>
                  <li>
                    Search for{" "}
                    <code className="rounded bg-muted px-1">
                      Cross-Tenant Synchronization Preview
                    </code>{" "}
                    in the gallery and click <strong>Create</strong>.
                  </li>
                  <li>
                    Once created, open the app and go to{" "}
                    <strong>Provisioning → Get started</strong> to configure the
                    sync job pointing at the target tenant.
                  </li>
                </ol>
                <p className="text-muted-foreground">
                  After the app is created, you do not need to manually copy its
                  client ID. On the project page, click the{" "}
                  <strong>Discover</strong> button next to the{" "}
                  <em>Provisioning App Client ID</em> field — it will search the
                  source tenant for enterprise apps with an existing cross-tenant
                  sync job and let you select the correct one.
                </p>
              </>
            }
          />

          <Step
            n={8}
            title="Configure cross-tenant access settings (both tenants)"
            body={
              <>
                <p>
                  Both tenants must be configured in each other&apos;s{" "}
                  <strong>Entra ID → External Identities → Cross-tenant access settings</strong>{" "}
                  before user synchronization or B2B collaboration will work.
                </p>

                <p className="font-medium text-foreground">
                  Source Tenant
                </p>
                <ol className="list-decimal space-y-1 pl-5">
                  <li>
                    Go to{" "}
                    <strong>
                      Entra ID → External Identities → Cross-tenant access
                      settings
                    </strong>{" "}
                    and click <strong>Add organization</strong>. Enter the target
                    tenant&apos;s domain or tenant ID.
                  </li>
                  <li>
                    Open the newly added organization and go to the{" "}
                    <strong>Outbound access</strong> tab. Under{" "}
                    <strong>B2B collaboration → Users and groups</strong>,
                    configure which users and groups are allowed outbound access
                    (or select <strong>Allow access</strong> for all).
                  </li>
                  <li>
                    Under <strong>Trust settings</strong>, check{" "}
                    <strong>
                      Automatically redeem invitations with the tenant
                    </strong>.
                  </li>
                </ol>

                <p className="font-medium text-foreground">
                  Target Tenant
                </p>
                <ol className="list-decimal space-y-1 pl-5">
                  <li>
                    Go to{" "}
                    <strong>
                      Entra ID → External Identities → Cross-tenant access
                      settings
                    </strong>{" "}
                    and add the source tenant organization.
                  </li>
                  <li>
                    Open the organization and go to the{" "}
                    <strong>Inbound access</strong> tab. Under{" "}
                    <strong>B2B collaboration</strong>, set Access status to{" "}
                    <strong>Allow access</strong> and Applies to to{" "}
                    <strong>All users and groups</strong> (from the source
                    tenant).
                  </li>
                  <li>
                    Under <strong>Trust settings</strong>, check{" "}
                    <strong>
                      Automatically redeem invitations with the tenant
                    </strong>.
                  </li>
                  <li>
                    Go to the <strong>Cross-tenant sync</strong> tab and enable{" "}
                    <strong>
                      Allow user synchronization into this tenant
                    </strong>.
                  </li>
                </ol>
              </>
            }
          />

          <Step
            n={9}
            title="Exchange cross-tenant mailbox migration prerequisites"
            body={
              <>
                <p>
                  Mailbox moves run via Microsoft&apos;s native cross-tenant
                  Mailbox Replication Service (MRS), driven by the EXO REST API.
                  The platform handles per-user MailUser provisioning, scope
                  group membership, organization relationships, the migration
                  endpoint, and batch submission automatically — but a few items
                  must be done manually <strong>once per tenant pair</strong>{" "}
                  because they require interactive admin consent or licensing.
                </p>

                <div className="rounded-md border border-blue-300 bg-blue-50 p-3 text-sm dark:border-blue-700 dark:bg-blue-950">
                  <p className="font-medium text-blue-800 dark:text-blue-300">
                    Note: CTIM (Cross-Tenant Identity Mapping) is not used
                  </p>
                  <p className="mt-1 text-blue-700 dark:text-blue-400">
                    Earlier revisions of this wizard required the preview{" "}
                    <code>CrossTenantIdentityMapping</code> module
                    (<code>Add-CtimServicePrincipal</code>,{" "}
                    <code>Accept-CtimCopyRequest</code>,{" "}
                    <code>New-CtimMapRequest</code>,{" "}
                    <code>New-CtimWriteRequest</code>). Those steps are no
                    longer needed — the platform now provisions the target
                    MailUser stubs directly via{" "}
                    <code>New-MailUser</code> + <code>Set-MailUser</code>{" "}
                    using attributes captured from the source mailbox.
                  </p>
                </div>

                <p className="font-medium text-foreground">
                  1. Register a cross-tenant Mailbox Migration app
                </p>
                <p className="text-muted-foreground">
                  Direct <code>adminconsent</code> URLs against the well-known
                  AppId <code>879f1d6d-c0b7-4543-a2dd-dfa812c5179d</code>{" "}
                  <strong>do not work</strong> in current Microsoft 365 tenants
                  (the app isn&apos;t pre-installed; you&apos;ll see{" "}
                  <code>AADSTS700016</code> on target and{" "}
                  <code>AADSTS500113</code> on source). The supported approach
                  is to register your own multi-tenant app and consent it from
                  both tenants.
                </p>

                <p className="font-medium text-foreground">
                  a. In the TARGET tenant: Entra → App registrations → New registration
                </p>
                <p className="text-muted-foreground">
                  Per Microsoft&apos;s current doc the app is homed in the{" "}
                  <strong>target</strong> (destination) tenant and the target is
                  configured first. (The Terraform{" "}
                  <code>target-tenant/</code> stack does this entire step.)
                </p>
                <ul className="list-inside list-disc space-y-1 text-muted-foreground">
                  <li>Name: <code>Cross-Tenant Mailbox Migration</code></li>
                  <li>
                    Supported account types:{" "}
                    <strong>
                      Accounts in any organizational directory (multitenant)
                    </strong>
                  </li>
                  <li>
                    Redirect URI: Web → <code>https://office.com</code>{" "}
                    (this exact reply URL is required — its absence is{" "}
                    <code>AADSTS500113</code>)
                  </li>
                  <li>
                    API permissions → APIs my organization uses → search{" "}
                    <strong>Office 365 Exchange Online</strong> → Application
                    permissions → <code>Mailbox.Migration</code> → Grant admin
                    consent (target tenant)
                  </li>
                  <li>
                    Certificates &amp; secrets → New client secret — copy the
                    secret <strong>value</strong> and note the expiry
                  </li>
                  <li>
                    Copy the <strong>Application (client) ID</strong> from the
                    Overview blade (NOT the enterprise-app object ID)
                  </li>
                </ul>

                <p className="font-medium text-foreground">
                  b. Consent the new app from the SOURCE tenant
                </p>
                <p className="text-muted-foreground">
                  A source-tenant Global Administrator opens this URL (or the
                  source admin runs the Terraform{" "}
                  <code>source-tenant/</code> stack, which consents
                  programmatically):
                </p>
                <Cmd>{`https://login.microsoftonline.com/<source>.onmicrosoft.com/adminconsent?client_id=<YOUR_APPID>&redirect_uri=https://office.com`}</Cmd>

                <p className="font-medium text-foreground">
                  c. Save the AppId + secret in the platform
                </p>
                <p className="text-muted-foreground">
                  Settings → Pre-Setup →{" "}
                  <strong>Cross-Tenant Mailbox Migration App</strong> (applied
                  without restart). The platform then stamps{" "}
                  <code>OAuthApplicationId</code> on both organization
                  relationships (source <code>RemoteOutbound</code>, target{" "}
                  <code>Inbound</code>) and creates the migration endpoint
                  automatically at batch start — no manual{" "}
                  <code>Set-OrganizationRelationship</code> or{" "}
                  <code>New-MigrationEndpoint</code> needed. Manual fallback for
                  the endpoint, if ever required (client-secret credentials, and{" "}
                  <code>outlook.office.com</code>, never{" "}
                  <code>outlook.office365.com</code>):
                </p>
                <Cmd>{`# On TARGET (fallback only — the platform normally creates this)
New-MigrationEndpoint -Name "CrossTenantEndpoint" \`
  -RemoteTenant <source>.onmicrosoft.com \`
  -ApplicationId <YOUR_APPID> \`
  -ExchangeRemoteMove -RemoteServer outlook.office.com \`
  -Credentials (New-Object PSCredential -ArgumentList <YOUR_APPID>, \`
    (ConvertTo-SecureString <SECRET_VALUE> -AsPlainText -Force))`}</Cmd>

                <p className="font-medium text-foreground">
                  2. Register the platform&apos;s service principal inside Exchange Online (both tenants)
                </p>
                <p className="text-muted-foreground">
                  Even with AAD admin consent on{" "}
                  <code>Exchange.ManageAsApp</code>, EXO returns{" "}
                  <strong>401 on every InvokeCommand</strong> until the app&apos;s
                  service principal is also registered inside Exchange Online.
                  Run this once per tenant from a Windows PowerShell host:
                </p>
                <Cmd>{`Connect-ExchangeOnline -Organization <tenant>.onmicrosoft.com

# Get the SP's ObjectId from Entra (Enterprise applications → your app reg → Object ID)
New-ServicePrincipal -AppId <yourAppId> -ObjectId <spObjectId> \`
  -DisplayName "Migration Platform"`}</Cmd>

                <p className="font-medium text-foreground">
                  3. Verified domains and accepted-domain status (both tenants)
                </p>
                <p className="text-muted-foreground">
                  All custom domains used in source UPNs / target routing must
                  be verified and configured as accepted domains in their
                  respective tenants. Source mailboxes must have a non-zero{" "}
                  <code>ExchangeGuid</code> and populated{" "}
                  <code>LegacyExchangeDN</code> (true for any normal cloud mailbox).
                </p>

                <p className="font-medium text-foreground">
                  4. Assign Cross Tenant User Data Migration license per migrating user
                </p>
                <p className="text-muted-foreground">
                  One-time per-user fee, available as an add-on on most
                  Microsoft 365/Office 365 plans. Can be assigned on the source
                  OR target user — without it, EXO emits a &quot;needs
                  approval&quot; warning and the move stalls. This also covers
                  the OneDrive content migration in step 10.
                </p>

                <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm dark:border-amber-700 dark:bg-amber-950">
                  <p className="font-medium text-amber-800 dark:text-amber-300">
                    Cleanup if upgrading from a prior CTIM attempt
                  </p>
                  <p className="mt-1 text-amber-700 dark:text-amber-400">
                    If earlier CTIM runs left soft-deleted MailUsers in target
                    holding source SMTP addresses, the platform&apos;s auto-provisioning
                    will fail with &quot;existing identity found&quot;. Purge first:
                  </p>
                  <Cmd>{`Connect-ExchangeOnline -Organization <target>.onmicrosoft.com
Get-MailUser -SoftDeletedMailUser -ResultSize Unlimited |
  Where-Object { $_.ExternalEmailAddress -like "*<sourceDomain>*" } |
  Remove-MailUser -PermanentlyDelete -Confirm:$false`}</Cmd>
                  <p className="mt-1 text-amber-700 dark:text-amber-400">
                    If EXO rejects the purge with &quot;an AAD user is backing
                    this MailUser&quot;, delete that AAD user from{" "}
                    <strong>Entra → Users</strong>, then{" "}
                    <strong>Entra → Deleted users → Permanently delete</strong>,
                    wait ~2 minutes for sync, and retry the purge.
                  </p>
                </div>

                <p className="font-medium text-foreground">
                  What the platform automates per migration
                </p>
                <ul className="list-inside list-disc space-y-1 text-muted-foreground">
                  <li>
                    Mail-enabled scope DG{" "}
                    <code>CTMS-{`{targetOnMicrosoftDomain}`}</code> on source +
                    adding each migrating UPN as a member
                  </li>
                  <li>
                    Source mailbox attribute capture via{" "}
                    <code>Get-Mailbox</code>
                  </li>
                  <li>
                    Target MailUser provisioning with target-routing UPN +
                    source-routing <code>ExternalEmailAddress</code> + stamped{" "}
                    <code>ExchangeGuid</code> +{" "}
                    <code>LegacyExchangeDN</code>-derived <code>x500:</code> proxy
                  </li>
                  <li>
                    Both organization relationships (source{" "}
                    <code>RemoteOutbound</code> with{" "}
                    <code>OAuthApplicationId</code> +{" "}
                    <code>MailboxMovePublishedScopes</code>; target{" "}
                    <code>Inbound</code> — Microsoft&apos;s script value, not{" "}
                    <code>RemoteInbound</code>)
                  </li>
                  <li>
                    Migration endpoint on target (<code>ExchangeRemoteMove</code>)
                  </li>
                  <li>
                    <code>New-MigrationBatch</code> + polling to terminal state
                  </li>
                </ul>

                <LinkOut href="https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-mailbox-migration">
                  Microsoft Learn: Cross-tenant mailbox migration
                </LinkOut>
              </>
            }
          />

          <Step
            n={10}
            title="Establish the cross-tenant relationship"
            body={
              <>
                <p>
                  The platform now establishes this relationship automatically on
                  the first content-migration start (via the Automation runbook,
                  both sides). The commands below are the manual fallback if
                  auto-establishment fails or you prefer to run the one-time
                  handshake yourself.
                </p>

                <p className="font-medium text-foreground">
                  Destination (Target) Tenant
                </p>
                <Cmd>{`Connect-SPOService -Url https://contoso-admin.sharepoint.com

# Get the local tenant's cross-tenant host URL
Get-SPOCrossTenantHostUrl
# → https://contoso-my.sharepoint.com/

# Establish the relationship (partner = source)
Set-SPOCrossTenantRelationship -Scenario MnA \\
  -PartnerRole Source \\
  -PartnerCrossTenantHostUrl https://fabrikam-my.sharepoint.com

# Verify
Test-SPOCrossTenantRelationship -Scenario MnA \\
  -PartnerRole Source \\
  -PartnerCrossTenantHostUrl https://fabrikam-my.sharepoint.com
# → GoodToProceed`}</Cmd>

                <p className="font-medium text-foreground">
                  Source Tenant
                </p>
                <Cmd>{`Connect-SPOService -Url https://fabrikam-admin.sharepoint.com

# Get the local tenant's cross-tenant host URL
Get-SPOCrossTenantHostUrl
# → https://fabrikam-my.sharepoint.com/

# Establish the relationship (partner = target)
Set-SPOCrossTenantRelationship -Scenario MnA \\
  -PartnerCrossTenantHostUrl https://contoso-my.sharepoint.com \\
  -PartnerRole Target

# Verify
Test-SPOCrossTenantRelationship -Scenario MnA \\
  -PartnerRole Target \\
  -PartnerCrossTenantHostUrl https://contoso-my.sharepoint.com
# → GoodToProceed`}</Cmd>

                <p className="text-muted-foreground">
                  Both sides must return <strong>GoodToProceed</strong> before
                  cross-tenant migrations will work. Replace the tenant names
                  above with your actual source and target{" "}
                  <code className="rounded bg-muted px-1">.onmicrosoft.com</code>{" "}
                  domains.
                </p>
                <LinkOut href="https://learn.microsoft.com/en-us/microsoft-365/enterprise/cross-tenant-onedrive-migration-step4">
                  Microsoft Learn: cross-tenant OneDrive migration prerequisites
                </LinkOut>
              </>
            }
          />

          <div className="flex flex-wrap gap-3 pt-2">
            <LinkOut href="https://portal.azure.com/#blade/HubsExtension/BrowseResource/resourceType/Microsoft.Automation%2FAutomationAccounts">
              Open Automation Accounts
            </LinkOut>
            <LinkOut href="https://learn.microsoft.com/azure/automation/automation-runbook-gallery">
              Azure Automation docs
            </LinkOut>
          </div>
        </CardContent>
      </Card>
      )}
    </div>
  );
}
