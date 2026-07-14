"use client";

import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, AlertTriangle, XCircle, PlugZap, RefreshCw } from "lucide-react";
import { healthApi, type HealthState } from "@/lib/api";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { cn } from "@/lib/utils";

/** Four visible states: healthy, degraded, unhealthy, and backend-unreachable. */
type OverallState = HealthState | "Unreachable";

function overallFrom(reachable: boolean, status: HealthState | undefined): OverallState {
  if (!reachable) return "Unreachable";
  return status ?? "Unhealthy";
}

const STATE_UI: Record<
  OverallState,
  { label: string; dot: string; text: string; Icon: typeof CheckCircle2 }
> = {
  Healthy: { label: "System OK", dot: "bg-emerald-500", text: "text-emerald-600 dark:text-emerald-400", Icon: CheckCircle2 },
  Degraded: { label: "Degraded", dot: "bg-amber-500", text: "text-amber-600 dark:text-amber-400", Icon: AlertTriangle },
  Unhealthy: { label: "Unhealthy", dot: "bg-red-500", text: "text-red-600 dark:text-red-400", Icon: XCircle },
  Unreachable: { label: "Backend unreachable", dot: "bg-red-500", text: "text-red-600 dark:text-red-400", Icon: PlugZap },
};

const CHECK_DOT: Record<HealthState, string> = {
  Healthy: "bg-emerald-500",
  Degraded: "bg-amber-500",
  Unhealthy: "bg-red-500",
};

export function SystemStatus() {
  const { data, isFetching, refetch, dataUpdatedAt } = useQuery({
    queryKey: ["system-health"],
    queryFn: () => healthApi.ready(),
    refetchInterval: 20_000,
    refetchOnWindowFocus: true,
    staleTime: 10_000,
  });

  const reachable = data?.reachable ?? false;
  const report = data?.report ?? null;
  const overall = overallFrom(reachable, report?.status);
  const ui = STATE_UI[overall];

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={`System status: ${ui.label}`}
          className="inline-flex items-center gap-2 rounded-md border px-2.5 py-1.5 text-xs font-medium hover:bg-accent"
        >
          <span className="relative flex h-2 w-2">
            {overall !== "Healthy" && (
              <span className={cn("absolute inline-flex h-full w-full animate-ping rounded-full opacity-75", ui.dot)} />
            )}
            <span className={cn("relative inline-flex h-2 w-2 rounded-full", ui.dot)} />
          </span>
          <span className={cn("hidden sm:inline", ui.text)}>{ui.label}</span>
        </button>
      </DropdownMenuTrigger>

      <DropdownMenuContent align="end" className="w-80">
        <DropdownMenuLabel className="flex items-center justify-between gap-2">
          <span className={cn("flex items-center gap-2", ui.text)}>
            <ui.Icon className="h-4 w-4" />
            {ui.label}
          </span>
          <button
            type="button"
            onClick={(e) => {
              e.preventDefault();
              void refetch();
            }}
            aria-label="Refresh status"
            className="rounded p-1 hover:bg-accent"
          >
            <RefreshCw className={cn("h-3.5 w-3.5", isFetching && "animate-spin")} />
          </button>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />

        {!reachable ? (
          <div className="px-2 py-1.5 text-sm text-muted-foreground">
            <p className="font-medium text-foreground">Can&apos;t reach the API.</p>
            <p className="mt-1">
              Is the stack running? Start it with{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">./start.sh</code> (or{" "}
              <code className="rounded bg-muted px-1 py-0.5 text-xs">make up</code>), then this will
              turn green once Postgres and the API are up.
            </p>
          </div>
        ) : (
          <div className="space-y-1 px-1 py-1">
            {(report?.checks ?? []).map((c) => (
              <div key={c.name} className="flex items-start gap-2 rounded px-1.5 py-1 text-sm">
                <span className={cn("mt-1.5 h-2 w-2 shrink-0 rounded-full", CHECK_DOT[c.status])} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center justify-between gap-2">
                    <span className="font-medium capitalize">{c.name}</span>
                    <span className="text-xs text-muted-foreground">{c.status}</span>
                  </div>
                  {(c.error || c.description) && (
                    <p className="truncate text-xs text-muted-foreground" title={c.error ?? c.description ?? ""}>
                      {c.error ?? c.description}
                    </p>
                  )}
                </div>
              </div>
            ))}
            {(report?.checks?.length ?? 0) === 0 && (
              <p className="px-2 py-1.5 text-sm text-muted-foreground">No dependency checks reported.</p>
            )}
          </div>
        )}

        <DropdownMenuSeparator />
        <p className="px-2 py-1 text-[11px] text-muted-foreground">
          {dataUpdatedAt ? `Checked ${new Date(dataUpdatedAt).toLocaleTimeString()}` : "Checking…"} · auto-refreshes
        </p>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
