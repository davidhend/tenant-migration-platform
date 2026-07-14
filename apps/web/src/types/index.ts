// ─── Tenants ────────────────────────────────────────────────────────────────

export type TenantRole = "source" | "target";
export type AuthMethod = "certificate" | "secret";
export type ConnectionStatus = "connected" | "pending" | "failed" | "unverified";

export interface Tenant {
  id: string;
  displayName: string;
  tenantId: string;
  role: TenantRole;
  appClientId: string;
  authMethod: AuthMethod;
  clientSecretHint?: string;
  adminConsentGranted: boolean;
  connectionStatus: ConnectionStatus;
  onMicrosoftDomain?: string;
  lastVerifiedAt?: string;
  createdAt: string;
}

export interface CreateTenantDto {
  displayName: string;
  tenantId: string;
  role: TenantRole;
  appClientId: string;
  authMethod: AuthMethod;
  clientSecret?: string;
}

// ─── Projects ────────────────────────────────────────────────────────────────

export type ProjectStatus = "draft" | "active" | "paused" | "completed";

export interface MigrationProject {
  id: string;
  name: string;
  sourceTenantId: string;
  targetTenantId: string;
  sourceTenant?: Tenant;
  targetTenant?: Tenant;
  status: ProjectStatus;
  createdAt: string;
}

export interface CreateProjectDto {
  name: string;
  sourceTenantId: string;
  targetTenantId: string;
}

// ─── Scans ───────────────────────────────────────────────────────────────────

export type ScanType = "full" | "users" | "mailboxes" | "sharepoint" | "onedrive" | "domains";
export type ScanStatus = "queued" | "running" | "completed" | "failed";

export interface ScanSummary {
  userCount: number;
  groupCount: number;
  mailboxCount: number;
  mailboxTotalSizeGb: number;
  siteCount: number;
  oneDriveCount: number;
  domainCount: number;
  blockerCount: number;
  warningCount: number;
  readinessScore: number;
}

export interface Scan {
  id: string;
  tenantId: string;
  projectId?: string;
  scanType: ScanType;
  status: ScanStatus;
  progress: number;
  startedAt?: string;
  completedAt?: string;
  createdAt: string;
  errorMessage?: string;
  summary?: ScanSummary;
}

export interface ScannedUser {
  id: string;
  scanId: string;
  sourceObjectId: string;
  displayName: string;
  upn: string;
  accountEnabled: boolean;
  licenses: string[];
  hasMailbox: boolean;
  mailboxSizeGb: number;
  mailboxType?: string;
  oneDriveSizeGb: number;
  mfaEnabled: boolean;
  proxyAddresses?: string[];
}

export interface ScannedGroup {
  id: string;
  scanId: string;
  sourceObjectId: string;
  displayName: string;
  groupType: string;
  mailEnabled: boolean;
  securityEnabled: boolean;
  memberCount: number;
}

export interface ScannedMailbox {
  id: string;
  scanId: string;
  displayName: string;
  primarySmtpAddress: string;
  mailboxType: string;
  sizeGb: number;
  itemCount: number;
  lastLogonTime?: string;
  hasArchive: boolean;
  archiveSizeGb?: number;
}

export interface ScannedSite {
  id: string;
  scanId: string;
  siteUrl: string;
  title: string;
  template: string;
  storageUsedGb: number;
  storageQuotaGb: number;
  owners: string[];
  lastActivityDate?: string;
  hasUniquePermissions: boolean;
  subsiteCount: number;
}

export interface ScannedOneDrive {
  id: string;
  scanId: string;
  ownerUpn: string;
  ownerDisplayName: string;
  driveUrl: string;
  storageUsedGb: number;
  storageQuotaGb: number;
  lastModified?: string;
  fileCount: number;
}

export interface ScannedDomain {
  id: string;
  scanId: string;
  name: string;
  isDefault: boolean;
  isVerified: boolean;
  userCount: number;
}

export type IssueSeverity = "blocker" | "warning" | "info";

export interface ScanIssue {
  id: string;
  scanId: string;
  severity: IssueSeverity;
  category: string;
  code: string;
  title: string;
  description: string;
  affectedObjectCount: number;
  remediationSteps: string[];
}

// ─── Identity Mapping ────────────────────────────────────────────────────────

export type MappingStatus = "mapped" | "unmapped" | "conflict" | "skipped";
export type MappingSource = "auto" | "manual" | "csv";

export interface IdentityMap {
  id: string;
  projectId: string;
  sourceUpn: string;
  targetUpn?: string;
  status: MappingStatus;
  conflictReason?: string;
  mappingSource: MappingSource;
}

// ─── Jobs ─────────────────────────────────────────────────────────────────────

export type JobType =
  | "scan"
  | "identity_map"
  | "mailbox_migrate"
  | "sharepoint_migrate"
  | "onedrive_migrate";

export type JobStatus = "queued" | "running" | "completed" | "failed" | "cancelled";

export interface Job {
  id: string;
  projectId: string;
  scanId?: string;
  type: JobType;
  status: JobStatus;
  progress: number;
  itemsTotal: number;
  itemsProcessed: number;
  itemsFailed: number;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  errorMessage?: string;
}

// ─── Mailbox Migration Batches ───────────────────────────────────────────────

export type BatchStatus = "draft" | "validating" | "syncing" | "synced" | "completing" | "completed" | "stopped" | "failed";

/**
 * Transport selection for a mailbox batch.
 * - `graphCopy`  — per-message Graph copy. No EXO infra; ~1–3 msg/sec/user; lossy.
 * - `nativeMrs`  — server-side cross-tenant MRS via Exchange Online. Full fidelity, ~1–2 GB/hr.
 */
export type MailboxMigrationStrategy = "graphCopy" | "nativeMrs";

export interface MailboxBatch {
  id: string;
  projectId: string;
  name: string;
  status: BatchStatus;
  strategy: MailboxMigrationStrategy;
  totalMailboxes: number;
  syncedMailboxes: number;
  failedMailboxes: number;
  skippedMailboxes: number;
  progressPercent: number;
  exoMigrationBatchId?: string;
  targetFolderName?: string;
  errorMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  lastSyncedAt?: string;
  /** Present only on the start response when license auto-assignment ran. */
  licenseAssignment?: LicenseAssignmentSummary;
}

export interface LicenseAssignmentSummary {
  attempted: boolean;
  side: string;
  skuFound: boolean;
  seatsAvailable: number;
  assigned: number;
  alreadyLicensed: number;
  failed: number;
  failures: { upn: string; reason: string }[];
  warning?: string;
}

export interface CreateMailboxBatchDto {
  name: string;
  mailboxes: { sourceUpn: string; targetUpn: string }[];
  targetFolderName?: string;
  strategy?: MailboxMigrationStrategy;
}

export interface MailboxMigrationEntry {
  id: string;
  batchId: string;
  sourceUpn: string;
  targetUpn: string;
  status: string;
  itemsSyncedPercent: number;
  messagesCopied: number;
  totalMessages: number;
  errorMessage?: string;
  lastUpdated?: string;
}

// ─── User Migration Batches (Graph POST /users) ──────────────────────────────

export type UserMigrationBatchStatus = "draft" | "provisioning" | "completed" | "failed" | "stopped";
export type UserMigrationEntryStatus = "queued" | "provisioning" | "provisioned" | "failed" | "skipped";

/**
 * Transport selection for a user batch.
 * - `directGraph`     — Graph `POST /users` per entry. Plain member accounts with a forced password reset on first sign-in.
 * - `crossTenantSync` — Entra cross-tenant sync (`provisionOnDemand`). Requires the cross-tenant sync app + job in the target tenant.
 */
export type UserMigrationStrategy = "directGraph" | "crossTenantSync";

export interface UserMigrationBatch {
  id: string;
  projectId: string;
  name: string;
  status: UserMigrationBatchStatus;
  strategy: UserMigrationStrategy;
  totalUsers: number;
  provisionedUsers: number;
  failedUsers: number;
  skippedUsers: number;
  progressPercent: number;
  errorMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  lastUpdatedAt?: string;
}

export interface UserMigrationEntry {
  id: string;
  batchId: string;
  sourceUpn: string;
  targetUpn: string;
  targetObjectId?: string;
  status: UserMigrationEntryStatus;
  errorMessage?: string;
  lastUpdated?: string;
}

export interface CreateUserMigrationBatchDto {
  name: string;
  users: { sourceUpn: string; targetUpn: string }[];
  strategy?: UserMigrationStrategy;
}

// ─── Content Migration Jobs ───────────────────────────────────────────────────

export type ContentJobType = "oneDrive" | "sharePoint";
export type ContentJobStatus = "draft" | "provisioning" | "ready" | "scheduled" | "running" | "paused" | "completed" | "failed";

export interface ContentMigrationJob {
  id: string;
  projectId: string;
  name: string;
  jobType: ContentJobType;
  status: ContentJobStatus;
  totalItems: number;
  migratedItems: number;
  failedItems: number;
  progressPercent: number;
  spoMigrationJobId?: string;
  errorMessage?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  lastUpdatedAt?: string;
}

export interface CreateContentMigrationJobDto {
  name: string;
  jobType: ContentJobType;
  items: { sourceUrl: string; targetUrl: string; ownerUpn?: string; targetOwnerUpn?: string }[];
}

export type ContentItemStatus = "queued" | "running" | "completed" | "failed" | "skipped";

export interface ContentMigrationItem {
  id: string;
  jobId: string;
  sourceUrl: string;
  targetUrl: string;
  ownerUpn?: string;
  targetOwnerUpn?: string;
  spoJobId?: string;
  status: ContentItemStatus;
  progressPercent: number;
  errorMessage?: string;
  lastUpdated?: string;
}

// ─── Migration Waves ──────────────────────────────────────────────────────────

export type WaveStatus = "draft" | "scheduled" | "running" | "completed" | "failed" | "cancelled";

export interface WaveBatchSummary {
  id: string;
  name: string;
  status: string;
  totalMailboxes: number;
  syncedMailboxes: number;
  failedMailboxes: number;
  progressPercent: number;
}

export interface WaveJobSummary {
  id: string;
  name: string;
  jobType: string;
  status: string;
  totalItems: number;
  migratedItems: number;
  failedItems: number;
  progressPercent: number;
}

export interface WaveUserBatchSummary {
  id: string;
  name: string;
  status: string;
  totalUsers: number;
  provisionedUsers: number;
  failedUsers: number;
  progressPercent: number;
}

export interface MigrationWave {
  id: string;
  projectId: string;
  name: string;
  description?: string;
  order: number;
  status: WaveStatus;
  scheduledStartAt?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  mailboxBatches: WaveBatchSummary[];
  contentJobs: WaveJobSummary[];
  userBatches: WaveUserBatchSummary[];
}

export interface CreateWaveDto {
  name: string;
  description?: string;
  order: number;
  scheduledStartAt?: string;
}

export interface UpdateWaveDto {
  name: string;
  description?: string;
  order: number;
  scheduledStartAt?: string;
}

// ─── Audit ────────────────────────────────────────────────────────────────────

export interface AuditEvent {
  id: string;
  timestamp: string;
  actor: string;
  action: string;
  resource: string;
  projectId?: string;
  outcome: "success" | "failure";
  details?: string;
}

// ─── Post-Migration Validation ───────────────────────────────────────────────

export type ValidationRunStatus = "pending" | "running" | "completed" | "failed";
export type ValidationCheckType = "mailbox" | "oneDrive" | "sharePoint" | "user";
export type ValidationOutcome = "pass" | "fail" | "warning";

export interface ValidationRun {
  id: string;
  projectId: string;
  name: string;
  waveId?: string;
  status: ValidationRunStatus;
  totalChecks: number;
  passedChecks: number;
  failedChecks: number;
  warningChecks: number;
  progressPercent: number;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  errorMessage?: string;
}

export interface ValidationCheck {
  id: string;
  runId: string;
  checkType: ValidationCheckType;
  sourceReference: string;
  targetReference: string;
  outcome: ValidationOutcome;
  errorMessage?: string;
  checkedAt: string;
}

export interface CreateValidationRunDto {
  name?: string;
  waveId?: string;
}

// ─── Domain Rules ─────────────────────────────────────────────────────────────

export type DomainRuleType = "directMap" | "prefixReplace" | "regexReplace" | "fullUpnMap";

export interface DomainRule {
  id: string;
  projectId: string;
  ruleType: DomainRuleType;
  sourcePattern: string;
  targetPattern: string;
  priority: number;
  isEnabled: boolean;
  description?: string;
  createdAt: string;
}

export interface CreateDomainRuleDto {
  ruleType: DomainRuleType;
  sourcePattern: string;
  targetPattern: string;
  priority: number;
  isEnabled: boolean;
  description?: string;
}

export interface TransformPreviewResult {
  sourceUpn: string;
  transformedUpn: string;
  matchedRuleId?: string;
  matchedRuleDescription?: string;
  wasTransformed: boolean;
}

// ─── Domain Cutover ──────────────────────────────────────────────────────────

export type DomainCutoverPhase =
  | "created"
  | "cleaningSource"
  | "removingDomain"
  | "waitingForRelease"
  | "awaitingDnsVerification"
  | "verifyingDomain"
  | "assigningUsers"
  | "awaitingMxUpdate"
  | "completed"
  | "failed";

export interface DomainCutoverJob {
  id: string;
  projectId: string;
  domainName: string;
  phase: DomainCutoverPhase;
  totalUsers: number;
  completedUsers: number;
  failedUsers: number;
  dnsVerificationRecord?: string;
  targetMxRecord?: string;
  errorMessage?: string;
  dnsInstructions?: string;
  createdAt: string;
  startedAt?: string;
  completedAt?: string;
  lastUpdatedAt?: string;
}

// ─── Dependency Check ─────────────────────────────────────────────────────────

export type CheckStatus = "pass" | "fail" | "warning" | "skipped";
export type OverallStatus = "ready" | "warning" | "blocked";

export interface DependencyCheck {
  key: string;
  category: string;
  name: string;
  status: CheckStatus;
  detail?: string;
  remediation?: string;
}

export interface DependencyCheckResult {
  overallStatus: OverallStatus;
  checks: DependencyCheck[];
}

/**
 * Result of probing the target tenant for an Entra cross-tenant sync
 * configuration that targets the source tenant. Returned by
 * `GET /api/projects/{id}/cross-tenant-sync-status`. `error` distinguishes
 * "could not check" from "not configured".
 */
export interface CrossTenantSyncDiscoveryResult {
  isConfigured: boolean;
  partnerPolicyConfigured: boolean;
  servicePrincipalId?: string;
  servicePrincipalDisplayName?: string;
  syncJobId?: string;
  syncJobTemplateId?: string;
  syncJobStatus?: string;
  lastSyncAt?: string;
  message: string;
  remediation?: string;
  error?: string;
}

// ─── Pagination ───────────────────────────────────────────────────────────────

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}


// ─── Setup wizard ─────────────────────────────────────────────────────────────

export type SetupStepCategory = "exchange" | "spo" | "entra" | "azure";
export type SetupStepAudience = "sourceAdmin" | "targetAdmin" | "either";
export type SetupStepKind = "link" | "script" | "config" | "info";
export type SetupStepStatus = "unknown" | "pending" | "done";

export interface SetupStep {
  id: string;
  title: string;
  category: SetupStepCategory;
  audience: SetupStepAudience;
  kind: SetupStepKind;
  status: SetupStepStatus;
  detail: string;
  actionUrl?: string;
  script?: string;
}

export interface SetupTenantInfo {
  id: string;
  displayName: string;
  aadTenantId: string;
  onMicrosoftDomain?: string;
  appClientId: string;
  credentialConfigured: boolean;
  /** Relative API path to POST for live prerequisite verification. */
  verifyEndpoint: string;
}

export interface SetupPlan {
  projectId: string;
  projectName: string;
  sourceTenant: SetupTenantInfo;
  targetTenant: SetupTenantInfo;
  migrationAppId?: string;
  clientSecretConfigured: boolean;
  steps: SetupStep[];
  generatedAt: string;
}

// ─── Tenant prerequisite diagnostics ─────────────────────────────────────────

export interface DiagCheck {
  id: string;
  description: string;
  status: "pass" | "fail" | "warn" | "unknown";
  message: string;
  evidence?: string;
  remediation?: string;
}

export interface TenantPrereqReport {
  tenantId: string;
  displayName: string;
  aadTenantId: string;
  appClientId: string;
  onMicrosoftDomain?: string;
  crossTenantAppId: string;
  generatedAt: string;
  summary: string;
  passCount: number;
  failCount: number;
  warnCount: number;
  unknownCount: number;
  checks: DiagCheck[];
}
