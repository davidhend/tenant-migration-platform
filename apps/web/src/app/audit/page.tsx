"use client";

import { useQuery } from "@tanstack/react-query";
import { CheckCircle2, XCircle, Clock, ClipboardList } from "lucide-react";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { auditApi } from "@/lib/api";
import { formatDateTime } from "@/lib/utils";

export default function AuditPage() {
  const { data: auditPage, isLoading } = useQuery({ queryKey: ["audit-full"], queryFn: () => auditApi.list(1, 100) });

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold tracking-tight">Audit Log</h1>
        <p className="text-muted-foreground">Immutable record of all platform actions</p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle>Audit Events</CardTitle>
          <CardDescription>{auditPage?.totalCount ?? 0} total events</CardDescription>
        </CardHeader>
        <CardContent>
          {isLoading ? (
            <div className="space-y-2">{Array.from({ length: 6 }).map((_, i) => <Skeleton key={i} className="h-12 w-full" />)}</div>
          ) : auditPage?.items.length === 0 ? (
            <div className="flex flex-col items-center gap-2 py-12 text-center text-muted-foreground">
              <ClipboardList className="h-10 w-10 opacity-30" />
              <p>No audit events yet.</p>
            </div>
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b">
                    {["Timestamp", "Outcome", "Action", "Resource", "Actor", "Project"].map((h) => (
                      <th key={h} className="py-2 text-left font-medium text-muted-foreground pr-4 whitespace-nowrap">{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody className="divide-y">
                  {auditPage?.items.map((event) => (
                    <tr key={event.id} className="hover:bg-muted/40">
                      <td className="py-2 pr-4 whitespace-nowrap text-xs text-muted-foreground font-mono">
                        <div className="flex items-center gap-1">
                          <Clock className="h-3 w-3 shrink-0" />
                          {formatDateTime(event.timestamp)}
                        </div>
                      </td>
                      <td className="py-2 pr-4">
                        {event.outcome === "success"
                          ? <CheckCircle2 className="h-4 w-4 text-green-500" />
                          : <XCircle className="h-4 w-4 text-destructive" />}
                      </td>
                      <td className="py-2 pr-4 font-medium whitespace-nowrap">
                        {event.action.replace(/_/g, " ")}
                      </td>
                      <td className="py-2 pr-4 font-mono text-xs text-muted-foreground">
                        {event.resource}
                      </td>
                      <td className="py-2 pr-4 text-muted-foreground text-xs whitespace-nowrap">
                        {event.actor}
                      </td>
                      <td className="py-2 pr-4">
                        {event.projectId
                          ? <Badge variant="secondary" className="text-xs font-mono">{event.projectId.slice(0, 8)}</Badge>
                          : <span className="text-muted-foreground">—</span>}
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
