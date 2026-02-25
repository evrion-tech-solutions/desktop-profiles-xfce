# Desktop Profiles — Xfce

Per-monitor, per-workspace wallpaper management for Xfce. Automatically changes wallpapers when you switch workspaces — with native per-monitor support.

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE)
[![Desktop: Xfce](https://img.shields.io/badge/Desktop-Xfce-2DABEF)](https://www.xfce.org/)

## Features

- **Per-monitor per-workspace** — Xfce's native backdrop system supports individual wallpapers per monitor per workspace
- **Event-driven** — uses `xprop -spy` for instant workspace detection (zero CPU when idle)
- **Multi-monitor** — detects all connected monitors via xrandr
- **Aspect ratio matching** — Core automatically picks the right resolution for each monitor
- **Theme scanning** — point at a folder and themes are discovered automatically
- **Daemon mode** — generates an optimized bash script for maximum efficiency
- **GUI mode** — Avalonia-based graphical settings panel with tray icon
- **Monitor hotplug** — detects when monitors are connected/removed
- **Live switching** — xfdesktop detects xfconf changes live, no reload needed

## Why Xfce?

Xfce has the **best wallpaper model** of any Linux desktop:
- Native per-monitor wallpapers (each screen gets its own image)
- Native per-workspace wallpapers (each workspace has its own backdrop)
- xfdesktop detects live `xfconf` property changes

Desktop Profiles leverages all of this for truly per-monitor, per-workspace wallpaper management.

## Install

### Prerequisites

- [.NET 8 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Xfce desktop environment (tested on Xubuntu 24.04, Xfce 4.18)

### Build from source

```bash
git clone --recurse-submodules https://github.com/evrion-tech-solutions/desktop-profiles-xfce.git
cd desktop-profiles-xfce
dotnet build src/DesktopProfiles.Xfce.Gui/DesktopProfiles.Xfce.Gui.csproj
```

### Run

```bash
# GUI (recommended)
dotnet run --project src/DesktopProfiles.Xfce.Gui

# Daemon (headless)
dotnet run --project src/DesktopProfiles.Xfce -- --init ~/wallpapers
dotnet run --project src/DesktopProfiles.Xfce
```

## Usage

### GUI

Launch the app — it creates a default config if none exists. Assign themes to workspaces, click "Save & Apply". The daemon uses `xprop -spy` for event-driven workspace detection: zero CPU when idle, instant wallpaper switch.

### Daemon (CLI)

```bash
desktop-profiles-xfce --init ~/wallpapers   # Generate default config
desktop-profiles-xfce                        # Run event-driven daemon
desktop-profiles-xfce --once                # Apply once and exit
desktop-profiles-xfce --config ~/dp.json    # Custom config path
```

## How it works

### The set-all-workspace-paths strategy

1. At startup, Core pre-resolves ALL workspace→monitor→wallpaper assignments
2. When a workspace switch is detected, the daemon sets **all xfconf workspace paths** for all monitors to the target theme's images
3. This guarantees a value-change event (because the previous switch set them to a different theme)
4. xfdesktop detects the xfconf change and updates the desktop immediately — no reload needed

### Architecture

```
desktop-profiles-xfce/
├── lib/core/                          ← Submodule: desktop-profiles-core (MIT)
├── src/
│   ├── DesktopProfiles.Xfce/         ← Daemon: event-driven workspace watcher
│   │   ├── XfceDesktopContextProvider.cs   (xprop → workspace index)
│   │   ├── XfceMonitorProvider.cs          (xrandr → monitor list)
│   │   ├── XfceWallpaperSetter.cs          (xfconf-query → per-monitor wallpaper)
│   │   └── Program.cs                     (daemon + bash script generator)
│   └── DesktopProfiles.Xfce.Gui/     ← GUI: Avalonia settings panel
│       ├── ViewModels/MainWindowViewModel.cs
│       ├── Views/MainWindow.axaml
│       └── Assets/Styles.axaml              (Evrion brand theme)
└── DesktopProfiles.Xfce.sln
```

### xfconf property paths

```
/backdrop/screen0/monitor{CONNECTOR}/workspace{N}/last-image    (string: image path)
/backdrop/screen0/monitor{CONNECTOR}/workspace{N}/image-style   (int: 5 = Zoomed)
```

## Theme structure

```
~/wallpapers/
├── ocean/
│   ├── ocean-desktop-background-3840x2160.png    (16:9)
│   ├── ocean-desktop-background-5120x2160.png    (21:9)
│   └── ocean-desktop-background-2560x1600.png    (16:10)
├── forest/
│   └── ...
└── sunset/
    └── ...
```

## Related projects

| Project | Description |
|---|---|
| [desktop-profiles-core](https://github.com/evrion-tech-solutions/desktop-profiles-core) | Shared cross-platform core library (MIT) |
| [desktop-profiles-gnome](https://github.com/evrion-tech-solutions/desktop-profiles-gnome) | GNOME edition |
| [Desktop Profiles for Windows](https://tech.evrion.se/products/desktop-profiles) | Commercial Windows 11 edition |

## Contributing

Contributions welcome! Please open an issue first to discuss what you'd like to change.

## License

GPL-3.0 — see [LICENSE](LICENSE).

Core library is MIT licensed — see [lib/core/LICENSE](lib/core/LICENSE).

Copyright (c) 2026 [Evrion Tech Solutions AB](https://tech.evrion.se)
