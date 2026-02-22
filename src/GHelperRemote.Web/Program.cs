using GHelperRemote.Core.Services;
using GHelperRemote.Web.Hubs;
using GHelperRemote.Web.Middleware;
using GHelperRemote.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows Service when deployed
builder.Host.UseWindowsService();

// Core services (singletons)
builder.Services.AddSingleton<AcpiSensorService>();
builder.Services.AddSingleton<GHelperConfigService>();
builder.Services.AddSingleton<GHelperProcessService>();
builder.Services.AddSingleton<ISensorBroadcaster, SignalRSensorBroadcaster>();

// Background hosted services
builder.Services.AddHostedService<SensorPollingService>();
builder.Services.AddHostedService<ConfigWatcherService>();

// SignalR for real-time sensor broadcasting
builder.Services.AddSignalR();

// API controllers
builder.Services.AddControllers();

var app = builder.Build();

// Serve static files (SPA frontend from wwwroot/)
app.UseStaticFiles();

// Restrict access to local network only
app.UseLocalNetworkOnly();

app.UseRouting();

// Map API controllers
app.MapControllers();

// Map the SignalR sensor hub
app.MapHub<SensorHub>("/hubs/sensors");

// SPA fallback: serve index.html for any unmatched routes
app.MapFallbackToFile("index.html");

app.Run();
