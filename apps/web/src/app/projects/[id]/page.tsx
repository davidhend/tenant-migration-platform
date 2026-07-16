"use client";

import { useState, useEffect, useCallback } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useParams } from "next/navigation";
import Link from "next/link";
import {
  ArrowRight, Play, Loader2, RefreshCw, RotateCcw, XCircle,
  AlertTriangle, CheckCircle2, Clock, Upload, Download,
  Layers, Calendar, Plus, Trash2, ShieldCheck, ShieldAlert, AlertCircle,
  Zap, CheckCircle, Info, Copy, Terminal, ClipboardCheck, Minus, HardDrive, Globe,
  Pencil, Check, X,
} from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Progress } from "@/components/ui/progress";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter,
  DialogHeader, DialogTitle, DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { ScanStatusBadge, JobStatusBadge, MappingStatusBadge } from "@/components/shared/status-badge";
import { SeverityBadge } from "@/components/shared/status-badge";
import { projectsApi, scansApi, jobsApi, identityMapsApi, auditApi, wavesApi, validationApi, domainRulesApi, mailboxBatchesApi, contentMigrationsApi, userMigrationsApi, domainCutoverApi, setupApi } from "@/lib/api";
import { formatDateTime, relativeTime, formatNumber, formatBytes } from "@/lib/utils";
import type { ScanType, WaveStatus, DomainRuleType, CreateDomainRuleDto, BatchStatus, ContentJobType, ContentMigrationJob, MailboxBatch, UserMigrationBatchStatus, CheckStatus, DomainCutoverPhase } from "@/types";

// Ordered domain-cutover phases for the stepper. The worker auto-advances until
// it reaches a PAUSE phase that needs the admin to make DNS changes, then stops.
const CUTOVER_PHASES: { key: DomainCutoverPhase; label: string }[] = [
  { key: "created", label: "Created" },
  { key: "cleaningSource", label: "Clean source" },
  { key: "removingDomain", label: "Remove domain" },
  { key: "waitingForRelease", label: "Wait for release" },
  { key: "awaitingDnsVerification", label: "Verify DNS" },
  { key: "verifyingDomain", label: "Verifying" },
  { key: "assigningUsers", label: "Assign users" },
  { key: "awaitingMxUpdate", label: "Update MX" },
  { key: "completed", label: "Completed" },
];
const CUTOVER_PAUSE_PHASES: DomainCutoverPhase[] = ["awaitingDnsVerification", "awaitingMxUpdate"];
import { PieChart, Pie, Cell, ResponsiveContainer, Tooltip } from "recharts";
import { useMigrationHub } from "@/hooks/useMigrationHub";

export default function ProjectDetailPage() {
  const { id } = useParams<{ id: string }>();
  const qc = useQueryClient();
  const [scanDialogOpen, setScanDialogOpen] = useState(false);
  const [scanType, setScanType] = useState<ScanType>("full");

  // Identity map inline edit state
  const [editingMapId, setEditingMapId] = useState<string | null>(null);
  const [editTargetUpn, setEditTargetUpn] = useState("");

  // Wave planner state
  const [waveDialogOpen, setWaveDialogOpen] = useState(false);
  const [waveName, setWaveName] = useState("");
  const [waveDescription, setWaveDescription] = useState("");
  const [waveOrder, setWaveOrder] = useState(1);
  const [waveSchedule, setWaveSchedule] = useState("");

  // Validation state
  const [validationDialogOpen, setValidationDialogOpen] = useState(false);
  const [validationName, setValidationName] = useState("");
  const [validationWaveFilter, setValidationWaveFilter] = useState("");
  const [expandedRunId, setExpandedRunId] = useState<string | null>(null);

  // Domain cutover state
  const [cutoverDialogOpen, setCutoverDialogOpen] = useState(false);
  const [cutoverDomainName, setCutoverDomainName] = useState("");

  // Exchange setup state
  const [exchangeSetupDialogOpen, setExchangeSetupDialogOpen] = useState(false);
  const [exchangeSetupResult, setExchangeSetupResult] = useState<Awaited<ReturnType<typeof projectsApi.setupExchange>> | null>(null);

  // Dependency check state
  const [depCheckEnabled, setDepCheckEnabled] = useState(false);

  const { data: project, isLoading: projectLoading } = useQuery({
    queryKey: ["projects", id],
    queryFn: () => projectsApi.get(id),
  });
  // SignalR is the primary live-update channel, but group membership can be
  // lost on reconnect and sockets do drop — long-running work must not appear
  // frozen, so these queries also poll while anything is in an active state.
  const { data: scans, isLoading: scansLoading } = useQuery({
    queryKey: ["scans", id],
    queryFn: () => scansApi.list(id),
    refetchInterval: (query) =>
      query.state.data?.some((s) => s.status === "queued" || s.status === "running") ? 20_000 : false,
  });
  const { data: jobs, isLoading: jobsLoading } = useQuery({
    queryKey: ["jobs", id],
    queryFn: () => jobsApi.list(id),
    refetchInterval: (query) =>
      query.state.data?.some((j) => j.status === "queued" || j.status === "running") ? 20_000 : false,
  });
  const { data: identityMaps, isLoading: mapsLoading } = useQuery({
    queryKey: ["identity-maps", id],
    queryFn: () => identityMapsApi.list(id),
  });
  // Setup config completeness — drives the attention nudge on the Setup button.
  // Only the machine-verifiable facts count (app + secret + both tenant
  // credentials); the wizard's manual steps are what the button leads to.
  const { data: setupPlan } = useQuery({
    queryKey: ["setup-plan", id],
    queryFn: () => setupApi.plan(id),
    staleTime: 60_000,
  });
  const configIncomplete =
    !!setupPlan &&
    (!setupPlan.migrationAppId ||
      !setupPlan.clientSecretConfigured ||
      !setupPlan.sourceTenant?.credentialConfigured ||
      !setupPlan.targetTenant?.credentialConfigured);
  // On an already-configured tenant pair every config fact is green from day
  // one, so a NEW project would never nudge — also nudge until this project's
  // setup page has been opened once (recorded in localStorage by that page).
  // Starts true (no flash during hydration), resolved in the effect.
  const [visitedSetup, setVisitedSetup] = useState(true);
  useEffect(() => {
    setVisitedSetup(localStorage.getItem(`setup-visited-${id}`) === "1");
  }, [id]);
  const setupIncomplete = configIncomplete || !visitedSetup;
  const { data: auditPage, isLoading: auditLoading } = useQuery({
    queryKey: ["audit", id],
    queryFn: () => auditApi.list(),
  });
  const { data: waves, isLoading: wavesLoading } = useQuery({
    queryKey: ["waves", id],
    queryFn: () => wavesApi.list(id),
  });
  const { data: validationRuns, isLoading: validationLoading } = useQuery({
    queryKey: ["validations", id],
    queryFn: () => validationApi.list(id),
    refetchInterval: (query) =>
      query.state.data?.some((r) => r.status === "pending" || r.status === "running") ? 20_000 : false,
  });
  const { data: expandedChecks } = useQuery({
    queryKey: ["validation-checks", expandedRunId],
    queryFn: () => validationApi.getChecks(id, expandedRunId!),
    enabled: !!expandedRunId,
  });
  const { data: domainRules, isLoading: rulesLoading } = useQuery({
    queryKey: ["domain-rules", id],
    queryFn: () => domainRulesApi.list(id),
  });
  const { data: mailboxBatches, isLoading: batchesLoading } = useQuery({
    queryKey: ["mailbox-batches", id],
    queryFn: () => mailboxBatchesApi.list(id),
    // "synced" batches are parked awaiting cutover but MRS keeps incremental-syncing them.
    refetchInterval: (query) =>
      query.state.data?.some((b) =>
        b.status === "validating" || b.status === "syncing" || b.status === "synced" || b.status === "completing"
      ) ? 20_000 : false,
  });
  // UPNs covered by any mailbox migration entry (except skipped ones). These
  // users must NOT go through user migration: the mailbox flow provisions the
  // target account itself (New-MailUser), and a pre-created member account at
  // the same UPN breaks it. The API enforces this at start; this set lets the
  // create dialog exclude them up front.
  const { data: mailboxCoveredUpns } = useQuery({
    queryKey: ["mailbox-covered-upns", id, mailboxBatches?.map((b) => b.id).join(",") ?? ""],
    enabled: !!mailboxBatches,
    queryFn: async () => {
      const perBatch = await Promise.all(
        (mailboxBatches ?? []).map((b) => mailboxBatchesApi.getEntries(id, b.id)),
      );
      const upns = new Set<string>();
      for (const entries of perBatch)
        for (const e of entries) {
          if (e.status === "skipped") continue;
          if (e.sourceUpn) upns.add(e.sourceUpn.toLowerCase());
          if (e.targetUpn) upns.add(e.targetUpn.toLowerCase());
        }
      return upns;
    },
  });
  const { data: contentJobs, isLoading: contentJobsLoading } = useQuery({
    queryKey: ["content-jobs", id],
    queryFn: () => contentMigrationsApi.list(id),
    refetchInterval: (query) =>
      query.state.data?.some((j) =>
        j.status === "provisioning" || j.status === "scheduled" || j.status === "running"
      ) ? 20_000 : false,
  });
  const { data: userMigrationBatches, isLoading: userMigrationBatchesLoading } = useQuery({
    queryKey: ["user-migrations", id],
    queryFn: () => userMigrationsApi.list(id),
    refetchInterval: (query) =>
      query.state.data?.some((b) => b.status === "provisioning") ? 20_000 : false,
  });
  const { data: cutoverJobs, isLoading: cutoverJobsLoading } = useQuery({
    queryKey: ["domain-cutover", id],
    queryFn: () => domainCutoverApi.list(id),
    // Poll while any job is mid-workflow (including the pause phases, so the UI
    // reflects the worker resuming after Continue). No SignalR event for cutover.
    refetchInterval: (query) =>
      query.state.data?.some((j) => j.phase !== "completed" && j.phase !== "failed") ? 15_000 : false,
  });

  // Cross-tenant sync app discovery — only fetched when at least one user
  // batch has selected the CrossTenantSync strategy. Drives both the inline
  // status indicator in the Create User Batch dialog and the discovery card
  // on the project overview tab.
  const hasCtsBatch = !!userMigrationBatches?.some((b) => b.strategy === "crossTenantSync");
  const {
    data: ctsDiscovery,
    isFetching: ctsDiscoveryFetching,
    refetch: refetchCtsDiscovery,
  } = useQuery({
    queryKey: ["cts-discovery", id],
    queryFn: () => projectsApi.crossTenantSyncStatus(id),
    enabled: hasCtsBatch,
    staleTime: 60_000,
  });

  const { data: depCheck, isFetching: depCheckFetching, refetch: runDepCheck } = useQuery({
    queryKey: ["dep-check", id],
    queryFn: () => projectsApi.dependencyCheck(id),
    enabled: depCheckEnabled,
    staleTime: 0,
  });

  const setupExchangeMutation = useMutation({
    mutationFn: () => projectsApi.setupExchange(id),
    onSuccess: (result) => {
      setExchangeSetupResult(result);
      setExchangeSetupDialogOpen(true);
      if (!result.warnings || result.warnings.length === 0) {
        toast.success("Exchange Online setup completed.");
      } else {
        toast.warning("Exchange setup completed with warnings.");
      }
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const startScanMutation = useMutation({
    mutationFn: () => scansApi.start(project?.sourceTenantId ?? "", id, scanType),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["scans", id] });
      toast.success("Scan started");
      setScanDialogOpen(false);
    },
    onError: () => toast.error("Failed to start scan"),
  });

  const autoMapMutation = useMutation({
    mutationFn: () => identityMapsApi.autoMap(id),
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ["identity-maps", id] });
      toast.success(`Auto-mapped: ${result.mapped} users, ${result.conflicts} conflicts, ${result.unmapped} unmapped`);
    },
    onError: () => toast.error("Auto-mapping failed"),
  });

  const updateMapMutation = useMutation({
    mutationFn: ({ mapId, targetUpn }: { mapId: string; targetUpn: string }) =>
      identityMapsApi.update(id, mapId, targetUpn),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["identity-maps", id] });
      setEditingMapId(null);
      toast.success("Mapping updated");
    },
    onError: () => toast.error("Failed to update mapping"),
  });

  const retryJobMutation = useMutation({
    mutationFn: jobsApi.retry,
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["jobs", id] }); toast.success("Job re-queued"); },
    // Non-scan job types answer 422 pointing at the workload-specific retry endpoint.
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to retry job";
      toast.error(msg, { duration: 8000 });
    },
  });
  const cancelJobMutation = useMutation({
    mutationFn: jobsApi.cancel,
    // Scan cancel is 202: the worker flips the status when it observes the request.
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["jobs", id] }); toast.success("Job cancellation requested"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to cancel job";
      toast.error(msg, { duration: 8000 });
    },
  });

  const createWaveMutation = useMutation({
    mutationFn: () => wavesApi.create(id, {
      name: waveName,
      description: waveDescription || undefined,
      order: waveOrder,
      scheduledStartAt: waveSchedule ? new Date(waveSchedule).toISOString() : undefined,
    }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["waves", id] });
      toast.success("Wave created");
      setWaveDialogOpen(false);
      setWaveName(""); setWaveDescription(""); setWaveOrder(1); setWaveSchedule("");
    },
    onError: () => toast.error("Failed to create wave"),
  });
  const startWaveMutation = useMutation({
    mutationFn: (waveId: string) => wavesApi.start(id, waveId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("Wave started"); },
    onError: () => toast.error("Failed to start wave"),
  });
  const cancelWaveMutation = useMutation({
    mutationFn: (waveId: string) => wavesApi.cancel(id, waveId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("Wave cancelled"); },
    onError: () => toast.error("Failed to cancel wave"),
  });
  const deleteWaveMutation = useMutation({
    mutationFn: (waveId: string) => wavesApi.delete(id, waveId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("Wave deleted"); },
    onError: () => toast.error("Failed to delete wave"),
  });

  const createValidationMutation = useMutation({
    mutationFn: () => validationApi.create(id, {
      name: validationName || undefined,
      waveId: validationWaveFilter || undefined,
    }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["validations", id] });
      toast.success("Validation run started");
      setValidationDialogOpen(false);
      setValidationName(""); setValidationWaveFilter("");
    },
    onError: () => toast.error("Failed to start validation"),
  });

  const deleteValidationMutation = useMutation({
    mutationFn: (runId: string) => validationApi.delete(id, runId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["validations", id] }); toast.success("Validation run deleted"); },
    onError: () => toast.error("Failed to delete validation run"),
  });

  // ── Domain rules state ───────────────────────────────────────────────────────
  const [ruleDialogOpen, setRuleDialogOpen] = useState(false);
  const [ruleForm, setRuleForm] = useState<CreateDomainRuleDto>({
    ruleType: "directMap", sourcePattern: "", targetPattern: "", priority: 10, isEnabled: true, description: "",
  });
  const [previewUpns, setPreviewUpns] = useState("");
  const [previewResults, setPreviewResults] = useState<{ sourceUpn: string; transformedUpn: string; wasTransformed: boolean }[]>([]);

  const createRuleMutation = useMutation({
    mutationFn: (dto: CreateDomainRuleDto) => domainRulesApi.create(id, dto),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["domain-rules", id] });
      toast.success("Rule created");
      setRuleDialogOpen(false);
      setRuleForm({ ruleType: "directMap", sourcePattern: "", targetPattern: "", priority: 10, isEnabled: true, description: "" });
    },
    onError: () => toast.error("Failed to create rule"),
  });

  const deleteRuleMutation = useMutation({
    mutationFn: (ruleId: string) => domainRulesApi.delete(id, ruleId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["domain-rules", id] }); toast.success("Rule deleted"); },
    onError: () => toast.error("Failed to delete rule"),
  });

  const previewMutation = useMutation({
    mutationFn: (upns: string[]) => domainRulesApi.preview(id, upns),
    onSuccess: (results) => setPreviewResults(results),
    onError: () => toast.error("Preview failed"),
  });

  // ── Entra user sync state ─────────────────────────────────────────────────────
  const [userBatchDialogOpen, setUserBatchDialogOpen] = useState(false);
  const [userBatchName, setUserBatchName] = useState("");
  const [userBatchStrategy, setUserBatchStrategy] = useState<"directGraph" | "crossTenantSync">("crossTenantSync");

  // ── Mailbox batch state ───────────────────────────────────────────────────────
  const [batchDialogOpen, setBatchDialogOpen] = useState(false);
  const [batchTargetFolder, setBatchTargetFolder] = useState("");
  const [batchName, setBatchName] = useState("");
  const [batchStrategy, setBatchStrategy] = useState<"graphCopy" | "nativeMrs">("nativeMrs");
  const [assignDialogWaveId, setAssignDialogWaveId] = useState<string | null>(null);
  const [selectedBatchIds, setSelectedBatchIds] = useState<string[]>([]);
  const [assignContentDialogWaveId, setAssignContentDialogWaveId] = useState<string | null>(null);
  const [selectedContentJobIds, setSelectedContentJobIds] = useState<string[]>([]);
  const [assignUserBatchDialogWaveId, setAssignUserBatchDialogWaveId] = useState<string | null>(null);
  const [selectedUserBatchIds, setSelectedUserBatchIds] = useState<string[]>([]);

  // ── Content migration state ───────────────────────────────────────────────────
  const [contentDialogOpen, setContentDialogOpen] = useState(false);
  const [contentJobName, setContentJobName] = useState("");
  const [contentJobType, setContentJobType] = useState<ContentJobType>("sharePoint");
  const [contentSourceUrl, setContentSourceUrl] = useState("");
  const [contentTargetUrl, setContentTargetUrl] = useState("");
  const [contentOwnerUpn, setContentOwnerUpn] = useState("");
  const [contentTargetOwnerUpn, setContentTargetOwnerUpn] = useState("");
  const [editContentJob, setEditContentJob] = useState<ContentMigrationJob | null>(null);
  const [editContentName, setEditContentName] = useState("");
  const [editContentSourceUrl, setEditContentSourceUrl] = useState("");
  const [editContentTargetUrl, setEditContentTargetUrl] = useState("");
  const [editContentOwnerUpn, setEditContentOwnerUpn] = useState("");
  const [editContentTargetOwnerUpn, setEditContentTargetOwnerUpn] = useState("");

  const createBatchMutation = useMutation({
    mutationFn: () => {
      const mapped = identityMaps?.filter((m) => m.status === "mapped" && m.targetUpn) ?? [];
      return mailboxBatchesApi.create(id, {
        name: batchName,
        mailboxes: mapped.map((m) => ({ sourceUpn: m.sourceUpn, targetUpn: m.targetUpn! })),
        // Native MRS recreates source folder hierarchy via EXO; only honoured for graphCopy.
        targetFolderName: batchStrategy === "graphCopy" && batchTargetFolder.trim()
          ? batchTargetFolder.trim()
          : undefined,
        strategy: batchStrategy,
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
      toast.success("Batch created");
      setBatchDialogOpen(false);
      setBatchName("");
      setBatchTargetFolder("");
      setBatchStrategy("nativeMrs");
    },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to create batch";
      toast.error(msg, { duration: 8000 });
    },
  });

  const startBatchMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.start(id, batchId),
    onMutate: async (batchId: string) => {
      await qc.cancelQueries({ queryKey: ["mailbox-batches", id] });
      const prev = qc.getQueryData<MailboxBatch[]>(["mailbox-batches", id]);
      qc.setQueryData<MailboxBatch[]>(["mailbox-batches", id], (old) =>
        old?.map((b) => b.id === batchId ? { ...b, status: "syncing" as const } : b));
      return { prev };
    },
    onSuccess: (batch) => {
      qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
      const lic = batch?.licenseAssignment;
      if (lic?.attempted && !lic.skuFound) {
        toast.warning(
          "Batch started, but no Cross Tenant User Data Migration SKU was found — purchase seats or assign licenses manually, or moves will stall in NeedsApproval.",
          { duration: 12000 });
      } else if (lic?.attempted && lic.failed > 0) {
        toast.warning(
          `Batch started, but license auto-assignment failed for ${lic.failed} user(s)` +
          (lic.failures?.[0] ? ` (${lic.failures[0].upn}: ${lic.failures[0].reason})` : "") +
          ". Assign the migration license manually or the move will stall in NeedsApproval.",
          { duration: 12000 });
      } else {
        toast.success("Batch started");
      }
    },
    onError: (err: Error, _batchId, context) => {
      if (context?.prev) qc.setQueryData(["mailbox-batches", id], context.prev);
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to start batch";
      toast.error(msg, { duration: 8000 });
    },
  });

  const stopBatchMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.stop(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["mailbox-batches", id] }); toast.success("Batch stopped"); },
    onError: () => toast.error("Failed to stop batch"),
  });

  const completeBatchMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.complete(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["mailbox-batches", id] }); toast.success("Cutover started — batch is completing"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to complete batch";
      toast.error(msg, { duration: 8000 });
    },
  });

  const retryBatchMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.retry(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["mailbox-batches", id] }); toast.success("Batch retry queued"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to retry batch";
      toast.error(msg, { duration: 8000 });
    },
  });

  const skipFailuresMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.skipFailures(id, batchId),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
      toast.success("Failed mailboxes reclassified as skipped");
    },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to reclassify entries";
      toast.error(msg, { duration: 8000 });
    },
  });

  const deleteMailboxBatchMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.delete(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["mailbox-batches", id] }); qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("Batch deleted"); },
    onError: () => toast.error("Failed to delete batch"),
  });

  const resetMailboxTargetMutation = useMutation({
    mutationFn: (batchId: string) => mailboxBatchesApi.resetTarget(id, batchId),
    onSuccess: (res) => {
      qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
      const summary =
        `Reset target — MoveRequests×${res.moveRequestsRemoved}, ` +
        `MailUsers×${res.mailUsersRemoved}, soft-deleted purged×${res.softDeletedMailUsersPurged}` +
        (res.exoBatchRemoved ? ", EXO batch removed" : "") +
        (res.warnings.length > 0 ? ` (${res.warnings.length} warning${res.warnings.length === 1 ? "" : "s"})` : "");
      if (res.warnings.length > 0) toast.warning(summary, { duration: 10000 });
      else toast.success(summary, { duration: 8000 });
    },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to reset target";
      toast.error(msg, { duration: 8000 });
    },
  });

  const createCutoverMutation = useMutation({
    mutationFn: () => domainCutoverApi.create(id, cutoverDomainName.trim()),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["domain-cutover", id] });
      toast.success("Domain cutover job created");
      setCutoverDialogOpen(false);
      setCutoverDomainName("");
    },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to create cutover job";
      toast.error(msg, { duration: 8000 });
    },
  });

  const startCutoverMutation = useMutation({
    mutationFn: (jobId: string) => domainCutoverApi.start(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["domain-cutover", id] }); toast.success("Cutover started"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to start cutover";
      toast.error(msg, { duration: 8000 });
    },
  });

  const continueCutoverMutation = useMutation({
    mutationFn: (jobId: string) => domainCutoverApi.continue(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["domain-cutover", id] }); toast.success("Cutover resumed"); },
    onError: (err: Error) => {
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to resume cutover";
      toast.error(msg, { duration: 8000 });
    },
  });

  const deleteCutoverMutation = useMutation({
    mutationFn: (jobId: string) => domainCutoverApi.delete(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["domain-cutover", id] }); toast.success("Cutover job deleted"); },
    onError: () => toast.error("Failed to delete cutover job"),
  });

  const assignBatchesMutation = useMutation({
    mutationFn: ({ waveId, batchIds }: { waveId: string; batchIds: string[] }) =>
      wavesApi.assignBatches(id, waveId, batchIds),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["waves", id] });
      toast.success("Batches assigned");
      setAssignDialogWaveId(null);
      setSelectedBatchIds([]);
    },
    onError: () => toast.error("Failed to assign batches"),
  });

  const assignContentJobsMutation = useMutation({
    mutationFn: ({ waveId, jobIds }: { waveId: string; jobIds: string[] }) =>
      wavesApi.assignContentJobs(id, waveId, jobIds),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["waves", id] });
      toast.success("Content jobs assigned");
      setAssignContentDialogWaveId(null);
      setSelectedContentJobIds([]);
    },
    onError: () => toast.error("Failed to assign content jobs"),
  });

  const assignUserBatchesMutation = useMutation({
    mutationFn: ({ waveId, batchIds }: { waveId: string; batchIds: string[] }) =>
      wavesApi.assignUserBatches(id, waveId, batchIds),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["waves", id] });
      toast.success("User batches assigned");
      setAssignUserBatchDialogWaveId(null);
      setSelectedUserBatchIds([]);
    },
    onError: () => toast.error("Failed to assign user batches"),
  });

  const createContentJobMutation = useMutation({
    mutationFn: () => contentMigrationsApi.create(id, {
      name: contentJobName,
      jobType: contentJobType,
      items: [{ sourceUrl: contentSourceUrl, targetUrl: contentTargetUrl, ownerUpn: contentOwnerUpn || undefined, targetOwnerUpn: contentTargetOwnerUpn || undefined }],
    }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["content-jobs", id] });
      toast.success("Content job created");
      setContentDialogOpen(false);
      setContentJobName(""); setContentSourceUrl(""); setContentTargetUrl(""); setContentOwnerUpn(""); setContentTargetOwnerUpn("");
    },
    onError: () => toast.error("Failed to create content job"),
  });

  const startContentJobMutation = useMutation({
    mutationFn: (jobId: string) => contentMigrationsApi.start(id, jobId),
    onMutate: async (jobId: string) => {
      await qc.cancelQueries({ queryKey: ["content-jobs", id] });
      const prev = qc.getQueryData<ContentMigrationJob[]>(["content-jobs", id]);
      qc.setQueryData<ContentMigrationJob[]>(["content-jobs", id], (old) =>
        old?.map((j) => j.id === jobId ? { ...j, status: "running" as const } : j));
      return { prev };
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["content-jobs", id] });
      toast.success("Content job started");
    },
    onError: (err: Error, _jobId, context) => {
      if (context?.prev) qc.setQueryData(["content-jobs", id], context.prev);
      const msg = err?.message?.replace(/^API error \d+:\s*/, "") || "Failed to start content job";
      toast.error(msg, { duration: 8000 });
    },
  });

  const updateContentJobMutation = useMutation({
    mutationFn: () => {
      if (!editContentJob) throw new Error("No job to update");
      return contentMigrationsApi.update(id, editContentJob.id, {
        name: editContentName,
        items: [{ sourceUrl: editContentSourceUrl, targetUrl: editContentTargetUrl, ownerUpn: editContentOwnerUpn || undefined, targetOwnerUpn: editContentTargetOwnerUpn || undefined }],
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["content-jobs", id] });
      toast.success("Content job updated");
      setEditContentJob(null);
    },
    onError: (e: Error) => toast.error(e.message),
  });

  const pauseContentJobMutation = useMutation({
    mutationFn: (jobId: string) => contentMigrationsApi.pause(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["content-jobs", id] }); toast.success("Content job paused"); },
    onError: () => toast.error("Failed to pause content job"),
  });

  const resumeContentJobMutation = useMutation({
    mutationFn: (jobId: string) => contentMigrationsApi.resume(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["content-jobs", id] }); toast.success("Content job resumed"); },
    onError: () => toast.error("Failed to resume content job"),
  });

  const cancelContentJobMutation = useMutation({
    mutationFn: (jobId: string) => contentMigrationsApi.cancel(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["content-jobs", id] }); toast.success("Content job cancelled"); },
    onError: () => toast.error("Failed to cancel content job"),
  });

  const deleteContentJobMutation = useMutation({
    mutationFn: (jobId: string) => contentMigrationsApi.delete(id, jobId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["content-jobs", id] }); qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("Content job deleted"); },
    onError: () => toast.error("Failed to delete content job"),
  });

  // Mapped identity pairs split by mailbox coverage: users whose mailbox
  // migrates are provisioned by the mailbox flow (MailUser) and must be kept
  // OUT of user migration batches. Mirrors the API's start-time 422 gate.
  const mappedIdentityPairs = identityMaps?.filter((m) => m.status === "mapped" && m.targetUpn) ?? [];
  const userBatchEligiblePairs = mappedIdentityPairs.filter(
    (m) =>
      !mailboxCoveredUpns?.has(m.sourceUpn.toLowerCase()) &&
      !mailboxCoveredUpns?.has(m.targetUpn!.toLowerCase()),
  );
  const userBatchExcludedCount = mappedIdentityPairs.length - userBatchEligiblePairs.length;

  const createUserBatchMutation = useMutation({
    mutationFn: () => {
      return userMigrationsApi.create(id, {
        name: userBatchName,
        strategy: userBatchStrategy,
        users: userBatchEligiblePairs.map((m) => ({ sourceUpn: m.sourceUpn, targetUpn: m.targetUpn! })),
      });
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ["user-migrations", id] });
      toast.success(
        userBatchExcludedCount > 0
          ? `User migration batch created (${userBatchExcludedCount} user(s) excluded — their mailbox migration provisions the account)`
          : "User migration batch created",
      );
      setUserBatchDialogOpen(false);
      setUserBatchName("");
      setUserBatchStrategy("crossTenantSync");
    },
    onError: (e: Error) => toast.error(e.message || "Failed to create user migration batch"),
  });

  const startUserBatchMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.start(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); toast.success("User migration batch started"); },
    onError: (e: Error) => toast.error(e.message || "Failed to start user migration batch"),
  });

  const stopUserBatchMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.stop(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); toast.success("User migration batch stopped"); },
    onError: () => toast.error("Failed to stop user migration batch"),
  });

  const retryFailedUserBatchMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.retryFailed(id, batchId),
    onSuccess: (data) => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); toast.success(data.message); },
    onError: (e: Error) => toast.error(e.message || "Failed to retry failed entries"),
  });

  const retryUserBatchMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.retry(id, batchId),
    onSuccess: (data) => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); toast.success(data.message); },
    onError: (e: Error) => toast.error(e.message || "Failed to retry batch"),
  });

  const skipUserFailuresMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.skipFailures(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); toast.success("Failed entries reclassified as skipped"); },
    onError: () => toast.error("Failed to skip failed entries"),
  });

  const deleteUserBatchMutation = useMutation({
    mutationFn: (batchId: string) => userMigrationsApi.delete(id, batchId),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ["user-migrations", id] }); qc.invalidateQueries({ queryKey: ["waves", id] }); toast.success("User migration batch deleted"); },
    onError: () => toast.error("Failed to delete user migration batch"),
  });

  const latestSourceScan = scans?.find((s) => s.tenantId === project?.sourceTenantId && s.status === "completed");
  const { data: scanIssues } = useQuery({
    queryKey: ["scan-issues", latestSourceScan?.id],
    queryFn: () => scansApi.getIssues(latestSourceScan!.id),
    enabled: !!latestSourceScan,
  });
  const { data: scannedSites } = useQuery({
    queryKey: ["scan-sites", latestSourceScan?.id],
    queryFn: () => scansApi.getSites(latestSourceScan!.id),
    enabled: !!latestSourceScan,
  });
  const { data: scannedOneDrives } = useQuery({
    queryKey: ["scan-onedrive", latestSourceScan?.id],
    queryFn: () => scansApi.getOneDrive(latestSourceScan!.id),
    enabled: !!latestSourceScan,
  });

  // ── SignalR real-time updates ────────────────────────────────────────────────
  const hub = useMigrationHub();

  const handleScanProgress = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["scans", id] });
  }, [qc, id]);

  const handleJobProgress = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["jobs", id] });
  }, [qc, id]);

  const handleMailboxBatchProgress = useCallback(() => {
    // Mailbox batch progress — invalidate jobs (which carry batch progress)
    // and any open mailbox batch queries.
    qc.invalidateQueries({ queryKey: ["jobs", id] });
    qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
  }, [qc, id]);

  const handleContentJobProgress = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["jobs", id] });
    qc.invalidateQueries({ queryKey: ["content-jobs", id] });
  }, [qc, id]);

  const handleContentSourceSelect = useCallback((sourceUrl: string) => {
    setContentSourceUrl(sourceUrl);
    const srcDomain = project?.sourceTenant?.onMicrosoftDomain;
    const tgtDomain = project?.targetTenant?.onMicrosoftDomain;
    if (srcDomain && tgtDomain) {
      setContentTargetUrl(sourceUrl.replaceAll(srcDomain, tgtDomain));
    } else if (srcDomain) {
      // Target domain unknown — insert a placeholder the user must replace
      setContentTargetUrl(sourceUrl.replaceAll(srcDomain, "<target-domain>"));
    }
  }, [project]);

  const handleValidationProgress = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["validations", id] });
    if (expandedRunId) qc.invalidateQueries({ queryKey: ["validation-checks", expandedRunId] });
  }, [qc, id, expandedRunId]);

  const handleUserMigrationProgress = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["user-migrations", id] });
  }, [qc, id]);

  // After an automatic reconnect the hub has re-joined our groups, but any
  // events emitted while the socket was down are gone — refetch everything live.
  const handleReconnected = useCallback(() => {
    qc.invalidateQueries({ queryKey: ["scans", id] });
    qc.invalidateQueries({ queryKey: ["jobs", id] });
    qc.invalidateQueries({ queryKey: ["mailbox-batches", id] });
    qc.invalidateQueries({ queryKey: ["content-jobs", id] });
    qc.invalidateQueries({ queryKey: ["user-migrations", id] });
    qc.invalidateQueries({ queryKey: ["validations", id] });
  }, [qc, id]);

  // Connect once on mount and join the project group; disconnect on unmount.
  useEffect(() => {
    let mounted = true;

    hub.connect().then(() => {
      if (!mounted) return;
      hub.joinProject(id);
      hub.onReconnected(handleReconnected);
      hub.on("ScanProgress", handleScanProgress);
      hub.on("JobProgress", handleJobProgress);
      hub.on("MailboxBatchProgress", handleMailboxBatchProgress);
      hub.on("ContentJobProgress", handleContentJobProgress);
      hub.on("ValidationProgress", handleValidationProgress);
      hub.on("UserMigrationProgress", handleUserMigrationProgress);
    });

    return () => {
      mounted = false;
      hub.offReconnected(handleReconnected);
      hub.off("ScanProgress", handleScanProgress);
      hub.off("JobProgress", handleJobProgress);
      hub.off("MailboxBatchProgress", handleMailboxBatchProgress);
      hub.off("ContentJobProgress", handleContentJobProgress);
      hub.off("ValidationProgress", handleValidationProgress);
      hub.off("UserMigrationProgress", handleUserMigrationProgress);
      hub.leaveProject(id);
      hub.disconnect();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id]);

  if (projectLoading) return <div className="space-y-4">{Array.from({ length: 4 }).map((_, i) => <Skeleton key={i} className="h-24 w-full" />)}</div>;
  if (!project) return <div className="py-20 text-center text-muted-foreground">Project not found.</div>;

  const score = latestSourceScan?.summary?.readinessScore ?? 0;
  const scoreColor = score >= 80 ? "#22c55e" : score >= 60 ? "#f59e0b" : "#ef4444";
  const pieData = [{ value: score }, { value: 100 - score }];

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <div className="flex items-center gap-2 text-sm text-muted-foreground mb-1">
            <Link href="/projects" className="hover:text-foreground">Projects</Link>
            <span>/</span>
            <span className="text-foreground">{project.name}</span>
          </div>
          <h1 className="text-2xl font-bold tracking-tight">{project.name}</h1>
          <div className="flex items-center gap-2 mt-1 text-sm text-muted-foreground">
            <span>{project.sourceTenant?.displayName ?? project.sourceTenantId}</span>
            <ArrowRight className="h-3 w-3" />
            <span>{project.targetTenant?.displayName ?? project.targetTenantId}</span>
          </div>
        </div>
        <div className="flex items-center gap-2">
        {setupIncomplete && (
          <span className="flex items-center gap-1.5 text-sm font-medium text-primary" role="status">
            Finish setup
            <ArrowRight className="h-4 w-4 animate-nudge-right" aria-hidden />
          </span>
        )}
        <Button variant="outline" asChild className={setupIncomplete ? "ring-2 ring-primary/60" : undefined}>
          <Link href={`/projects/${id}/setup`}><ClipboardCheck className="mr-2 h-4 w-4" />Setup</Link>
        </Button>
        <Dialog open={scanDialogOpen} onOpenChange={setScanDialogOpen}>
          <DialogTrigger asChild>
            <Button><Play className="mr-2 h-4 w-4" />Run Scan</Button>
          </DialogTrigger>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>Start Discovery Scan</DialogTitle>
              <DialogDescription>Scan the source tenant to inventory objects and detect migration blockers.</DialogDescription>
            </DialogHeader>
            <div className="space-y-3">
              <Select value={scanType} onValueChange={(v) => setScanType(v as ScanType)}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="full">Full Scan (all workloads)</SelectItem>
                  <SelectItem value="users">Users &amp; Groups only</SelectItem>
                  <SelectItem value="mailboxes">Mailboxes only</SelectItem>
                  <SelectItem value="sharepoint">SharePoint only</SelectItem>
                  <SelectItem value="onedrive">OneDrive only</SelectItem>
                  <SelectItem value="domains">Domains only</SelectItem>
                </SelectContent>
              </Select>
            </div>
            <DialogFooter>
              <Button variant="outline" onClick={() => setScanDialogOpen(false)}>Cancel</Button>
              <Button onClick={() => startScanMutation.mutate()} disabled={startScanMutation.isPending}>
                {startScanMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                Start Scan
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
        </div>
      </div>

      {/* Tabs */}
      <Tabs defaultValue="overview">
        <TabsList className="w-full justify-start overflow-x-auto">
          <TabsTrigger value="overview">Overview</TabsTrigger>
          <TabsTrigger value="domain-rules">Domain Rules</TabsTrigger>
          <TabsTrigger value="scans">Scans</TabsTrigger>
          <TabsTrigger value="identity">Identity Mapping</TabsTrigger>
          <TabsTrigger value="users">Users</TabsTrigger>
          <TabsTrigger value="mailboxes">Mailboxes</TabsTrigger>
          <TabsTrigger value="content">Content</TabsTrigger>
          <TabsTrigger value="domain-cutover">Domain Cutover</TabsTrigger>
          <TabsTrigger value="waves">Waves</TabsTrigger>
          <TabsTrigger value="validation">Validation</TabsTrigger>
          <TabsTrigger value="jobs">Jobs</TabsTrigger>
          <TabsTrigger value="audit">Audit</TabsTrigger>
        </TabsList>

        {/* ── Overview ── */}
        <TabsContent value="overview" className="space-y-4 mt-4">
          {/* Prerequisites / Dependency Checker */}
          <Card>
            <CardHeader className="pb-3">
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base flex items-center gap-2">
                    <ClipboardCheck className="h-4 w-4" />
                    Migration Prerequisites
                  </CardTitle>
                  <CardDescription>Check that all dependencies are satisfied before starting migration.</CardDescription>
                </div>
                <div className="flex items-center gap-2">
                  {depCheck && (
                    <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${
                      depCheck.overallStatus === "ready"   ? "bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300" :
                      depCheck.overallStatus === "warning" ? "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/40 dark:text-yellow-300" :
                      "bg-destructive/10 text-destructive"
                    }`}>
                      {depCheck.overallStatus === "ready" ? "Ready" : depCheck.overallStatus === "warning" ? "Warnings" : "Blocked"}
                    </span>
                  )}
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => { setDepCheckEnabled(true); runDepCheck(); }}
                    disabled={depCheckFetching}
                  >
                    {depCheckFetching
                      ? <><Loader2 className="mr-1.5 h-3.5 w-3.5 animate-spin" />Checking…</>
                      : <><RefreshCw className="mr-1.5 h-3.5 w-3.5" />{depCheck ? "Re-check" : "Run Checks"}</>
                    }
                  </Button>
                </div>
              </div>
            </CardHeader>
            {depCheck && (
              <CardContent className="pt-0">
                {(() => {
                  const icon = (status: CheckStatus) => {
                    if (status === "pass")    return <CheckCircle className="h-4 w-4 shrink-0 text-green-500" />;
                    if (status === "fail")    return <AlertTriangle className="h-4 w-4 shrink-0 text-destructive" />;
                    if (status === "warning") return <AlertCircle className="h-4 w-4 shrink-0 text-yellow-500" />;
                    return <Minus className="h-4 w-4 shrink-0 text-muted-foreground" />;
                  };

                  const categories = [...new Set(depCheck.checks.map(c => c.category))];
                  return (
                    <div className="space-y-4">
                      {categories.map(cat => (
                        <div key={cat}>
                          <p className="text-xs font-semibold uppercase tracking-wide text-muted-foreground mb-2">{cat}</p>
                          <div className="space-y-1.5">
                            {depCheck.checks.filter(c => c.category === cat).map(check => (
                              <div key={check.key} className={`flex items-start gap-3 rounded-md border px-3 py-2.5 text-sm ${
                                check.status === "fail"    ? "border-destructive/40 bg-destructive/5" :
                                check.status === "warning" ? "border-yellow-500/40 bg-yellow-50/50 dark:bg-yellow-900/10" :
                                check.status === "pass"    ? "border-green-500/20 bg-green-50/30 dark:bg-green-900/10" :
                                "border-border bg-muted/30"
                              }`}>
                                <div className="mt-0.5">{icon(check.status)}</div>
                                <div className="min-w-0 flex-1">
                                  <p className="font-medium">{check.name}</p>
                                  {check.detail && (
                                    <p className="text-xs text-muted-foreground mt-0.5">{check.detail}</p>
                                  )}
                                  {check.remediation && check.status !== "pass" && (
                                    <p className="text-xs text-muted-foreground mt-1 italic">{check.remediation}</p>
                                  )}
                                </div>
                              </div>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  );
                })()}
              </CardContent>
            )}
          </Card>

          <div className="grid gap-4 md:grid-cols-3">
            {/* Readiness score */}
            <Card className="md:col-span-1">
              <CardHeader><CardTitle className="text-base">Source Readiness</CardTitle></CardHeader>
              <CardContent className="flex flex-col items-center">
                <ResponsiveContainer width={140} height={140}>
                  <PieChart>
                    <Pie data={pieData} cx={65} cy={65} innerRadius={45} outerRadius={60} startAngle={90} endAngle={-270} dataKey="value" strokeWidth={0}>
                      <Cell fill={scoreColor} />
                      <Cell fill="hsl(var(--muted))" />
                    </Pie>
                  </PieChart>
                </ResponsiveContainer>
                <div className="text-center -mt-16 mb-12">
                  <span className="text-3xl font-bold" style={{ color: scoreColor }}>{score}</span>
                  <span className="text-muted-foreground text-sm">/100</span>
                </div>
                {latestSourceScan?.summary && (
                  <div className="w-full space-y-1 text-sm">
                    <div className="flex justify-between"><span className="text-muted-foreground">Blockers</span><span className="font-medium text-destructive">{latestSourceScan.summary.blockerCount}</span></div>
                    <div className="flex justify-between"><span className="text-muted-foreground">Warnings</span><span className="font-medium text-yellow-600">{latestSourceScan.summary.warningCount}</span></div>
                  </div>
                )}
              </CardContent>
            </Card>

            {/* Summary stats */}
            <Card className="md:col-span-2">
              <CardHeader><CardTitle className="text-base">Inventory Summary</CardTitle></CardHeader>
              <CardContent>
                {latestSourceScan?.summary ? (
                  <div className="grid grid-cols-2 gap-3">
                    {[
                      { label: "Users", value: formatNumber(latestSourceScan.summary.userCount) },
                      { label: "Groups", value: formatNumber(latestSourceScan.summary.groupCount) },
                      { label: "Mailboxes", value: formatNumber(latestSourceScan.summary.mailboxCount) },
                      { label: "Mailbox Data", value: formatBytes(latestSourceScan.summary.mailboxTotalSizeGb) },
                      { label: "SharePoint Sites", value: formatNumber(latestSourceScan.summary.siteCount) },
                      { label: "OneDrive Accounts", value: formatNumber(latestSourceScan.summary.oneDriveCount) },
                    ].map(({ label, value }) => (
                      <div key={label} className="rounded-md border p-3">
                        <p className="text-xs text-muted-foreground">{label}</p>
                        <p className="text-xl font-semibold mt-0.5">{value}</p>
                      </div>
                    ))}
                  </div>
                ) : (
                  <div className="flex flex-col items-center gap-2 py-8 text-center text-muted-foreground">
                    <p className="text-sm">No scan data yet. Run a scan to see inventory.</p>
                    <Button size="sm" variant="outline" onClick={() => setScanDialogOpen(true)}>
                      <Play className="mr-1 h-3 w-3" />Run first scan
                    </Button>
                  </div>
                )}
              </CardContent>
            </Card>
          </div>

          {/* Blockers */}
          {scanIssues && scanIssues.filter((i) => i.severity === "blocker").length > 0 && (
            <Card className="border-destructive/50">
              <CardHeader>
                <CardTitle className="text-base text-destructive flex items-center gap-2">
                  <AlertTriangle className="h-4 w-4" />Migration Blockers
                </CardTitle>
                <CardDescription>These issues must be resolved before migration can proceed.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-3">
                {scanIssues.filter((i) => i.severity === "blocker").map((issue) => (
                  <div key={issue.id} className="rounded-md border border-destructive/30 bg-destructive/5 p-3 space-y-1">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-sm">{issue.title}</span>
                      <Badge variant="secondary" className="text-xs">{issue.code}</Badge>
                    </div>
                    <p className="text-xs text-muted-foreground">{issue.description}</p>
                    <ul className="list-inside list-disc text-xs text-muted-foreground space-y-0.5 pl-1">
                      {issue.remediationSteps.map((step, i) => <li key={i}>{step}</li>)}
                    </ul>
                  </div>
                ))}
              </CardContent>
            </Card>
          )}

          {/* Warnings */}
          {scanIssues && scanIssues.filter((i) => i.severity === "warning").length > 0 && (
            <Card className="border-yellow-500/30">
              <CardHeader>
                <CardTitle className="text-base text-yellow-600 flex items-center gap-2">
                  <AlertCircle className="h-4 w-4" />Warnings
                </CardTitle>
                <CardDescription>These issues may impact migration quality and should be reviewed.</CardDescription>
              </CardHeader>
              <CardContent className="space-y-3">
                {scanIssues.filter((i) => i.severity === "warning").map((issue) => (
                  <div key={issue.id} className="rounded-md border border-yellow-500/30 bg-yellow-50/50 dark:bg-yellow-900/10 p-3 space-y-1">
                    <div className="flex items-center gap-2">
                      <span className="font-medium text-sm">{issue.title}</span>
                      <Badge variant="secondary" className="text-xs">{issue.code}</Badge>
                    </div>
                    <p className="text-xs text-muted-foreground">{issue.description}</p>
                    {issue.remediationSteps.length > 0 && (
                      <ul className="list-inside list-disc text-xs text-muted-foreground space-y-0.5 pl-1">
                        {issue.remediationSteps.map((step, i) => <li key={i}>{step}</li>)}
                      </ul>
                    )}
                  </div>
                ))}
              </CardContent>
            </Card>
          )}
          {/* Exchange Online Setup */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Exchange Online Prerequisites</CardTitle>
              <CardDescription>
                Automates the Exchange Online setup required before mailbox migration:
                organization relationships on both tenants and a cross-tenant migration endpoint on the source.
              </CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <div className="rounded-md border bg-muted/40 p-3 text-xs text-muted-foreground space-y-1">
                <p className="font-medium text-foreground">What this does (idempotent — safe to run multiple times):</p>
                <ul className="list-inside list-disc space-y-0.5 pl-1">
                  <li>Source tenant → create outbound organization relationship to target</li>
                  <li>Target tenant → create inbound organization relationship to source</li>
                  <li>Source tenant → create CrossTenantMigration endpoint</li>
                </ul>
                <p className="pt-1">
                  Requires <code>Exchange.ManageAsApp</code> permission and the{" "}
                  <code>Federated Sharing</code>, <code>Migration</code>, and <code>Mail Recipients</code> Exchange
                  management roles on both tenant app registrations.
                </p>
              </div>
              <Button
                onClick={() => setupExchangeMutation.mutate()}
                disabled={setupExchangeMutation.isPending}
                className="w-full sm:w-auto"
              >
                {setupExchangeMutation.isPending
                  ? <><Loader2 className="mr-2 h-4 w-4 animate-spin" />Setting up…</>
                  : <><Zap className="mr-2 h-4 w-4" />Setup Exchange Migration</>
                }
              </Button>

              {/* RBAC prerequisite script — must be run once by an Exchange admin */}
              {project?.sourceTenant && project?.targetTenant && (() => {
                const src = project.sourceTenant!;
                const tgt = project.targetTenant!;
                const srcOrg = src.onMicrosoftDomain ? `${src.onMicrosoftDomain}.onmicrosoft.com` : "<source.onmicrosoft.com>";
                const tgtOrg = tgt.onMicrosoftDomain ? `${tgt.onMicrosoftDomain}.onmicrosoft.com` : "<target.onmicrosoft.com>";
                const script = [
                  "# ── One-time Exchange RBAC setup ──────────────────────────────────────",
                  "# Requires: EXO V3 module + Microsoft Graph PowerShell module",
                  "#   Install-Module ExchangeOnlineManagement",
                  "#   Install-Module Microsoft.Graph",
                  "# Run as Global Admin or Exchange Admin. Required once per app registration.",
                  "",
                  "# ── Source tenant — mailbox migration + org relationship ────────────",
                  `# ${src.displayName}`,
                  "",
                  "# 1. Resolve the service principal via Graph (Get-ServicePrincipal is unreliable)",
                  `Connect-MgGraph -TenantId "${src.tenantId}" -Scopes "Application.Read.All"`,
                  `$sp = Get-MgServicePrincipal -Filter "appId eq '${src.appClientId}'"`,
                  `Write-Host "Source SP ObjectId: $($sp.Id)"`,
                  "",
                  "# 2. Create a custom role group with the required migration roles",
                  `Connect-ExchangeOnline -Organization ${srcOrg}`,
                  `$roles = @("Federated Sharing","Migration","Move Mailboxes","Mail Recipients","Mail Recipient Creation","Organization Configuration","Organization Client Access","Remote and Accepted Domains")`,
                  `New-RoleGroup -Name "Migration App EXO" -Roles $roles`,
                  `Add-RoleGroupMember -Identity "Migration App EXO" -Member $sp.Id`,
                  "",
                  "# 3. Verify",
                  `Get-RoleGroupMember -Identity "Migration App EXO" | Format-List Name, RecipientType`,
                  `Disconnect-ExchangeOnline`,
                  `Disconnect-MgGraph`,
                  "",
                  "# ── Target tenant — inbound org relationship only ───────────────────",
                  `# ${tgt.displayName}`,
                  "",
                  `Connect-MgGraph -TenantId "${tgt.tenantId}" -Scopes "Application.Read.All"`,
                  `$sp = Get-MgServicePrincipal -Filter "appId eq '${tgt.appClientId}'"`,
                  `Write-Host "Target SP ObjectId: $($sp.Id)"`,
                  "",
                  `Connect-ExchangeOnline -Organization ${tgtOrg}`,
                  `$roles = @("Federated Sharing","Mail Recipients","Mail Recipient Creation","Organization Configuration","Organization Client Access","Remote and Accepted Domains")`,
                  `New-RoleGroup -Name "Migration App EXO" -Roles $roles`,
                  `Add-RoleGroupMember -Identity "Migration App EXO" -Member $sp.Id`,
                  "",
                  `Get-RoleGroupMember -Identity "Migration App EXO" | Format-List Name, RecipientType`,
                  `Disconnect-ExchangeOnline`,
                  `Disconnect-MgGraph`,
                ].join("\n");

                return (
                  <details className="group">
                    <summary className="flex cursor-pointer items-center gap-2 text-xs font-medium text-muted-foreground hover:text-foreground select-none list-none">
                      <Terminal className="h-3.5 w-3.5" />
                      RBAC management role setup — run once per tenant per app registration
                      <span className="ml-auto text-[10px] group-open:hidden">Show script ▸</span>
                      <span className="ml-auto text-[10px] hidden group-open:inline">Hide ▴</span>
                    </summary>
                    <div className="mt-2 space-y-2">
                      <p className="text-xs text-muted-foreground">
                        Creates a custom role group <code className="text-xs">Migration App EXO</code> with the
                        required Exchange roles and adds the app&apos;s service principal as a member.
                        Uses the Microsoft Graph PowerShell module to resolve the service principal ObjectId
                        (required because <code className="text-xs">Get-ServicePrincipal</code> is unreliable for app registrations).
                        Run once per tenant per app registration, then click &ldquo;Setup Exchange Migration&rdquo; above.
                      </p>
                      <div className="relative rounded-md bg-muted/60 border">
                        <pre className="overflow-x-auto p-3 text-[11px] leading-relaxed text-muted-foreground whitespace-pre">{script}</pre>
                        <Button
                          variant="ghost"
                          size="icon"
                          className="absolute right-1 top-1 h-7 w-7"
                          onClick={() => {
                            navigator.clipboard.writeText(script);
                            toast.success("Script copied to clipboard");
                          }}
                        >
                          <Copy className="h-3.5 w-3.5" />
                        </Button>
                      </div>
                    </div>
                  </details>
                );
              })()}
            </CardContent>
          </Card>

          {/* Cross-tenant sync app discovery — only when at least one user batch
              opts into CrossTenantSync. Probes the target tenant for the sync
              app + job and surfaces remediation steps when missing. */}
          {hasCtsBatch && (
            <Card>
              <CardHeader>
                <div className="flex items-start justify-between gap-2">
                  <div>
                    <CardTitle className="text-base">Cross-Tenant Sync App Discovery</CardTitle>
                    <CardDescription>
                      One or more user batches selected the CrossTenantSync strategy.
                      Probes the target tenant for an Entra cross-tenant sync app and synchronization job
                      bound to the source tenant.
                    </CardDescription>
                  </div>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => refetchCtsDiscovery()}
                    disabled={ctsDiscoveryFetching}
                  >
                    {ctsDiscoveryFetching
                      ? <><Loader2 className="mr-2 h-3 w-3 animate-spin" />Probing…</>
                      : <><RotateCcw className="mr-2 h-3 w-3" />Re-check</>}
                  </Button>
                </div>
              </CardHeader>
              <CardContent className="space-y-3">
                {!ctsDiscovery && ctsDiscoveryFetching && (
                  <p className="text-xs text-muted-foreground">Probing target tenant…</p>
                )}
                {ctsDiscovery && (
                  <>
                    <div className="flex items-start gap-3">
                      {ctsDiscovery.error
                        ? <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-500" />
                        : ctsDiscovery.isConfigured
                        ? <CheckCircle className="mt-0.5 h-4 w-4 shrink-0 text-green-500" />
                        : <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />}
                      <div className="min-w-0 flex-1">
                        <p className="text-sm font-medium">
                          {ctsDiscovery.error
                            ? "Could not probe target tenant"
                            : ctsDiscovery.isConfigured
                            ? "Cross-tenant sync configured"
                            : "Cross-tenant sync not configured"}
                        </p>
                        <p className="text-xs text-muted-foreground mt-0.5 break-words">
                          {ctsDiscovery.message}
                        </p>
                        {ctsDiscovery.remediation && (
                          <p className="text-xs text-muted-foreground mt-2 break-words">
                            <span className="font-medium text-foreground">Next step: </span>
                            {ctsDiscovery.remediation}
                          </p>
                        )}
                      </div>
                    </div>
                    {(ctsDiscovery.servicePrincipalDisplayName || ctsDiscovery.syncJobId || ctsDiscovery.syncJobStatus) && (
                      <div className="rounded-md border bg-muted/40 p-3 text-xs space-y-1">
                        {ctsDiscovery.servicePrincipalDisplayName && (
                          <div><span className="text-muted-foreground">App:</span> <code className="text-[11px]">{ctsDiscovery.servicePrincipalDisplayName}</code></div>
                        )}
                        {ctsDiscovery.syncJobTemplateId && (
                          <div><span className="text-muted-foreground">Template:</span> <code className="text-[11px]">{ctsDiscovery.syncJobTemplateId}</code></div>
                        )}
                        {ctsDiscovery.syncJobId && (
                          <div><span className="text-muted-foreground">Job ID:</span> <code className="text-[11px]">{ctsDiscovery.syncJobId}</code></div>
                        )}
                        {ctsDiscovery.syncJobStatus && (
                          <div><span className="text-muted-foreground">Status:</span> {ctsDiscovery.syncJobStatus}</div>
                        )}
                        {ctsDiscovery.lastSyncAt && (
                          <div><span className="text-muted-foreground">Last sync:</span> {relativeTime(ctsDiscovery.lastSyncAt)}</div>
                        )}
                        <div>
                          <span className="text-muted-foreground">Partner policy:</span>{" "}
                          {ctsDiscovery.partnerPolicyConfigured
                            ? <span className="text-green-600">configured</span>
                            : <span className="text-destructive">missing</span>}
                        </div>
                      </div>
                    )}
                  </>
                )}
              </CardContent>
            </Card>
          )}

          {/* Exchange Setup Result Dialog */}
          <Dialog open={exchangeSetupDialogOpen} onOpenChange={setExchangeSetupDialogOpen}>
            <DialogContent className="sm:max-w-[520px]">
              <DialogHeader>
                <DialogTitle>Exchange Setup Results</DialogTitle>
                <DialogDescription>
                  Results of the automated Exchange Online prerequisite setup.
                </DialogDescription>
              </DialogHeader>
              {exchangeSetupResult && (
                <div className="space-y-3 text-sm">
                  {exchangeSetupResult.mock && (
                    <div className="rounded-md border border-blue-500/50 bg-blue-500/10 p-2 text-xs text-blue-700 dark:text-blue-400">
                      Mock mode — no real EXO calls were made (Platform:MockGraphCalls=true).
                    </div>
                  )}
                  {[
                    {
                      label: "Source org relationship",
                      sub: `→ ${exchangeSetupResult.sourceOrgRelationship.domain} (Outbound)`,
                      status: exchangeSetupResult.sourceOrgRelationship.status,
                      error: exchangeSetupResult.sourceOrgRelationship.error,
                    },
                    {
                      label: "Target org relationship",
                      sub: `→ ${exchangeSetupResult.targetOrgRelationship.domain} (Inbound)`,
                      status: exchangeSetupResult.targetOrgRelationship.status,
                      error: exchangeSetupResult.targetOrgRelationship.error,
                    },
                    {
                      label: "Migration endpoint",
                      sub: exchangeSetupResult.migrationEndpoint.identity
                        ? `Identity: ${exchangeSetupResult.migrationEndpoint.identity}`
                        : "No identity returned",
                      status: exchangeSetupResult.migrationEndpoint.status,
                      error: exchangeSetupResult.migrationEndpoint.error,
                    },
                  ].map(({ label, sub, status, error }) => (
                    <div key={label} className="flex items-start gap-3">
                      {status === "created"
                        ? <CheckCircle className="mt-0.5 h-4 w-4 shrink-0 text-green-500" />
                        : status === "existing"
                        ? <Info className="mt-0.5 h-4 w-4 shrink-0 text-blue-500" />
                        : <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                      }
                      <div className="min-w-0">
                        <p className="font-medium">{label}</p>
                        <p className="text-xs text-muted-foreground">{sub}</p>
                        <p className={`text-xs font-medium ${
                          status === "created" ? "text-green-600" :
                          status === "existing" ? "text-blue-600" :
                          "text-destructive"
                        }`}>
                          {status === "created" ? "Created" :
                           status === "existing" ? "Already existed — no change needed" :
                           "Failed"}
                        </p>
                        {error && (
                          <p className="text-xs text-muted-foreground mt-1 break-words">{error}</p>
                        )}
                      </div>
                    </div>
                  ))}
                </div>
              )}
              <DialogFooter>
                <Button onClick={() => setExchangeSetupDialogOpen(false)}>Close</Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

        </TabsContent>

        {/* ── Domain Rules ── */}
        <TabsContent value="domain-rules" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm text-muted-foreground">
                Rules transform source UPNs to target UPNs during identity mapping. Evaluated in priority order — lowest number wins.
              </p>
            </div>
            <Dialog open={ruleDialogOpen} onOpenChange={setRuleDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm"><Plus className="mr-2 h-4 w-4" />Add Rule</Button>
              </DialogTrigger>
              <DialogContent className="sm:max-w-[480px]">
                <DialogHeader>
                  <DialogTitle>Add Domain Rule</DialogTitle>
                  <DialogDescription>Define how source UPNs are transformed to target UPNs.</DialogDescription>
                </DialogHeader>
                <div className="space-y-4">
                  <div className="space-y-2">
                    <Label>Rule Type</Label>
                    <Select value={ruleForm.ruleType} onValueChange={(v) => setRuleForm((f) => ({ ...f, ruleType: v as DomainRuleType }))}>
                      <SelectTrigger><SelectValue /></SelectTrigger>
                      <SelectContent>
                        <SelectItem value="directMap">Direct Map — swap domain</SelectItem>
                        <SelectItem value="prefixReplace">Prefix Replace — swap domain</SelectItem>
                        <SelectItem value="regexReplace">Regex Replace</SelectItem>
                        <SelectItem value="fullUpnMap">Full UPN Map — exact override</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div className="space-y-2">
                      <Label>{ruleForm.ruleType === "fullUpnMap" ? "Source UPN" : ruleForm.ruleType === "regexReplace" ? "Regex Pattern" : "Source Domain"}</Label>
                      <Input placeholder={ruleForm.ruleType === "fullUpnMap" ? "user@contoso.com" : ruleForm.ruleType === "regexReplace" ? "^(.+)@contoso\\.com$" : "contoso.com"}
                        value={ruleForm.sourcePattern} onChange={(e) => setRuleForm((f) => ({ ...f, sourcePattern: e.target.value }))} />
                    </div>
                    <div className="space-y-2">
                      <Label>{ruleForm.ruleType === "fullUpnMap" ? "Target UPN" : ruleForm.ruleType === "regexReplace" ? "Replacement" : "Target Domain"}</Label>
                      <Input placeholder={ruleForm.ruleType === "fullUpnMap" ? "user@fabrikam.com" : ruleForm.ruleType === "regexReplace" ? "$1@fabrikam.com" : "fabrikam.com"}
                        value={ruleForm.targetPattern} onChange={(e) => setRuleForm((f) => ({ ...f, targetPattern: e.target.value }))} />
                    </div>
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div className="space-y-2">
                      <Label>Priority</Label>
                      <Input type="number" min={1} value={ruleForm.priority}
                        onChange={(e) => setRuleForm((f) => ({ ...f, priority: parseInt(e.target.value) || 10 }))} />
                    </div>
                    <div className="space-y-2">
                      <Label>Description (optional)</Label>
                      <Input placeholder="e.g. Main domain swap" value={ruleForm.description ?? ""}
                        onChange={(e) => setRuleForm((f) => ({ ...f, description: e.target.value }))} />
                    </div>
                  </div>
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setRuleDialogOpen(false)}>Cancel</Button>
                  <Button onClick={() => createRuleMutation.mutate(ruleForm)}
                    disabled={createRuleMutation.isPending || !ruleForm.sourcePattern || !ruleForm.targetPattern}>
                    {createRuleMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Add Rule
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          <Card>
            <CardHeader><CardTitle className="text-base">Rules</CardTitle></CardHeader>
            <CardContent>
              {rulesLoading ? <Skeleton className="h-20 w-full" /> : !domainRules?.length ? (
                <p className="py-8 text-center text-sm text-muted-foreground">No rules yet. Add a rule to enable auto-mapping.</p>
              ) : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      <th className="py-2 text-left font-medium text-muted-foreground">Priority</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Type</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Source</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Target</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Description</th>
                      <th className="py-2 text-left font-medium text-muted-foreground"></th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {domainRules.map((rule) => (
                      <tr key={rule.id} className="hover:bg-muted/40">
                        <td className="py-2 text-muted-foreground">{rule.priority}</td>
                        <td className="py-2 capitalize text-muted-foreground">{rule.ruleType.replace(/([A-Z])/g, " $1").trim()}</td>
                        <td className="py-2 font-mono text-xs">{rule.sourcePattern}</td>
                        <td className="py-2 font-mono text-xs">{rule.targetPattern}</td>
                        <td className="py-2 text-muted-foreground text-xs">{rule.description ?? "—"}</td>
                        <td className="py-2">
                          <Button variant="ghost" size="icon" className="h-7 w-7 text-destructive"
                            onClick={() => deleteRuleMutation.mutate(rule.id)}>
                            <Trash2 className="h-3.5 w-3.5" />
                          </Button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>

          {/* Preview */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Test Rules</CardTitle>
              <CardDescription>Enter sample UPNs to preview how rules will transform them.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-3">
              <div className="flex gap-2">
                <Input
                  placeholder="alice@contoso.com, bob@contoso.com"
                  value={previewUpns}
                  onChange={(e) => setPreviewUpns(e.target.value)}
                  className="flex-1"
                />
                <Button variant="outline" disabled={previewMutation.isPending || !previewUpns.trim()}
                  onClick={() => previewMutation.mutate(previewUpns.split(",").map((u) => u.trim()).filter(Boolean))}>
                  {previewMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : "Preview"}
                </Button>
              </div>
              {previewResults.length > 0 && (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      <th className="py-2 text-left font-medium text-muted-foreground">Source UPN</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Transformed UPN</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Matched Rule</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {previewResults.map((r) => (
                      <tr key={r.sourceUpn}>
                        <td className="py-2 font-mono text-xs">{r.sourceUpn}</td>
                        <td className={`py-2 font-mono text-xs ${r.wasTransformed ? "text-green-600 dark:text-green-400" : "text-muted-foreground"}`}>
                          {r.transformedUpn}
                        </td>
                        <td className="py-2 text-xs text-muted-foreground">
                          {r.wasTransformed ? "matched" : "no match"}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Scans ── */}
        <TabsContent value="scans" className="mt-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <CardTitle className="text-base">Scan History</CardTitle>
                <Button size="sm" onClick={() => setScanDialogOpen(true)}><Play className="mr-1 h-3 w-3" />Run Scan</Button>
              </div>
            </CardHeader>
            <CardContent>
              {scansLoading ? <Skeleton className="h-32 w-full" /> : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      <th className="py-2 text-left font-medium text-muted-foreground">Type</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Status</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Progress</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Score</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Started</th>
                      <th className="py-2 text-left font-medium text-muted-foreground"></th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {scans?.map((scan) => (
                      <tr key={scan.id} className="hover:bg-muted/40">
                        <td className="py-2 capitalize">{scan.scanType}</td>
                        <td className="py-2"><ScanStatusBadge status={scan.status} /></td>
                        <td className="py-2 w-32">
                          <div className="flex items-center gap-2">
                            <Progress value={scan.progress} className="h-1.5 flex-1" />
                            <span className="text-xs text-muted-foreground w-7">{scan.progress}%</span>
                          </div>
                        </td>
                        <td className="py-2">{scan.summary?.readinessScore !== undefined ? `${scan.summary.readinessScore}/100` : "—"}</td>
                        <td className="py-2 text-muted-foreground">{relativeTime(scan.startedAt)}</td>
                        <td className="py-2">
                          <Button variant="ghost" size="sm" asChild>
                            <Link href={`/scans/${scan.id}`}>Details</Link>
                          </Button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Identity Mapping ── */}
        <TabsContent value="identity" className="mt-4">
          <Card>
            <CardHeader>
              <div className="flex items-center justify-between">
                <div>
                  <CardTitle className="text-base">Identity Mappings</CardTitle>
                  <CardDescription>Source UPN → Target UPN mappings</CardDescription>
                </div>
                <div className="flex gap-2">
                  <Button variant="outline" size="sm"><Upload className="mr-1 h-3 w-3" />Import CSV</Button>
                  <Button variant="outline" size="sm"><Download className="mr-1 h-3 w-3" />Export CSV</Button>
                  <Button size="sm" onClick={() => autoMapMutation.mutate()} disabled={autoMapMutation.isPending}>
                    {autoMapMutation.isPending ? <Loader2 className="mr-1 h-3 w-3 animate-spin" /> : <RefreshCw className="mr-1 h-3 w-3" />}
                    Auto-Map
                  </Button>
                </div>
              </div>
            </CardHeader>
            <CardContent>
              {mapsLoading ? <Skeleton className="h-40 w-full" /> : (
                <>
                  <div className="flex gap-3 mb-4">
                    {[
                      { label: "Mapped", count: identityMaps?.filter((m) => m.status === "mapped").length ?? 0, color: "text-green-600" },
                      { label: "Unmapped", count: identityMaps?.filter((m) => m.status === "unmapped").length ?? 0, color: "text-muted-foreground" },
                      { label: "Conflicts", count: identityMaps?.filter((m) => m.status === "conflict").length ?? 0, color: "text-destructive" },
                    ].map(({ label, count, color }) => (
                      <div key={label} className="rounded-md border px-3 py-2 text-sm">
                        <span className={`font-semibold ${color}`}>{count}</span>
                        <span className="ml-1 text-muted-foreground">{label}</span>
                      </div>
                    ))}
                  </div>
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b">
                        <th className="py-2 text-left font-medium text-muted-foreground">Source UPN</th>
                        <th className="py-2 text-left font-medium text-muted-foreground">Target UPN</th>
                        <th className="py-2 text-left font-medium text-muted-foreground">Status</th>
                        <th className="py-2 text-left font-medium text-muted-foreground">Source</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y">
                      {identityMaps?.map((map) => (
                        <tr key={map.id} className="hover:bg-muted/40">
                          <td className="py-2 font-mono text-xs">{map.sourceUpn}</td>
                          <td className="py-2 font-mono text-xs">
                            {editingMapId === map.id ? (
                              <div className="flex items-center gap-1">
                                <Input
                                  value={editTargetUpn}
                                  onChange={(e) => setEditTargetUpn(e.target.value)}
                                  onKeyDown={(e) => {
                                    if (e.key === "Enter" && editTargetUpn.trim() && !updateMapMutation.isPending)
                                      updateMapMutation.mutate({ mapId: map.id, targetUpn: editTargetUpn.trim() });
                                    if (e.key === "Escape") setEditingMapId(null);
                                  }}
                                  className="h-7 w-64 font-mono text-xs"
                                  autoFocus
                                />
                                <Button
                                  variant="ghost" size="sm" className="h-7 px-2"
                                  disabled={!editTargetUpn.trim() || updateMapMutation.isPending}
                                  onClick={() => updateMapMutation.mutate({ mapId: map.id, targetUpn: editTargetUpn.trim() })}
                                >
                                  {updateMapMutation.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Check className="h-3 w-3" />}
                                </Button>
                                <Button variant="ghost" size="sm" className="h-7 px-2" onClick={() => setEditingMapId(null)}>
                                  <X className="h-3 w-3" />
                                </Button>
                              </div>
                            ) : (
                              <div className="group flex items-center gap-1">
                                {map.targetUpn ?? <span className="text-muted-foreground italic">not mapped</span>}
                                <Button
                                  variant="ghost" size="sm"
                                  className="h-6 px-1.5 opacity-0 group-hover:opacity-100"
                                  title="Edit target UPN"
                                  onClick={() => { setEditingMapId(map.id); setEditTargetUpn(map.targetUpn ?? ""); }}
                                >
                                  <Pencil className="h-3 w-3" />
                                </Button>
                              </div>
                            )}
                          </td>
                          <td className="py-2">
                            <div className="space-y-0.5">
                              <MappingStatusBadge status={map.status} />
                              {map.conflictReason && <p className="text-xs text-destructive">{map.conflictReason}</p>}
                            </div>
                          </td>
                          <td className="py-2 capitalize text-muted-foreground text-xs">{map.mappingSource}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Users (direct Graph user provisioning) ── */}
        <TabsContent value="users" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">User Migration</h2>
              <p className="text-sm text-muted-foreground">
                Provision identities as member accounts in the target tenant via Microsoft Graph.
              </p>
            </div>
            <Dialog open={userBatchDialogOpen} onOpenChange={setUserBatchDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm" disabled={!identityMaps?.some((m) => m.status === "mapped")}>
                  <Plus className="mr-1 h-3 w-3" />Create Batch
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Create User Migration Batch</DialogTitle>
                  <DialogDescription>
                    Creates a batch from mapped identity pairs ({userBatchEligiblePairs.length} of{" "}
                    {mappedIdentityPairs.length} eligible).
                    Run auto-map on the Identity Mapping tab first if none are shown.
                  </DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <Label>Batch Name</Label>
                    <Input
                      placeholder="e.g. Wave 1 Users"
                      value={userBatchName}
                      onChange={(e) => setUserBatchName(e.target.value)}
                    />
                  </div>
                  <div className="space-y-1">
                    <Label>Provisioning Strategy</Label>
                    <Select value={userBatchStrategy} onValueChange={(v) => setUserBatchStrategy(v as "directGraph" | "crossTenantSync")}>
                      <SelectTrigger>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="crossTenantSync">Entra cross-tenant sync — recommended</SelectItem>
                        <SelectItem value="directGraph">Direct Graph (POST /users) — fallback</SelectItem>
                      </SelectContent>
                    </Select>
                    <p className="text-xs text-muted-foreground">
                      {userBatchStrategy === "directGraph"
                        ? "Fallback: creates plain member accounts in the target tenant via Graph POST /users. Each user gets a fresh password forced to reset on first sign-in. No Entra dependency — use when the cross-tenant sync prerequisites aren't in place."
                        : "Microsoft-native (recommended): Entra cross-tenant synchronization (provisionOnDemand). Source users keep their home credentials and appear as synced identities. Requires the cross-tenant sync app + sync job in the source tenant and cross-tenant access settings in both."}
                    </p>
                  </div>
                  <p className="text-xs text-muted-foreground">
                    {userBatchEligiblePairs.length} mapped pairs will be included.
                    Unmapped or conflicted identities are excluded.
                  </p>
                  {userBatchExcludedCount > 0 && (
                    <p className="text-xs text-amber-600 dark:text-amber-500">
                      {userBatchExcludedCount} user(s) excluded because they are in a mailbox migration
                      batch — the mailbox move provisions the target account itself (MailUser), and a
                      pre-created account would break it. Mailbox first; user migration is only for
                      users whose mailbox is not migrating.
                    </p>
                  )}
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setUserBatchDialogOpen(false)}>Cancel</Button>
                  <Button
                    onClick={() => createUserBatchMutation.mutate()}
                    disabled={createUserBatchMutation.isPending || !userBatchName.trim() || userBatchEligiblePairs.length === 0}
                  >
                    {createUserBatchMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Create Batch
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {userMigrationBatchesLoading ? (
            <div className="space-y-2">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}</div>
          ) : userMigrationBatches?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <Download className="h-10 w-10 opacity-30" />
                <p className="text-sm">No user migration batches yet.</p>
                <p className="text-xs">Complete identity mapping, then create a batch to start provisioning users.</p>
              </CardContent>
            </Card>
          ) : (
            <Card>
              <CardContent className="p-0">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      {["Name", "Strategy", "Status", "Progress", "Users", "Created", "Actions"].map((h) => (
                        <th key={h} className="px-4 py-3 text-left font-medium text-muted-foreground text-xs whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {userMigrationBatches?.map((batch) => {
                      const statusColors: Record<UserMigrationBatchStatus, string> = {
                        draft:        "bg-muted text-muted-foreground",
                        provisioning: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                        completed:    "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                        stopped:      "bg-muted text-muted-foreground",
                        failed:       "bg-destructive/10 text-destructive",
                      };
                      return (
                        <tr key={batch.id} className="hover:bg-muted/40">
                          <td className="px-4 py-3 font-medium">{batch.name}</td>
                          <td className="px-4 py-3">
                            <div className="flex items-center gap-1.5 flex-wrap">
                              <span
                                className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
                                  batch.strategy === "crossTenantSync"
                                    ? "bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-400"
                                    : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"
                                }`}
                                title={batch.strategy === "crossTenantSync" ? "Entra cross-tenant sync (provisionOnDemand)" : "Graph POST /users"}
                              >
                                {batch.strategy === "crossTenantSync" ? "Cross-tenant sync" : "Direct Graph"}
                              </span>
                              {batch.strategy === "crossTenantSync" && batch.status === "draft" && (
                                <span
                                  className="inline-flex items-center rounded-full bg-yellow-100 px-2 py-0.5 text-xs font-medium text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400"
                                  title="Requires the cross-tenant sync app + synchronization job to be configured in the target tenant before this batch can run."
                                >
                                  Needs sync app
                                </span>
                              )}
                            </div>
                          </td>
                          <td className="px-4 py-3">
                            <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium capitalize ${statusColors[batch.status]}`}>
                              {batch.status}
                            </span>
                          </td>
                          <td className="px-4 py-3 w-36">
                            <div className="flex items-center gap-2">
                              <Progress value={batch.progressPercent} className="h-1.5 flex-1" />
                              <span className="text-xs text-muted-foreground w-8 shrink-0">{batch.progressPercent.toFixed(0)}%</span>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">
                            {batch.provisionedUsers + (batch.skippedUsers ?? 0)}/{batch.totalUsers}
                            {batch.failedUsers > 0 && <span className="ml-1 text-destructive">({batch.failedUsers} failed)</span>}
                            {(batch.skippedUsers ?? 0) > 0 && <span className="ml-1 text-muted-foreground">({batch.skippedUsers} skipped)</span>}
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">{relativeTime(batch.createdAt)}</td>
                          <td className="px-4 py-3">
                            <div className="flex gap-1">
                              {batch.status === "draft" && (
                                <Button size="sm" variant="outline" onClick={() => startUserBatchMutation.mutate(batch.id)} disabled={startUserBatchMutation.isPending}>
                                  <Play className="h-3 w-3 mr-1" />Start
                                </Button>
                              )}
                              {batch.status === "provisioning" && (
                                <Button size="sm" variant="ghost" onClick={() => stopUserBatchMutation.mutate(batch.id)} disabled={stopUserBatchMutation.isPending}>
                                  <XCircle className="h-3 w-3" />
                                </Button>
                              )}
                              {(batch.status === "completed" || batch.status === "failed") && batch.failedUsers > 0 && (
                                <Button size="sm" variant="outline" onClick={() => retryFailedUserBatchMutation.mutate(batch.id)} disabled={retryFailedUserBatchMutation.isPending}>
                                  <RotateCcw className="h-3 w-3 mr-1" />Retry Failed
                                </Button>
                              )}
                              {(batch.status === "failed" || batch.status === "stopped") && batch.failedUsers === 0 && (
                                <Button
                                  size="sm"
                                  variant="outline"
                                  title={
                                    batch.status === "failed"
                                      ? "Re-queue every non-Provisioned entry. Use this when a CrossTenantSync batch failed before any entry was attempted."
                                      : "Re-queue every non-Provisioned entry to resume the batch."
                                  }
                                  onClick={() => retryUserBatchMutation.mutate(batch.id)}
                                  disabled={retryUserBatchMutation.isPending}
                                >
                                  <RotateCcw className="h-3 w-3 mr-1" />Retry
                                </Button>
                              )}
                              {batch.failedUsers > 0 && batch.status !== "provisioning" && (
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  title="Reclassify failed users as skipped (e.g. targets that were never mapped)."
                                  onClick={() => {
                                    if (confirm(`Mark ${batch.failedUsers} failed user(s) as skipped? The batch status will be recomputed against mapped users only.`))
                                      skipUserFailuresMutation.mutate(batch.id);
                                  }}
                                  disabled={skipUserFailuresMutation.isPending}
                                >
                                  Skip failures
                                </Button>
                              )}
                              {batch.status !== "provisioning" && (
                                <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => { if (confirm(`Delete batch "${batch.name}"?`)) deleteUserBatchMutation.mutate(batch.id); }} disabled={deleteUserBatchMutation.isPending}>
                                  <Trash2 className="h-3 w-3" />
                                </Button>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </CardContent>
            </Card>
          )}

          {!identityMaps?.some((m) => m.status === "mapped") && (
            <p className="text-xs text-muted-foreground">
              No mapped identities found. Go to the <strong>Identity Mapping</strong> tab and run auto-map first.
            </p>
          )}
        </TabsContent>

        {/* ── Waves ── */}
        {/* ── Mailboxes ── */}
        <TabsContent value="mailboxes" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">Mailbox Migration Batches</h2>
              <p className="text-sm text-muted-foreground">Create batches from your identity map, then assign them to a wave.</p>
            </div>
            <Dialog open={batchDialogOpen} onOpenChange={setBatchDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm" disabled={!identityMaps?.some((m) => m.status === "mapped")}>
                  <Plus className="mr-1 h-3 w-3" />Create Batch
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Create Mailbox Batch</DialogTitle>
                  <DialogDescription>
                    Creates a batch from all <strong>mapped</strong> identity pairs (
                    {identityMaps?.filter((m) => m.status === "mapped").length ?? 0} mailboxes).
                    Run auto-map on the Identity Mapping tab first if none are shown.
                  </DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <Label>Batch Name</Label>
                    <Input
                      placeholder="e.g. Wave 1 Mailboxes"
                      value={batchName}
                      onChange={(e) => setBatchName(e.target.value)}
                    />
                  </div>
                  <div className="space-y-1">
                    <Label>Migration Strategy</Label>
                    <Select value={batchStrategy} onValueChange={(v) => setBatchStrategy(v as "graphCopy" | "nativeMrs")}>
                      <SelectTrigger>
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value="nativeMrs">Native cross-tenant MRS (Exchange) — recommended</SelectItem>
                        <SelectItem value="graphCopy">Graph copy (per-message) — fallback</SelectItem>
                      </SelectContent>
                    </Select>
                    <p className="text-xs text-muted-foreground">
                      {batchStrategy === "graphCopy"
                        ? "Fallback: copies one message at a time via Microsoft Graph. No EXO setup; ~1–3 msg/sec/user. Use when the cross-tenant EXO setup isn't in place or for <10 GB mailboxes."
                        : "Microsoft-native (recommended): server-side cross-tenant move via Exchange Online MRS. Requires org relationship, migration endpoint, and target MailUser stubs (the platform automates these). ~1–2 GB/hr, true move with cutover."}
                    </p>
                  </div>
                  {batchStrategy === "graphCopy" && (
                    <div className="space-y-1">
                      <Label>Target Folder <span className="text-muted-foreground font-normal">(optional)</span></Label>
                      <Input
                        placeholder="e.g. Migrated Mail"
                        value={batchTargetFolder}
                        onChange={(e) => setBatchTargetFolder(e.target.value)}
                      />
                      <p className="text-xs text-muted-foreground">
                        When set, all copied mail is placed under this folder in the target mailbox.
                        Leave blank to copy into matching folders (Inbox, Sent, etc.).
                      </p>
                    </div>
                  )}
                  <p className="text-xs text-muted-foreground">
                    {identityMaps?.filter((m) => m.status === "mapped").length ?? 0} mapped pairs will be included.
                    Unmapped or conflicted identities are excluded.
                  </p>
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setBatchDialogOpen(false)}>Cancel</Button>
                  <Button
                    onClick={() => createBatchMutation.mutate()}
                    disabled={createBatchMutation.isPending || !batchName.trim() || !identityMaps?.some((m) => m.status === "mapped")}
                  >
                    {createBatchMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Create Batch
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {batchesLoading ? (
            <div className="space-y-2">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}</div>
          ) : mailboxBatches?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <Download className="h-10 w-10 opacity-30" />
                <p className="text-sm">No mailbox batches yet.</p>
                <p className="text-xs">Complete identity mapping, then create a batch to start migrating mailboxes.</p>
              </CardContent>
            </Card>
          ) : (
            <Card>
              <CardContent className="p-0">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      {["Name", "Strategy", "Status", "Progress", "Mailboxes", "Created", "Actions"].map((h) => (
                        <th key={h} className="px-4 py-3 text-left font-medium text-muted-foreground text-xs whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {mailboxBatches?.map((batch) => {
                      const batchStatusColors: Record<BatchStatus, string> = {
                        draft: "bg-muted text-muted-foreground",
                        validating: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                        syncing: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                        synced: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400",
                        completing: "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400",
                        completed: "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                        stopped: "bg-muted text-muted-foreground",
                        failed: "bg-destructive/10 text-destructive",
                      };
                      const batchStatusLabels: Partial<Record<BatchStatus, string>> = {
                        synced: "Awaiting cutover",
                      };
                      return (
                        <tr key={batch.id} className="hover:bg-muted/40">
                          <td className="px-4 py-3 font-medium">{batch.name}</td>
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
                                batch.strategy === "nativeMrs"
                                  ? "bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-400"
                                  : "bg-slate-100 text-slate-700 dark:bg-slate-800 dark:text-slate-300"
                              }`}
                              title={batch.strategy === "nativeMrs" ? "Native cross-tenant MRS" : "Per-message Graph copy"}
                            >
                              {batch.strategy === "nativeMrs" ? "Native MRS" : "Graph copy"}
                            </span>
                          </td>
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${batchStatusLabels[batch.status] ? "" : "capitalize "}${batchStatusColors[batch.status]}`}
                              title={batch.status === "failed" ? batch.errorMessage : batch.status === "synced" ? "Initial sync complete — click Complete to cut the mailboxes over to the target tenant." : undefined}
                            >
                              {batchStatusLabels[batch.status] ?? batch.status}
                            </span>
                          </td>
                          <td className="px-4 py-3 w-36">
                            <div className="flex items-center gap-2">
                              <Progress value={batch.progressPercent} className="h-1.5 flex-1" />
                              <span className="text-xs text-muted-foreground w-8 shrink-0">{batch.progressPercent.toFixed(0)}%</span>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">
                            {batch.syncedMailboxes + (batch.skippedMailboxes ?? 0)}/{batch.totalMailboxes}
                            {batch.failedMailboxes > 0 && <span className="ml-1 text-destructive">({batch.failedMailboxes} failed)</span>}
                            {(batch.skippedMailboxes ?? 0) > 0 && <span className="ml-1 text-muted-foreground">({batch.skippedMailboxes} skipped)</span>}
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">{relativeTime(batch.createdAt)}</td>
                          <td className="px-4 py-3">
                            <div className="flex gap-1">
                              {batch.status === "draft" && (
                                <Button size="sm" variant="outline" onClick={() => startBatchMutation.mutate(batch.id)} disabled={startBatchMutation.isPending}>
                                  <Play className="h-3 w-3 mr-1" />Start
                                </Button>
                              )}
                              {batch.status === "synced" && batch.strategy === "nativeMrs" && (
                                <Button
                                  size="sm"
                                  variant="outline"
                                  title="Finalize the migration: cut the mailboxes over to the target tenant (Complete-MigrationBatch)."
                                  onClick={() => completeBatchMutation.mutate(batch.id)}
                                  disabled={completeBatchMutation.isPending}
                                >
                                  <CheckCircle2 className="h-3 w-3 mr-1" />Complete
                                </Button>
                              )}
                              {(batch.status === "syncing" || batch.status === "synced") && (
                                <Button size="sm" variant="ghost" title="Stop the batch" onClick={() => stopBatchMutation.mutate(batch.id)} disabled={stopBatchMutation.isPending}>
                                  <XCircle className="h-3 w-3" />
                                </Button>
                              )}
                              {batch.status === "failed" && (
                                <Button
                                  size="sm"
                                  variant="outline"
                                  title="Re-queue failed mailboxes. For Native MRS the stale EXO batch is removed and a fresh one is created."
                                  onClick={() => retryBatchMutation.mutate(batch.id)}
                                  disabled={retryBatchMutation.isPending}
                                >
                                  <RotateCcw className="h-3 w-3 mr-1" />Retry
                                </Button>
                              )}
                              {batch.failedMailboxes > 0 && batch.status !== "syncing" && batch.status !== "synced" && batch.status !== "completing" && (
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  title="Reclassify failed mailboxes as skipped (e.g. targets that were never mapped)."
                                  onClick={() => {
                                    if (confirm(`Mark ${batch.failedMailboxes} failed mailbox(es) as skipped? The batch status will be recomputed against mapped mailboxes only.`))
                                      skipFailuresMutation.mutate(batch.id);
                                  }}
                                  disabled={skipFailuresMutation.isPending}
                                >
                                  Skip failures
                                </Button>
                              )}
                              {batch.status !== "syncing" && batch.status !== "synced" && batch.status !== "completing" && (
                                <Button
                                  size="sm"
                                  variant="ghost"
                                  title="Wipe target-tenant EXO state for this batch (MoveRequests, target MailUsers, soft-deleted stubs, EXO batch) and reset entries to Draft for a clean retry."
                                  onClick={() => {
                                    if (confirm(`Reset target-tenant state for "${batch.name}"?\n\nThis will:\n• Remove MoveRequests for each target UPN\n• Remove target MailUser stubs\n• Permanently purge soft-deleted MailUsers matching this batch's source UPNs\n• Drop the EXO migration batch\n• Reset all entries to Queued, batch to Draft\n\nNothing on the source tenant is touched.`))
                                      resetMailboxTargetMutation.mutate(batch.id);
                                  }}
                                  disabled={resetMailboxTargetMutation.isPending}
                                >
                                  Reset target
                                </Button>
                              )}
                              {batch.status !== "syncing" && batch.status !== "synced" && batch.status !== "completing" && (
                                <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => { if (confirm(`Delete batch "${batch.name}"?`)) deleteMailboxBatchMutation.mutate(batch.id); }} disabled={deleteMailboxBatchMutation.isPending}>
                                  <Trash2 className="h-3 w-3" />
                                </Button>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </CardContent>
            </Card>
          )}

          {!identityMaps?.some((m) => m.status === "mapped") && (
            <p className="text-xs text-muted-foreground">
              No mapped identities found. Go to the <strong>Identity Mapping</strong> tab and run auto-map first.
            </p>
          )}
        </TabsContent>

        {/* ── Content Migrations ── */}
        <TabsContent value="content" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">Content Migration Jobs</h2>
              <p className="text-sm text-muted-foreground">Migrate SharePoint sites and OneDrive drives to the target tenant.</p>
            </div>
            <Dialog open={contentDialogOpen} onOpenChange={setContentDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm"><Plus className="mr-1 h-3 w-3" />New Job</Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Create Content Migration Job</DialogTitle>
                  <DialogDescription>Define a SharePoint or OneDrive migration job.</DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <Label>Job Name</Label>
                    <Input placeholder="e.g. Marketing SharePoint" value={contentJobName} onChange={(e) => setContentJobName(e.target.value)} />
                  </div>
                  <div className="space-y-1">
                    <Label>Type</Label>
                    <Select value={contentJobType} onValueChange={(v) => { setContentJobType(v as ContentJobType); setContentSourceUrl(""); setContentTargetUrl(""); }}>
                      <SelectTrigger><SelectValue /></SelectTrigger>
                      <SelectContent>
                        <SelectItem value="sharePoint">SharePoint</SelectItem>
                        <SelectItem value="oneDrive">OneDrive</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="space-y-1">
                    <Label>Source {contentJobType === "sharePoint" ? "Site" : "Drive"}</Label>
                    {contentJobType === "sharePoint" && scannedSites && scannedSites.length > 0 ? (
                      <Select value={contentSourceUrl} onValueChange={handleContentSourceSelect}>
                        <SelectTrigger><SelectValue placeholder="Select a site from the scan…" /></SelectTrigger>
                        <SelectContent>
                          {scannedSites.map((site) => (
                            <SelectItem key={site.id} value={site.siteUrl}>
                              <span className="font-medium">{site.title}</span>
                              <span className="ml-2 text-xs text-muted-foreground truncate">{site.siteUrl}</span>
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    ) : contentJobType === "oneDrive" && scannedOneDrives && scannedOneDrives.length > 0 ? (
                      <Select value={contentSourceUrl} onValueChange={handleContentSourceSelect}>
                        <SelectTrigger><SelectValue placeholder="Select a OneDrive from the scan…" /></SelectTrigger>
                        <SelectContent>
                          {scannedOneDrives.map((od) => (
                            <SelectItem key={od.id} value={od.driveUrl}>
                              <span className="font-medium">{od.ownerDisplayName}</span>
                              <span className="ml-2 text-xs text-muted-foreground">{od.ownerUpn}</span>
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                    ) : (
                      <Input
                        placeholder={contentJobType === "sharePoint" ? "https://source.sharepoint.com/sites/marketing" : "https://source-my.sharepoint.com/personal/user"}
                        value={contentSourceUrl}
                        onChange={(e) => setContentSourceUrl(e.target.value)}
                      />
                    )}
                    {!latestSourceScan && (
                      <p className="text-xs text-muted-foreground">Run a scan first to enable site selection.</p>
                    )}
                  </div>
                  <div className="space-y-1">
                    <Label>Target URL</Label>
                    <Input
                      placeholder={contentJobType === "sharePoint" ? "https://target.sharepoint.com/sites/marketing" : "https://target-my.sharepoint.com/personal/user"}
                      value={contentTargetUrl}
                      onChange={(e) => setContentTargetUrl(e.target.value)}
                    />
                    {contentTargetUrl.includes("<target-domain>") && (
                      <p className="text-xs text-amber-500">Replace &lt;target-domain&gt; with the target SharePoint prefix before continuing. Verify the target tenant to enable auto-fill.</p>
                    )}
                  </div>
                  {contentJobType === "oneDrive" && (
                    <>
                      <div className="space-y-1">
                        <Label>Source Owner UPN</Label>
                        <Input placeholder="user@source.onmicrosoft.com" value={contentOwnerUpn} onChange={(e) => setContentOwnerUpn(e.target.value)} />
                      </div>
                      <div className="space-y-1">
                        <Label>Target Owner UPN</Label>
                        <Input placeholder="user@target.onmicrosoft.com" value={contentTargetOwnerUpn} onChange={(e) => setContentTargetOwnerUpn(e.target.value)} />
                      </div>
                    </>
                  )}
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setContentDialogOpen(false)}>Cancel</Button>
                  <Button
                    onClick={() => createContentJobMutation.mutate()}
                    disabled={createContentJobMutation.isPending || !contentJobName.trim() || !contentSourceUrl.trim() || !contentTargetUrl.trim() || contentTargetUrl.includes("<target-domain>")}
                  >
                    {createContentJobMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Create Job
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {/* Edit content job dialog */}
          <Dialog open={!!editContentJob} onOpenChange={(open) => { if (!open) setEditContentJob(null); }}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Edit Content Job</DialogTitle>
                <DialogDescription>Update the job name and item details. Only Draft jobs can be edited.</DialogDescription>
              </DialogHeader>
              <div className="space-y-3">
                <div className="space-y-1">
                  <Label>Job Name</Label>
                  <Input value={editContentName} onChange={(e) => setEditContentName(e.target.value)} />
                </div>
                <div className="space-y-1">
                  <Label>Source URL</Label>
                  <Input value={editContentSourceUrl} onChange={(e) => setEditContentSourceUrl(e.target.value)} />
                </div>
                <div className="space-y-1">
                  <Label>Target URL</Label>
                  <Input value={editContentTargetUrl} onChange={(e) => setEditContentTargetUrl(e.target.value)} />
                </div>
                {editContentJob?.jobType === "oneDrive" && (
                  <>
                    <div className="space-y-1">
                      <Label>Source Owner UPN</Label>
                      <Input placeholder="user@source.onmicrosoft.com" value={editContentOwnerUpn} onChange={(e) => setEditContentOwnerUpn(e.target.value)} />
                    </div>
                    <div className="space-y-1">
                      <Label>Target Owner UPN</Label>
                      <Input placeholder="user@target.onmicrosoft.com" value={editContentTargetOwnerUpn} onChange={(e) => setEditContentTargetOwnerUpn(e.target.value)} />
                    </div>
                  </>
                )}
              </div>
              <DialogFooter>
                <Button variant="outline" onClick={() => setEditContentJob(null)}>Cancel</Button>
                <Button
                  onClick={() => updateContentJobMutation.mutate()}
                  disabled={updateContentJobMutation.isPending || !editContentName.trim() || !editContentSourceUrl.trim() || !editContentTargetUrl.trim()}
                >
                  {updateContentJobMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                  Save
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {contentJobsLoading ? (
            <div className="space-y-2">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-14 w-full" />)}</div>
          ) : contentJobs?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <Upload className="h-10 w-10 opacity-30" />
                <p className="text-sm">No content migration jobs yet.</p>
                <p className="text-xs">Create a job to migrate SharePoint sites or OneDrive drives.</p>
              </CardContent>
            </Card>
          ) : (
            <Card>
              <CardContent className="p-0">
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      {["Name", "Type", "Status", "Progress", "Items", "Created", "Actions"].map((h) => (
                        <th key={h} className="px-4 py-3 text-left font-medium text-muted-foreground text-xs whitespace-nowrap">{h}</th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {contentJobs?.map((job) => {
                      const jobStatusColors: Record<string, string> = {
                        draft: "bg-muted text-muted-foreground",
                        provisioning: "bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-400",
                        ready: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400",
                        scheduled: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                        running: "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400",
                        paused: "bg-orange-100 text-orange-700 dark:bg-orange-900/30 dark:text-orange-400",
                        completed: "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                        failed: "bg-destructive/10 text-destructive",
                      };
                      return (
                        <tr key={job.id} className="hover:bg-muted/40">
                          <td className="px-4 py-3 font-medium">
                            {job.status === "draft" ? (
                              <button
                                className="text-left hover:underline text-primary"
                                onClick={async () => {
                                  setEditContentJob(job);
                                  setEditContentName(job.name);
                                  try {
                                    const items = await contentMigrationsApi.getItems(id, job.id);
                                    if (items.length > 0) {
                                      setEditContentSourceUrl(items[0].sourceUrl ?? "");
                                      setEditContentTargetUrl(items[0].targetUrl ?? "");
                                      setEditContentOwnerUpn(items[0].ownerUpn ?? "");
                                      setEditContentTargetOwnerUpn(items[0].targetOwnerUpn ?? "");
                                    }
                                  } catch { /* items will stay empty */ }
                                }}
                              >
                                {job.name}
                              </button>
                            ) : (
                              job.name
                            )}
                          </td>
                          <td className="px-4 py-3 text-xs text-muted-foreground capitalize">{job.jobType}</td>
                          <td className="px-4 py-3">
                            <span
                              className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium capitalize ${jobStatusColors[job.status] ?? "bg-muted text-muted-foreground"}`}
                              title={job.status === "failed" ? job.errorMessage : undefined}
                            >
                              {job.status}
                            </span>
                            {job.status === "failed" && job.errorMessage && (
                              <p className="mt-1 max-w-[16rem] text-xs text-destructive line-clamp-2" title={job.errorMessage}>
                                {job.errorMessage}
                              </p>
                            )}
                          </td>
                          <td className="px-4 py-3 w-36">
                            <div className="flex items-center gap-2">
                              <Progress value={job.progressPercent} className="h-1.5 flex-1" />
                              <span className="text-xs text-muted-foreground w-8 shrink-0">{job.progressPercent.toFixed(0)}%</span>
                            </div>
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">
                            {job.migratedItems}/{job.totalItems}
                            {job.failedItems > 0 && <span className="ml-1 text-destructive">({job.failedItems} failed)</span>}
                          </td>
                          <td className="px-4 py-3 text-muted-foreground text-xs whitespace-nowrap">{relativeTime(job.createdAt)}</td>
                          <td className="px-4 py-3">
                            <div className="flex gap-1">
                              {job.status === "provisioning" && (
                                <Button size="sm" variant="outline" disabled>
                                  <Loader2 className="h-3 w-3 mr-1 animate-spin" />Provisioning
                                </Button>
                              )}
                              {(job.status === "draft" || job.status === "ready") && (
                                <Button size="sm" variant="outline" onClick={() => startContentJobMutation.mutate(job.id)} disabled={startContentJobMutation.isPending}>
                                  <Play className="h-3 w-3 mr-1" />Start
                                </Button>
                              )}
                              {job.status === "running" && (
                                <Button size="sm" variant="outline" onClick={() => pauseContentJobMutation.mutate(job.id)} disabled={pauseContentJobMutation.isPending}>
                                  <Clock className="h-3 w-3 mr-1" />Pause
                                </Button>
                              )}
                              {job.status === "paused" && (
                                <Button size="sm" variant="outline" onClick={() => resumeContentJobMutation.mutate(job.id)} disabled={resumeContentJobMutation.isPending}>
                                  <Play className="h-3 w-3 mr-1" />Resume
                                </Button>
                              )}
                              {(job.status === "draft" || job.status === "ready" || job.status === "running" || job.status === "paused") && (
                                <Button size="sm" variant="ghost" onClick={() => cancelContentJobMutation.mutate(job.id)} disabled={cancelContentJobMutation.isPending}>
                                  <XCircle className="h-3 w-3" />
                                </Button>
                              )}
                              {job.status !== "running" && (
                                <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => { if (confirm(`Delete job "${job.name}"?`)) deleteContentJobMutation.mutate(job.id); }} disabled={deleteContentJobMutation.isPending}>
                                  <Trash2 className="h-3 w-3" />
                                </Button>
                              )}
                            </div>
                          </td>
                        </tr>
                      );
                    })}
                  </tbody>
                </table>
              </CardContent>
            </Card>
          )}
        </TabsContent>

        {/* ── Waves ── */}
        <TabsContent value="domain-cutover" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">Domain Cutover</h2>
              <p className="text-sm text-muted-foreground">
                Move a custom domain from the source tenant to the target. The workflow runs automatically
                but pauses for you to make DNS changes (verification TXT, then MX).
              </p>
            </div>
            <Dialog open={cutoverDialogOpen} onOpenChange={setCutoverDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm"><Plus className="mr-1 h-3 w-3" />New Cutover</Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>New Domain Cutover</DialogTitle>
                  <DialogDescription>
                    Enter the custom domain to move from the source tenant to the target. This removes it
                    from the source, adds it to the target, and reassigns user UPNs/SMTP to it.
                  </DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <Label>Domain name</Label>
                    <Input
                      placeholder="contoso.com"
                      value={cutoverDomainName}
                      onChange={(e) => setCutoverDomainName(e.target.value)}
                    />
                    <p className="text-xs text-muted-foreground">
                      Must be a verified custom domain on the source tenant (not the .onmicrosoft.com domain).
                    </p>
                  </div>
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setCutoverDialogOpen(false)}>Cancel</Button>
                  <Button
                    onClick={() => createCutoverMutation.mutate()}
                    disabled={createCutoverMutation.isPending || !cutoverDomainName.trim()}
                  >
                    {createCutoverMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Create
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {cutoverJobsLoading ? (
            <div className="space-y-2">{Array.from({ length: 2 }).map((_, i) => <Skeleton key={i} className="h-32 w-full" />)}</div>
          ) : cutoverJobs?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <Globe className="h-10 w-10 opacity-30" />
                <p className="text-sm">No domain cutover jobs yet.</p>
                <p className="text-xs">Create one to move a custom domain from the source tenant to the target.</p>
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-4">
              {cutoverJobs?.map((job) => {
                const isPause = CUTOVER_PAUSE_PHASES.includes(job.phase);
                const isFailed = job.phase === "failed";
                const isDone = job.phase === "completed";
                const currentIdx = CUTOVER_PHASES.findIndex((p) => p.key === job.phase);
                const isWorking = !isPause && !isFailed && !isDone && job.phase !== "created";
                return (
                  <Card key={job.id}>
                    <CardHeader className="pb-3">
                      <div className="flex items-center justify-between">
                        <div className="flex items-center gap-2">
                          <Globe className="h-4 w-4 text-muted-foreground" />
                          <CardTitle className="text-base">{job.domainName}</CardTitle>
                          <span
                            className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium ${
                              isFailed ? "bg-destructive/10 text-destructive"
                                : isDone ? "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400"
                                : isPause ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
                                : isWorking ? "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400"
                                : "bg-muted text-muted-foreground"
                            }`}
                          >
                            {isPause && <Clock className="mr-1 h-3 w-3" />}
                            {isWorking && <Loader2 className="mr-1 h-3 w-3 animate-spin" />}
                            {isDone && <CheckCircle2 className="mr-1 h-3 w-3" />}
                            {isFailed && <AlertTriangle className="mr-1 h-3 w-3" />}
                            {isPause ? "Action needed" : isWorking ? "Working" : isDone ? "Completed" : isFailed ? "Failed" : "Ready"}
                          </span>
                        </div>
                        <div className="flex items-center gap-2">
                          <span className="text-xs text-muted-foreground">
                            {job.totalUsers > 0 && `${job.completedUsers}/${job.totalUsers} users`}
                            {job.failedUsers > 0 && <span className="ml-1 text-destructive">({job.failedUsers} failed)</span>}
                          </span>
                          {job.phase === "created" && (
                            <Button size="sm" variant="outline" onClick={() => startCutoverMutation.mutate(job.id)} disabled={startCutoverMutation.isPending}>
                              <Play className="h-3 w-3 mr-1" />Start
                            </Button>
                          )}
                          {(job.phase === "created" || isFailed || isDone) && (
                            <Button size="sm" variant="ghost" className="text-destructive hover:text-destructive" onClick={() => { if (confirm(`Delete domain cutover for "${job.domainName}"?`)) deleteCutoverMutation.mutate(job.id); }} disabled={deleteCutoverMutation.isPending}>
                              <Trash2 className="h-3 w-3" />
                            </Button>
                          )}
                        </div>
                      </div>
                    </CardHeader>
                    <CardContent className="space-y-4">
                      {/* Phase stepper */}
                      <div className="flex flex-wrap items-center gap-1.5">
                        {CUTOVER_PHASES.map((p, idx) => {
                          const done = !isFailed && idx < currentIdx;
                          const active = p.key === job.phase;
                          return (
                            <div key={p.key} className="flex items-center gap-1.5">
                              <span
                                className={`inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium ${
                                  active && isPause ? "bg-amber-100 text-amber-700 dark:bg-amber-900/30 dark:text-amber-400"
                                    : active && isFailed ? "bg-destructive/10 text-destructive"
                                    : active ? "bg-primary text-primary-foreground"
                                    : done ? "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-400"
                                    : "bg-muted text-muted-foreground"
                                }`}
                              >
                                {done && <CheckCircle2 className="mr-1 h-3 w-3" />}
                                {p.label}
                              </span>
                              {idx < CUTOVER_PHASES.length - 1 && <ArrowRight className="h-3 w-3 text-muted-foreground/40" />}
                            </div>
                          );
                        })}
                      </div>

                      {/* Failure */}
                      {isFailed && job.errorMessage && (
                        <div className="rounded-md border border-destructive/30 bg-destructive/5 p-3 text-sm text-destructive">
                          <p className="font-medium">Cutover failed</p>
                          <p className="mt-1 text-xs">{job.errorMessage}</p>
                        </div>
                      )}

                      {/* PAUSE: DNS verification */}
                      {job.phase === "awaitingDnsVerification" && (
                        <div className="rounded-md border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-950">
                          <p className="flex items-center gap-2 text-sm font-medium text-amber-800 dark:text-amber-300">
                            <Clock className="h-4 w-4" /> Action needed: verify the domain on the target tenant
                          </p>
                          <p className="mt-1 text-xs text-amber-700 dark:text-amber-400">
                            Add this <strong>TXT record</strong> to <code>{job.domainName}</code>&apos;s DNS at your registrar,
                            wait for it to propagate, then click <strong>Continue</strong> to verify.
                          </p>
                          {job.dnsInstructions && (
                            <p className="mt-2 whitespace-pre-wrap text-xs text-amber-700 dark:text-amber-400">{job.dnsInstructions}</p>
                          )}
                          {job.dnsVerificationRecord && (
                            <div className="mt-2 flex items-center gap-2">
                              <pre className="flex-1 overflow-x-auto rounded bg-amber-100 px-3 py-2 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200"><code>{job.dnsVerificationRecord}</code></pre>
                              <Button size="sm" variant="outline" onClick={() => navigator.clipboard.writeText(job.dnsVerificationRecord!).then(() => toast.success("TXT record copied"))}>
                                <Copy className="h-3 w-3" />
                              </Button>
                            </div>
                          )}
                          <Button
                            className="mt-3"
                            size="sm"
                            onClick={() => continueCutoverMutation.mutate(job.id)}
                            disabled={continueCutoverMutation.isPending}
                          >
                            {continueCutoverMutation.isPending ? <Loader2 className="mr-2 h-3 w-3 animate-spin" /> : <Play className="mr-1 h-3 w-3" />}
                            I&apos;ve added the TXT record — Continue
                          </Button>
                        </div>
                      )}

                      {/* PAUSE: MX update */}
                      {job.phase === "awaitingMxUpdate" && (
                        <div className="rounded-md border border-amber-300 bg-amber-50 p-4 dark:border-amber-700 dark:bg-amber-950">
                          <p className="flex items-center gap-2 text-sm font-medium text-amber-800 dark:text-amber-300">
                            <Clock className="h-4 w-4" /> Action needed: update the domain&apos;s MX record
                          </p>
                          <p className="mt-1 text-xs text-amber-700 dark:text-amber-400">
                            Users are assigned. Point <code>{job.domainName}</code>&apos;s <strong>MX record</strong> at the target
                            tenant so mail flows there, then click <strong>Continue</strong> to finish.
                          </p>
                          {job.targetMxRecord && (
                            <div className="mt-2 flex items-center gap-2">
                              <pre className="flex-1 overflow-x-auto rounded bg-amber-100 px-3 py-2 text-xs text-amber-900 dark:bg-amber-900/40 dark:text-amber-200"><code>{job.targetMxRecord}</code></pre>
                              <Button size="sm" variant="outline" onClick={() => navigator.clipboard.writeText(job.targetMxRecord!).then(() => toast.success("MX record copied"))}>
                                <Copy className="h-3 w-3" />
                              </Button>
                            </div>
                          )}
                          <Button
                            className="mt-3"
                            size="sm"
                            onClick={() => continueCutoverMutation.mutate(job.id)}
                            disabled={continueCutoverMutation.isPending}
                          >
                            {continueCutoverMutation.isPending ? <Loader2 className="mr-2 h-3 w-3 animate-spin" /> : <Play className="mr-1 h-3 w-3" />}
                            I&apos;ve updated the MX record — Continue
                          </Button>
                        </div>
                      )}

                      {isDone && (
                        <p className="flex items-center gap-2 text-sm text-green-700 dark:text-green-400">
                          <CheckCircle2 className="h-4 w-4" /> Domain moved to the target tenant and users reassigned.
                        </p>
                      )}

                      <p className="text-xs text-muted-foreground">
                        Created {relativeTime(job.createdAt)}{job.lastUpdatedAt && ` · updated ${relativeTime(job.lastUpdatedAt)}`}
                      </p>
                    </CardContent>
                  </Card>
                );
              })}
            </div>
          )}
        </TabsContent>

        <TabsContent value="waves" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">Wave Plan</h2>
              <p className="text-sm text-muted-foreground">Group batches and jobs into phased migration waves.</p>
            </div>
            <Dialog open={waveDialogOpen} onOpenChange={setWaveDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm" onClick={() => setWaveOrder((waves?.length ?? 0) + 1)}>
                  <Plus className="mr-1 h-3 w-3" />New Wave
                </Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Create Migration Wave</DialogTitle>
                  <DialogDescription>Group batches and jobs into a named phase. Set a schedule to auto-start at a future time.</DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <label className="text-sm font-medium">Name</label>
                    <input
                      className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                      placeholder="e.g. Wave 1 — Pilot Group"
                      value={waveName}
                      onChange={(e) => setWaveName(e.target.value)}
                    />
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium">Description <span className="text-muted-foreground">(optional)</span></label>
                    <textarea
                      className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring resize-none"
                      rows={2}
                      placeholder="Who is included and why this wave is sequenced here."
                      value={waveDescription}
                      onChange={(e) => setWaveDescription(e.target.value)}
                    />
                  </div>
                  <div className="grid grid-cols-2 gap-3">
                    <div className="space-y-1">
                      <label className="text-sm font-medium">Order</label>
                      <input
                        type="number"
                        min={1}
                        className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                        value={waveOrder}
                        onChange={(e) => setWaveOrder(parseInt(e.target.value, 10) || 1)}
                      />
                    </div>
                    <div className="space-y-1">
                      <label className="text-sm font-medium">Scheduled Start <span className="text-muted-foreground">(optional)</span></label>
                      <input
                        type="datetime-local"
                        className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                        value={waveSchedule}
                        onChange={(e) => setWaveSchedule(e.target.value)}
                      />
                    </div>
                  </div>
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setWaveDialogOpen(false)}>Cancel</Button>
                  <Button
                    onClick={() => createWaveMutation.mutate()}
                    disabled={createWaveMutation.isPending || !waveName.trim()}
                  >
                    {createWaveMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Create Wave
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {wavesLoading ? (
            <div className="space-y-3">{Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-32 w-full" />)}</div>
          ) : waves?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <Layers className="h-10 w-10 opacity-30" />
                <p className="text-sm">No waves defined yet.</p>
                <p className="text-xs">Create waves to organise batches and jobs into phased migration phases.</p>
                <Button size="sm" variant="outline" onClick={() => setWaveDialogOpen(true)}>
                  <Plus className="mr-1 h-3 w-3" />Create first wave
                </Button>
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-3">
              {waves?.map((wave) => {
                const totalBatchMailboxes = wave.mailboxBatches.reduce((s, b) => s + b.totalMailboxes, 0);
                const syncedBatchMailboxes = wave.mailboxBatches.reduce((s, b) => s + b.syncedMailboxes, 0);
                const totalJobItems = wave.contentJobs.reduce((s, j) => s + j.totalItems, 0);
                const migratedJobItems = wave.contentJobs.reduce((s, j) => s + j.migratedItems, 0);
                const overallTotal = totalBatchMailboxes + totalJobItems;
                const overallDone = syncedBatchMailboxes + migratedJobItems;
                const overallPct = overallTotal > 0 ? Math.round((overallDone / overallTotal) * 100) : 0;

                const statusColors: Record<WaveStatus, string> = {
                  draft: "bg-muted text-muted-foreground",
                  scheduled: "bg-blue-100 text-blue-700 dark:bg-blue-900/30 dark:text-blue-400",
                  running: "bg-yellow-100 text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400",
                  completed: "bg-green-100 text-green-700 dark:bg-green-900/30 dark:text-green-400",
                  failed: "bg-destructive/10 text-destructive",
                  cancelled: "bg-muted text-muted-foreground line-through",
                };

                const canStart = wave.status === "draft" || wave.status === "scheduled";
                const canCancel = wave.status === "draft" || wave.status === "scheduled" || wave.status === "running";
                const canDelete = wave.status === "draft" || wave.status === "scheduled";

                return (
                  <Card key={wave.id}>
                    <CardHeader className="pb-3">
                      <div className="flex items-start justify-between gap-3">
                        <div className="flex items-center gap-3 min-w-0">
                          <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-muted text-xs font-semibold">
                            {wave.order}
                          </span>
                          <div className="min-w-0">
                            <div className="flex items-center gap-2 flex-wrap">
                              <span className="font-semibold text-sm truncate">{wave.name}</span>
                              <span className={`inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium capitalize ${statusColors[wave.status]}`}>
                                {wave.status}
                              </span>
                            </div>
                            {wave.description && (
                              <p className="text-xs text-muted-foreground mt-0.5 line-clamp-1">{wave.description}</p>
                            )}
                          </div>
                        </div>
                        <div className="flex shrink-0 items-center gap-1">
                          {canStart && (
                            <Button
                              size="sm"
                              variant="outline"
                              onClick={() => startWaveMutation.mutate(wave.id)}
                              disabled={startWaveMutation.isPending}
                            >
                              {startWaveMutation.isPending ? <Loader2 className="h-3 w-3 animate-spin" /> : <Play className="h-3 w-3" />}
                              <span className="ml-1 hidden sm:inline">Start</span>
                            </Button>
                          )}
                          {canCancel && (
                            <Button
                              size="sm"
                              variant="ghost"
                              onClick={() => cancelWaveMutation.mutate(wave.id)}
                              disabled={cancelWaveMutation.isPending}
                            >
                              <XCircle className="h-3 w-3" />
                            </Button>
                          )}
                          {canDelete && (
                            <Button
                              size="sm"
                              variant="ghost"
                              onClick={() => deleteWaveMutation.mutate(wave.id)}
                              disabled={deleteWaveMutation.isPending}
                            >
                              <Trash2 className="h-3 w-3" />
                            </Button>
                          )}
                        </div>
                      </div>
                    </CardHeader>
                    <CardContent className="pt-0 space-y-3">
                      {/* Timing */}
                      <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
                        {wave.scheduledStartAt && (
                          <span className="flex items-center gap-1">
                            <Calendar className="h-3 w-3" />
                            {wave.status === "scheduled" ? "Scheduled: " : "Was scheduled: "}
                            {formatDateTime(wave.scheduledStartAt)}
                          </span>
                        )}
                        {wave.startedAt && (
                          <span className="flex items-center gap-1">
                            <Play className="h-3 w-3" />
                            Started: {relativeTime(wave.startedAt)}
                          </span>
                        )}
                        {wave.completedAt && (
                          <span className="flex items-center gap-1">
                            <CheckCircle2 className="h-3 w-3" />
                            Completed: {relativeTime(wave.completedAt)}
                          </span>
                        )}
                      </div>

                      {/* Overall progress */}
                      {overallTotal > 0 && (
                        <div className="space-y-1">
                          <div className="flex justify-between text-xs text-muted-foreground">
                            <span>Overall progress</span>
                            <span>{overallDone}/{overallTotal} items ({overallPct}%)</span>
                          </div>
                          <Progress value={overallPct} className="h-1.5" />
                        </div>
                      )}

                      {/* Assigned batches + jobs */}
                      {(wave.mailboxBatches.length > 0 || wave.contentJobs.length > 0) && (
                        <div className="grid gap-2 sm:grid-cols-2">
                          {wave.mailboxBatches.map((batch) => (
                            <div key={batch.id} className="rounded-md border px-3 py-2 space-y-1">
                              <div className="flex items-center justify-between gap-2">
                                <span className="text-xs font-medium truncate">{batch.name}</span>
                                <span className="shrink-0 text-xs text-muted-foreground capitalize">{batch.status}</span>
                              </div>
                              <div className="flex items-center gap-2">
                                <Progress value={batch.progressPercent} className="h-1 flex-1" />
                                <span className="shrink-0 text-xs text-muted-foreground w-8 text-right">
                                  {batch.progressPercent.toFixed(0)}%
                                </span>
                              </div>
                              <p className="text-xs text-muted-foreground">
                                {batch.syncedMailboxes}/{batch.totalMailboxes} mailboxes
                                {batch.failedMailboxes > 0 && <span className="ml-1 text-destructive">({batch.failedMailboxes} failed)</span>}
                              </p>
                            </div>
                          ))}
                          {wave.contentJobs.map((job) => (
                            <div key={job.id} className="rounded-md border px-3 py-2 space-y-1">
                              <div className="flex items-center justify-between gap-2">
                                <span className="text-xs font-medium truncate">{job.name}</span>
                                <span className="shrink-0 text-xs text-muted-foreground capitalize">{job.jobType}</span>
                              </div>
                              <div className="flex items-center gap-2">
                                <Progress value={job.progressPercent} className="h-1 flex-1" />
                                <span className="shrink-0 text-xs text-muted-foreground w-8 text-right">
                                  {job.progressPercent.toFixed(0)}%
                                </span>
                              </div>
                              <p className="text-xs text-muted-foreground">
                                {job.migratedItems}/{job.totalItems} items
                                {job.failedItems > 0 && <span className="ml-1 text-destructive">({job.failedItems} failed)</span>}
                              </p>
                            </div>
                          ))}
                        </div>
                      )}

                      {wave.mailboxBatches.length === 0 && wave.contentJobs.length === 0 && (
                        <p className="text-xs text-muted-foreground italic">No batches or jobs assigned yet.</p>
                      )}

                      {(wave.status === "draft" || wave.status === "scheduled") && (
                        <div className="pt-1 flex gap-2 flex-wrap">
                          <Button
                            size="sm"
                            variant="outline"
                            className="text-xs"
                            onClick={() => {
                              setAssignDialogWaveId(wave.id);
                              setSelectedBatchIds(wave.mailboxBatches.map((b) => b.id));
                            }}
                          >
                            <Layers className="h-3 w-3 mr-1" />Assign Batches
                          </Button>
                          <Button
                            size="sm"
                            variant="outline"
                            className="text-xs"
                            onClick={() => {
                              setAssignContentDialogWaveId(wave.id);
                              setSelectedContentJobIds(wave.contentJobs.map((j) => j.id));
                            }}
                          >
                            <Upload className="h-3 w-3 mr-1" />Assign Content Jobs
                          </Button>
                          <Button
                            size="sm"
                            variant="outline"
                            className="text-xs"
                            onClick={() => {
                              setAssignUserBatchDialogWaveId(wave.id);
                              setSelectedUserBatchIds(wave.userBatches.map((b) => b.id));
                            }}
                          >
                            <ShieldCheck className="h-3 w-3 mr-1" />Assign User Batches
                          </Button>
                        </div>
                      )}
                    </CardContent>
                  </Card>
                );
              })}
            </div>
          )}

          {/* Assign Batches Dialog */}
          <Dialog open={!!assignDialogWaveId} onOpenChange={(open) => { if (!open) { setAssignDialogWaveId(null); setSelectedBatchIds([]); } }}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Assign Mailbox Batches</DialogTitle>
                <DialogDescription>
                  Select which batches to include in this wave. Existing assignments will be replaced.
                </DialogDescription>
              </DialogHeader>
              {mailboxBatches?.length === 0 ? (
                <p className="text-sm text-muted-foreground py-4 text-center">
                  No batches exist yet. Create one on the <strong>Mailboxes</strong> tab first.
                </p>
              ) : (
                <div className="space-y-2 max-h-64 overflow-y-auto">
                  {mailboxBatches?.map((batch) => (
                    <label key={batch.id} className="flex items-center gap-3 rounded-md border px-3 py-2 cursor-pointer hover:bg-muted/40">
                      <input
                        type="checkbox"
                        className="h-4 w-4"
                        checked={selectedBatchIds.includes(batch.id)}
                        onChange={(e) => setSelectedBatchIds((prev) =>
                          e.target.checked ? [...prev, batch.id] : prev.filter((x) => x !== batch.id)
                        )}
                      />
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium truncate">{batch.name}</p>
                        <p className="text-xs text-muted-foreground capitalize">{batch.status} · {batch.totalMailboxes} mailboxes</p>
                      </div>
                    </label>
                  ))}
                </div>
              )}
              <DialogFooter>
                <Button variant="outline" onClick={() => { setAssignDialogWaveId(null); setSelectedBatchIds([]); }}>Cancel</Button>
                <Button
                  disabled={assignBatchesMutation.isPending || !mailboxBatches?.length}
                  onClick={() => assignDialogWaveId && assignBatchesMutation.mutate({ waveId: assignDialogWaveId, batchIds: selectedBatchIds })}
                >
                  {assignBatchesMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                  Save Assignment
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {/* Assign Content Jobs Dialog */}
          <Dialog open={!!assignContentDialogWaveId} onOpenChange={(open) => { if (!open) { setAssignContentDialogWaveId(null); setSelectedContentJobIds([]); } }}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Assign Content Jobs</DialogTitle>
                <DialogDescription>
                  Select which content migration jobs to include in this wave. Existing assignments will be replaced.
                </DialogDescription>
              </DialogHeader>
              {contentJobs?.length === 0 ? (
                <p className="text-sm text-muted-foreground py-4 text-center">
                  No content jobs exist yet. Create one on the <strong>Content</strong> tab first.
                </p>
              ) : (
                <div className="space-y-2 max-h-64 overflow-y-auto">
                  {contentJobs?.map((job) => (
                    <label key={job.id} className="flex items-center gap-3 rounded-md border px-3 py-2 cursor-pointer hover:bg-muted/40">
                      <input
                        type="checkbox"
                        className="h-4 w-4"
                        checked={selectedContentJobIds.includes(job.id)}
                        onChange={(e) => setSelectedContentJobIds((prev) =>
                          e.target.checked ? [...prev, job.id] : prev.filter((x) => x !== job.id)
                        )}
                      />
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium truncate">{job.name}</p>
                        <p className="text-xs text-muted-foreground capitalize">{job.jobType} · {job.status} · {job.totalItems} items</p>
                      </div>
                    </label>
                  ))}
                </div>
              )}
              <DialogFooter>
                <Button variant="outline" onClick={() => { setAssignContentDialogWaveId(null); setSelectedContentJobIds([]); }}>Cancel</Button>
                <Button
                  disabled={assignContentJobsMutation.isPending || !contentJobs?.length}
                  onClick={() => assignContentDialogWaveId && assignContentJobsMutation.mutate({ waveId: assignContentDialogWaveId, jobIds: selectedContentJobIds })}
                >
                  {assignContentJobsMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                  Save Assignment
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>

          {/* Assign User Batches Dialog */}
          <Dialog open={!!assignUserBatchDialogWaveId} onOpenChange={(open) => { if (!open) { setAssignUserBatchDialogWaveId(null); setSelectedUserBatchIds([]); } }}>
            <DialogContent>
              <DialogHeader>
                <DialogTitle>Assign User Batches</DialogTitle>
                <DialogDescription>Select which user migration batches to include in this wave. Existing assignments will be replaced.</DialogDescription>
              </DialogHeader>
              {!userMigrationBatches?.length ? (
                <p className="py-4 text-center text-sm text-muted-foreground">No user migration batches yet. Create one on the Users tab first.</p>
              ) : (
                <div className="max-h-64 space-y-2 overflow-y-auto">
                  {userMigrationBatches.map((batch) => (
                    <label key={batch.id} className="flex cursor-pointer items-center gap-3 rounded-md border p-3 hover:bg-muted/40">
                      <input
                        type="checkbox"
                        checked={selectedUserBatchIds.includes(batch.id)}
                        onChange={(e) => setSelectedUserBatchIds((prev) =>
                          e.target.checked ? [...prev, batch.id] : prev.filter((x) => x !== batch.id)
                        )}
                        className="h-4 w-4"
                      />
                      <div className="flex-1 min-w-0">
                        <p className="text-sm font-medium truncate">{batch.name}</p>
                        <p className="text-xs text-muted-foreground capitalize">{batch.status} · {batch.totalUsers} users</p>
                      </div>
                    </label>
                  ))}
                </div>
              )}
              <DialogFooter>
                <Button variant="outline" onClick={() => { setAssignUserBatchDialogWaveId(null); setSelectedUserBatchIds([]); }}>Cancel</Button>
                <Button
                  disabled={assignUserBatchesMutation.isPending || !userMigrationBatches?.length}
                  onClick={() => assignUserBatchDialogWaveId && assignUserBatchesMutation.mutate({ waveId: assignUserBatchDialogWaveId, batchIds: selectedUserBatchIds })}
                >
                  {assignUserBatchesMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                  Save Assignment
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </TabsContent>

        {/* ── Validation ── */}
        <TabsContent value="validation" className="mt-4 space-y-4">
          <div className="flex items-center justify-between">
            <div>
              <h2 className="text-base font-semibold">Post-Migration Validation</h2>
              <p className="text-sm text-muted-foreground">Verify migrated mailboxes, OneDrive accounts, and SharePoint sites are accessible in the target tenant.</p>
            </div>
            <Dialog open={validationDialogOpen} onOpenChange={setValidationDialogOpen}>
              <DialogTrigger asChild>
                <Button size="sm"><ShieldCheck className="mr-1 h-3 w-3" />Run Validation</Button>
              </DialogTrigger>
              <DialogContent>
                <DialogHeader>
                  <DialogTitle>Start Validation Run</DialogTitle>
                  <DialogDescription>Checks all completed mailboxes, OneDrive accounts, and SharePoint sites against the target tenant.</DialogDescription>
                </DialogHeader>
                <div className="space-y-3">
                  <div className="space-y-1">
                    <label className="text-sm font-medium">Name <span className="text-muted-foreground">(optional)</span></label>
                    <input
                      className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                      placeholder="e.g. Wave 1 Validation"
                      value={validationName}
                      onChange={(e) => setValidationName(e.target.value)}
                    />
                  </div>
                  <div className="space-y-1">
                    <label className="text-sm font-medium">Scope to wave <span className="text-muted-foreground">(optional — leave blank for all completed items)</span></label>
                    <select
                      className="w-full rounded-md border bg-background px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-ring"
                      value={validationWaveFilter}
                      onChange={(e) => setValidationWaveFilter(e.target.value)}
                    >
                      <option value="">All completed items</option>
                      {waves?.filter((w) => w.status === "completed" || w.status === "running").map((w) => (
                        <option key={w.id} value={w.id}>{w.name}</option>
                      ))}
                    </select>
                  </div>
                </div>
                <DialogFooter>
                  <Button variant="outline" onClick={() => setValidationDialogOpen(false)}>Cancel</Button>
                  <Button onClick={() => createValidationMutation.mutate()} disabled={createValidationMutation.isPending}>
                    {createValidationMutation.isPending && <Loader2 className="mr-2 h-4 w-4 animate-spin" />}
                    Start
                  </Button>
                </DialogFooter>
              </DialogContent>
            </Dialog>
          </div>

          {validationLoading ? (
            <div className="space-y-3">{Array.from({ length: 2 }).map((_, i) => <Skeleton key={i} className="h-28 w-full" />)}</div>
          ) : validationRuns?.length === 0 ? (
            <Card>
              <CardContent className="flex flex-col items-center gap-3 py-12 text-center text-muted-foreground">
                <ShieldCheck className="h-10 w-10 opacity-30" />
                <p className="text-sm">No validation runs yet.</p>
                <p className="text-xs">Run validation after completing a wave to verify objects migrated successfully.</p>
                <Button size="sm" variant="outline" onClick={() => setValidationDialogOpen(true)}>
                  <ShieldCheck className="mr-1 h-3 w-3" />Run first validation
                </Button>
              </CardContent>
            </Card>
          ) : (
            <div className="space-y-3">
              {validationRuns?.map((run) => {
                const isExpanded = expandedRunId === run.id;
                const passRate = run.totalChecks > 0
                  ? Math.round((run.passedChecks / run.totalChecks) * 100)
                  : 0;

                return (
                  <Card key={run.id}>
                    <CardHeader className="pb-3">
                      <div className="flex items-start justify-between gap-3">
                        <div className="min-w-0">
                          <div className="flex items-center gap-2 flex-wrap">
                            <span className="font-semibold text-sm">{run.name}</span>
                            {run.status === "completed" && run.failedChecks === 0 && (
                              <span className="inline-flex items-center gap-1 rounded-full bg-green-100 px-2 py-0.5 text-xs font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400">
                                <ShieldCheck className="h-3 w-3" />All passed
                              </span>
                            )}
                            {run.status === "completed" && run.failedChecks > 0 && (
                              <span className="inline-flex items-center gap-1 rounded-full bg-destructive/10 px-2 py-0.5 text-xs font-medium text-destructive">
                                <ShieldAlert className="h-3 w-3" />{run.failedChecks} failed
                              </span>
                            )}
                            {run.status === "running" && (
                              <span className="inline-flex items-center gap-1 rounded-full bg-yellow-100 px-2 py-0.5 text-xs font-medium text-yellow-700 dark:bg-yellow-900/30 dark:text-yellow-400">
                                <Loader2 className="h-3 w-3 animate-spin" />Running
                              </span>
                            )}
                            {run.status === "pending" && (
                              <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-xs font-medium text-muted-foreground">
                                <Clock className="h-3 w-3" />Pending
                              </span>
                            )}
                          </div>
                          {run.waveId && (
                            <p className="text-xs text-muted-foreground mt-0.5">
                              Scoped to: {waves?.find((w) => w.id === run.waveId)?.name ?? run.waveId}
                            </p>
                          )}
                        </div>
                        <div className="flex items-center gap-1">
                          <Button
                            size="sm"
                            variant="ghost"
                            onClick={() => setExpandedRunId(isExpanded ? null : run.id)}
                          >
                            {isExpanded ? "Hide checks" : "View checks"}
                          </Button>
                          {run.status !== "pending" && run.status !== "running" && (
                            <Button
                              size="sm"
                              variant="ghost"
                              className="text-destructive hover:text-destructive"
                              title="Delete validation run"
                              onClick={() => {
                                if (confirm(`Delete validation run "${run.name}"? This will remove all of its check records.`)) {
                                  if (isExpanded) setExpandedRunId(null);
                                  deleteValidationMutation.mutate(run.id);
                                }
                              }}
                              disabled={deleteValidationMutation.isPending}
                            >
                              <Trash2 className="h-3 w-3" />
                            </Button>
                          )}
                        </div>
                      </div>
                    </CardHeader>
                    <CardContent className="pt-0 space-y-3">
                      {/* Summary stats */}
                      <div className="flex flex-wrap gap-3">
                        <div className="flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm">
                          <ShieldCheck className="h-3.5 w-3.5 text-green-500" />
                          <span className="font-semibold text-green-600">{run.passedChecks}</span>
                          <span className="text-muted-foreground">passed</span>
                        </div>
                        <div className="flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm">
                          <ShieldAlert className="h-3.5 w-3.5 text-destructive" />
                          <span className="font-semibold text-destructive">{run.failedChecks}</span>
                          <span className="text-muted-foreground">failed</span>
                        </div>
                        {run.warningChecks > 0 && (
                          <div className="flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm">
                            <AlertCircle className="h-3.5 w-3.5 text-yellow-500" />
                            <span className="font-semibold text-yellow-600">{run.warningChecks}</span>
                            <span className="text-muted-foreground">warnings</span>
                          </div>
                        )}
                        <div className="flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm text-muted-foreground">
                          {run.totalChecks} total
                        </div>
                        {run.status === "completed" && (
                          <div className="flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-sm text-muted-foreground">
                            {passRate}% pass rate
                          </div>
                        )}
                      </div>

                      {/* Progress bar for running/completed */}
                      {run.totalChecks > 0 && (
                        <div className="space-y-1">
                          <Progress value={run.progressPercent} className="h-1.5" />
                          {run.status === "running" && (
                            <p className="text-xs text-muted-foreground">
                              {run.passedChecks + run.failedChecks + run.warningChecks} / {run.totalChecks} checked
                            </p>
                          )}
                        </div>
                      )}

                      {/* Timing */}
                      <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
                        {run.startedAt && (
                          <span className="flex items-center gap-1">
                            <Clock className="h-3 w-3" />Started: {relativeTime(run.startedAt)}
                          </span>
                        )}
                        {run.completedAt && (
                          <span className="flex items-center gap-1">
                            <CheckCircle2 className="h-3 w-3" />Completed: {relativeTime(run.completedAt)}
                          </span>
                        )}
                      </div>

                      {/* Expanded check results */}
                      {isExpanded && (
                        <div className="mt-2 rounded-md border">
                          {!expandedChecks ? (
                            <div className="p-4"><Skeleton className="h-20 w-full" /></div>
                          ) : expandedChecks.length === 0 ? (
                            <p className="p-4 text-sm text-muted-foreground">No checks recorded yet.</p>
                          ) : (
                            <table className="w-full text-xs">
                              <thead>
                                <tr className="border-b bg-muted/40">
                                  <th className="px-3 py-2 text-left font-medium text-muted-foreground">Type</th>
                                  <th className="px-3 py-2 text-left font-medium text-muted-foreground">Source</th>
                                  <th className="px-3 py-2 text-left font-medium text-muted-foreground">Target</th>
                                  <th className="px-3 py-2 text-left font-medium text-muted-foreground">Result</th>
                                </tr>
                              </thead>
                              <tbody className="divide-y">
                                {expandedChecks.map((chk) => (
                                  <tr key={chk.id} className="hover:bg-muted/20">
                                    <td className="px-3 py-2 capitalize text-muted-foreground">{chk.checkType}</td>
                                    <td className="px-3 py-2 font-mono max-w-[180px] truncate" title={chk.sourceReference}>{chk.sourceReference}</td>
                                    <td className="px-3 py-2 font-mono max-w-[180px] truncate" title={chk.targetReference}>{chk.targetReference}</td>
                                    <td className="px-3 py-2">
                                      {chk.outcome === "pass" && (
                                        <span className="inline-flex items-center gap-1 text-green-600">
                                          <ShieldCheck className="h-3 w-3" />Pass
                                        </span>
                                      )}
                                      {chk.outcome === "fail" && (
                                        <div>
                                          <span className="inline-flex items-center gap-1 text-destructive">
                                            <ShieldAlert className="h-3 w-3" />Fail
                                          </span>
                                          {chk.errorMessage && (
                                            <p className="mt-0.5 text-muted-foreground leading-snug">{chk.errorMessage}</p>
                                          )}
                                        </div>
                                      )}
                                      {chk.outcome === "warning" && (
                                        <div>
                                          <span className="inline-flex items-center gap-1 text-yellow-600">
                                            <AlertCircle className="h-3 w-3" />Warning
                                          </span>
                                          {chk.errorMessage && (
                                            <p className="mt-0.5 text-muted-foreground leading-snug">{chk.errorMessage}</p>
                                          )}
                                        </div>
                                      )}
                                    </td>
                                  </tr>
                                ))}
                              </tbody>
                            </table>
                          )}
                        </div>
                      )}
                    </CardContent>
                  </Card>
                );
              })}
            </div>
          )}
        </TabsContent>

        {/* ── Jobs ── */}
        <TabsContent value="jobs" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Migration Jobs</CardTitle></CardHeader>
            <CardContent>
              {jobsLoading ? <Skeleton className="h-40 w-full" /> : (
                <table className="w-full text-sm">
                  <thead>
                    <tr className="border-b">
                      <th className="py-2 text-left font-medium text-muted-foreground">Type</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Status</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Progress</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Items</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Started</th>
                      <th className="py-2 text-left font-medium text-muted-foreground">Actions</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y">
                    {jobs?.map((job) => (
                      <tr key={job.id} className="hover:bg-muted/40">
                        <td className="py-2 capitalize font-medium">{job.type.replace(/_/g, " ")}</td>
                        <td className="py-2"><JobStatusBadge status={job.status} /></td>
                        <td className="py-2 w-32">
                          <div className="flex items-center gap-2">
                            <Progress value={job.progress} className="h-1.5 flex-1" />
                            <span className="text-xs text-muted-foreground w-7">{job.progress}%</span>
                          </div>
                        </td>
                        <td className="py-2 text-muted-foreground text-xs">
                          {job.itemsProcessed}/{job.itemsTotal}
                          {job.itemsFailed > 0 && <span className="ml-1 text-destructive">({job.itemsFailed} failed)</span>}
                        </td>
                        <td className="py-2 text-muted-foreground">{relativeTime(job.startedAt)}</td>
                        <td className="py-2">
                          <div className="flex gap-1">
                            {(job.status === "failed" || job.status === "cancelled") && (
                              <Button variant="ghost" size="sm" onClick={() => retryJobMutation.mutate(job.id)}>
                                <RotateCcw className="h-3 w-3" />
                              </Button>
                            )}
                            {(job.status === "running" || job.status === "queued") && (
                              <Button variant="ghost" size="sm" onClick={() => cancelJobMutation.mutate(job.id)}>
                                <XCircle className="h-3 w-3" />
                              </Button>
                            )}
                          </div>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              )}
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Audit ── */}
        <TabsContent value="audit" className="mt-4">
          <Card>
            <CardHeader><CardTitle className="text-base">Audit Log</CardTitle></CardHeader>
            <CardContent>
              {auditLoading ? <Skeleton className="h-40 w-full" /> : (
                <div className="divide-y">
                  {auditPage?.items.map((event) => (
                    <div key={event.id} className="flex items-start gap-3 py-3 text-sm">
                      {event.outcome === "success"
                        ? <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-500" />
                        : <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-destructive" />}
                      <div className="min-w-0 flex-1">
                        <div className="font-medium">{event.action.replace(/_/g, " ")}</div>
                        <div className="text-xs text-muted-foreground">{event.actor} · {event.resource}</div>
                      </div>
                      <div className="shrink-0 flex items-center gap-1 text-xs text-muted-foreground">
                        <Clock className="h-3 w-3" />
                        {formatDateTime(event.timestamp)}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>
    </div>
  );
}
