using Microsoft.AspNetCore.SignalR;

namespace GHelperRemote.Web.Hubs;

/// <summary>
/// SignalR hub for broadcasting real-time sensor data to connected clients.
/// Server-push only — no client-callable methods.
/// Client method name: "SensorData".
/// </summary>
public class SensorHub : Hub
{
}
