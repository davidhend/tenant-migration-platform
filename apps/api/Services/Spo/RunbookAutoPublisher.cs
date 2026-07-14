using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MigrationPlatform.Api.Services.Spo;

/// <summary>
/// One-shot startup service that keeps the deployed Azure Automation runbook in
/// sync with the repo copy (<c>scripts/Invoke-SpoCrossTenantOperation.ps1</c>).
/// The API drives the runbook by name, so a stale deployed copy fails silently
/// (unknown operations / missing parameters) — this eliminates the manual
/// "re-import and re-publish the runbook" step by diffing the published content
/// against the local script and publishing a new draft when they differ.
///
/// <para>Requires the API's Azure identity to hold <b>Automation Contributor</b>
/// (Job Operator is enough to run jobs but not to write runbook content). On 403
/// it degrades to an actionable warning and content migrations use the currently
/// deployed runbook, exactly as before.</para>
///
/// <para>Gate: <c>Azure:Automation:AutoPublishRunbook</c> (default true). Skipped
/// when the Automation account settings are not configured.</para>
/// </summary>
public sealed class RunbookAutoPublisher : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly AutomationArmHelper _arm;
    private readonly ILogger<RunbookAutoPublisher> _logger;

    private const string ScriptFileName = "Invoke-SpoCrossTenantOperation.ps1";
    private static readonly TimeSpan PublishPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PublishTimeout      = TimeSpan.FromMinutes(3);

    public RunbookAutoPublisher(
        IHttpClientFactory httpFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        AutomationArmHelper arm,
        ILogger<RunbookAutoPublisher> logger)
    {
        _httpFactory   = httpFactory;
        _configuration = configuration;
        _environment   = environment;
        _arm           = arm;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SyncRunbookAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down — nothing to do.
        }
        catch (Exception ex)
        {
            // Never take the host down over runbook sync — content migrations
            // simply keep using whatever runbook version is deployed.
            _logger.LogWarning(ex,
                "Runbook auto-publish failed — content migrations will run against the currently " +
                "deployed runbook. Fix the error or re-import scripts/{Script} manually.",
                ScriptFileName);
        }
    }

    private async Task SyncRunbookAsync(CancellationToken ct)
    {
        if (!_configuration.GetValue("Azure:Automation:AutoPublishRunbook", true))
        {
            _logger.LogInformation("Runbook auto-publish disabled via Azure:Automation:AutoPublishRunbook=false.");
            return;
        }

        var settings = _arm.LoadSettings();
        if (!settings.IsConfigured)
        {
            _logger.LogInformation(
                "Azure:Automation not configured — skipping runbook auto-publish.");
            return;
        }

        var localPath = ResolveScriptPath();
        if (localPath is null)
        {
            _logger.LogWarning(
                "Runbook auto-publish: local script {Script} not found under the app base directory " +
                "or content root — skipping sync.", ScriptFileName);
            return;
        }

        var localContent = Normalize(await File.ReadAllTextAsync(localPath, ct));

        // Surface the local runbook version so operators can correlate it with
        // GET /api/version (runbookVersion). Marker line: "# RUNBOOK_VERSION: x.y.z".
        var versionMatch = System.Text.RegularExpressions.Regex.Match(
            localContent, @"RUNBOOK_VERSION:\s*(\S+)");
        _logger.LogInformation(
            "RunbookAutoPublisher: local runbook version {RunbookVersion} (platform {PlatformVersion}).",
            versionMatch.Success ? versionMatch.Groups[1].Value : "unknown",
            Services.PlatformVersion.Current);

        var http = _httpFactory.CreateClient("spo");
        var bearer = await _arm.GetTokenAsync(ct);
        var runbookUrl = $"{settings.AccountBaseUrl}/runbooks/{settings.RunbookName}";

        // ── Compare against the published content ────────────────────────────
        // ARM serves runbook content as text/powershell and 415s content
        // negotiation it doesn't like — ask for it explicitly.
        using (var contentResp = await SendAsync(http, HttpMethod.Get,
                   $"{runbookUrl}/content?api-version={AutomationArmHelper.ApiVersion}", bearer, null, ct,
                   accept: "text/powershell"))
        {
            if (contentResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LogPermissionWarning((int)contentResp.StatusCode);
                return;
            }

            if (contentResp.IsSuccessStatusCode)
            {
                var deployed = Normalize(await contentResp.Content.ReadAsStringAsync(ct));
                if (deployed == localContent)
                {
                    _logger.LogInformation(
                        "Runbook '{Runbook}' is up to date with scripts/{Script}.",
                        settings.RunbookName, ScriptFileName);
                    return;
                }

                _logger.LogInformation(
                    "Runbook '{Runbook}' differs from scripts/{Script} — publishing the local version.",
                    settings.RunbookName, ScriptFileName);
            }
            else if (contentResp.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation(
                    "Runbook '{Runbook}' does not exist in Automation account '{Account}' — creating it.",
                    settings.RunbookName, settings.AccountName);
                if (!await CreateRunbookShellAsync(http, settings, runbookUrl, bearer, ct))
                    return;
            }
            else
            {
                // Can't compare (e.g. an ARM content-negotiation quirk) — publish
                // anyway rather than silently skipping: the draft PUT + publish is
                // idempotent and cheap, and skipping would let drift persist.
                var body = await contentResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Runbook auto-publish: could not read deployed runbook content ({Status}): {Body} — " +
                    "publishing the local version unconditionally.",
                    (int)contentResp.StatusCode, Truncate(body));
            }
        }

        // ── PUT draft content ────────────────────────────────────────────────
        using (var draftResp = await SendAsync(http, HttpMethod.Put,
                   $"{runbookUrl}/draft/content?api-version={AutomationArmHelper.ApiVersion}",
                   bearer, new StringContent(localContent, Encoding.UTF8, "text/powershell"), ct))
        {
            if (draftResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                LogPermissionWarning((int)draftResp.StatusCode);
                return;
            }
            if (!draftResp.IsSuccessStatusCode)
            {
                var body = await draftResp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Runbook auto-publish: draft upload failed ({Status}): {Body}",
                    (int)draftResp.StatusCode, Truncate(body));
                return;
            }
        }

        // ── Publish (retry briefly while the async draft write settles) ──────
        var deadline = DateTime.UtcNow + PublishTimeout;
        while (true)
        {
            using var publishResp = await SendAsync(http, HttpMethod.Post,
                $"{runbookUrl}/publish?api-version={AutomationArmHelper.ApiVersion}",
                bearer, new StringContent(string.Empty), ct);

            if (publishResp.IsSuccessStatusCode)
                break;

            var body = await publishResp.Content.ReadAsStringAsync(ct);
            if ((publishResp.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest) &&
                DateTime.UtcNow < deadline)
            {
                _logger.LogDebug(
                    "Runbook publish not ready yet ({Status}) — retrying. {Body}",
                    (int)publishResp.StatusCode, Truncate(body));
                await Task.Delay(PublishPollInterval, ct);
                continue;
            }

            _logger.LogWarning(
                "Runbook auto-publish: publish failed ({Status}): {Body}",
                (int)publishResp.StatusCode, Truncate(body));
            return;
        }

        // ── Wait until the runbook reports Published ─────────────────────────
        deadline = DateTime.UtcNow + PublishTimeout;
        while (DateTime.UtcNow < deadline)
        {
            using var stateResp = await SendAsync(http, HttpMethod.Get,
                $"{runbookUrl}?api-version={AutomationArmHelper.ApiVersion}", bearer, null, ct);
            if (stateResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await stateResp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("properties", out var props) &&
                    props.TryGetProperty("state", out var st) &&
                    string.Equals(st.GetString(), "Published", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Runbook '{Runbook}' updated and published (local scripts/{Script} differed from the deployed copy).",
                        settings.RunbookName, ScriptFileName);
                    return;
                }
            }
            await Task.Delay(PublishPollInterval, ct);
        }

        _logger.LogWarning(
            "Runbook '{Runbook}' publish was accepted but the runbook did not report state 'Published' " +
            "within {Timeout} — verify it in the Azure portal.",
            settings.RunbookName, PublishTimeout);
    }

    /// <summary>Create the runbook resource itself (required before a draft can be uploaded).</summary>
    private async Task<bool> CreateRunbookShellAsync(
        HttpClient http, AutomationSettings settings, string runbookUrl, string bearer, CancellationToken ct)
    {
        // The create PUT needs the account's ARM location.
        string? location = null;
        using (var accountResp = await SendAsync(http, HttpMethod.Get,
                   $"{settings.AccountBaseUrl}?api-version={AutomationArmHelper.ApiVersion}", bearer, null, ct))
        {
            if (accountResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await accountResp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("location", out var loc))
                    location = loc.GetString();
            }
        }
        if (string.IsNullOrWhiteSpace(location))
        {
            _logger.LogWarning(
                "Runbook auto-publish: could not resolve the Automation account location — cannot create runbook '{Runbook}'.",
                settings.RunbookName);
            return false;
        }

        var createBody = JsonSerializer.Serialize(new
        {
            name = settings.RunbookName,
            location,
            properties = new
            {
                runbookType = "PowerShell",
                logVerbose  = true,
                logProgress = false,
                description = "Managed by the migration platform API (auto-published from scripts/Invoke-SpoCrossTenantOperation.ps1).",
            },
        });

        using var createResp = await SendAsync(http, HttpMethod.Put,
            $"{runbookUrl}?api-version={AutomationArmHelper.ApiVersion}",
            bearer, new StringContent(createBody, Encoding.UTF8, "application/json"), ct);
        if (createResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            LogPermissionWarning((int)createResp.StatusCode);
            return false;
        }
        if (!createResp.IsSuccessStatusCode)
        {
            var body = await createResp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "Runbook auto-publish: creating runbook '{Runbook}' failed ({Status}): {Body}",
                settings.RunbookName, (int)createResp.StatusCode, Truncate(body));
            return false;
        }
        return true;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpMethod method, string url, string bearer, HttpContent? content, CancellationToken ct,
        string? accept = null)
    {
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (accept is not null)
            req.Headers.Accept.ParseAdd(accept);
        return await http.SendAsync(req, ct);
    }

    private void LogPermissionWarning(int status)
        => _logger.LogWarning(
            "Runbook auto-publish: the API's Azure identity lacks write access to the Automation account ({Status}). " +
            "Grant it the Automation Contributor role (Job Operator only allows running jobs), or keep re-importing " +
            "scripts/{Script} manually after changes.",
            status, ScriptFileName);

    /// <summary>
    /// Prefer the content-root copy (the repo source of truth when running from
    /// the project directory); fall back to the build-output copy (csproj
    /// CopyToOutputDirectory) for published deployments.
    /// </summary>
    private string? ResolveScriptPath()
    {
        var candidates = new[]
        {
            Path.Combine(_environment.ContentRootPath, "scripts", ScriptFileName),
            Path.Combine(AppContext.BaseDirectory, "scripts", ScriptFileName),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string Normalize(string content) =>
        content.Replace("\r\n", "\n").Trim();

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "…";
}
