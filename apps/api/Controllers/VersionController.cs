using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MigrationPlatform.Api.Services;

namespace MigrationPlatform.Api.Controllers;

/// <summary>
/// Reports the running platform version. Anonymous so deployment/ops tooling
/// (and the frontend footer) can read it without a token.
/// </summary>
[ApiController]
[Route("api/version")]
[AllowAnonymous]
public class VersionController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public VersionController(IWebHostEnvironment env) => _env = env;

    [HttpGet]
    public IActionResult Get() => Ok(new VersionResponse(
        Version:        PlatformVersion.Current,
        RunbookVersion: PlatformVersion.RunbookVersion,
        Environment:    _env.EnvironmentName));
}

/// <param name="Version">Running platform (API) semantic version.</param>
/// <param name="RunbookVersion">Version the API expects the deployed SPO runbook to be.</param>
/// <param name="Environment">ASP.NET Core hosting environment name.</param>
public record VersionResponse(string Version, string RunbookVersion, string Environment);
