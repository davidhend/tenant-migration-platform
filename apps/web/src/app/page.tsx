"use client";

import { useQuery } from "@tanstack/react-query";
import Link from "next/link";
import {
  Building2, FolderKanban, Briefcase, AlertTriangle,
  ArrowRight, CheckCircle2, Clock, XCircle,
} from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { tenantsApi, projectsApi, jobsApi, auditApi } from "@/lib/api";
import { formatDateTime, relativeTime, formatNumber } from "@/lib/utils";
import { BarChart, Bar, XAxis, YAxis, Tooltip, ResponsiveContainer, CartesianGrid } from "recharts";

const JOB_TYPE_LABELS: Record<string, string> = {
  scan: "Scan",
  identityMap: "Identity",
  mailboxMigrate: "Mailboxes",
  sharePointMigrate: "SharePoint",
  oneDriveMigrate: "OneDrive",
};

export default function DashboardPage() {
  const { data: tenants, isLoading: tenantsLoading } = useQuery({ queryKey: ["tenants"], queryFn: tenantsApi.list });
  const { data: projects, isLoading: projectsLoading } = useQuery({ queryKey: ["projects"], queryFn: projectsApi.list });
  const { data: jobs, isLoading: jobsLoading } = useQuery({ queryKey: ["jobs"], queryFn: () => jobsApi.list() });
  const { data: auditPage, isLoading: auditLoading } = useQuery({ queryKey: ["audit"], queryFn: () => auditApi.list(1, 8) });

  const migrationProgressData = Object.entries(
    (jobs ?? [])
      .filter((j) => j.type !== "scan")
      .reduce<Record<string, { total: number; done: number }>>((acc, job) => {
        const label = JOB_TYPE_LABELS[job.type] ?? job.type;
        if (!acc[label]) acc[label] = { total: 0, done: 0 };
        acc[label].total += job.itemsTotal;
        acc[label].done += job.itemsProcessed;
        return acc;
      }, {})
  ).map(([name, { total, done }]) => ({ name, total, done }));

  const activeProjects = projects?.filter((p) => p.status === "active").length ?? 0;
  const runningJobs = jobs?.filter((j) => j.status === "running").length ?? 0;
  const failedJobs = jobs?.filter((j) => j.status === "failed").length ?? 0;
  const connectedTenants = tenants?.filter((t) => t.connectionStatus === "connected").length ?? 0;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">Dashboard</h1>
          <p className="text-muted-foreground">Migration control plane overview</p>
        </div>
        <div className="flex gap-2">
          <Button variant="outline" asChild>
            <Link href="/tenants">Add Tenant</Link>
          </Button>
          <Button asChild>
            <Link href="/projects">New Project</Link>
          </Button>
        </div>
      </div>

      {/* Summary cards */}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-4">
        <SummaryCard
          label="Connected Tenants"
          value={connectedTenants}
          total={tenants?.length}
          icon={<Building2 className="h-4 w-4 text-muted-foreground" />}
          loading={tenantsLoading}
          href="/tenants"
        />
        <SummaryCard
          label="Active Projects"
          value={activeProjects}
          total={projects?.length}
          icon={<FolderKanban className="h-4 w-4 text-muted-foreground" />}
          loading={projectsLoading}
          href="/projects"
        />
        <SummaryCard
          label="Running Jobs"
          value={runningJobs}
          icon={<Briefcase className="h-4 w-4 text-muted-foreground" />}
          loading={jobsLoading}
          href="/jobs"
          highlight={runningJobs > 0 ? "info" : undefined}
        />
        <SummaryCard
          label="Failed Jobs"
          value={failedJobs}
          icon={<AlertTriangle className="h-4 w-4 text-muted-foreground" />}
          loading={jobsLoading}
          href="/jobs"
          highlight={failedJobs > 0 ? "destructive" : undefined}
        />
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        {/* Migration progress chart */}
        <Card>
          <CardHeader>
            <CardTitle>Migration Progress</CardTitle>
            <CardDescription>Objects migrated by workload across all projects</CardDescription>
          </CardHeader>
          <CardContent>
            {jobsLoading ? (
              <Skeleton className="h-[220px] w-full" />
            ) : migrationProgressData.length === 0 ? (
              <div className="flex h-[220px] flex-col items-center justify-center gap-2 text-sm text-muted-foreground">
                <Briefcase className="h-8 w-8" />
                <p>No migration jobs yet</p>
              </div>
            ) : (
              <ResponsiveContainer width="100%" height={220}>
                <BarChart data={migrationProgressData} barGap={4}>
                  <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
                  <XAxis dataKey="name" className="text-xs fill-muted-foreground" />
                  <YAxis className="text-xs fill-muted-foreground" />
                  <Tooltip
                    contentStyle={{ background: "hsl(var(--card))", border: "1px solid hsl(var(--border))", borderRadius: "6px" }}
                    labelStyle={{ color: "hsl(var(--foreground))" }}
                  />
                  <Bar dataKey="total" name="Total" fill="hsl(var(--muted))" radius={[4, 4, 0, 0]} />
                  <Bar dataKey="done" name="Migrated" fill="hsl(var(--primary))" radius={[4, 4, 0, 0]} />
                </BarChart>
              </ResponsiveContainer>
            )}
          </CardContent>
        </Card>

        {/* Active jobs */}
        <Card>
          <CardHeader className="flex flex-row items-center justify-between pb-2">
            <div>
              <CardTitle>Active Jobs</CardTitle>
              <CardDescription>Currently running migration tasks</CardDescription>
            </div>
            <Button variant="ghost" size="sm" asChild>
              <Link href="/jobs">View all <ArrowRight className="ml-1 h-3 w-3" /></Link>
            </Button>
          </CardHeader>
          <CardContent className="space-y-3">
            {jobsLoading ? (
              Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)
            ) : jobs?.filter((j) => j.status === "running" || j.status === "queued").length === 0 ? (
              <div className="flex flex-col items-center gap-1 py-8 text-center text-sm text-muted-foreground">
                <CheckCircle2 className="h-8 w-8 text-green-500" />
                <p>No active jobs</p>
              </div>
            ) : (
              jobs
                ?.filter((j) => j.status === "running" || j.status === "queued")
                .slice(0, 4)
                .map((job) => (
                  <div key={job.id} className="space-y-1.5 rounded-md border p-3">
                    <div className="flex items-center justify-between text-sm">
                      <span className="font-medium capitalize">{job.type.replace("_", " ")}</span>
                      <Badge variant={job.status === "running" ? "info" : "secondary"} className="capitalize">
                        {job.status}
                      </Badge>
                    </div>
                    <Progress value={job.progress} className="h-1.5" />
                    <div className="flex justify-between text-xs text-muted-foreground">
                      <span>{formatNumber(job.itemsProcessed)} / {formatNumber(job.itemsTotal)}</span>
                      <span>{job.progress}%</span>
                    </div>
                  </div>
                ))
            )}
          </CardContent>
        </Card>
      </div>

      {/* Recent activity */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between pb-2">
          <div>
            <CardTitle>Recent Activity</CardTitle>
            <CardDescription>Latest audit events across all projects</CardDescription>
          </div>
          <Button variant="ghost" size="sm" asChild>
            <Link href="/audit">View all <ArrowRight className="ml-1 h-3 w-3" /></Link>
          </Button>
        </CardHeader>
        <CardContent>
          {auditLoading ? (
            <div className="space-y-2">
              {Array.from({ length: 5 }).map((_, i) => <Skeleton key={i} className="h-10 w-full" />)}
            </div>
          ) : (
            <div className="divide-y">
              {auditPage?.items.slice(0, 8).map((event) => (
                <div key={event.id} className="flex items-center gap-3 py-2.5 text-sm">
                  {event.outcome === "success" ? (
                    <CheckCircle2 className="h-4 w-4 shrink-0 text-green-500" />
                  ) : (
                    <XCircle className="h-4 w-4 shrink-0 text-destructive" />
                  )}
                  <div className="min-w-0 flex-1">
                    <span className="font-medium">{event.action.replace(/_/g, " ")}</span>
                    <span className="ml-2 text-muted-foreground">{event.resource}</span>
                  </div>
                  <div className="flex items-center gap-1 shrink-0 text-xs text-muted-foreground">
                    <Clock className="h-3 w-3" />
                    {relativeTime(event.timestamp)}
                  </div>
                </div>
              ))}
            </div>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

function SummaryCard({
  label, value, total, icon, loading, href, highlight,
}: {
  label: string;
  value: number;
  total?: number;
  icon: React.ReactNode;
  loading?: boolean;
  href: string;
  highlight?: "info" | "destructive";
}) {
  return (
    <Card className={highlight === "destructive" ? "border-destructive/50" : highlight === "info" ? "border-primary/50" : ""}>
      <CardHeader className="flex flex-row items-center justify-between pb-2">
        <CardDescription>{label}</CardDescription>
        {icon}
      </CardHeader>
      <CardContent>
        {loading ? (
          <Skeleton className="h-8 w-16" />
        ) : (
          <div className="flex items-end gap-1">
            <span className="text-3xl font-bold">{value}</span>
            {total !== undefined && (
              <span className="mb-0.5 text-sm text-muted-foreground">/ {total}</span>
            )}
          </div>
        )}
        <Link href={href} className="mt-1 flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground transition-colors">
          View details <ArrowRight className="h-3 w-3" />
        </Link>
      </CardContent>
    </Card>
  );
}
