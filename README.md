# G-Helper Remote Control

A web-based remote control for [G-Helper](https://github.com/seerge/g-helper) (open-source Armoury Crate replacement) on ASUS Zephyrus laptops. Access all your laptop controls from any phone, tablet, or PC on the same network.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4) ![Windows](https://img.shields.io/badge/platform-Windows-0078D6) ![License](https://img.shields.io/badge/license-MIT-green)

## Features

- **Live Monitoring** - CPU/GPU temps, fan speeds, and battery updated every second via WebSocket
- **Performance Modes** - Switch between Silent, Balanced, and Turbo
- **GPU Modes** - Eco, Standard, and Ultimate (MUX switch)
- **Interactive Fan Curves** - Drag-to-edit SVG editor with 8 control points per fan
- **Display Controls** - Refresh rate, auto-switch, and overdrive
- **Keyboard Controls** - Backlight brightness and lighting mode
- **Battery Management** - Charge limit slider and health monitoring
- **LAN-Only Security** - Rejects connections from outside your local network
- **Windows Service** - Runs in the background, starts automatically

## Architecture

```
[Phone/Tablet/PC Browser] --HTTP/WS--> [ASP.NET Core on Zephyrus:5123]
                                              |
                                  +-----------+-----------+
                                  |           |           |
                           ACPI reads    config.json   Process mgmt
                           (sensors)    (read/write)   (restart G-Helper)
```

- **Monitoring**: Direct ATKACPI DSTS reads (read-only, safe, no conflict with G-Helper)
- **Control**: Modify `config.json` -> kill G-Helper -> atomic file replace -> restart G-Helper
- **Real-time**: SignalR WebSocket pushes sensor data every ~1 second

## Download

**[Download GHelperRemote-win-x64.zip](https://github.com/n1tr0maverick/GHelperRemote/releases/latest/download/GHelperRemote-win-x64.zip)** (self-contained, no .NET install required)

1. Download and extract the zip
2. Right-click `GHelperRemote.Web.exe` -> **Run as Administrator**
3. Open `http://localhost:5123` on the laptop, or `http://<laptop-ip>:5123` from your phone

To install as an auto-start background service, run `scripts/install-service.ps1` as Administrator (see below).

## Prerequisites

- Windows 10/11 with ASUS system drivers
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build)
- [G-Helper](https://github.com/seerge/g-helper) installed and running
- Administrator privileges (required for ACPI sensor access)

## Quick Start

```powershell
# Clone
git clone https://github.com/n1tr0maverick/GHelperRemote.git
cd GHelperRemote

# Build
dotnet publish src/GHelperRemote.Web -c Release -o ./publish

# Run (as Administrator)
.\publish\GHelperRemote.Web.exe
```

Open `http://localhost:5123` on the laptop, or `http://<laptop-ip>:5123` from any device on the same network.

## Install as Windows Service

```powershell
# Install (run as Administrator)
.\scripts\install-service.ps1

# Or specify a custom path
.\scripts\install-service.ps1 -Path "C:\GHelperRemote\GHelperRemote.Web.exe"

# Uninstall
.\scripts\uninstall-service.ps1
```

The install script:
1. Registers `GHelperRemote` as an auto-start Windows service
2. Adds a firewall rule for TCP port 5123 (private network profile only)
3. Starts the service

## Configuration

Edit `appsettings.json`:

| Setting | Default | Description |
|---|---|---|
| `GHelper:ConfigPath` | `%AppData%\GHelper\config.json` | Path to G-Helper's config |
| `GHelper:ExecutablePath` | Auto-detected | Path to `GHelper.exe` |
| `Network:AllowedSubnets` | `10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8` | Allowed client subnets |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:5123` | Listening address and port |

## Usage

### Status Bar
Always-visible bar showing live CPU/GPU temperatures (color-coded), fan RPMs, and battery percentage. Updates every second.

### Performance Mode
Tap **Silent**, **Balanced**, or **Turbo**. G-Helper restarts automatically (~3-4s) to apply the change.

### GPU Mode
- **Eco** - dGPU powered off, iGPU only (best battery life)
- **Standard** - dGPU available on demand via Optimus
- **Ultimate** - MUX switch direct to dGPU (requires reboot)

### Fan Curve Editor
1. Select which **Profile** to edit (Balanced/Turbo/Silent)
2. Switch between **CPU Fan** and **GPU Fan** tabs
3. Drag the 8 points on the SVG chart (works with mouse and touch)
4. Points enforce monotonic ordering automatically
5. Click **Save Curve** to apply

### Display
Set min/max refresh rate, toggle auto-switch and overdrive.

### Keyboard
Brightness slider (Off/Low/Med/High) and lighting mode selector.

### Battery
View health info (capacity, health %) and set the charge limit (20-100%).

## How It Works

G-Helper has no API or IPC mechanism. This companion app works around that:

1. **Sensors** are read directly from the ATKACPI driver via `DeviceIoControl` (DSTS queries). This is read-only and safe to do concurrently with G-Helper.

2. **Settings changes** follow this sequence:
   - Stop G-Helper process
   - Write updated values to `config.json` (atomic: write to `.tmp` then `File.Move`)
   - Restart G-Helper (which reads the fresh config on startup)
   - Total cycle: ~3-4 seconds, serialized with a 3-second cooldown

3. **Config monitoring** via `FileSystemWatcher` detects when G-Helper itself changes the config, and invalidates the in-memory cache.

## Project Structure

```
GHelperRemote/
├── src/
│   ├── GHelperRemote.Web/          # ASP.NET Core host
│   │   ├── Controllers/            # 7 REST API controllers
│   │   ├── Hubs/SensorHub.cs       # SignalR real-time push
│   │   ├── Middleware/             # LAN-only IP filtering
│   │   └── wwwroot/               # Static SPA (HTML/CSS/JS)
│   └── GHelperRemote.Core/        # Business logic library
│       ├── Acpi/                   # P/Invoke ATKACPI driver
│       ├── Models/                 # Data models
│       └── Services/              # Config, process, sensor services
└── scripts/                       # Service install/uninstall
```

## API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| GET | `/api/status` | Full system status (sensors + config + battery) |
| GET/PUT | `/api/mode` | Performance mode (0=Balanced, 1=Turbo, 2=Silent) |
| GET/PUT | `/api/gpu` | GPU mode (0=Eco, 1=Standard, 2=Ultimate) |
| GET/PUT | `/api/fans/{modeId}` | Fan curves for a performance profile |
| GET/PUT | `/api/display` | Display settings |
| GET/PUT | `/api/keyboard` | Keyboard backlight settings |
| GET/PUT | `/api/battery` | Battery status and charge limit |
| WS | `/hubs/sensors` | SignalR hub for live sensor data |

## Troubleshooting

| Problem | Solution |
|---|---|
| Can't connect from phone | Check firewall: `Get-NetFirewallRule -Name GHelperRemote` |
| Sensors show 0 | Must run as Administrator or LocalSystem service |
| Config not found | Set `GHelper:ConfigPath` in `appsettings.json` |
| G-Helper doesn't restart | Set `GHelper:ExecutablePath` in `appsettings.json` |
| 403 Forbidden | Your device's IP isn't in `Network:AllowedSubnets` |

## Dependencies

- `Microsoft.Extensions.Hosting.WindowsServices` - Windows service support
- `System.Management` - WMI battery queries
- No other third-party packages

## License

MIT
