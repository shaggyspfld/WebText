using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WebText.Web.Hubs;

/// <summary>
/// Real-time channel shared by every open browser. Pushes new/updated messages and shows who else is
/// viewing or replying to a thread, so two staff members don't answer the same client at once.
/// </summary>
[Authorize]
public class MessagingHub : Hub
{
    // connectionId -> (clientId, user). Presence is per-server; fine for a single IIS site.
    private static readonly ConcurrentDictionary<string, (int ClientId, string User)> Viewing = new();

    public static string ThreadGroup(int clientId) => $"thread-{clientId}";

    private string UserName => Context.User?.Identity?.Name ?? "unknown";

    public async Task JoinThread(int clientId)
    {
        await LeaveCurrent();
        Viewing[Context.ConnectionId] = (clientId, UserName);
        await Groups.AddToGroupAsync(Context.ConnectionId, ThreadGroup(clientId));
        await BroadcastViewers(clientId);
    }

    public async Task LeaveThread() => await LeaveCurrent();

    /// <summary>Called (throttled) by the browser while the user is typing a reply.</summary>
    public Task Composing(int clientId) =>
        Clients.OthersInGroup(ThreadGroup(clientId)).SendAsync("composing", clientId, UserName);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await LeaveCurrent();
        await base.OnDisconnectedAsync(exception);
    }

    private async Task LeaveCurrent()
    {
        if (Viewing.TryRemove(Context.ConnectionId, out var v))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, ThreadGroup(v.ClientId));
            await BroadcastViewers(v.ClientId);
        }
    }

    private Task BroadcastViewers(int clientId)
    {
        var users = Viewing.Values.Where(x => x.ClientId == clientId).Select(x => x.User)
                           .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        return Clients.Group(ThreadGroup(clientId)).SendAsync("viewers", clientId, users);
    }
}
