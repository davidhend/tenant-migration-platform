"use client";

import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, AlertTriangle, XCircle, PlugZap, RefreshCw } from "lucide-react";
import { healthApi, versionApi, type HealthState } from "@/lib/api";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

const CHECK_DOT: Record<HealthState, string> = {
  Healthy: "bg-emerald-500",
  Degraded: "bg-amber-500",
  Unhealthy: "bg-red-500",
};

export default function StatusPage() {
  const { data, isFetching, refetch, dataUpdatedAt } = useQuery({
    queryKey: ["system-health"],
    queryFn: () => healthApi.ready(),
    refetchInterval: 20_000,
  });

  const { data: version } = useQuery({
    queryKey: ["platform-version"],
    queryFn: () => versionApi.get(),
    staleTime: 5 * 60_000,
    retry: false,
  });

  const reachable = data?.reachable ?? false;
  const report = data?.report ?? null;
  const overall = !reachable ? "Unreachable" : report?.status ?? "Unhealthy";

  const banner =
    overall === "Healthy"
      ? { Icon: CheckCircle2, cls: "text-emerald-600 dark:text-emerald-400", msg: "All systems operational." }
      : overall === "Degraded"
        ? { Icon: AlertTriangle, cls: "text-amber-600 dark:text-amber-400", msg: "Running with a degraded optional dependency." }
        : overall === "Unreachable"
          ? { Icon: PlugZap, cls: "text-red-600 dark:text-red-400", msg: "The API is unreachable — is the stack running?" }
          : { Icon: XCircle, cls: "text-red-600 dark:text-red-400", msg: "A required dependency is unhealthy." };

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-semibold">System Status</h1>
        <Button variant="outline" size="sm" onClick={() => void refetch()}>
          <RefreshCw className={cn("mr-2 h-4 w-4", isFetching && "animate-spin")} />
          Refresh
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className={cn("flex items-center gap-2", banner.cls)}>
            <banner.Icon className="h-5 w-5" />
            {overall}
          </CardTitle>
          <CardDescription>{banner.msg}</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
          {!reachable ? (
            <div className="rounded-md border border-red-300 bg-red-50 p-4 text-sm dark:border-red-800 dark:bg-red-950/40">
              <p className="font-medium">Backend not responding.</p>
              <p className="mt-1 text-muted-foreground">
                Start the stack, then this page will update automatically:
              </p>
              <pre className="mt-2 overflow-x-auto rounded bg-muted p-2 text-xs">
                ./start.sh      # or: make up
              </pre>
              <p className="mt-2 text-muted-foreground">
                Requires Docker (Docker Desktop with WSL integration is recommended).
              </p>
            </div>
          ) : (
            <div className="divide-y rounded-md border">
              {(report?.checks ?? []).map((c) => (
                <div key={c.name} className="flex items-start gap-3 p-3">
                  <span className={cn("mt-1.5 h-2.5 w-2.5 shrink-0 rounded-full", CHECK_DOT[c.status])} />
                  <div className="min-w-0 flex-1">
                    <div className="flex items-center justify-between gap-2">
                      <span className="font-medium capitalize">{c.name}</span>
                      <span className="text-xs text-muted-foreground">
                        {c.status} · {Math.round(c.durationMs)}ms
                      </span>
                    </div>
                    {(c.error || c.description) && (
                      <p className="mt-0.5 text-sm text-muted-foreground">{c.error ?? c.description}</p>
                    )}
                  </div>
                </div>
              ))}
              {(report?.checks?.length ?? 0) === 0 && (
                <p className="p-3 text-sm text-muted-foreground">No dependency checks reported.</p>
              )}
            </div>
          )}

          <p className="text-xs text-muted-foreground">
            {dataUpdatedAt ? `Last checked ${new Date(dataUpdatedAt).toLocaleTimeString()}` : "Checking…"} ·
            auto-refreshes every 20s
            {version && (
              <> · Platform v{version.version}
                {version.runbookVersion !== version.version && ` · runbook v${version.runbookVersion}`}
                {` · ${version.environment}`}
              </>
            )}
          </p>
        </CardContent>
      </Card>
    </div>
  );
}
