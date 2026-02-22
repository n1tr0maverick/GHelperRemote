using Microsoft.AspNetCore.SignalR;

using GHelperRemote.Core.Models;
using GHelperRemote.Core.Services;
using GHelperRemote.Web.Hubs;

namespace GHelperRemote.Web.Services;

/// <summary>
/// Broadcasts sensor data to all connected SignalR clients via the SensorHub.
/// </summary>
public class SignalRSensorBroadcaster : ISensorBroadcaster
{
    private readonly IHubContext<SensorHub> _hubContext;

    public SignalRSensorBroadcaster(IHubContext<SensorHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task BroadcastSensorDataAsync(SystemStatus status)
    {
        await _hubContext.Clients.All.SendAsync("SensorData", status);
    }
}
