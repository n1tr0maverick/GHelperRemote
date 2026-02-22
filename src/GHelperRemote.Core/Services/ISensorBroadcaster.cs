using GHelperRemote.Core.Models;

namespace GHelperRemote.Core.Services;

/// <summary>
/// Abstraction for broadcasting sensor data to connected clients.
/// Implemented by the Web project to push data via SignalR.
/// </summary>
public interface ISensorBroadcaster
{
    /// <summary>
    /// Broadcasts the current sensor data to all connected clients.
    /// </summary>
    /// <param name="status">The current system status including sensor readings and mode information.</param>
    Task BroadcastSensorDataAsync(SystemStatus status);
}
