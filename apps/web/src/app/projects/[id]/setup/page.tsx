"use client";

import { useState } from "react";
import { useQuery, useMutation } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import Link from "next/link";
import {
  ExternalLink, Copy, Check, RefreshCw, Loader2, CheckCircle2,
  AlertTriangle, XCircle, HelpCircle, Mail, HardDrive, Users, Cloud,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { setupApi, diagnosticsApi } from "@/lib/api";
import type {
  SetupPlan, SetupStep, SetupStepCategory, SetupTenantInfo,
  TenantPrereqReport, DiagCheck,
} from "@/types";

const CATEGORY_META: Record<SetupStepCategory, { label: string; icon: typeof Mail }> = {
  exchange: { label: "Exchange / Mailbox migration", icon: Mail },
  entra: { label: "Entra ID / Directory", icon: Users },
  spo: { label: "SharePoint / OneDrive", icon: HardDrive },
  azure: { label: "Platform & Azure", icon: Cloud },
};

const AUDIENCE_LABEL: Record<string, string> = {
  sourceAdmin: "Source admin",
  targetAdmin: "Target admin",
  either: "Either admin",
};

function StepStatusBadge({ status }: { status: SetupStep["status"] }) {
  if (status === "done")
    return <Badge className="bg-emerald-500/15 text-emerald-600 hover:bg-emerald-500/15">Done</Badge>;
  if (status === "pending")
    return <Badge className="bg-amber-500/15 text-amber-600 hover:bg-amber-500/15">Action needed</Badge>;
  return <Badge variant="outline" className="text-muted-foreground">Verify below</Badge>;
}

function DiagStatusIcon({ status }: { status: DiagCheck["status"] }) {
  if (status === "pass") return <CheckCircle2 className="h-4 w-4 shrink-0 text-emerald-500" />;
  if (status === "fail") return <XCircle className="h-4 w-4 shrink-0 text-red-500" />;
  if (status === "warn") return <AlertTriangle className="h-4 w-4 shrink-0 text-amber-500" />;
  return <HelpCircle className="h-4 w-4 shrink-0 text-muted-foreground" />;
}

function ScriptBlock({ script }: { script: string }) {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    await navigator.clipboard.writeText(script);
    setCopied(true);
    toast.success("Script copied to clipboard");
    setTimeout(() => setCopied(false), 2000);
  };
  return (
    <div className="relative mt-2">
      <Button
        size="sm"
        variant="secondary"
        className="absolute right-2 top-2 h-7"
        onClick={copy}
      >
        {copied ? <Check className="mr-1 h-3 w-3" /> : <Copy className="mr-1 h-3 w-3" />}
        {copied ? "Copied" : "Copy"}
      </Button>
      <pre className="max-h-72 overflow-auto rounded-md border bg-muted/50 p-3 pr-24 text-xs leading-relaxed">
        {script}
      </pre>
    </div>
  );
}

function StepCard({ step }: { step: SetupStep }) {
  return (
    <div className="rounded-lg border p-4">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="font-medium">{step.title}</span>
          <Badge variant="outline" className="text-xs text-muted-foreground">
            {AUDIENCE_LABEL[step.audience] ?? step.audience}
          </Badge>
        </div>
        <div className="flex items-center gap-2">
          <StepStatusBadge status={step.status} />
          {step.kind === "link" && step.actionUrl && (
            <Button size="sm" asChild>
              <a href={step.actionUrl} target="_blank" rel="noopener noreferrer">
                <ExternalLink className="mr-1 h-3 w-3" />
                Open consent page
              </a>
            </Button>
          )}
        </div>
      </div>
      <p className="mt-2 whitespace-pre-line text-sm text-muted-foreground">{step.detail}</p>
      {step.script && <ScriptBlock script={step.script} />}
    </div>
  );
}

function TenantVerifyCard({ tenant, side }: { tenant: SetupTenantInfo; side: "Source" | "Target" }) {
  const [report, setReport] = useState<TenantPrereqReport | null>(null);
  const runChecks = useMutation({
    mutationFn: () => diagnosticsApi.tenantPrereqs(tenant.id),
    onSuccess: (r) => setReport(r),
    onError: (e: Error) => toast.error(`Checks failed for ${tenant.displayName}: ${e.message}`),
  });

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-base">
              {side}: {tenant.displayName}
            </CardTitle>
            <CardDescription>
              {tenant.onMicrosoftDomain ?? tenant.aadTenantId} · app {tenant.appClientId || "—"}
            </CardDescription>
          </div>
          <Button
            size="sm"
            variant="outline"
            onClick={() => runChecks.mutate()}
            disabled={runChecks.isPending}
          >
            {runChecks.isPending
              ? <Loader2 className="mr-1 h-3 w-3 animate-spin" />
              : <RefreshCw className="mr-1 h-3 w-3" />}
            Run checks
          </Button>
        </div>
      </CardHeader>
      <CardContent>
        {!report && !runChecks.isPending && (
          <p className="text-sm text-muted-foreground">
            Runs the live prerequisite diagnostics against this tenant (credential, token roles,
            EXO service principal, org relationship, endpoint, licenses).
          </p>
        )}
        {runChecks.isPending && (
          <p className="text-sm text-muted-foreground">Running checks — this probes EXO and can take up to a minute…</p>
        )}
        {report && (
          <div className="space-y-2">
            <div className="flex gap-2 text-xs">
              <Badge className="bg-emerald-500/15 text-emerald-600 hover:bg-emerald-500/15">{report.passCount} pass</Badge>
              {report.failCount > 0 && <Badge className="bg-red-500/15 text-red-600 hover:bg-red-500/15">{report.failCount} fail</Badge>}
              {report.warnCount > 0 && <Badge className="bg-amber-500/15 text-amber-600 hover:bg-amber-500/15">{report.warnCount} warn</Badge>}
              {report.unknownCount > 0 && <Badge variant="outline">{report.unknownCount} unknown</Badge>}
            </div>
            <div className="max-h-96 space-y-1.5 overflow-auto pr-1">
              {report.checks.map((c) => (
                <div key={c.id} className="flex items-start gap-2 rounded-md border px-3 py-2">
                  <DiagStatusIcon status={c.status} />
                  <div className="min-w-0 text-sm">
                    <div className="font-medium">{c.description}</div>
                    <div className="break-words text-muted-foreground">{c.message}</div>
                    {c.remediation && c.status !== "pass" && (
                      <div className="mt-1 break-words text-xs text-amber-600">Fix: {c.remediation}</div>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

export default function ProjectSetupPage() {
  const params = useParams<{ id: string }>();
  const id = params.id;

  const { data: plan, isLoading, error } = useQuery<SetupPlan>({
    queryKey: ["setup-plan", id],
    queryFn: () => setupApi.plan(id),
  });

  if (isLoading)
    return <div className="space-y-4">{Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-28 w-full" />)}</div>;
  if (error || !plan)
    return (
      <div className="py-20 text-center text-muted-foreground">
        {error instanceof Error ? error.message : "Setup plan unavailable."}
      </div>
    );

  const pendingCount = plan.steps.filter((s) => s.status === "pending").length;
  const categories = (Object.keys(CATEGORY_META) as SetupStepCategory[])
    .map((cat) => ({ cat, steps: plan.steps.filter((s) => s.category === cat) }))
    .filter((g) => g.steps.length > 0);

  return (
    <div className="space-y-6">
      <div>
        <div className="mb-1 flex items-center gap-2 text-sm text-muted-foreground">
          <Link href="/projects" className="hover:text-foreground">Projects</Link>
          <span>/</span>
          <Link href={`/projects/${id}`} className="hover:text-foreground">{plan.projectName}</Link>
          <span>/</span>
          <span className="text-foreground">Setup</span>
        </div>
        <h1 className="text-2xl font-bold tracking-tight">Tenant-pair setup</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          {pendingCount === 0
            ? "No configuration gaps detected. Use Run checks to verify each tenant live."
            : `${pendingCount} step(s) need action. Complete them in order, then verify each tenant live below.`}
        </p>
      </div>

      {categories.map(({ cat, steps }) => {
        const Icon = CATEGORY_META[cat].icon;
        return (
          <Card key={cat}>
            <CardHeader className="pb-3">
              <CardTitle className="flex items-center gap-2 text-base">
                <Icon className="h-4 w-4" />
                {CATEGORY_META[cat].label}
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-3">
              {steps.map((s) => <StepCard key={s.id} step={s} />)}
            </CardContent>
          </Card>
        );
      })}

      <div>
        <h2 className="mb-3 text-lg font-semibold">Live verification</h2>
        <div className="grid gap-4 lg:grid-cols-2">
          <TenantVerifyCard tenant={plan.sourceTenant} side="Source" />
          <TenantVerifyCard tenant={plan.targetTenant} side="Target" />
        </div>
      </div>
    </div>
  );
}
