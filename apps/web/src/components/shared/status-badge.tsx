import { Badge } from "@/components/ui/badge";
import type { ConnectionStatus, ProjectStatus, ScanStatus, JobStatus, IssueSeverity, MappingStatus } from "@/types";

export function ConnectionStatusBadge({ status }: { status: ConnectionStatus }) {
  const map: Record<ConnectionStatus, { label: string; variant: "success" | "warning" | "destructive" | "secondary" }> = {
    connected: { label: "Connected", variant: "success" },
    pending: { label: "Pending", variant: "warning" },
    failed: { label: "Failed", variant: "destructive" },
    unverified: { label: "Unverified", variant: "secondary" },
  };
  const { label, variant } = map[status];
  return <Badge variant={variant}>{label}</Badge>;
}

export function ProjectStatusBadge({ status }: { status: ProjectStatus }) {
  const map: Record<ProjectStatus, { label: string; variant: "success" | "info" | "secondary" | "warning" }> = {
    active: { label: "Active", variant: "success" },
    draft: { label: "Draft", variant: "secondary" },
    paused: { label: "Paused", variant: "warning" },
    completed: { label: "Completed", variant: "info" },
  };
  const { label, variant } = map[status];
  return <Badge variant={variant}>{label}</Badge>;
}

export function ScanStatusBadge({ status }: { status: ScanStatus }) {
  const map: Record<ScanStatus, { label: string; variant: "success" | "info" | "secondary" | "destructive" }> = {
    completed: { label: "Completed", variant: "success" },
    running: { label: "Running", variant: "info" },
    queued: { label: "Queued", variant: "secondary" },
    failed: { label: "Failed", variant: "destructive" },
  };
  const { label, variant } = map[status];
  return <Badge variant={variant}>{label}</Badge>;
}

export function JobStatusBadge({ status }: { status: JobStatus }) {
  const map: Record<JobStatus, { label: string; variant: "success" | "info" | "secondary" | "destructive" | "warning" }> = {
    completed: { label: "Completed", variant: "success" },
    running: { label: "Running", variant: "info" },
    queued: { label: "Queued", variant: "secondary" },
    failed: { label: "Failed", variant: "destructive" },
    cancelled: { label: "Cancelled", variant: "warning" },
  };
  const { label, variant } = map[status];
  return <Badge variant={variant}>{label}</Badge>;
}

export function SeverityBadge({ severity }: { severity: IssueSeverity }) {
  const map: Record<IssueSeverity, { label: string; variant: "destructive" | "warning" | "info" }> = {
    blocker: { label: "Blocker", variant: "destructive" },
    warning: { label: "Warning", variant: "warning" },
    info: { label: "Info", variant: "info" },
  };
  const { label, variant } = map[severity];
  return <Badge variant={variant}>{label}</Badge>;
}

export function MappingStatusBadge({ status }: { status: MappingStatus }) {
  const map: Record<MappingStatus, { label: string; variant: "success" | "destructive" | "secondary" | "warning" }> = {
    mapped: { label: "Mapped", variant: "success" },
    unmapped: { label: "Unmapped", variant: "secondary" },
    conflict: { label: "Conflict", variant: "destructive" },
    skipped: { label: "Skipped", variant: "warning" },
  };
  const { label, variant } = map[status];
  return <Badge variant={variant}>{label}</Badge>;
}
