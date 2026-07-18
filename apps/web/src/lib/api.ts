import type { AuthMethod, DiagCheck } from "@/types";
import type {
  Tenant,
  CreateTenantDto,
  MigrationProject,
  CreateProjectDto,
  Scan,
  ScanType,
  ScannedUser,
  ScannedGroup,
  ScannedMailbox,
  ScannedSite,
  ScannedOneDrive,
  ScannedDomain,
  ScanIssue,
  IdentityMap,
  Job,
  AuditEvent,
  PagedResult,
  MigrationWave,
  CreateWaveDto,
  UpdateWaveDto,
  ValidationRun,
  ValidationCheck,
  CreateValidationRunDto,
  DomainRule,
  CreateDomainRuleDto,
  TransformPreviewResult,
  MailboxBatch,
  CreateMailboxBatchDto,
  ContentMigrationJob,
  ContentMigrationItem,
  CreateContentMigrationJobDto,
  UserMigrationBatch,
  UserMigrationEntry,
  CreateUserMigrationBatchDto,
  MailboxMigrationEntry,
  DomainCutoverJob,
} from "@/types";

import { getAccessToken, handleUnauthorized } from "./auth";

const BASE_URL = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5000/api";

async function authHeaders(): Promise<Record<string, string>> {
  // Mode-aware: Entra ID (MSAL silent renewal) or the local dev token —
  // both flow through getAccessToken().
  const token = await getAccessToken();
  return token ? { Authorization: `Bearer ${token}` } : {};
}

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE_URL}${path}`, {
    headers: { "Content-Type": "application/json", ...(await authHeaders()), ...options?.headers },
    ...options,
  });
  if (res.status === 401) {
    await handleUnauthorized();
    throw new Error("Session expired. Redirecting to login.");
  }
  if (!res.ok) {
    const text = await res.text().catch(() => res.statusText);
    let message = text;
    try {
      const json = JSON.parse(text);
      if (json.message) message = json.message;
      else if (json.title) message = json.title;

      // Append per-user license-check reasons when the start-batch precheck fails —
      // the controller returns { message, unlicensedUsers: [{ upn, reason }] } and the
      // generic message alone hides which user / which plan / what state is the problem.
      if (Array.isArray(json.unlicensedUsers) && json.unlicensedUsers.length > 0) {
        const detail = json.unlicensedUsers
          .map((u: { upn?: string; reason?: string }) => `${u.upn ?? "?"}: ${u.reason ?? "(no reason)"}`)
          .join(" | ");
        message = `${message} — ${detail}`;
      }
    } catch {
      // not JSON — use raw text
    }
    throw new Error(`API error ${res.status}: ${message}`);
  }
  if (res.status === 204 || res.headers.get("content-length") === "0") {
    return undefined as T;
  }
  return res.json() as Promise<T>;
}

// ─── Tenants ──────────────────────────────────────────────────────────────────

export const tenantsApi = {
  list: () => request<Tenant[]>("/tenants"),
  get: (id: string) => request<Tenant>(`/tenants/${id}`),
  create: (dto: CreateTenantDto) =>
    request<Tenant>("/tenants", { method: "POST", body: JSON.stringify(dto) }),
  updateCredentials: (id: string, dto: {
    authMethod: AuthMethod;
    appClientId?: string;
    clientSecret?: string;
    clientCertificateBase64?: string;
    clientCertificatePassword?: string;
    clientCertificateThumbprint?: string;
  }) => request(`/tenants/${id}/credentials`, { method: "PUT", body: JSON.stringify(dto) }),
  verify: (id: string) =>
    request<{ success: boolean; message: string }>(`/tenants/${id}/verify`, { method: "POST" }),
  diagnoseExo: (id: string) =>
    request<{
      success: boolean;
      tenantId?: string;
      appClientId?: string;
      token?: {
        aud: string | null;
        appid: string | null;
        tid: string | null;
        roles: string[];
        expiresAt: string | null;
        hasExchangeManageAsApp: boolean;
        diagnosis: string;
      };
      error?: string;
      detail?: string;
    }>(`/tenants/${id}/diagnose-exo`, { method: "POST" }),
  delete: (id: string) => request(`/tenants/${id}`, { method: "DELETE" }),
};

// ─── Projects ─────────────────────────────────────────────────────────────────

export const projectsApi = {
  list: () => request<MigrationProject[]>("/projects"),
  get: (id: string) => request<MigrationProject>(`/projects/${id}`),
  create: (dto: CreateProjectDto) =>
    request<MigrationProject>("/projects", { method: "POST", body: JSON.stringify(dto) }),
  dependencyCheck: (id: string) =>
    request<import("@/types").DependencyCheckResult>(`/projects/${id}/dependency-check`),
  crossTenantSyncStatus: (id: string) =>
    request<import("@/types").CrossTenantSyncDiscoveryResult>(`/projects/${id}/cross-tenant-sync-status`),
  setTargetDirectoryMode: (id: string, mode: import("@/types").TargetDirectoryMode) =>
    request<MigrationProject>(`/projects/${id}/target-directory-mode`, {
      method: "PUT",
      body: JSON.stringify({ mode }),
    }),
  setupExchange: (id: string) =>
    request<{
      sourceOrgRelationship: { status: "created" | "existing" | "failed"; domain: string; error?: string };
      targetOrgRelationship: { status: "created" | "existing" | "failed"; domain: string; error?: string };
      migrationEndpoint:     { status: "created" | "existing" | "failed"; identity: string; error?: string };
      warnings: string[];
      mock?: boolean;
    }>(`/projects/${id}/setup-exchange`, { method: "POST" }),
  delete: (id: string) => request(`/projects/${id}`, { method: "DELETE" }),
};

// ─── Scans ────────────────────────────────────────────────────────────────────

export const scansApi = {
  list: (projectId?: string) =>
    request<Scan[]>(`/scans${projectId ? `?projectId=${projectId}` : ""}`),
  get: (id: string) => request<Scan>(`/scans/${id}`),
  start: (tenantId: string, projectId: string, scanType: ScanType) =>
    request<Scan>("/scans", { method: "POST", body: JSON.stringify({ tenantId, projectId, scanType }) }),
  getUsers: (id: string) => request<ScannedUser[]>(`/scans/${id}/users`),
  getGroups: (id: string) => request<ScannedGroup[]>(`/scans/${id}/groups`),
  getMailboxes: (id: string) => request<ScannedMailbox[]>(`/scans/${id}/mailboxes`),
  getSites: (id: string) => request<ScannedSite[]>(`/scans/${id}/sites`),
  getOneDrive: (id: string) => request<ScannedOneDrive[]>(`/scans/${id}/onedrive`),
  getDomains: (id: string) => request<ScannedDomain[]>(`/scans/${id}/domains`),
  getIssues: (id: string) => request<ScanIssue[]>(`/scans/${id}/issues`),
};

// ─── Identity Maps ────────────────────────────────────────────────────────────

export const identityMapsApi = {
  list: (projectId: string) => request<IdentityMap[]>(`/projects/${projectId}/identity-maps`),
  autoMap: (projectId: string) =>
    request<{ mapped: number; conflicts: number; unmapped: number }>(
      `/projects/${projectId}/identity-maps/auto-map`,
      { method: "POST" }
    ),
  update: (projectId: string, mapId: string, targetUpn: string) =>
    request<IdentityMap>(`/projects/${projectId}/identity-maps/${mapId}`, {
      method: "PUT",
      body: JSON.stringify({ targetUpn }),
    }),
  applyDomainRules: (projectId: string) =>
    request<{ applied: number; skipped: number }>(
      `/projects/${projectId}/identity-maps/apply-domain-rules`,
      { method: "POST" }
    ),
};

// ─── Jobs ─────────────────────────────────────────────────────────────────────

export const jobsApi = {
  list: (projectId?: string) =>
    request<Job[]>(`/jobs${projectId ? `?projectId=${projectId}` : ""}`),
  get: (id: string) => request<Job>(`/jobs/${id}`),
  retry: (id: string) => request<Job>(`/jobs/${id}/retry`, { method: "POST" }),
  cancel: (id: string) => request<Job>(`/jobs/${id}/cancel`, { method: "POST" }),
};

// ─── Waves ────────────────────────────────────────────────────────────────────

export const wavesApi = {
  list: (projectId: string) => request<MigrationWave[]>(`/projects/${projectId}/waves`),
  get: (projectId: string, waveId: string) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}`),
  create: (projectId: string, dto: CreateWaveDto) =>
    request<MigrationWave>(`/projects/${projectId}/waves`, { method: "POST", body: JSON.stringify(dto) }),
  update: (projectId: string, waveId: string, dto: UpdateWaveDto) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}`, { method: "PUT", body: JSON.stringify(dto) }),
  delete: (projectId: string, waveId: string) =>
    request(`/projects/${projectId}/waves/${waveId}`, { method: "DELETE" }),
  start: (projectId: string, waveId: string) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}/start`, { method: "POST" }),
  cancel: (projectId: string, waveId: string) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}/cancel`, { method: "POST" }),
  assignBatches: (projectId: string, waveId: string, batchIds: string[]) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}/batches`, {
      method: "PUT",
      body: JSON.stringify({ batchIds }),
    }),
  assignContentJobs: (projectId: string, waveId: string, jobIds: string[]) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}/content-jobs`, {
      method: "PUT",
      body: JSON.stringify({ jobIds }),
    }),
  assignUserBatches: (projectId: string, waveId: string, batchIds: string[]) =>
    request<MigrationWave>(`/projects/${projectId}/waves/${waveId}/user-batches`, {
      method: "PUT",
      body: JSON.stringify({ batchIds }),
    }),
};

// ─── Validation ───────────────────────────────────────────────────────────────

export const validationApi = {
  list: (projectId: string) => request<ValidationRun[]>(`/projects/${projectId}/validations`),
  get: (projectId: string, runId: string) =>
    request<ValidationRun>(`/projects/${projectId}/validations/${runId}`),
  create: (projectId: string, dto: CreateValidationRunDto) =>
    request<ValidationRun>(`/projects/${projectId}/validations`, { method: "POST", body: JSON.stringify(dto) }),
  getChecks: (projectId: string, runId: string) =>
    request<ValidationCheck[]>(`/projects/${projectId}/validations/${runId}/checks`),
  delete: (projectId: string, runId: string) =>
    request<void>(`/projects/${projectId}/validations/${runId}`, { method: "DELETE" }),
};

// ─── Domain Rules ─────────────────────────────────────────────────────────────

export const domainRulesApi = {
  list: (projectId: string) => request<DomainRule[]>(`/projects/${projectId}/domain-rules`),
  get: (projectId: string, ruleId: string) =>
    request<DomainRule>(`/projects/${projectId}/domain-rules/${ruleId}`),
  create: (projectId: string, dto: CreateDomainRuleDto) =>
    request<DomainRule>(`/projects/${projectId}/domain-rules`, { method: "POST", body: JSON.stringify(dto) }),
  update: (projectId: string, ruleId: string, dto: CreateDomainRuleDto) =>
    request<DomainRule>(`/projects/${projectId}/domain-rules/${ruleId}`, {
      method: "PUT",
      body: JSON.stringify(dto),
    }),
  delete: (projectId: string, ruleId: string) =>
    request(`/projects/${projectId}/domain-rules/${ruleId}`, { method: "DELETE" }),
  preview: (projectId: string, sampleUpns: string[]) =>
    request<TransformPreviewResult[]>(`/projects/${projectId}/domain-rules/preview`, {
      method: "POST",
      body: JSON.stringify({ sampleUpns }),
    }),
};

// ─── Mailbox Migration Batches ────────────────────────────────────────────────

export interface HybridHandoffKit {
  batchId: string;
  batchName: string;
  userCount: number;
  alreadySyncedCount: number;
  generatedAt: string;
  script: string;
}

export const mailboxBatchesApi = {
  hybridHandoff: (projectId: string, batchId: string) =>
    request<HybridHandoffKit>(`/projects/${projectId}/mailbox-batches/${batchId}/hybrid-handoff`),
  list: (projectId: string) => request<MailboxBatch[]>(`/projects/${projectId}/mailbox-batches`),
  get: (projectId: string, batchId: string) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches/${batchId}`),
  create: (projectId: string, dto: CreateMailboxBatchDto) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches`, { method: "POST", body: JSON.stringify(dto) }),
  start: (projectId: string, batchId: string) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches/${batchId}/start`, { method: "POST" }),
  stop: (projectId: string, batchId: string) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches/${batchId}/stop`, { method: "POST" }),
  complete: (projectId: string, batchId: string) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches/${batchId}/complete`, { method: "POST" }),
  retry: (projectId: string, batchId: string) =>
    request<MailboxBatch>(`/projects/${projectId}/mailbox-batches/${batchId}/retry`, { method: "POST" }),
  getEntries: (projectId: string, batchId: string) =>
    request<MailboxMigrationEntry[]>(`/projects/${projectId}/mailbox-batches/${batchId}/entries`),
  skipEntry: (projectId: string, batchId: string, entryId: string) =>
    request<MailboxMigrationEntry>(
      `/projects/${projectId}/mailbox-batches/${batchId}/entries/${entryId}/skip`,
      { method: "POST" }),
  skipFailures: (projectId: string, batchId: string) =>
    request<MailboxBatch>(
      `/projects/${projectId}/mailbox-batches/${batchId}/skip-failures`,
      { method: "POST" }),
  resetTarget: (projectId: string, batchId: string) =>
    request<{
      batchId: string;
      moveRequestsRemoved: number;
      mailUsersRemoved: number;
      softDeletedMailUsersPurged: number;
      exoBatchRemoved: boolean;
      entriesReset: number;
      warnings: string[];
      batch: MailboxBatch;
    }>(
      `/projects/${projectId}/mailbox-batches/${batchId}/reset-target`,
      { method: "POST" }),
  delete: (projectId: string, batchId: string) =>
    request<void>(`/projects/${projectId}/mailbox-batches/${batchId}`, { method: "DELETE" }),
};

// ─── User Migration Batches ───────────────────────────────────────────────────

export const userMigrationsApi = {
  list: (projectId: string) => request<UserMigrationBatch[]>(`/projects/${projectId}/user-migrations`),
  get: (projectId: string, batchId: string) =>
    request<UserMigrationBatch>(`/projects/${projectId}/user-migrations/${batchId}`),
  create: (projectId: string, dto: CreateUserMigrationBatchDto) =>
    request<UserMigrationBatch>(`/projects/${projectId}/user-migrations`, { method: "POST", body: JSON.stringify(dto) }),
  start: (projectId: string, batchId: string) =>
    request<UserMigrationBatch>(`/projects/${projectId}/user-migrations/${batchId}/start`, { method: "POST" }),
  stop: (projectId: string, batchId: string) =>
    request<UserMigrationBatch>(`/projects/${projectId}/user-migrations/${batchId}/stop`, { method: "POST" }),
  getEntries: (projectId: string, batchId: string) =>
    request<UserMigrationEntry[]>(`/projects/${projectId}/user-migrations/${batchId}/entries`),
  retryFailed: (projectId: string, batchId: string) =>
    request<{ message: string; batch: UserMigrationBatch }>(
      `/projects/${projectId}/user-migrations/${batchId}/retry-failed`, { method: "POST" }),
  retry: (projectId: string, batchId: string) =>
    request<{ message: string; batch: UserMigrationBatch }>(
      `/projects/${projectId}/user-migrations/${batchId}/retry`, { method: "POST" }),
  skipFailures: (projectId: string, batchId: string) =>
    request<UserMigrationBatch>(
      `/projects/${projectId}/user-migrations/${batchId}/skip-failures`, { method: "POST" }),
  delete: (projectId: string, batchId: string) =>
    request<void>(`/projects/${projectId}/user-migrations/${batchId}`, { method: "DELETE" }),
};

// ─── Content Migration Jobs ───────────────────────────────────────────────────

export const contentMigrationsApi = {
  list: (projectId: string) => request<ContentMigrationJob[]>(`/projects/${projectId}/content-migrations`),
  get: (projectId: string, jobId: string) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}`),
  create: (projectId: string, dto: CreateContentMigrationJobDto) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations`, { method: "POST", body: JSON.stringify(dto) }),
  update: (projectId: string, jobId: string, dto: { name?: string; items?: CreateContentMigrationJobDto["items"] }) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}`, { method: "PUT", body: JSON.stringify(dto) }),
  start: (projectId: string, jobId: string) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}/start`, { method: "POST" }),
  pause: (projectId: string, jobId: string) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}/pause`, { method: "POST" }),
  resume: (projectId: string, jobId: string) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}/resume`, { method: "POST" }),
  cancel: (projectId: string, jobId: string) =>
    request<ContentMigrationJob>(`/projects/${projectId}/content-migrations/${jobId}/cancel`, { method: "POST" }),
  getItems: (projectId: string, jobId: string) =>
    request<ContentMigrationItem[]>(`/projects/${projectId}/content-migrations/${jobId}/items`),
  delete: (projectId: string, jobId: string) =>
    request<void>(`/projects/${projectId}/content-migrations/${jobId}`, { method: "DELETE" }),
};

// ─── Domain Cutover ──────────────────────────────────────────────────────────

export const domainCutoverApi = {
  list: (projectId: string) =>
    request<DomainCutoverJob[]>(`/projects/${projectId}/domain-cutover`),
  get: (projectId: string, jobId: string) =>
    request<DomainCutoverJob>(`/projects/${projectId}/domain-cutover/${jobId}`),
  create: (projectId: string, domainName: string) =>
    request<DomainCutoverJob>(`/projects/${projectId}/domain-cutover`, {
      method: "POST",
      body: JSON.stringify({ domainName }),
    }),
  start: (projectId: string, jobId: string) =>
    request<DomainCutoverJob>(`/projects/${projectId}/domain-cutover/${jobId}/start`, { method: "POST" }),
  continue: (projectId: string, jobId: string) =>
    request<DomainCutoverJob>(`/projects/${projectId}/domain-cutover/${jobId}/continue`, { method: "POST" }),
  delete: (projectId: string, jobId: string) =>
    request<void>(`/projects/${projectId}/domain-cutover/${jobId}`, { method: "DELETE" }),
};

// ─── Audit ────────────────────────────────────────────────────────────────────

export const auditApi = {
  list: (page = 1, pageSize = 50) =>
    request<PagedResult<AuditEvent>>(`/audit?page=${page}&pageSize=${pageSize}`),
};

// ─── Auth ─────────────────────────────────────────────────────────────────────

export interface AuthTokenResponse {
  token: string;
  expiresAt: string;
  /** True when the account still uses the seeded default password — prompt a change. */
  mustChangePassword?: boolean;
}

// ─── Settings ────────────────────────────────────────────────────────────────

export interface AzureAutomationSettings {
  subscriptionId: string;
  resourceGroup: string;
  accountName: string;
  runbookName: string;
  jobPollIntervalSeconds: number;
  jobTimeoutMinutes: number;
}

export interface AzureIdentityResponse {
  tenantId: string;
  clientId: string;
  authMethod: "secret" | "certificate";
  clientSecretHint: string;
  hasCertificate: boolean;
  certificateThumbprint: string;
  isConfigured: boolean;
  /** Save-time live probe of the credential (an ARM token mint). */
  credentialTest?: { success: boolean; error?: string };
}

export interface AzureIdentityRequest {
  tenantId: string;
  clientId: string;
  authMethod: "secret" | "certificate";
  clientSecret?: string;
  certificateBase64?: string;
  certificatePassword?: string;
  certificateThumbprint?: string;
}

export interface CrossTenantMigrationSettings {
  appId: string;
  clientSecretHint: string;
  isConfigured: boolean;
}

/** Live ARM scan of the Azure Automation execution environment. */
export interface AutomationEnvironmentReport {
  generatedAt: string;
  isDeployed: boolean;
  summary: string;
  passCount: number;
  failCount: number;
  warnCount: number;
  unknownCount: number;
  checks: DiagCheck[];
}

export interface CrossTenantMigrationRequest {
  appId: string;
  /** Leave empty to keep the currently stored secret. */
  clientSecret?: string;
}

export const settingsApi = {
  getCrossTenantMigration: () =>
    request<CrossTenantMigrationSettings>("/settings/cross-tenant-migration"),
  updateCrossTenantMigration: (s: CrossTenantMigrationRequest) =>
    request<CrossTenantMigrationSettings>("/settings/cross-tenant-migration", {
      method: "PUT",
      body: JSON.stringify(s),
    }),
  getAzureAutomation: () =>
    request<AzureAutomationSettings>("/settings/azure-automation"),
  verifyAzureAutomation: () =>
    request<AutomationEnvironmentReport>("/settings/azure-automation/verify", {
      method: "POST",
    }),
  updateAzureAutomation: (s: AzureAutomationSettings) =>
    request<AzureAutomationSettings>("/settings/azure-automation", {
      method: "PUT",
      body: JSON.stringify(s),
    }),
  getAzureIdentity: () =>
    request<AzureIdentityResponse>("/settings/azure-identity"),
  updateAzureIdentity: (s: AzureIdentityRequest) =>
    request<AzureIdentityResponse>("/settings/azure-identity", {
      method: "PUT",
      body: JSON.stringify(s),
    }),
  deleteAzureIdentity: () =>
    request<AzureIdentityResponse>("/settings/azure-identity", {
      method: "DELETE",
    }),
};

export const authApi = {
  login: async (username: string, password: string): Promise<AuthTokenResponse> => {
    const res = await fetch(`${BASE_URL}/auth/token`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password }),
    });
    if (!res.ok) {
      const text = await res.text().catch(() => res.statusText);
      throw new Error(`Login failed ${res.status}: ${text}`);
    }
    return res.json() as Promise<AuthTokenResponse>;
  },
  me: () => request<MeResponse>("/auth/me"),
  changePassword: (currentPassword: string, newPassword: string) =>
    request<{ message: string }>("/auth/change-password", {
      method: "POST",
      body: JSON.stringify({ currentPassword, newPassword }),
    }),
};

export interface MeResponse {
  userName: string;
  roles: string[];
  authenticated: boolean;
}

// ─── Setup wizard ─────────────────────────────────────────────────────────────

export const setupApi = {
  plan: (projectId: string) =>
    request<import("@/types").SetupPlan>(`/setup/${projectId}`),
};

export const diagnosticsApi = {
  tenantPrereqs: (tenantId: string) =>
    request<import("@/types").TenantPrereqReport>(
      `/diagnostics/tenant-prereqs/${tenantId}`,
      { method: "POST" },
    ),
};

// ─── Health / System Status ───────────────────────────────────────────────────
// Health endpoints live at the ORIGIN ROOT (/health/*), NOT under /api, and are
// anonymous. We derive the origin by stripping a trailing "/api" from BASE_URL
// and fetch directly so we can tolerate a 503 (readiness still returns the JSON
// body) and distinguish "backend unreachable" (network error) from "unhealthy".

const HEALTH_ORIGIN = BASE_URL.replace(/\/api\/?$/, "");

export type HealthState = "Healthy" | "Degraded" | "Unhealthy";

export interface HealthCheck {
  name: string;
  status: HealthState;
  description: string | null;
  durationMs: number;
  error: string | null;
}

export interface HealthReport {
  status: HealthState;
  /** Running platform version (also available via versionApi.get()). */
  version?: string;
  totalDurationMs: number;
  checks: HealthCheck[];
}

export interface VersionInfo {
  version: string;
  runbookVersion: string;
  environment: string;
}

export const versionApi = {
  /** Anonymous — running platform version, runbook version, environment. */
  get: () => request<VersionInfo>("/version"),
};

/** Result of a readiness probe. `reachable=false` means the API could not be contacted at all. */
export interface HealthProbe {
  reachable: boolean;
  report: HealthReport | null;
}

export const healthApi = {
  /** Readiness — tolerates 503 (body still present) and reports unreachable on network failure. */
  ready: async (): Promise<HealthProbe> => {
    try {
      const res = await fetch(`${HEALTH_ORIGIN}/health/ready`, {
        headers: { Accept: "application/json" },
        cache: "no-store",
      });
      // Healthy → 200, Unhealthy → 503; both carry the JSON payload.
      const report = (await res.json()) as HealthReport;
      return { reachable: true, report };
    } catch {
      // Network error / CORS / DNS — the API process isn't answering.
      return { reachable: false, report: null };
    }
  },
  /** Liveness — true when the API process is up. */
  live: async (): Promise<boolean> => {
    try {
      const res = await fetch(`${HEALTH_ORIGIN}/health/live`, { cache: "no-store" });
      return res.ok;
    } catch {
      return false;
    }
  },
};
