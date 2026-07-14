"use client";

import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useRouter } from "next/navigation";
import { Plus, ArrowRight, Loader2, FolderKanban, Trash2, MoreHorizontal } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter,
  DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ProjectStatusBadge } from "@/components/shared/status-badge";
import { projectsApi, tenantsApi } from "@/lib/api";
import { formatDate } from "@/lib/utils";
import type { CreateProjectDto } from "@/types";

export default function ProjectsPage() {
  const qc = useQueryClient();
  const router = useRouter();
  const [open, setOpen] = useState(false);
  const [form, setForm] = useState<CreateProjectDto>({ name: "", sourceTenantId: "", targetTenantId: "" });

  const { data: projects, isLoading } = useQuery({ queryKey: ["projects"], queryFn: projectsApi.list });
  const { data: tenants } = useQuery({ queryKey: ["tenants"], queryFn: tenantsApi.list });

  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);

  const createMutation = useMutation({
    mutationFn: projectsApi.create,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["projects"] });
      toast.success("Project created");
      setOpen(false);
      setForm({ name: "", sourceTenantId: "", targetTenantId: "" });
    },
    onError: () => toast.error("Failed to create project"),
  });

  const deleteMutation = useMutation({
    mutationFn: projectsApi.delete,
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["projects"] });
      toast.success("Project deleted");
      setConfirmDeleteId(null);
    },
    onError: () => toast.error("Failed to delete project"),
  });

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold tracking-tight">Projects</h1>
          <p className="text-muted-foreground">Manage tenant-to-tenant migration projects</p>
        </div>
        <Dialog open={open} onOpenChange={setOpen}>
          <DialogTrigger asChild>
            <Button><Plus className="mr-2 h-4 w-4" />New Project</Button>
          </DialogTrigger>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Create Migration Project</DialogTitle>
              <DialogDescription>Define the source and target tenants for this migration.</DialogDescription>
            </DialogHeader>
            <div className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="projectName">Project Name</Label>
                <Input id="projectName" placeholder="e.g. Contoso → Fabrikam Migration"
                  value={form.name} onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))} />
              </div>
              <div className="space-y-2">
                <Label>Source Tenant</Label>
                <Select value={form.sourceTenantId} onValueChange={(v) => setForm((f) => ({ ...f, sourceTenantId: v }))}>
                  <SelectTrigger><SelectValue placeholder="Select source tenant" /></SelectTrigger>
                  <SelectContent>
                    {tenants?.filter((t) => t.role === "source").map((t) => (
                      <SelectItem key={t.id} value={t.id}>{t.displayName}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-2">
                <Label>Target Tenant</Label>
                <Select value={form.targetTenantId} onValueChange={(v) => setForm((f) => ({ ...f, targetTenantId: v }))}>
                  <SelectTrigger><SelectValue placeholder="Select target tenant" /></SelectTrigger>
                  <SelectContent>
                    {tenants?.filter((t) => t.role === "target").map((t) => (
                      <SelectItem key={t.id} value={t.id}>{t.displayName}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </div>
            <DialogFooter>
              <Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
              <Button onClick={() => createMutation.mutate(form)} disabled={createMutation.isPending || !form.name || !form.sourceTenantId || !form.targetTenantId}>
                {createMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                Create Project
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>

      {isLoading ? (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-48 w-full rounded-lg" />)}
        </div>
      ) : projects?.length === 0 ? (
        <div className="flex flex-col items-center gap-3 py-20 text-center text-muted-foreground">
          <FolderKanban className="h-12 w-12 opacity-30" />
          <p className="text-lg font-medium">No projects yet</p>
          <p className="text-sm">Create your first migration project to get started.</p>
          <Button onClick={() => setOpen(true)}><Plus className="mr-2 h-4 w-4" />New Project</Button>
        </div>
      ) : (
        <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
          {projects?.map((project) => {
            const source = tenants?.find((t) => t.id === project.sourceTenantId);
            const target = tenants?.find((t) => t.id === project.targetTenantId);
            return (
              <Card
                key={project.id}
                className="h-full cursor-pointer transition-shadow hover:shadow-md"
                onClick={() => router.push(`/projects/${project.id}`)}
              >
                <CardHeader>
                  <div className="flex items-start justify-between gap-1">
                    <CardTitle className="text-base leading-tight flex-1 min-w-0">{project.name}</CardTitle>
                    <div className="flex items-center gap-1 shrink-0">
                      <ProjectStatusBadge status={project.status} />
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button
                            variant="ghost" size="icon" className="h-7 w-7"
                            onClick={(e) => e.stopPropagation()}
                          >
                            <MoreHorizontal className="h-4 w-4" />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          <DropdownMenuItem
                            className="text-destructive"
                            onClick={(e) => { e.stopPropagation(); setConfirmDeleteId(project.id); }}
                          >
                            <Trash2 className="mr-2 h-4 w-4" />Delete Project
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </div>
                  </div>
                  <CardDescription>Created {formatDate(project.createdAt)}</CardDescription>
                </CardHeader>
                <CardContent>
                  <div className="flex items-center gap-2 text-sm">
                    <div className="rounded-md border bg-muted px-2 py-1 text-xs font-medium">
                      {source?.displayName ?? "Unknown"}
                    </div>
                    <ArrowRight className="h-3 w-3 text-muted-foreground shrink-0" />
                    <div className="rounded-md border bg-primary/10 px-2 py-1 text-xs font-medium text-primary">
                      {target?.displayName ?? "Unknown"}
                    </div>
                  </div>
                </CardContent>
              </Card>
            );
          })}
        </div>
      )}

      {/* Delete confirmation dialog — outside ternary, controlled by state */}
      <Dialog open={!!confirmDeleteId} onOpenChange={(open) => { if (!open) setConfirmDeleteId(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Project</DialogTitle>
            <DialogDescription>
              This will permanently delete the project and all associated data — scans, jobs,
              identity maps, waves, and validation runs. This cannot be undone.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmDeleteId(null)}>Cancel</Button>
            <Button
              variant="destructive"
              disabled={deleteMutation.isPending}
              onClick={() => confirmDeleteId && deleteMutation.mutate(confirmDeleteId)}
            >
              {deleteMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
              Delete Project
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
