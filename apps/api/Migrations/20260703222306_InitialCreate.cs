using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MigrationPlatform.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AppClientId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ClientSecretHint = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    AdminConsentGranted = table.Column<bool>(type: "boolean", nullable: false),
                    ConnectionStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LastVerifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientCertificateBase64 = table.Column<string>(type: "text", nullable: true),
                    ClientCertificateThumbprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClientCertificatePassword = table.Column<string>(type: "text", nullable: true),
                    OnMicrosoftDomain = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Projects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Projects_SourceTenant",
                        column: x => x.SourceTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Projects_TargetTenant",
                        column: x => x.TargetTenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Resource = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AuditEvents_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DomainCutoverJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Phase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TotalUsers = table.Column<int>(type: "integer", nullable: false),
                    CompletedUsers = table.Column<int>(type: "integer", nullable: false),
                    FailedUsers = table.Column<int>(type: "integer", nullable: false),
                    DnsVerificationRecord = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TargetMxRecord = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainCutoverJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DomainCutover_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DomainRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    RuleType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourcePattern = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetPattern = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DomainRules_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityMaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUpn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TargetUpn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConflictReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    MappingSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMaps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityMaps_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MigrationWaves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScheduledStartAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MigrationWaves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MigrationWaves_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Scans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    ScanType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Progress = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Summary_UserCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_GroupCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_MailboxCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_MailboxTotalSizeGb = table.Column<double>(type: "double precision", nullable: true),
                    Summary_SiteCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_OneDriveCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_DomainCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_BlockerCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_WarningCount = table.Column<int>(type: "integer", nullable: true),
                    Summary_ReadinessScore = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Scans_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Scans_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ValidationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalChecks = table.Column<int>(type: "integer", nullable: false),
                    PassedChecks = table.Column<int>(type: "integer", nullable: false),
                    FailedChecks = table.Column<int>(type: "integer", nullable: false),
                    WarningChecks = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationRuns_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentMigrationJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    JobType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalItems = table.Column<int>(type: "integer", nullable: false),
                    MigratedItems = table.Column<int>(type: "integer", nullable: false),
                    FailedItems = table.Column<int>(type: "integer", nullable: false),
                    SpoMigrationJobId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentMigrationJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentJobs_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContentJobs_Wave",
                        column: x => x.WaveId,
                        principalTable: "MigrationWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "MailboxMigrationBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalMailboxes = table.Column<int>(type: "integer", nullable: false),
                    SyncedMailboxes = table.Column<int>(type: "integer", nullable: false),
                    FailedMailboxes = table.Column<int>(type: "integer", nullable: false),
                    SkippedMailboxes = table.Column<int>(type: "integer", nullable: false),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "GraphCopy"),
                    ExoMigrationBatchId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TargetFolderName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxMigrationBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailboxBatches_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MailboxBatches_Wave",
                        column: x => x.WaveId,
                        principalTable: "MigrationWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserMigrationBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Strategy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "DirectGraph"),
                    TotalUsers = table.Column<int>(type: "integer", nullable: false),
                    ProvisionedUsers = table.Column<int>(type: "integer", nullable: false),
                    FailedUsers = table.Column<int>(type: "integer", nullable: false),
                    SkippedUsers = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CrossTenantSyncJobId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CrossTenantSyncRuleId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMigrationBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMigrationBatches_Project",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserMigrationBatches_Wave",
                        column: x => x.WaveId,
                        principalTable: "MigrationWaves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Progress = table.Column<int>(type: "integer", nullable: false),
                    ItemsTotal = table.Column<int>(type: "integer", nullable: false),
                    ItemsProcessed = table.Column<int>(type: "integer", nullable: false),
                    ItemsFailed = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Jobs_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Jobs_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ScanIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    AffectedObjectCount = table.Column<int>(type: "integer", nullable: false),
                    RemediationSteps = table.Column<List<string>>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScanIssues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScanIssues_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedDomains",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    UserCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedDomains_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceObjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    GroupType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MailEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SecurityEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MemberCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedGroups_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PrimarySmtpAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    MailboxType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeGb = table.Column<double>(type: "double precision", nullable: false),
                    ItemCount = table.Column<int>(type: "integer", nullable: false),
                    LastLogonTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HasArchive = table.Column<bool>(type: "boolean", nullable: false),
                    ArchiveSizeGb = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedMailboxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedMailboxes_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedOneDrives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUpn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    OwnerDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DriveUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    StorageUsedGb = table.Column<double>(type: "double precision", nullable: false),
                    StorageQuotaGb = table.Column<double>(type: "double precision", nullable: false),
                    LastModified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FileCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedOneDrives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedOneDrives_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Template = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    StorageUsedGb = table.Column<double>(type: "double precision", nullable: false),
                    StorageQuotaGb = table.Column<double>(type: "double precision", nullable: false),
                    Owners = table.Column<List<string>>(type: "jsonb", nullable: false),
                    LastActivityDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HasUniquePermissions = table.Column<bool>(type: "boolean", nullable: false),
                    SubsiteCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedSites_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScannedUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceObjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Upn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    AccountEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Licenses = table.Column<List<string>>(type: "jsonb", nullable: false),
                    HasMailbox = table.Column<bool>(type: "boolean", nullable: false),
                    MailboxSizeGb = table.Column<double>(type: "double precision", nullable: false),
                    MailboxType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OneDriveSizeGb = table.Column<double>(type: "double precision", nullable: false),
                    MfaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ProxyAddresses = table.Column<List<string>>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScannedUsers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScannedUsers_Scans_ScanId",
                        column: x => x.ScanId,
                        principalTable: "Scans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ValidationChecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetReference = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ValidationChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ValidationChecks_Run",
                        column: x => x.RunId,
                        principalTable: "ValidationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ContentMigrationItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    TargetUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OwnerUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    TargetOwnerUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SpoJobId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContentMigrationItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContentItems_Job",
                        column: x => x.JobId,
                        principalTable: "ContentMigrationJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MailboxMigrationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ItemsSyncedPercent = table.Column<double>(type: "double precision", nullable: false),
                    MessagesCopied = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    TotalMessages = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailboxMigrationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MailboxEntries_Batch",
                        column: x => x.BatchId,
                        principalTable: "MailboxMigrationBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserMigrationEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetUpn = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    TargetObjectId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorMessage = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMigrationEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMigrationEntries_Batch",
                        column: x => x.BatchId,
                        principalTable: "UserMigrationBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_ProjectId",
                table: "AuditEvents",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Timestamp",
                table: "AuditEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ContentMigrationItems_JobId",
                table: "ContentMigrationItems",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentMigrationJobs_ProjectId",
                table: "ContentMigrationJobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ContentMigrationJobs_WaveId",
                table: "ContentMigrationJobs",
                column: "WaveId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainCutoverJobs_ProjectId",
                table: "DomainCutoverJobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainRules_ProjectId_Priority",
                table: "DomainRules",
                columns: new[] { "ProjectId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityMaps_ProjectId",
                table: "IdentityMaps",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ProjectId",
                table: "Jobs",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_ScanId",
                table: "Jobs",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_MailboxMigrationBatches_ProjectId",
                table: "MailboxMigrationBatches",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MailboxMigrationBatches_WaveId",
                table: "MailboxMigrationBatches",
                column: "WaveId");

            migrationBuilder.CreateIndex(
                name: "IX_MailboxMigrationEntries_BatchId",
                table: "MailboxMigrationEntries",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationWaves_ProjectId",
                table: "MigrationWaves",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_MigrationWaves_ProjectId_Order",
                table: "MigrationWaves",
                columns: new[] { "ProjectId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_SourceTenantId",
                table: "Projects",
                column: "SourceTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TargetTenantId",
                table: "Projects",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_ScanIssues_ScanId",
                table: "ScanIssues",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedDomains_ScanId",
                table: "ScannedDomains",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedGroups_ScanId",
                table: "ScannedGroups",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedMailboxes_ScanId",
                table: "ScannedMailboxes",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedOneDrives_ScanId",
                table: "ScannedOneDrives",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedSites_ScanId",
                table: "ScannedSites",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_ScannedUsers_ScanId",
                table: "ScannedUsers",
                column: "ScanId");

            migrationBuilder.CreateIndex(
                name: "IX_Scans_ProjectId",
                table: "Scans",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_Scans_TenantId",
                table: "Scans",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantId",
                table: "Tenants",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserMigrationBatches_ProjectId",
                table: "UserMigrationBatches",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMigrationBatches_WaveId",
                table: "UserMigrationBatches",
                column: "WaveId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMigrationEntries_BatchId",
                table: "UserMigrationEntries",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationChecks_RunId",
                table: "ValidationChecks",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_ProjectId",
                table: "ValidationRuns",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ValidationRuns_WaveId",
                table: "ValidationRuns",
                column: "WaveId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ContentMigrationItems");

            migrationBuilder.DropTable(
                name: "DomainCutoverJobs");

            migrationBuilder.DropTable(
                name: "DomainRules");

            migrationBuilder.DropTable(
                name: "IdentityMaps");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "MailboxMigrationEntries");

            migrationBuilder.DropTable(
                name: "ScanIssues");

            migrationBuilder.DropTable(
                name: "ScannedDomains");

            migrationBuilder.DropTable(
                name: "ScannedGroups");

            migrationBuilder.DropTable(
                name: "ScannedMailboxes");

            migrationBuilder.DropTable(
                name: "ScannedOneDrives");

            migrationBuilder.DropTable(
                name: "ScannedSites");

            migrationBuilder.DropTable(
                name: "ScannedUsers");

            migrationBuilder.DropTable(
                name: "UserMigrationEntries");

            migrationBuilder.DropTable(
                name: "ValidationChecks");

            migrationBuilder.DropTable(
                name: "ContentMigrationJobs");

            migrationBuilder.DropTable(
                name: "MailboxMigrationBatches");

            migrationBuilder.DropTable(
                name: "Scans");

            migrationBuilder.DropTable(
                name: "UserMigrationBatches");

            migrationBuilder.DropTable(
                name: "ValidationRuns");

            migrationBuilder.DropTable(
                name: "MigrationWaves");

            migrationBuilder.DropTable(
                name: "Projects");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
