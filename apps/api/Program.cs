using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using MigrationPlatform.Api.Data;
using MigrationPlatform.Api.Data.Repositories;
using MigrationPlatform.Api.Middleware;
using MigrationPlatform.Api.Services.Discovery;
using MigrationPlatform.Api.Services.Discovery.Analyzers;
using MigrationPlatform.Api.Services.Discovery.Scanners;
using MigrationPlatform.Api.Services;
using MigrationPlatform.Api.Services.Graph;
using MigrationPlatform.Api.Services.KeyVault;
using MigrationPlatform.Api.Services.Exo;
using MigrationPlatform.Api.Services.Spo;
using MigrationPlatform.Api.Hubs;
using MigrationPlatform.Api.Workers;
using MigrationPlatform.Api.HealthChecks;
using MigrationPlatform.Api.Services.InstanceLock;
using MigrationPlatform.Api.Services.Telemetry;

var builder = WebApplication.CreateBuilder(args);

// ── Writable runtime settings override ─────────────────────────────────────
// settings.override.json is written by SettingsController and layered on top
// of appsettings.json so admins can change values (like the Azure Automation
// account) from the UI without editing files or restarting the API.
builder.Configuration.AddJsonFile(
    "settings.override.json", optional: true, reloadOnChange: true);

// ── Observability (OpenTelemetry) ────────────────────────────────────────────
// No-op unless an OTLP endpoint or Application Insights connection string is
// configured; the notice is logged after the host is built.
var telemetryNotice = builder.AddPlatformTelemetry();

// ── Controllers + JSON ──────────────────────────────────────────────────────
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// ── Swagger ─────────────────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "M365 Migration Platform API", Version = "v1" });

    // Allow pasting a Bearer token via the Swagger UI "Authorize" button
    var securityScheme = new OpenApiSecurityScheme
    {
        Name         = "Authorization",
        Description  = "Enter: Bearer {token}  — obtain a token from POST /api/auth/token",
        In           = ParameterLocation.Header,
        Type         = SecuritySchemeType.Http,
        Scheme       = "bearer",
        BearerFormat = "JWT",
        Reference    = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };

    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, Array.Empty<string>() },
    });
});

// ── Authentication ───────────────────────────────────────────────────────────
// Two bearer schemes with graceful degradation:
//   "Local"   — symmetric-key dev JWT issued by POST /api/auth/token; usable
//               only when Platform:DevMode=true.
//   "EntraId" — Microsoft Entra ID tokens; registered only when
//               AzureAd:TenantId + AzureAd:ClientId are configured.
// The default scheme ("Smart") routes each request by the token's issuer so
// both can coexist. With neither configured the app still starts (authorized
// endpoints return 401) and a critical warning is logged after startup.

var devMode         = builder.Configuration.GetValue<bool>("Platform:DevMode");
var azureAdTenantId = builder.Configuration["AzureAd:TenantId"];
var azureAdClientId = builder.Configuration["AzureAd:ClientId"];
var entraEnabled    = !string.IsNullOrWhiteSpace(azureAdTenantId) &&
                      !string.IsNullOrWhiteSpace(azureAdClientId);
// The local symmetric-key scheme is a DEV convenience. Once Entra is configured
// it must NOT coexist with it outside Development — otherwise a forgeable local
// token (esp. with the placeholder signing key) is a second admin path in an
// "Entra-secured" deployment. Both schemes may run together only in the
// Development environment (for local Entra testing).
var localEnabled    = devMode && (!entraEnabled || builder.Environment.IsDevelopment());

// SignalR sends the JWT as ?access_token= because browsers cannot add custom
// headers during a WebSocket upgrade. Both schemes need the same extraction so
// [Authorize] on MigrationHub works regardless of which issued the token.
static JwtBearerEvents SignalRTokenEvents() => new()
{
    OnMessageReceived = context =>
    {
        var accessToken = context.Request.Query["access_token"];
        var path        = context.HttpContext.Request.Path;

        if (!string.IsNullOrEmpty(accessToken) &&
            path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            context.Token = accessToken;
        }

        return Task.CompletedTask;
    },
};

var jwtDevPlaceholderInUse = false;

var auth = builder.Services.AddAuthentication("Smart");

auth.AddPolicyScheme("Smart", "Entra ID or local dev JWT", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        if (entraEnabled && !localEnabled) return "EntraId";
        if (!entraEnabled) return "Local"; // dev-only, or the inert fallback below

        // Both schemes available — route by the bearer token's issuer.
        string? token = null;
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token) &&
            context.Request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
            token = context.Request.Query["access_token"];

        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                var jwt = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
                    .ReadJsonWebToken(token);
                if (jwt.Issuer.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase) ||
                    jwt.Issuer.Contains("sts.windows.net", StringComparison.OrdinalIgnoreCase))
                    return "EntraId";
            }
            catch
            {
                // Malformed token — fall through and let the local handler 401 it.
            }
        }

        return "Local";
    };
});

if (entraEnabled)
{
    var azureAdAudience = builder.Configuration["AzureAd:Audience"];
    auth.AddJwtBearer("EntraId", options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{azureAdTenantId}/v2.0";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences = new[]
            {
                azureAdClientId!,
                $"api://{azureAdClientId}",
                string.IsNullOrWhiteSpace(azureAdAudience) ? $"api://{azureAdClientId}" : azureAdAudience,
            },
            // Entra app roles arrive in "roles"; preferred_username carries the UPN.
            RoleClaimType = "roles",
            NameClaimType = "preferred_username",
        };
        options.Events = SignalRTokenEvents();
    });
}

if (localEnabled || !entraEnabled)
{
    // Local symmetric-key JWT. When neither DevMode nor AzureAd is configured
    // this scheme is still registered — with a random throwaway key if none is
    // set — purely so [Authorize] endpoints produce clean 401s instead of
    // "no authentication scheme" server errors; no tokens can be issued then.
    var jwtSection = builder.Configuration.GetSection("Jwt");
    string secretKey;
    if (localEnabled)
    {
        // Real dev-login scheme: honor the configured signing key.
        secretKey = jwtSection["SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey is not configured.");
        if (string.IsNullOrWhiteSpace(secretKey))
            throw new InvalidOperationException("Jwt:SecretKey is not configured.");

        // Known dev placeholders keep working in Development, but must not be
        // trusted outside it (the value is public in the repo) — flagged here and
        // turned into a hard startup failure below.
        jwtDevPlaceholderInUse = !builder.Environment.IsDevelopment() &&
            (secretKey.Contains("dev-only-secret-key", StringComparison.OrdinalIgnoreCase) ||
             secretKey.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase));
    }
    else
    {
        // Inert fallback (reached only when no real auth is configured): ALWAYS a
        // random per-process key, ignoring Jwt:SecretKey entirely, so no token —
        // including one forged with a committed placeholder key — can validate.
        // The scheme exists purely to yield clean 401s instead of a 500.
        secretKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    }

    var issuer   = jwtSection["Issuer"]   ?? "https://migration-platform.local";
    var audience = jwtSection["Audience"] ?? "migration-platform-api";

    auth.AddJwtBearer("Local", options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = issuer,
            ValidateAudience         = true,
            ValidAudience            = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey)),
            ValidateLifetime         = true,
            ClockSkew                = TimeSpan.FromMinutes(1),
        };
        options.Events = SignalRTokenEvents();
    });
}

builder.Services.AddAuthorization(options =>
{
    // Reader level = any authenticated user (plain [Authorize]).
    // Operator level = Admin or Operator role, from either the mapped role
    // claim (dev tokens, mapped Entra roles) or the raw "roles" claim.
    options.AddPolicy("Operator", policy => policy.RequireAssertion(ctx =>
        ctx.User.Claims
            .Where(c => c.Type is System.Security.Claims.ClaimTypes.Role or "roles" or "role")
            .SelectMany(c => c.Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            .Any(r => r.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                      r.Equals("Operator", StringComparison.OrdinalIgnoreCase))));
});

// Caller identity for audit attribution (Entra UPN / dev username / system@platform).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();

// ── DataProtection ───────────────────────────────────────────────────────────
// Persist the key ring under the content root ('keys/', gitignored) instead of
// the user profile, and encrypt it at rest with a Key Vault key when Key Vault
// is enabled. Degrades to filesystem-only (with a logged notice) when the
// identity lacks key permissions or the vault is unreachable — never blocks
// startup.
string dataProtectionNotice;
bool dataProtectionDegraded = false;
{
    var dpKeysDir = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "keys"));
    var dp = builder.Services.AddDataProtection()
        .SetApplicationName("MigrationPlatform")
        .PersistKeysToFileSystem(dpKeysDir);

    var dpVaultUri = builder.Configuration["KeyVault:VaultUri"];
    if (builder.Configuration.GetValue<bool>("KeyVault:Enabled") &&
        !string.IsNullOrWhiteSpace(dpVaultUri))
    {
        const string DpKeyName = "platform-dataprotection";
        try
        {
            var keyClient = new Azure.Security.KeyVault.Keys.KeyClient(
                new Uri(dpVaultUri), new Azure.Identity.DefaultAzureCredential(),
                new Azure.Security.KeyVault.Keys.KeyClientOptions
                {
                    Retry = { MaxRetries = 1, NetworkTimeout = TimeSpan.FromSeconds(10) },
                });

            try
            {
                keyClient.GetKey(DpKeyName);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                keyClient.CreateKey(DpKeyName, Azure.Security.KeyVault.Keys.KeyType.Rsa);
            }

            dp.ProtectKeysWithAzureKeyVault(
                new Uri($"{dpVaultUri.TrimEnd('/')}/keys/{DpKeyName}"),
                new Azure.Identity.DefaultAzureCredential());
            dataProtectionNotice =
                $"DataProtection key ring persisted to '{dpKeysDir.FullName}' and encrypted with Key Vault key '{DpKeyName}'.";
        }
        catch (Exception ex)
        {
            dataProtectionDegraded = true;
            dataProtectionNotice =
                $"DataProtection Key Vault encryption unavailable ({ex.Message}) — key ring persisted to " +
                $"'{dpKeysDir.FullName}' UNENCRYPTED. Grant the API identity key permissions " +
                "(get/create/wrapKey/unwrapKey, or the 'Key Vault Crypto Officer' role) to enable encryption.";
        }
    }
    else
    {
        dataProtectionNotice =
            $"DataProtection key ring persisted to '{dpKeysDir.FullName}' unencrypted " +
            "(KeyVault:Enabled=false) — acceptable for development.";
    }
}

// ── SignalR ───────────────────────────────────────────────────────────────────
// Microsoft.AspNetCore.SignalR ships inside the ASP.NET Core shared framework
// for .NET 8 — no additional NuGet package is required.
builder.Services.AddSignalR();
builder.Services.AddScoped<IProgressNotifier, ProgressNotifier>();

// ── CORS ─────────────────────────────────────────────────────────────────────
// Origins come from Cors:AllowedOrigins (appsettings / environment) so a
// deployed frontend origin can be allowed without a code change; the localhost
// defaults keep local development working when the key is absent.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
    corsOrigins = ["http://localhost:3000", "http://localhost:3001"];

builder.Services.AddCors(opt =>
{
    opt.AddPolicy("Frontend", policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              // AllowCredentials is required for SignalR WebSocket connections:
              // the SignalR JS client sends the JWT as the ?access_token= query
              // parameter instead of a header, and browsers enforce credentials
              // mode when upgrading to WebSocket.
              .AllowCredentials());
});

// ── In-process scan queue (Singleton — lives for the application lifetime) ──
builder.Services.AddSingleton<ScanJobQueue>();

// ── In-process mailbox migration queue (Singleton) ───────────────────────────
builder.Services.AddSingleton<MailboxMigrationQueue>();

// ── In-process content migration queue (Singleton) ───────────────────────────
builder.Services.AddSingleton<ContentMigrationQueue>();

// ── In-process OneDrive provisioning queue (Singleton) ───────────────────────
builder.Services.AddSingleton<OneDriveProvisioningQueue>();

// ── In-process user migration queue (Singleton) ──────────────────────────────
builder.Services.AddSingleton<UserMigrationQueue>();

// ── In-process domain cutover queue (Singleton) ──────────────────────────────
builder.Services.AddSingleton<DomainCutoverQueue>();

// ── In-process validation queue (Singleton) ───────────────────────────────────
builder.Services.AddSingleton<ValidationQueue>();

// ── EF Core / PostgreSQL ─────────────────────────────────────────────────────
var dataSource = new Npgsql.NpgsqlDataSourceBuilder(
        builder.Configuration.GetConnectionString("DefaultConnection"))
    .EnableDynamicJson()
    .Build();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(dataSource));

// ── Health checks ────────────────────────────────────────────────────────────
// /health/live (liveness, no dependencies) and /health/ready (readiness) are
// mapped after the pipeline is built. PostgreSQL is a hard readiness dependency;
// Key Vault and Automation are "degraded" signals (registered only when
// relevant) so a dependency wobble does not fail readiness and kill the pod.
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("postgresql", tags: ["ready"]);
if (builder.Configuration.GetValue<bool>("KeyVault:Enabled"))
    healthChecks.AddCheck<KeyVaultHealthCheck>("keyvault", tags: ["ready"]);
healthChecks.AddCheck<AutomationConfigHealthCheck>("automation", tags: ["ready"]);
healthChecks.AddCheck<StuckJobsHealthCheck>("stuck-jobs", tags: ["ready"]);

// ── Custom metrics + single-instance role (see below for the guard) ──────────
builder.Services.AddSingleton<PlatformMetrics>();
builder.Services.AddSingleton<IInstanceRole, InstanceRole>();

// ── Repositories (Scoped — one per HTTP request / DI scope) ─────────────────
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IProjectRepository, ProjectRepository>();
builder.Services.AddScoped<IScanRepository, ScanRepository>();
builder.Services.AddScoped<IIdentityMapRepository, IdentityMapRepository>();
builder.Services.AddScoped<IJobRepository, JobRepository>();
builder.Services.AddScoped<IAuditRepository, AuditRepository>();
builder.Services.AddScoped<IDomainRuleRepository, DomainRuleRepository>();
builder.Services.AddScoped<IMailboxMigrationRepository, MailboxMigrationRepository>();
builder.Services.AddScoped<IUserMigrationRepository, UserMigrationRepository>();
builder.Services.AddScoped<IContentMigrationRepository, ContentMigrationRepository>();
builder.Services.AddScoped<IDomainCutoverRepository, DomainCutoverRepository>();
builder.Services.AddScoped<IWaveRepository, WaveRepository>();
builder.Services.AddScoped<IValidationRepository, ValidationRepository>();

// ── Domain transformation ────────────────────────────────────────────────────
builder.Services.AddScoped<IDomainTransformService, DomainTransformService>();

// ── Domain management (Graph) ────────────────────────────────────────────────
builder.Services.AddScoped<IDomainManagementClient, DomainManagementClient>();

// ── Database seeder ──────────────────────────────────────────────────────────
builder.Services.AddScoped<DatabaseSeeder>();

// ── Key Vault credential service (Singleton — SecretClient is thread-safe) ───
builder.Services.AddSingleton<IKeyVaultCredentialService, KeyVaultCredentialService>();

// ── Platform secret store + resolver ─────────────────────────────────────────
// Platform-level secrets (ARM service principal, cross-tenant migration app
// secret) live in Key Vault when it is enabled; otherwise they stay in
// settings.override.json exactly as before. The resolver is the single read
// path (config value wins, kv: markers redirect to the store, short TTL cache).
var keyVaultSecretsActive =
    builder.Configuration.GetValue<bool>("KeyVault:Enabled") &&
    !string.IsNullOrWhiteSpace(builder.Configuration["KeyVault:VaultUri"]);
if (keyVaultSecretsActive)
    builder.Services.AddSingleton<IPlatformSecretStore, KeyVaultPlatformSecretStore>();
else
    builder.Services.AddSingleton<IPlatformSecretStore, FilePlatformSecretStore>();
builder.Services.AddSingleton<IPlatformSecretResolver, PlatformSecretResolver>();
// One-shot startup migration of any plaintext secrets left in the override file.
builder.Services.AddHostedService<PlatformSecretMigrator>();

// ── Tenant credential factory (Singleton — stateless, just builds credentials) ──
builder.Services.AddSingleton<ITenantCredentialFactory, TenantCredentialFactory>();

// ── Named HttpClients for external APIs ──────────────────────────────────────
builder.Services.AddHttpClient("exo");
builder.Services.AddHttpClient("spo");

// ── EXO REST client (Singleton — HttpClient is managed by IHttpClientFactory) ─
builder.Services.AddSingleton<IExoRestClient, ExoRestClient>();

// ── SPO cross-tenant client (triggers an Azure Automation runbook that runs
// Microsoft.Online.SharePoint.PowerShell on a Microsoft-managed Windows sandbox —
// the SPO module is Windows-only and cannot load inside the Linux API container).
// Requires Azure:Automation config and the API identity to hold the
// Automation Job Operator role on the Automation account.
builder.Services.AddSingleton<AutomationArmHelper>();
builder.Services.AddSingleton<ISpoRestClient, SpoRestClient>();

// ── Graph client factory ─────────────────────────────────────────────────────
// Scoped so that each scan job gets its own factory instance; the underlying
// GraphServiceClient is lightweight and does not pool connections at this level.
builder.Services.AddScoped<IGraphClientFactory, GraphClientFactory>();
builder.Services.AddScoped<IGraphMailCopyService, GraphMailCopyService>();
builder.Services.AddScoped<IOneDriveProvisioningService, OneDriveProvisioningService>();
builder.Services.AddScoped<ILicenseCheckService, LicenseCheckService>();
builder.Services.AddScoped<ICrossTenantSyncDiscoveryService, CrossTenantSyncDiscoveryService>();
builder.Services.AddScoped<IGraphSyncClient, GraphSyncClient>();

// ── Discovery engine and scanners ───────────────────────────────────────────
builder.Services.AddScoped<IDiscoveryEngine, DiscoveryEngine>();
builder.Services.AddScoped<UserScanner>();
builder.Services.AddScoped<GroupScanner>();
builder.Services.AddScoped<MailboxScanner>();
builder.Services.AddScoped<SharePointScanner>();
builder.Services.AddScoped<OneDriveScanner>();
builder.Services.AddScoped<DomainScanner>();
builder.Services.AddScoped<ReadinessAnalyzer>();
builder.Services.AddScoped<IssueDetector>();

// ── Single-instance guard ────────────────────────────────────────────────────
// MUST be registered before the workers: it acquires a PostgreSQL advisory lock
// synchronously in StartAsync, and hosted services start in registration order,
// so IInstanceRole.IsPrimary is decided before any worker's ExecuteAsync runs.
// A secondary instance (lock already held) marks itself non-primary and its
// workers self-suppress, preventing duplicate migration batches.
builder.Services.AddHostedService<SingleInstanceGuard>();

// ── Background workers ──────────────────────────────────────────────────────
// One-shot startup sync of the Azure Automation runbook with the repo copy
// (Azure:Automation:AutoPublishRunbook, default true; needs Automation Contributor).
builder.Services.AddHostedService<RunbookAutoPublisher>();
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddHostedService<MailboxMigrationWorker>();
builder.Services.AddHostedService<ContentMigrationWorker>();
builder.Services.AddHostedService<OneDriveProvisioningWorker>();
builder.Services.AddHostedService<WaveSchedulerService>();
builder.Services.AddHostedService<ValidationWorker>();
builder.Services.AddHostedService<UserMigrationWorker>();
builder.Services.AddHostedService<DomainCutoverWorker>();

// Observability + housekeeping: active-work/stuck-job metrics refresh, and the
// opt-in audit-event retention sweep (Retention:Enabled, default false).
builder.Services.AddHostedService<ActiveWorkMetrics>();
builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();

// Writer-triggered config reload: the reloadOnChange watcher on
// settings.override.json never fires when the file sits on a Windows-drive
// bind mount (Docker Desktop / WSL2), so every writer explicitly reloads the
// configuration root after persisting.
SettingsOverrideFile.ConfigurationReloader =
    () => ((IConfigurationRoot)app.Configuration).Reload();

// ── Apply EF migrations and seed on startup ──────────────────────────────────
// Database:AutoMigrate controls ONLY whether EF migrations are applied and the
// seeder runs at startup — background workers run regardless (gate them with
// Workers:Enabled=false instead). When the flag is absent or false the app
// starts without touching the schema, so non-DB endpoints (auth, health,
// SignalR) work even without PostgreSQL — but a reachability warning is logged
// below so a missing database is loud rather than a silent worker stall.
//
// Legacy databases (created by the old EnsureCreated + raw-SQL-patch startup)
// have the full schema but no "__EFMigrationsHistory" table. Those are
// BASELINED: the InitialCreate migration is recorded as applied without being
// run, so MigrateAsync only ever applies migrations added after the baseline.
if (app.Configuration.GetValue<bool>("Database:AutoMigrate"))
{
    using var scope = app.Services.CreateScope();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        static async Task<bool> TableExistsAsync(AppDbContext ctx, string table)
        {
            var conn = ctx.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = @t)";
            var p = cmd.CreateParameter();
            p.ParameterName = "@t";
            p.Value = table;
            cmd.Parameters.Add(p);
            return (bool)(await cmd.ExecuteScalarAsync())!;
        }

        var hasHistory = await TableExistsAsync(db, "__EFMigrationsHistory");
        var hasLegacySchema = await TableExistsAsync(db, "Projects");
        if (!hasHistory && hasLegacySchema)
        {
            // The migration ID must match apps/api/Migrations exactly; the
            // product version is informational (EF does not validate it).
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL
                );
                INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                VALUES ('20260703222306_InitialCreate', '8.0.11')
                ON CONFLICT ("MigrationId") DO NOTHING;
                """);
            startupLogger.LogInformation(
                "Existing pre-migrations schema detected — baselined at InitialCreate " +
                "(recorded as applied without running it).");
        }

        await db.Database.MigrateAsync();

        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedAsync();
    }
    catch (Exception ex)
    {
        // Don't rethrow: a database problem at startup should not produce an
        // unhandled-exception crash dump. The app starts degraded (non-DB endpoints
        // work, DB endpoints 500, workers log their own errors) and recovers once
        // PostgreSQL is reachable and the app is restarted.
        startupLogger.LogError(ex,
            "Database migration/seed failed at startup. Verify PostgreSQL is running at " +
            "the connection string in ConnectionStrings:DefaultConnection. " +
            "DB-backed endpoints will fail until the database is available.");
    }
}
else
{
    // AutoMigrate is off — the schema is expected to already exist. Check
    // reachability anyway and warn loudly: every background worker depends on
    // the database, and an unreachable one otherwise surfaces only as jobs
    // sitting in Queued/Running forever.
    using var scope = app.Services.CreateScope();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Database.CanConnectAsync())
        {
            startupLogger.LogWarning(
                "Database is unreachable at ConnectionStrings:DefaultConnection. " +
                "Controllers will error and background workers cannot process jobs until it is available. " +
                "Set Database:AutoMigrate=true to apply EF migrations automatically at startup.");
        }
    }
    catch (Exception ex)
    {
        startupLogger.LogWarning(ex,
            "Database connectivity check failed — background workers cannot process jobs until " +
            "the database at ConnectionStrings:DefaultConnection is available.");
    }
}

// ── Middleware pipeline ─────────────────────────────────────────────────────
if (!entraEnabled && !localEnabled)
{
    app.Logger.LogCritical(
        "No authentication scheme is available: Platform:DevMode is false and AzureAd:TenantId/ClientId " +
        "are not configured. Every authorized endpoint will return 401. Configure the AzureAd section " +
        "(production) or enable Platform:DevMode (development only).");
}

if (jwtDevPlaceholderInUse)
{
    // Refuse to start: a known placeholder signing key outside Development means
    // anyone with the (public) repo can forge Admin tokens for the local scheme.
    throw new InvalidOperationException(
        $"Jwt:SecretKey is a known development placeholder but the environment is " +
        $"'{app.Environment.EnvironmentName}'. Configure AzureAd for Entra ID auth and set " +
        "Platform:DevMode=false, or supply a real Jwt:SecretKey, before starting this instance.");
}

if (dataProtectionDegraded)
    app.Logger.LogWarning("{Notice}", dataProtectionNotice);
else
    app.Logger.LogInformation("{Notice}", dataProtectionNotice);

app.Logger.LogInformation("{Notice}", telemetryNotice);

// Correlation ID first so every downstream log line (including errors) carries it.
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseMiddleware<ErrorHandlingMiddleware>();

// HSTS + HTTPS redirect outside Development only. Http:EnforceHttps=false opts
// out for deployments where TLS terminates at a reverse proxy / ingress.
if (!app.Environment.IsDevelopment() && app.Configuration.GetValue("Http:EnforceHttps", true))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Migration Platform API v1"));
}

app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<MigrationHub>("/hubs/migration");

// ── Health endpoints (anonymous; not gated by auth) ──────────────────────────
// /health/live — liveness: process is up, no dependency checks.
// /health/ready — readiness: PostgreSQL (+ Key Vault / Automation when relevant).
//   Degraded checks keep the endpoint at 200 (still serving); only an Unhealthy
//   check (PostgreSQL down) returns 503.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false, // no checks — pure liveness
    ResponseWriter = HealthReportWriter.WriteAsync,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthReportWriter.WriteAsync,
    ResultStatusCodes =
    {
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy]   = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded]  = StatusCodes.Status200OK,
        [Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
    },
}).AllowAnonymous();

app.Run();
