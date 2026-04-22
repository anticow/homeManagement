using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace HomeManagement.Broker.Host.Hubs;

/// <summary>
/// SignalR hub for real-time event broadcasting — job progress, agent status, audit events.
/// Clients subscribe to specific event groups.
/// </summary>
[Authorize]
public sealed class EventHub : Hub
{
    /// <summary>
    /// Join a group to receive specific event types (e.g., "jobs", "agents", "audit").
    /// </summary>
    public Task JoinGroup(string groupName)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, groupName);
    }

    /// <summary>
    /// Leave an event group.
    /// </summary>
    public Task LeaveGroup(string groupName)
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
    }
}
