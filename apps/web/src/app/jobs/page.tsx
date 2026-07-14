"use client";

import { useEffect, useRef, useCallback } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { RotateCcw, XCircle, Briefcase } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { JobStatusBadge } from "@/components/shared/status-badge";
import { jobsApi } from "@/lib/api";
import { formatDateTime, formatNumber, relativeTime } from "@/lib/utils";
import { useMigrationHub } from "@/hooks/useMigrationHub";

export default function JobsPage() {
  const qc = useQueryClient();
  const { data: jobs, isLoading } = useQuery({
    queryKey: ["jobs"],
    queryFn: () => jobsApi.list(),
    // Poll while anything is active so progress survives a dropped SignalR socket.
    refetchInterval: (query) =>
      query.state.data?.some((j) => j.status === "queued" || j.status === "running") ? 20_000 : false,
  });

  const retryMutation = useMutation({
    mutationFn: jobsApi.retry,
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["jobs"] }); toast.success("Job re-queued"); },
    // Non-scan job types answer 422 pointing at the workload-specific retry endpoint.
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to retry job";
      toast.error(msg, { duration: 8000 });
    },
  });
  const cancelMutation = useMutation({
    mutationFn: jobsApi.cancel,
    // Scan cancel is 202: the worker flips the status when it observes the request.
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["jobs"] }); toast.success("Job cancellation requested"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to cancel job";
      toast.error(msg, { duration: 8000 });
    },
  });

  // ── SignalR real-time updates ────────────────────────────────────────────────
  const hub = useMigrationHub();
  // Track which project groups we have already joined so we don't re-join on
  // every render when the jobs list re-fetches.
  const joinedProjects = useRef(new Set<string>());

  const handleJobProgress = useCallback(() => {
    // A job status/progress changed — refetch the list so the UI reflects the
    // latest state.  React Query deduplicates rapid-fire invalidations.
    qc.invalidateQueries({ queryKey: ["jobs"] });
  }, [qc]);

  // Connect once on mount, disconnect on unmount.
  useEffect(() => {
    let mounted = true;

    hub.connect().then(() => {
      if (!mounted) return;
      hub.on("JobProgress", handleJobProgress);
    });

    return () => {
      mounted = false;
      hub.off("JobProgress", handleJobProgress);
      // Leave all project groups we joined.
      joinedProjects.current.forEach((pid) => hub.leaveProject(pid));
      joinedProjects.current.clear();
      hub.disconnect();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // When the jobs list loads, join each unique project group so we receive
  // JobProgress events for every project represented on this page.
  useEffect(() => {
    if (!jobs) return;

    const projectIds = [...new Set(jobs.map((j) => j.projectId))];
    for (const pid of projectIds) {
      if (!joinedProjects.current.has(pid)) {
        hub.joinProject(pid);
        joinedProjects.current.add(pid);
      }
    }
  }, [jobs, hub]);

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Jobs</h1>
        <p className="text-muted-foreground">Monitor all migration jobs across projects</p>
      </div>

      {/* Summary row */}
      {!isLoading && jobs && (
        <div className="flex gap-3 flex-wrap">
          {[
            { label: "Running", count: jobs.filter((j) => j.status === "running").length, color: "text-blue-600" },
            { label: "Queued", count: jobs.filter((j) => j.status === "queued").length, color: "text-muted-foreground" },
            { label: "Completed", count: jobs.filter((j) => j.status === "completed").length, color: "text-green-600" },
            { label: "Failed", count: jobs.filter((j) => j.status === "failed").length, color: "text-destructive" },
          ].map(({ label, count, color }) => (
            <div key={label} className="rounded-md border px-3 py-2 text-sm">
              <span className={`font-semibold ${color}`}>{count}</span>
              <span className="ml-1 text-muted-foreground">{label}</span>
            </div>
          ))}
        </div>
      )}

      <Card>
        <CardHeader>
          <CardTitle>All Jobs</CardTitle>
          <CardDescription>{jobs?.length ?? 0} total jobs</CardDescription>
        </CardHeader>
        <CardContent>
          {isLoading ? (
            <div className="space-y-2">{Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}</div>
          ) : jobs?.length === 0 ? (
            <div className="flex flex-col items-center gap-2 py-12 text-center text-muted-foreground">
              <Briefcase className="h-10 w-10 opacity-30" />
              <p>No jobs yet. Start a scan or migration to create jobs.</p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b">
                    {["Job Type", "Status", "Progress", "Items", "Errors", "Created", "Duration", "Actions"].map((h) => (
                      <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4 whitespace-nowrap">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {jobs?.map((job) => {
                    const durationMs = job.startedAt && job.completedAt
                      ? new Date(job.completedAt).getTime() - new Date(job.startedAt).getTime()
                      : job.startedAt
                      ? Date.now() - new Date(job.startedAt).getTime()
                      : null;
                    const durationStr = durationMs !== null
                      ? durationMs < 60000 ? `${Math.floor(durationMs / 1000)}s` : `${Math.floor(durationMs / 60000)}m ${Math.floor((durationMs % 60000) / 1000)}s`
                      : "—";

                    return (
                      <tr key={job.id} className="hover:bg-muted/40">
                        <td className="py-2 pr-4 font-medium capitalize whitespace-nowrap">
                          {job.type.replace(/_/g, " ")}
                        </td>
                        <td className="py-2 pr-4"><JobStatusBadge status={job.status} /></td>
                        <td className="py-2 pr-4 w-36">
                          <div className="flex items-center gap-2">
                            <Progress value={job.progress} className="h-1.5 flex-1" />
                            <span className="text-xs text-muted-foreground w-7 shrink-0">{job.progress}%</span>
                          </div>
                        </td>
                        <td className="py-2 pr-4 text-muted-foreground whitespace-nowrap">
                          {formatNumber(job.itemsProcessed)}/{formatNumber(job.itemsTotal)}
                        </td>
                        <td className="py-2 pr-4">
                          {job.itemsFailed > 0 ? (
                            <span className="text-destructive font-medium">{job.itemsFailed}</span>
                          ) : (
                            <span className="text-muted-foreground">0</span>
                          )}
                        </td>
                        <td className="py-2 pr-4 text-muted-foreground text-xs whitespace-nowrap">
                          {relativeTime(job.createdAt)}
                        </td>
                        <td className="py-2 pr-4 text-muted-foreground text-xs whitespace-nowrap">{durationStr}</td>
                        <td className="py-2 pr-4">
                          <div className="flex gap-1">
                            {(job.status === "failed" || job.status === "cancelled") && (
                              <Button
                                variant="ghost" size="sm"
                                onClick={() => retryMutation.mutate(job.id)}
                                disabled={retryMutation.isPending}
                                title="Retry"
                              >
                                <RotateCcw className="h-3 w-3" />
                              </Button>
                            )}
                            {(job.status === "running" || job.status === "queued") && (
                              <Button
                                variant="ghost" size="sm"
                                onClick={() => cancelMutation.mutate(job.id)}
                                disabled={cancelMutation.isPending}
                                title="Cancel"
                              >
                                <XCircle className="h-3 w-3" />
                              </Button>
                            )}
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
