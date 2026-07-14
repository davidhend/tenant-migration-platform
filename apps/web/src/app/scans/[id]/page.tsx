"use client";

import { useQuery } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import Link from "next/link";
import { AlertTriangle, CheckCircle2, Info } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { ScanStatusBadge, SeverityBadge } from "@/components/shared/status-badge";
import { scansApi } from "@/lib/api";
import { formatDateTime, formatBytes, formatNumber, relativeTime } from "@/lib/utils";
import { PieChart, Pie, Cell, ResponsiveContainer } from "recharts";

export default function ScanDetailPage() {
  const { id } = useParams<{ id: string }>();

  const { data: scan, isLoading: scanLoading } = useQuery({
    queryKey: ["scans", id],
    queryFn: () => scansApi.get(id),
    // This page has no SignalR subscription — poll while the scan is active.
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "queued" || status === "running" ? 10_000 : false;
    },
  });
  const { data: users, isLoading: usersLoading } = useQuery({ queryKey: ["scan-users", id], queryFn: () => scansApi.getUsers(id), enabled: !!scan });
  const { data: groups } = useQuery({ queryKey: ["scan-groups", id], queryFn: () => scansApi.getGroups(id), enabled: !!scan });
  const { data: mailboxes } = useQuery({ queryKey: ["scan-mailboxes", id], queryFn: () => scansApi.getMailboxes(id), enabled: !!scan });
  const { data: sites } = useQuery({ queryKey: ["scan-sites", id], queryFn: () => scansApi.getSites(id), enabled: !!scan });
  const { data: onedrive } = useQuery({ queryKey: ["scan-onedrive", id], queryFn: () => scansApi.getOneDrive(id), enabled: !!scan });
  const { data: domains } = useQuery({ queryKey: ["scan-domains", id], queryFn: () => scansApi.getDomains(id), enabled: !!scan });
  const { data: issues } = useQuery({ queryKey: ["scan-issues", id], queryFn: () => scansApi.getIssues(id), enabled: !!scan });

  if (scanLoading) return <div className="space-y-4">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-24 w-full" />)}</div>;
  if (!scan) return <div className="py-20 text-center text-muted-foreground">Scan not found.</div>;

  const score = scan.summary?.readinessScore ?? 0;
  const scoreColor = score >= 80 ? "#22c55e" : score >= 60 ? "#f59e0b" : "#ef4444";
  const blockers = issues?.filter((i) => i.severity === "blocker") ?? [];
  const warnings = issues?.filter((i) => i.severity === "warning") ?? [];
  const infos = issues?.filter((i) => i.severity === "info") ?? [];

  return (
    <div className="space-y-6">
      {/* Header */}
      <div>
        <div className="flex items-center gap-2 text-sm text-muted-foreground mb-1">
          <Link href="/projects" className="hover:text-foreground">Projects</Link>
          <span>/</span>
          <span>Scan Details</span>
        </div>
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold tracking-tight">Scan Results</h1>
            <p className="text-muted-foreground capitalize text-sm">{scan.scanType} scan · started {relativeTime(scan.startedAt)}</p>
          </div>
          <ScanStatusBadge status={scan.status} />
        </div>
        {scan.status === "running" && (
          <div className="mt-3 flex items-center gap-3">
            <Progress value={scan.progress} className="flex-1 h-2" />
            <span className="text-sm text-muted-foreground w-10">{scan.progress}%</span>
          </div>
        )}
      </div>

      {/* Summary stats row */}
      {scan.summary && (
        <div className="grid gap-3 grid-cols-2 sm:grid-cols-4 lg:grid-cols-7">
          {[
            { label: "Users", value: formatNumber(scan.summary.userCount) },
            { label: "Groups", value: formatNumber(scan.summary.groupCount) },
            { label: "Mailboxes", value: formatNumber(scan.summary.mailboxCount) },
            { label: "Mailbox Data", value: formatBytes(scan.summary.mailboxTotalSizeGb) },
            { label: "Sites", value: formatNumber(scan.summary.siteCount) },
            { label: "OneDrive", value: formatNumber(scan.summary.oneDriveCount) },
            { label: "Domains", value: formatNumber(scan.summary.domainCount) },
          ].map(({ label, value }) => (
            <div key={label} className="rounded-md border p-3 text-center">
              <p className="text-xs text-muted-foreground">{label}</p>
              <p className="text-lg font-bold">{value}</p>
            </div>
          ))}
        </div>
      )}

      <Tabs defaultValue="users">
        <TabsList className="w-full justify-start flex-wrap h-auto gap-1">
          <TabsTrigger value="users">Users ({scan.summary?.userCount ?? 0})</TabsTrigger>
          <TabsTrigger value="mailboxes">Mailboxes ({scan.summary?.mailboxCount ?? 0})</TabsTrigger>
          <TabsTrigger value="groups">Groups ({scan.summary?.groupCount ?? 0})</TabsTrigger>
          <TabsTrigger value="sites">SharePoint ({scan.summary?.siteCount ?? 0})</TabsTrigger>
          <TabsTrigger value="onedrive">OneDrive ({scan.summary?.oneDriveCount ?? 0})</TabsTrigger>
          <TabsTrigger value="domains">Domains ({scan.summary?.domainCount ?? 0})</TabsTrigger>
          <TabsTrigger value="issues">
            Issues
            {(scan.summary?.blockerCount ?? 0) > 0 && (
              <span className="ml-1 rounded-full bg-destructive px-1.5 py-0.5 text-xs text-white">{scan.summary!.blockerCount}</span>
            )}
          </TabsTrigger>
          <TabsTrigger value="readiness">Readiness Score</TabsTrigger>
        </TabsList>

        {/* Users */}
        <TabsContent value="users" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Scanned Users ({users?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              {usersLoading ? <Skeleton className="h-40 w-full" /> : (
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead><tr className="border-b">
                      {["Display Name", "UPN", "Enabled", "Mailbox", "Mailbox Size", "OneDrive", "MFA", "Licenses"].map((h) => (
                        <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                      ))}
                    </tr></thead>
                    <tbody className="divide-y">
                      {users?.slice(0, 100).map((u) => (
                        <tr key={u.id} className="hover:bg-muted/40">
                          <td className="py-1.5 pr-4 font-medium">{u.displayName}</td>
                          <td className="py-1.5 pr-4 font-mono text-xs text-muted-foreground">{u.upn}</td>
                          <td className="py-1.5 pr-4">{u.accountEnabled ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : <Badge variant="secondary">Disabled</Badge>}</td>
                          <td className="py-1.5 pr-4">{u.hasMailbox ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : "—"}</td>
                          <td className="py-1.5 pr-4 text-muted-foreground">{u.hasMailbox ? formatBytes(u.mailboxSizeGb) : "—"}</td>
                          <td className="py-1.5 pr-4 text-muted-foreground">{formatBytes(u.oneDriveSizeGb)}</td>
                          <td className="py-1.5 pr-4">{u.mfaEnabled ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : <AlertTriangle className="h-4 w-4 text-yellow-500" />}</td>
                          <td className="py-1.5 pr-4 text-xs text-muted-foreground">{u.licenses.join(", ")}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {(users?.length ?? 0) > 100 && <p className="mt-2 text-xs text-muted-foreground text-center">Showing first 100 of {users?.length} users</p>}
                </div>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        {/* Mailboxes */}
        <TabsContent value="mailboxes" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Mailboxes ({mailboxes?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead><tr className="border-b">{["Display Name", "SMTP Address", "Type", "Size", "Items", "Archive", "Last Logon"].map((h) => (
                    <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                  ))}</tr></thead>
                  <tbody className="divide-y">
                    {mailboxes?.slice(0, 100).map((m) => (
                      <tr key={m.id} className="hover:bg-muted/40">
                        <td className="py-1.5 pr-4 font-medium">{m.displayName}</td>
                        <td className="py-1.5 pr-4 font-mono text-xs">{m.primarySmtpAddress}</td>
                        <td className="py-1.5 pr-4"><Badge variant="secondary" className="text-xs">{m.mailboxType}</Badge></td>
                        <td className="py-1.5 pr-4">{formatBytes(m.sizeGb)}</td>
                        <td className="py-1.5 pr-4 text-muted-foreground">{formatNumber(m.itemCount)}</td>
                        <td className="py-1.5 pr-4">{m.hasArchive ? <span>{formatBytes(m.archiveSizeGb ?? 0)}</span> : "—"}</td>
                        <td className="py-1.5 pr-4 text-muted-foreground text-xs">{relativeTime(m.lastLogonTime)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Groups */}
        <TabsContent value="groups" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Groups ({groups?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              <table className="w-full text-sm">
                <thead><tr className="border-b">{["Display Name", "Type", "Members", "Mail Enabled", "Security"].map((h) => (
                  <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                ))}</tr></thead>
                <tbody className="divide-y">
                  {groups?.map((g) => (
                    <tr key={g.id} className="hover:bg-muted/40">
                      <td className="py-1.5 pr-4 font-medium">{g.displayName}</td>
                      <td className="py-1.5 pr-4"><Badge variant="secondary" className="text-xs">{g.groupType}</Badge></td>
                      <td className="py-1.5 pr-4 text-muted-foreground">{g.memberCount}</td>
                      <td className="py-1.5 pr-4">{g.mailEnabled ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : "—"}</td>
                      <td className="py-1.5 pr-4">{g.securityEnabled ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : "—"}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </CardContent>
          </Card>
        </TabsContent>

        {/* SharePoint Sites */}
        <TabsContent value="sites" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">SharePoint Sites ({sites?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              <table className="w-full text-sm">
                <thead><tr className="border-b">{["Title", "Template", "Storage Used", "Quota", "Owners", "Last Activity", "Unique Perms"].map((h) => (
                  <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                ))}</tr></thead>
                <tbody className="divide-y">
                  {sites?.map((s) => (
                    <tr key={s.id} className="hover:bg-muted/40">
                      <td className="py-1.5 pr-4">
                        <div className="font-medium">{s.title}</div>
                        <div className="text-xs text-muted-foreground truncate max-w-[200px]">{s.siteUrl}</div>
                      </td>
                      <td className="py-1.5 pr-4"><Badge variant="secondary" className="text-xs">{s.template}</Badge></td>
                      <td className="py-1.5 pr-4">{formatBytes(s.storageUsedGb)}</td>
                      <td className="py-1.5 pr-4 text-muted-foreground">{formatBytes(s.storageQuotaGb)}</td>
                      <td className="py-1.5 pr-4 text-xs text-muted-foreground">{s.owners[0]}{s.owners.length > 1 && ` +${s.owners.length - 1}`}</td>
                      <td className="py-1.5 pr-4 text-muted-foreground text-xs">{relativeTime(s.lastActivityDate)}</td>
                      <td className="py-1.5 pr-4">{s.hasUniquePermissions ? <AlertTriangle className="h-4 w-4 text-yellow-500" /> : <CheckCircle2 className="h-4 w-4 text-green-500" />}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </CardContent>
          </Card>
        </TabsContent>

        {/* OneDrive */}
        <TabsContent value="onedrive" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">OneDrive Accounts ({onedrive?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              <div className="overflow-x-auto">
                <table className="w-full text-sm">
                  <thead><tr className="border-b">{["Owner", "UPN", "Used", "Quota", "Files", "Last Modified"].map((h) => (
                    <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                  ))}</tr></thead>
                  <tbody className="divide-y">
                    {onedrive?.slice(0, 100).map((od) => (
                      <tr key={od.id} className="hover:bg-muted/40">
                        <td className="py-1.5 pr-4 font-medium">{od.ownerDisplayName}</td>
                        <td className="py-1.5 pr-4 font-mono text-xs text-muted-foreground">{od.ownerUpn}</td>
                        <td className="py-1.5 pr-4">{formatBytes(od.storageUsedGb)}</td>
                        <td className="py-1.5 pr-4 text-muted-foreground">{formatBytes(od.storageQuotaGb)}</td>
                        <td className="py-1.5 pr-4 text-muted-foreground">{formatNumber(od.fileCount)}</td>
                        <td className="py-1.5 pr-4 text-muted-foreground text-xs">{relativeTime(od.lastModified)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Domains */}
        <TabsContent value="domains" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Accepted Domains ({domains?.length ?? 0})</CardTitle></CardHeader>
            <CardContent>
              <table className="w-full text-sm">
                <thead><tr className="border-b">{["Domain", "Default", "Verified", "Users"].map((h) => (
                  <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4">{h}</th>
                ))}</tr></thead>
                <tbody className="divide-y">
                  {domains?.map((d) => (
                    <tr key={d.id} className="hover:bg-muted/40">
                      <td className="py-2 pr-4 font-mono font-medium">{d.name}</td>
                      <td className="py-2 pr-4">{d.isDefault ? <Badge variant="info">Primary</Badge> : "—"}</td>
                      <td className="py-2 pr-4">{d.isVerified ? <CheckCircle2 className="h-4 w-4 text-green-500" /> : <AlertTriangle className="h-4 w-4 text-yellow-500" />}</td>
                      <td className="py-2 pr-4 text-muted-foreground">{formatNumber(d.userCount)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </CardContent>
          </Card>
        </TabsContent>

        {/* Issues */}
        <TabsContent value="issues" className="mt-4 space-y-4">
          {[
            { label: "Blockers", items: blockers, icon: <AlertTriangle className="h-4 w-4 text-destructive" /> },
            { label: "Warnings", items: warnings, icon: <AlertTriangle className="h-4 w-4 text-yellow-500" /> },
            { label: "Info", items: infos, icon: <Info className="h-4 w-4 text-blue-500" /> },
          ].map(({ label, items, icon }) =>
            items.length === 0 ? null : (
              <Card key={label}>
                <CardHeader>
                  <CardTitle className="flex items-center gap-2 text-base">{icon}{label} ({items.length})</CardTitle>
                </CardHeader>
                <CardContent className="space-y-3">
                  {items.map((issue) => (
                    <div key={issue.id} className="rounded-md border p-4 space-y-2">
                      <div className="flex items-center gap-2">
                        <SeverityBadge severity={issue.severity} />
                        <span className="font-medium text-sm">{issue.title}</span>
                        <Badge variant="outline" className="text-xs font-mono">{issue.code}</Badge>
                        <span className="ml-auto text-xs text-muted-foreground">{issue.affectedObjectCount} affected</span>
                      </div>
                      <p className="text-sm text-muted-foreground">{issue.description}</p>
                      <div>
                        <p className="text-xs font-medium mb-1">Remediation steps:</p>
                        <ul className="list-inside list-disc text-xs text-muted-foreground space-y-0.5">
                          {issue.remediationSteps.map((step, i) => <li key={i}>{step}</li>)}
                        </ul>
                      </div>
                    </div>
                  ))}
                </CardContent>
              </Card>
            )
          )}
          {issues?.length === 0 && (
            <div className="flex flex-col items-center gap-2 py-12 text-center text-muted-foreground">
              <CheckCircle2 className="h-10 w-10 text-green-500" />
              <p>No issues detected — tenant is migration-ready.</p>
            </div>
          )}
        </TabsContent>

        {/* Readiness Score */}
        <TabsContent value="readiness" className="mt-4">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Readiness Score</CardTitle>
              <CardDescription>Computed from scan results across all workload categories</CardDescription>
            </CardHeader>
            <CardContent className="flex flex-col items-center gap-6 py-4">
              <ResponsiveContainer width={200} height={200}>
                <PieChart>
                  <Pie data={[{ value: score }, { value: 100 - score }]} cx={95} cy={95} innerRadius={65} outerRadius={85} startAngle={90} endAngle={-270} dataKey="value" strokeWidth={0}>
                    <Cell fill={scoreColor} />
                    <Cell fill="hsl(var(--muted))" />
                  </Pie>
                </PieChart>
              </ResponsiveContainer>
              <div className="text-center -mt-28 mb-20">
                <span className="text-5xl font-bold" style={{ color: scoreColor }}>{score}</span>
                <span className="text-xl text-muted-foreground">/100</span>
                <p className="mt-1 text-sm text-muted-foreground">
                  {score >= 80 ? "Ready for migration" : score >= 60 ? "Minor issues to resolve" : "Blockers must be resolved first"}
                </p>
              </div>
              <div className="w-full max-w-md space-y-2 text-sm">
                {[
                  { label: "Blockers", count: scan.summary?.blockerCount ?? 0, impact: "−15 per blocker", bad: true },
                  { label: "Warnings", count: scan.summary?.warningCount ?? 0, impact: "−3 per warning", bad: false },
                  { label: "Total Users Scanned", count: scan.summary?.userCount ?? 0, impact: "", bad: false },
                  { label: "Domains Verified", count: scan.summary?.domainCount ?? 0, impact: "", bad: false },
                ].map(({ label, count, impact, bad }) => (
                  <div key={label} className="flex justify-between border-b pb-1">
                    <span className="text-muted-foreground">{label}</span>
                    <span className={bad && count > 0 ? "text-destructive font-medium" : "font-medium"}>
                      {count} {impact && <span className="text-xs text-muted-foreground">{impact}</span>}
                    </span>
                  </div>
                ))}
              </div>
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}
