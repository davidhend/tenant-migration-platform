"use client";

import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Plus, RefreshCw, Trash2, CheckCircle2, Loader2, MoreHorizontal, FlaskConical, AlertTriangle, CheckCircle, Settings } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter,
  DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem,
  DropdownMenuTrigger, DropdownMenuSeparator,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ConnectionStatusBadge } from "@/components/shared/status-badge";
import { tenantsApi } from "@/lib/api";
import { formatDateTime } from "@/lib/utils";
import type { CreateTenantDto, TenantRole, AuthMethod } from "@/types";

const STEPS = ["Tenant Details", "App Registration", "Verify Connection"];

export default function TenantsPage() {
  const qc = useQueryClient();
  const [wizardOpen, setWizardOpen] = useState(false);
  const [step, setStep] = useState(0);
  const [form, setForm] = useState<CreateTenantDto>({
    displayName: "",
    tenantId: "",
    role: "source",
    appClientId: "",
    authMethod: "secret",
    clientSecret: "",
  });
  const [certBase64, setCertBase64] = useState("");
  const [certPassword, setCertPassword] = useState("");
  const [certThumbprint, setCertThumbprint] = useState("");
  const [verifyResult, setVerifyResult] = useState<{ success: boolean; message: string } | null>(null);
  const [createdId, setCreatedId] = useState<string | null>(null);
  const [exoDiagOpen, setExoDiagOpen] = useState(false);
  const [exoDiagResult, setExoDiagResult] = useState<Awaited<ReturnType<typeof tenantsApi.diagnoseExo>> | null>(null);

  // Re-configure app registration dialog
  const [reconfigTarget, setReconfigTarget] = useState<string | null>(null);
  const [reconfigForm, setReconfigForm] = useState({ appClientId: "", authMethod: "certificate" as AuthMethod, clientSecret: "" });
  const [reconfigCertBase64, setReconfigCertBase64] = useState("");
  const [reconfigCertPassword, setReconfigCertPassword] = useState("");
  const [reconfigCertThumbprint, setReconfigCertThumbprint] = useState("");

  const { data: tenants, isLoading } = useQuery({ queryKey: ["tenants"], queryFn: tenantsApi.list });

  const createMutation = useMutation({
    mutationFn: async (dto: CreateTenantDto) => {
      const tenant = await tenantsApi.create(dto);
      // Persist credentials immediately — ClientSecretPlain is never stored by
      // the create endpoint so we must call PUT /credentials before returning.
      try {
        await tenantsApi.updateCredentials(tenant.id, {
          authMethod: dto.authMethod,
          clientSecret: dto.authMethod === "secret" ? dto.clientSecret : undefined,
          clientCertificateBase64: dto.authMethod === "certificate" ? certBase64 : undefined,
          clientCertificatePassword: dto.authMethod === "certificate" ? certPassword : undefined,
          clientCertificateThumbprint: dto.authMethod === "certificate" ? certThumbprint : undefined,
        });
      } catch {
        toast.error("Tenant registered but credentials could not be saved — re-enter them before verifying");
      }
      return tenant;
    },
    onSuccess: (tenant) => {
      qc.invalidateQueries({ queryKey: ["tenants"] });
      setCreatedId(tenant.id);
      setStep(2);
    },
    onError: () => toast.error("Failed to register tenant"),
  });

  const verifyMutation = useMutation({
    mutationFn: (id: string) => tenantsApi.verify(id),
    onSuccess: (result) => {
      setVerifyResult(result);
      if (result.success) {
        qc.invalidateQueries({ queryKey: ["tenants"] });
        toast.success("Tenant connection verified");
      }
    },
    onError: () => toast.error("Verification failed"),
  });

  const deleteMutation = useMutation({
    mutationFn: tenantsApi.delete,
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["tenants"] }); toast.success("Tenant removed"); },
    onError: () => toast.error("Failed to remove tenant"),
  });

  const diagnoseExoMutation = useMutation({
    mutationFn: (id: string) => tenantsApi.diagnoseExo(id),
    onSuccess: (result) => {
      setExoDiagResult(result);
      setExoDiagOpen(true);
    },
    onError: () => toast.error("Diagnostic request failed"),
  });

  const reconfigMutation = useMutation({
    mutationFn: (id: string) => tenantsApi.updateCredentials(id, {
      authMethod: reconfigForm.authMethod,
      appClientId: reconfigForm.appClientId || undefined,
      clientSecret: reconfigForm.authMethod === "secret" ? reconfigForm.clientSecret : undefined,
      clientCertificateBase64: reconfigForm.authMethod === "certificate" ? reconfigCertBase64 : undefined,
      clientCertificatePassword: reconfigForm.authMethod === "certificate" ? reconfigCertPassword : undefined,
      clientCertificateThumbprint: reconfigForm.authMethod === "certificate" ? reconfigCertThumbprint : undefined,
    }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["tenants"] });
      toast.success("Credentials updated");
      setReconfigTarget(null);
    },
    onError: () => toast.error("Failed to update credentials"),
  });

  function resetWizard() {
    setStep(0);
    setForm({ displayName: "", tenantId: "", role: "source", appClientId: "", authMethod: "secret", clientSecret: "" });
    setCertBase64(""); setCertPassword(""); setCertThumbprint("");
    setVerifyResult(null);
    setCreatedId(null);
  }

  function handleOpenChange(open: boolean) {
    setWizardOpen(open);
    if (!open) resetWizard();
  }

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">Tenants</h1>
          <p className="text-muted-foreground">Manage source and target Microsoft 365 tenant connections</p>
        </div>
        <Dialog open={wizardOpen} onOpenChange={handleOpenChange}>
          <DialogTrigger asChild>
            <Button><Plus className="mr-2 h-4 w-4" />Add Tenant</Button>
          </DialogTrigger>
          <DialogContent className="sm:max-w-[520px]">
            <DialogHeader>
              <DialogTitle>Add Tenant Connection</DialogTitle>
              <DialogDescription>
                Step {step + 1} of {STEPS.length} — {STEPS[step]}
              </DialogDescription>
            </DialogHeader>

            {/* Step indicator */}
            <div className="flex gap-2">
              {STEPS.map((s, i) => (
                <div key={s} className={`h-1 flex-1 rounded-full transition-colors ${i <= step ? "bg-primary" : "bg-muted"}`} />
              ))}
            </div>

            {/* Step 1: Tenant details */}
            {step === 0 && (
              <div className="space-y-4">
                <div className="space-y-2">
                  <Label htmlFor="displayName">Display Name</Label>
                  <Input id="displayName" placeholder="Contoso Ltd" value={form.displayName}
                    onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))} />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="tenantId">Azure Tenant ID</Label>
                  <Input id="tenantId" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                    value={form.tenantId} onChange={(e) => setForm((f) => ({ ...f, tenantId: e.target.value }))} />
                  <p className="text-xs text-muted-foreground">Found in Azure AD &rsaquo; Properties</p>
                </div>
                <div className="space-y-2">
                  <Label>Tenant Role</Label>
                  <Select value={form.role} onValueChange={(v) => setForm((f) => ({ ...f, role: v as TenantRole }))}>
                    <SelectTrigger><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="source">Source (migrating from)</SelectItem>
                      <SelectItem value="target">Target (migrating to)</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
              </div>
            )}

            {/* Step 2: App registration */}
            {step === 1 && (
              <div className="space-y-4">
                <div className="rounded-md border bg-muted/40 p-3 text-sm space-y-1">
                  <p className="font-medium">Required Graph permissions</p>
                  <ul className="list-inside list-disc text-muted-foreground space-y-0.5 text-xs">
                    <li>User.Read.All (Application)</li>
                    <li>Group.Read.All (Application)</li>
                    <li>Sites.Read.All (Application)</li>
                    <li>Files.Read.All (Application)</li>
                    <li>Domain.Read.All (Application)</li>
                    <li>MailboxSettings.Read (Application)</li>
                  </ul>
                </div>
                <div className="space-y-2">
                  <Label htmlFor="appClientId">Application (Client) ID</Label>
                  <Input id="appClientId" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx"
                    value={form.appClientId} onChange={(e) => setForm((f) => ({ ...f, appClientId: e.target.value }))} />
                </div>
                <div className="space-y-2">
                  <Label>Authentication Method</Label>
                  <Select value={form.authMethod} onValueChange={(v) => setForm((f) => ({ ...f, authMethod: v as AuthMethod }))}>
                    <SelectTrigger><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="certificate">Certificate (recommended)</SelectItem>
                      <SelectItem value="secret">Client Secret</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                {form.authMethod === "secret" && (
                  <div className="space-y-2">
                    <Label htmlFor="clientSecret">Client Secret</Label>
                    <Input id="clientSecret" type="password" placeholder="Enter client secret"
                      value={form.clientSecret} onChange={(e) => setForm((f) => ({ ...f, clientSecret: e.target.value }))} />
                    <p className="text-xs text-muted-foreground">Stored encrypted. Only the last 4 characters are displayed after saving.</p>
                  </div>
                )}
                {form.authMethod === "certificate" && (
                  <div className="space-y-3">
                    <div className="space-y-2">
                      <Label htmlFor="certFile">PFX Certificate</Label>
                      <Input
                        id="certFile"
                        type="file"
                        accept=".pfx,.p12"
                        onChange={(e) => {
                          const file = e.target.files?.[0];
                          if (!file) return;
                          const reader = new FileReader();
                          reader.onload = () => {
                            const b64 = (reader.result as string).split(",")[1];
                            setCertBase64(b64);
                          };
                          reader.readAsDataURL(file);
                        }}
                      />
                      <p className="text-xs text-muted-foreground">Upload the PFX/P12 certificate for the app registration.</p>
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="certPassword">Certificate Password <span className="text-muted-foreground">(if any)</span></Label>
                      <Input id="certPassword" type="password" placeholder="Leave blank if no password"
                        value={certPassword} onChange={(e) => setCertPassword(e.target.value)} />
                    </div>
                    <div className="space-y-2">
                      <Label htmlFor="certThumbprint">Thumbprint <span className="text-muted-foreground">(optional, for display)</span></Label>
                      <Input id="certThumbprint" placeholder="e.g. A1B2C3D4..."
                        value={certThumbprint} onChange={(e) => setCertThumbprint(e.target.value)} />
                    </div>
                  </div>
                )}
              </div>
            )}

            {/* Step 3: Verify */}
            {step === 2 && (
              <div className="space-y-4">
                {verifyResult === null ? (
                  <div className="flex flex-col items-center gap-4 py-4">
                    <div className="rounded-full bg-muted p-4">
                      <CheckCircle2 className="h-8 w-8 text-muted-foreground" />
                    </div>
                    <div className="text-center">
                      <p className="font-medium">Tenant registered successfully</p>
                      <p className="text-sm text-muted-foreground mt-1">
                        Click below to verify the app connection and graph access.
                      </p>
                    </div>
                    <Button
                      onClick={() => createdId && verifyMutation.mutate(createdId)}
                      disabled={verifyMutation.isPending}
                      className="w-full"
                    >
                      {verifyMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                      Verify Connection
                    </Button>
                  </div>
                ) : verifyResult.success ? (
                  <div className="flex flex-col items-center gap-3 py-4 text-center">
                    <CheckCircle2 className="h-10 w-10 text-green-500" />
                    <p className="font-medium text-green-700 dark:text-green-400">Connection Verified</p>
                    <p className="text-sm text-muted-foreground">{verifyResult.message}</p>
                  </div>
                ) : (
                  <div className="rounded-md border border-destructive/50 bg-destructive/10 p-4 text-sm text-destructive">
                    {verifyResult.message}
                  </div>
                )}
              </div>
            )}

            <DialogFooter>
              {step < 2 && (
                <>
                  {step > 0 && (
                    <Button variant="outline" onClick={() => setStep((s) => s - 1)}>Back</Button>
                  )}
                  <Button
                    onClick={() => {
                      if (step === 0) setStep(1);
                      else if (step === 1) createMutation.mutate(form);
                    }}
                    disabled={createMutation.isPending}
                  >
                    {createMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    {step === 1 ? "Register Tenant" : "Next"}
                  </Button>
                </>
              )}
              {step === 2 && (
                <Button onClick={() => setWizardOpen(false)}>Done</Button>
              )}
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>

      {/* EXO Diagnostic Result Dialog */}
      <Dialog open={exoDiagOpen} onOpenChange={setExoDiagOpen}>
        <DialogContent className="sm:max-w-[560px]">
          <DialogHeader>
            <DialogTitle>EXO Token Diagnostic</DialogTitle>
            <DialogDescription>
              Claims decoded from the Exchange Online access token.
            </DialogDescription>
          </DialogHeader>
          {exoDiagResult && (
            <div className="space-y-4 text-sm">
              {!exoDiagResult.success ? (
                <div className="rounded-md border border-destructive/50 bg-destructive/10 p-3 text-destructive">
                  <p className="font-medium">{exoDiagResult.error}</p>
                  {exoDiagResult.detail && <p className="mt-1 text-xs">{exoDiagResult.detail}</p>}
                </div>
              ) : exoDiagResult.token && (
                <>
                  <div className={`flex items-start gap-3 rounded-md border p-3 ${
                    exoDiagResult.token.hasExchangeManageAsApp
                      ? "border-green-500/50 bg-green-500/10"
                      : "border-amber-500/50 bg-amber-500/10"
                  }`}>
                    {exoDiagResult.token.hasExchangeManageAsApp
                      ? <CheckCircle className="mt-0.5 h-4 w-4 shrink-0 text-green-500" />
                      : <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
                    }
                    <p className={exoDiagResult.token.hasExchangeManageAsApp ? "text-green-700 dark:text-green-400" : "text-amber-700 dark:text-amber-400"}>
                      {exoDiagResult.token.diagnosis}
                    </p>
                  </div>
                  <table className="w-full text-xs">
                    <tbody className="divide-y">
                      {[
                        ["Audience (aud)", exoDiagResult.token.aud],
                        ["App ID (appid)", exoDiagResult.token.appid],
                        ["Tenant ID (tid)", exoDiagResult.token.tid],
                        ["Expires", exoDiagResult.token.expiresAt],
                      ].map(([label, value]) => (
                        <tr key={label as string}>
                          <td className="py-1.5 pr-4 font-medium text-muted-foreground w-36">{label}</td>
                          <td className="py-1.5 font-mono break-all">{value ?? "—"}</td>
                        </tr>
                      ))}
                      <tr>
                        <td className="py-1.5 pr-4 font-medium text-muted-foreground align-top">Roles</td>
                        <td className="py-1.5">
                          {exoDiagResult.token.roles.length === 0
                            ? <span className="text-destructive font-medium">None — token has no application roles!</span>
                            : exoDiagResult.token.roles.map((r) => (
                              <span key={r} className={`mr-1 inline-block rounded px-1.5 py-0.5 font-mono text-xs ${
                                r === "Exchange.ManageAsApp"
                                  ? "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300"
                                  : "bg-muted text-muted-foreground"
                              }`}>{r}</span>
                            ))
                          }
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </>
              )}
            </div>
          )}
          <DialogFooter>
            <Button onClick={() => setExoDiagOpen(false)}>Close</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Re-configure app registration dialog */}
      <Dialog open={!!reconfigTarget} onOpenChange={(open) => { if (!open) setReconfigTarget(null); }}>
        <DialogContent className="sm:max-w-[480px]">
          <DialogHeader>
            <DialogTitle>Re-configure App Registration</DialogTitle>
            <DialogDescription>
              Update the Application (Client) ID and credentials. Leave App Client ID blank to keep the existing value.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4">
            <div className="space-y-2">
              <Label htmlFor="reconfig-appClientId">Application (Client) ID</Label>
              <Input id="reconfig-appClientId" placeholder="xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx (new app ID)"
                value={reconfigForm.appClientId}
                onChange={(e) => setReconfigForm((f) => ({ ...f, appClientId: e.target.value }))} />
            </div>
            <div className="space-y-2">
              <Label>Authentication Method</Label>
              <Select value={reconfigForm.authMethod} onValueChange={(v) => setReconfigForm((f) => ({ ...f, authMethod: v as AuthMethod }))}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="certificate">Certificate (recommended)</SelectItem>
                  <SelectItem value="secret">Client Secret</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {reconfigForm.authMethod === "secret" && (
              <div className="space-y-2">
                <Label htmlFor="reconfig-secret">Client Secret</Label>
                <Input id="reconfig-secret" type="password" placeholder="New client secret"
                  value={reconfigForm.clientSecret}
                  onChange={(e) => setReconfigForm((f) => ({ ...f, clientSecret: e.target.value }))} />
              </div>
            )}
            {reconfigForm.authMethod === "certificate" && (
              <div className="space-y-3">
                <div className="space-y-2">
                  <Label htmlFor="reconfig-cert">PFX Certificate</Label>
                  <Input id="reconfig-cert" type="file" accept=".pfx,.p12"
                    onChange={(e) => {
                      const file = e.target.files?.[0];
                      if (!file) return;
                      const reader = new FileReader();
                      reader.onload = () => { setReconfigCertBase64((reader.result as string).split(",")[1]); };
                      reader.readAsDataURL(file);
                    }} />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="reconfig-certpw">Certificate Password <span className="text-muted-foreground">(if any)</span></Label>
                  <Input id="reconfig-certpw" type="password" placeholder="Leave blank if no password"
                    value={reconfigCertPassword} onChange={(e) => setReconfigCertPassword(e.target.value)} />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="reconfig-thumb">Thumbprint <span className="text-muted-foreground">(optional)</span></Label>
                  <Input id="reconfig-thumb" placeholder="SHA-1 thumbprint"
                    value={reconfigCertThumbprint} onChange={(e) => setReconfigCertThumbprint(e.target.value)} />
                </div>
              </div>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setReconfigTarget(null)}>Cancel</Button>
            <Button onClick={() => reconfigTarget && reconfigMutation.mutate(reconfigTarget)} disabled={reconfigMutation.isPending}>
              {reconfigMutation.isPending ? <><Loader2 className="mr-2 h-4 w-4 animate-spin" />Saving…</> : "Save Credentials"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Tenant table */}
      <Card>
        <CardHeader>
          <CardTitle>Registered Tenants</CardTitle>
          <CardDescription>{tenants?.length ?? 0} tenants configured</CardDescription>
        </CardHeader>
        <CardContent>
          {isLoading ? (
            <div className="space-y-2">
              {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}
            </div>
          ) : tenants?.length === 0 ? (
            <div className="flex flex-col items-center gap-2 py-12 text-center text-muted-foreground">
              <p className="text-sm">No tenants registered yet.</p>
              <Button size="sm" onClick={() => setWizardOpen(true)}><Plus className="mr-1 h-3 w-3" />Add your first tenant</Button>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b">
                    <th className="py-3 text-left font-medium text-muted-foreground">Name</th>
                    <th className="py-3 text-left font-medium text-muted-foreground">Tenant ID</th>
                    <th className="py-3 text-left font-medium text-muted-foreground">Role</th>
                    <th className="py-3 text-left font-medium text-muted-foreground">Auth</th>
                    <th className="py-3 text-left font-medium text-muted-foreground">Status</th>
                    <th className="py-3 text-left font-medium text-muted-foreground">Last Verified</th>
                    <th className="py-3 text-left font-medium text-muted-foreground"></th>
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {tenants?.map((tenant) => (
                    <tr key={tenant.id} className="group hover:bg-muted/40 transition-colors">
                      <td className="py-3 font-medium">{tenant.displayName}</td>
                      <td className="py-3 font-mono text-xs text-muted-foreground">{tenant.tenantId}</td>
                      <td className="py-3">
                        <Badge variant={tenant.role === "source" ? "secondary" : "info"} className="capitalize">
                          {tenant.role}
                        </Badge>
                      </td>
                      <td className="py-3 capitalize text-muted-foreground">{tenant.authMethod}</td>
                      <td className="py-3">
                        <div className="flex items-center gap-1.5">
                          <ConnectionStatusBadge status={tenant.connectionStatus} />
                          {tenant.directorySyncEnabled && (
                            <span className="inline-flex items-center rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-700 dark:bg-blue-900/30 dark:text-blue-400" title="Entra Connect (on-prem directory sync) detected on this tenant.">
                              Hybrid AD
                            </span>
                          )}
                        </div>
                      </td>
                      <td className="py-3 text-muted-foreground">{formatDateTime(tenant.lastVerifiedAt)}</td>
                      <td className="py-3">
                        <DropdownMenu>
                          <DropdownMenuTrigger asChild>
                            <Button variant="ghost" size="icon" className="opacity-0 group-hover:opacity-100">
                              <MoreHorizontal className="h-4 w-4" />
                            </Button>
                          </DropdownMenuTrigger>
                          <DropdownMenuContent align="end">
                            <DropdownMenuItem onClick={() => verifyMutation.mutate(tenant.id)}>
                              <RefreshCw className="mr-2 h-4 w-4" />Verify Connection
                            </DropdownMenuItem>
                            <DropdownMenuItem onClick={() => diagnoseExoMutation.mutate(tenant.id)} disabled={diagnoseExoMutation.isPending}>
                              <FlaskConical className="mr-2 h-4 w-4" />Diagnose EXO Token
                            </DropdownMenuItem>
                            <DropdownMenuItem onClick={() => {
                              setReconfigForm({ appClientId: "", authMethod: tenant.authMethod, clientSecret: "" });
                              setReconfigCertBase64(""); setReconfigCertPassword(""); setReconfigCertThumbprint("");
                              setReconfigTarget(tenant.id);
                            }}>
                              <Settings className="mr-2 h-4 w-4" />Re-configure App
                            </DropdownMenuItem>
                            <DropdownMenuSeparator />
                            <DropdownMenuItem
                              className="text-destructive"
                              onClick={() => deleteMutation.mutate(tenant.id)}
                            >
                              <Trash2 className="mr-2 h-4 w-4" />Remove
                            </DropdownMenuItem>
                          </DropdownMenuContent>
                        </DropdownMenu>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
