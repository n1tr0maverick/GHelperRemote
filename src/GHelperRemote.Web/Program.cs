using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using GHelperRemote.Core.Services;
using GHelperRemote.Web.Diagnostics;
using GHelperRemote.Web.Hubs;
using GHelperRemote.Web.Middleware;
using GHelperRemote.Web.Services;

// ============================================================
// GHelperRemote — Diagnostic Startup
//
// All output goes to both console AND a timestamped log file
// in the "logs" folder next to the exe (or %TEMP% as fallback).
// Share the .log file for debugging.
// ============================================================

var dumpPath = Path.Combine(AppContext.BaseDirectory, "logs",
    $"ghelperremote-{DateTime.Now:yyyyMMdd-HHmmss}.log");

try
{
    DiagnosticLog.Initialize(dumpPath);
}
catch
{
    // Fall back to temp directory if we can't write next to the exe
    dumpPath = Path.Combine(Path.GetTempPath(),
        $"ghelperremote-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    try { DiagnosticLog.Initialize(dumpPath); }
    catch (Exception ex2)
    {
        Console.Error.WriteLine($"FATAL: Cannot create log file anywhere: {ex2.Message}");
    }
}

DiagnosticLog.Write("========================================");
DiagnosticLog.Write("  GHelperRemote - Diagnostic Startup");
DiagnosticLog.Write("========================================");
DiagnosticLog.Write("");
DiagnosticLog.Write($"Log file:      {DiagnosticLog.FilePath}");
DiagnosticLog.Write($"Time (UTC):    {DateTime.UtcNow:O}");
DiagnosticLog.Write($"Time (Local):  {DateTime.Now:O}");
DiagnosticLog.Write($"OS:            {RuntimeInformation.OSDescription}");
DiagnosticLog.Write($"Architecture:  {RuntimeInformation.OSArchitecture}");
DiagnosticLog.Write($"Framework:     {RuntimeInformation.FrameworkDescription}");
DiagnosticLog.Write($"Process ID:    {Environment.ProcessId}");
DiagnosticLog.Write($"Is 64-bit:     {Environment.Is64BitProcess}");
DiagnosticLog.Write($"Working dir:   {Environment.CurrentDirectory}");
DiagnosticLog.Write($"Base dir:      {AppContext.BaseDirectory}");
DiagnosticLog.Write($"User:          {Environment.UserName}");
DiagnosticLog.Write($"Machine:       {Environment.MachineName}");
DiagnosticLog.Write($"Command line:  {Environment.CommandLine}");
DiagnosticLog.Write($"Args:          [{string.Join(", ", args)}]");

// --- Admin check ---
try
{
    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
        DiagnosticLog.Write($"Is Admin:      {isAdmin}");
        if (!isAdmin)
        {
            DiagnosticLog.Write("  WARNING: Not running as Administrator!");
            DiagnosticLog.Write("  ACPI sensor access and process management will likely fail.");
            DiagnosticLog.Write("  Right-click the exe -> 'Run as administrator', or install as a Windows Service.");
        }
    }
}
catch (Exception ex)
{
    DiagnosticLog.Write($"Admin check:   FAILED ({ex.Message})");
}

// --- G-Helper config ---
DiagnosticLog.Write("");
DiagnosticLog.Write("--- G-Helper Config ---");
var defaultConfigPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "GHelper", "config.json");
DiagnosticLog.Write($"Default config path:  {defaultConfigPath}");
DiagnosticLog.Write($"Config file exists:   {File.Exists(defaultConfigPath)}");
if (File.Exists(defaultConfigPath))
{
    try
    {
        var fi = new FileInfo(defaultConfigPath);
        DiagnosticLog.Write($"Config file size:     {fi.Length} bytes");
        DiagnosticLog.Write($"Config last modified: {fi.LastWriteTime:O}");
    }
    catch { }
}
else
{
    DiagnosticLog.Write("  (Config file missing — this is OK, an empty config will be used.)");
    DiagnosticLog.Write("  (GHelper must have been run at least once for config to exist.)");
}

// --- G-Helper process ---
DiagnosticLog.Write("");
DiagnosticLog.Write("--- G-Helper Process ---");
try
{
    var ghelperProcs = Process.GetProcessesByName("GHelper");
    DiagnosticLog.Write($"GHelper running:      {ghelperProcs.Length > 0} ({ghelperProcs.Length} instance(s))");
    foreach (var p in ghelperProcs)
    {
        try
        {
            DiagnosticLog.Write($"  PID {p.Id}: {p.MainModule?.FileName ?? "(unknown path)"}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"  PID {p.Id}: (can't read path: {ex.Message})");
        }
        p.Dispose();
    }
}
catch (Exception ex)
{
    DiagnosticLog.Write($"Process check failed: {ex.Message}");
}

// --- ATKACPI ---
DiagnosticLog.Write("");
DiagnosticLog.Write("--- ATKACPI Device ---");
if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
{
    DiagnosticLog.Write(@"Device path:          \\.\ATKACPI");
    DiagnosticLog.Write("  (Device access will be tested on first sensor read)");
}
else
{
    DiagnosticLog.Write("  Not on Windows — ACPI device is unavailable.");
}

// --- Network ---
DiagnosticLog.Write("");
DiagnosticLog.Write("--- Network Interfaces ---");
DiagnosticLog.Write("Will listen on:       http://0.0.0.0:5123");
try
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

        var props = ni.GetIPProperties();
        foreach (var addr in props.UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
            {
                DiagnosticLog.Write($"  {ni.Name}: {addr.Address}  ->  http://{addr.Address}:5123");
            }
        }
    }
}
catch (Exception ex)
{
    DiagnosticLog.Write($"  Network enum failed: {ex.Message}");
}

// --- ASP.NET Core startup ---
DiagnosticLog.Write("");
DiagnosticLog.Write("--- Starting ASP.NET Core ---");

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Run as a Windows Service when deployed
    builder.Host.UseWindowsService();

    // === Logging: verbose file + console ===
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
    builder.Logging.AddProvider(new DiagnosticFileLoggerProvider());
    builder.Logging.SetMinimumLevel(LogLevel.Debug);
    // Keep ASP.NET internal noise at Information level
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Information);

    DiagnosticLog.Write("Logging configured (Debug level, writing to file + console)");

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

    DiagnosticLog.Write("Services registered");

    var app = builder.Build();

    DiagnosticLog.Write("Application built successfully");

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

    DiagnosticLog.Write("Middleware pipeline configured");
    DiagnosticLog.Write("");
    DiagnosticLog.Write("Starting web server on http://0.0.0.0:5123 ...");
    DiagnosticLog.Write("Press Ctrl+C to stop.");
    DiagnosticLog.Write("");

    app.Run();
}
catch (Exception ex)
{
    DiagnosticLog.Write("");
    DiagnosticLog.Write("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
    DiagnosticLog.Write("!!!        FATAL STARTUP ERROR      !!!");
    DiagnosticLog.Write("!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!");
    DiagnosticLog.Write("");
    DiagnosticLog.Write(ex.ToString());
    DiagnosticLog.Write("");
    DiagnosticLog.Write("The application failed to start.");
    DiagnosticLog.Write($"Share this log file for debugging: {DiagnosticLog.FilePath}");

    Console.Error.WriteLine();
    Console.Error.WriteLine("FATAL: " + ex.Message);
    Console.Error.WriteLine($"Full diagnostic log: {DiagnosticLog.FilePath}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Press any key to exit...");
    try { Console.ReadKey(intercept: true); }
    catch { /* non-interactive — just exit */ }
}
finally
{
    DiagnosticLog.Write("Application shutting down.");
    DiagnosticLog.Dispose();
}
