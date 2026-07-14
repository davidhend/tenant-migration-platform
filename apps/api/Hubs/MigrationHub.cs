using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MigrationPlatform.Api.Hubs;

/// <summary>
/// SignalR hub for real-time migration progress notifications.
///
/// Clients subscribe to targeted groups so they only receive events relevant
/// to the project or scan they are currently viewing:
/// <list type="bullet">
///   <item><c>project:{projectId}</c> — all job and migration progress for a project</item>
///   <item><c>scan:{scanId}</c> — granular scanner progress for a specific scan</item>
/// </list>
///
/// The JWT bearer token must be supplied as the <c>access_token</c> query parameter
/// because the SignalR WebSocket handshake cannot carry custom headers in browsers.
/// </summary>
[Authorize]
public class MigrationHub : Hub
{
    /// <summary>Subscribe the current connection to all progress events for a project.</summary>
    public async Task JoinProject(string projectId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project:{projectId}");

    /// <summary>Unsubscribe from a project group (e.g. when navigating away).</summary>
    public async Task LeaveProject(string projectId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project:{projectId}");

    /// <summary>Subscribe to granular scanner progress for a specific scan.</summary>
    public async Task JoinScan(string scanId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, $"scan:{scanId}");

    /// <summary>Unsubscribe from a scan group.</summary>
    public async Task LeaveScan(string scanId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"scan:{scanId}");
}
