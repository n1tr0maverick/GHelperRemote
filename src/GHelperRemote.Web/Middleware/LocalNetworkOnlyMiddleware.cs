using System.Net;

namespace GHelperRemote.Web.Middleware;

/// <summary>
/// Middleware that rejects requests originating from non-LAN IP addresses with a 403 Forbidden response.
/// Allowed subnets are configurable via the "Network:AllowedSubnets" section in appsettings.
/// </summary>
public class LocalNetworkOnlyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly List<(IPAddress Network, int PrefixLength)> _allowedSubnets;
    private readonly ILogger<LocalNetworkOnlyMiddleware> _logger;

    public LocalNetworkOnlyMiddleware(
        RequestDelegate next,
        IConfiguration configuration,
        ILogger<LocalNetworkOnlyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowedSubnets = new List<(IPAddress, int)>();

        var subnets = configuration.GetSection("Network:AllowedSubnets").Get<string[]>()
            ?? new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8" };

        foreach (var subnet in subnets)
        {
            var parts = subnet.Split('/');
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out var network)
                && int.TryParse(parts[1], out var prefixLength))
            {
                _allowedSubnets.Add((network, prefixLength));
            }
            else
            {
                _logger.LogWarning("Invalid subnet format in configuration: {Subnet}", subnet);
            }
        }

        // Always allow IPv6 loopback
        _allowedSubnets.Add((IPAddress.IPv6Loopback, 128));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;

        if (remoteIp == null)
        {
            _logger.LogWarning("Request rejected: no remote IP address available");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Forbidden");
            return;
        }

        // Map IPv6-mapped IPv4 addresses back to IPv4
        if (remoteIp.IsIPv4MappedToIPv6)
        {
            remoteIp = remoteIp.MapToIPv4();
        }

        if (IPAddress.IsLoopback(remoteIp))
        {
            await _next(context);
            return;
        }

        if (IsAllowed(remoteIp))
        {
            await _next(context);
            return;
        }

        _logger.LogWarning("Request rejected from non-LAN IP: {RemoteIp}", remoteIp);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden: only local network access is allowed");
    }

    private bool IsAllowed(IPAddress address)
    {
        foreach (var (network, prefixLength) in _allowedSubnets)
        {
            if (IsInSubnet(address, network, prefixLength))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInSubnet(IPAddress address, IPAddress network, int prefixLength)
    {
        var addressBytes = address.GetAddressBytes();
        var networkBytes = network.GetAddressBytes();

        if (addressBytes.Length != networkBytes.Length)
        {
            return false;
        }

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits > 0 && fullBytes < addressBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((addressBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Extension methods for registering the LocalNetworkOnlyMiddleware.
/// </summary>
public static class LocalNetworkOnlyMiddlewareExtensions
{
    public static IApplicationBuilder UseLocalNetworkOnly(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<LocalNetworkOnlyMiddleware>();
    }
}
