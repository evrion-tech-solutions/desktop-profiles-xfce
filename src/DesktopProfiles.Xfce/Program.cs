using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DesktopProfiles.Core;
using DesktopProfiles.Core.Abstractions;
using DesktopProfiles.Core.Models;

namespace DesktopProfiles.Xfce
{
    /// <summary>
    /// Desktop Profiles Xfce — workspace-aware wallpaper daemon with per-monitor support.
    ///
    /// Uses event-driven workspace detection via xprop -spy for instant response
    /// (zero CPU when idle, immediate wallpaper change on workspace switch).
    ///
    /// Usage:
    ///   desktop-profiles-xfce                        # Run daemon with default config
    ///   desktop-profiles-xfce --config ~/dp.json     # Custom config path
    ///   desktop-profiles-xfce --init ~/wallpapers    # Generate default config
    ///   desktop-profiles-xfce --once                 # Apply current workspace and exit
    /// </summary>
    class Program
    {
        private static readonly string DefaultConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "desktop-profiles", "config.json");

        static int Main(string[] args)
        {
            string configPath = DefaultConfigPath;
            bool runOnce = false;
            bool initMode = false;
            string initThemesPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--config" when i + 1 < args.Length:
                        configPath = args[++i];
                        break;
                    case "--init" when i + 1 < args.Length:
                        initMode = true;
                        initThemesPath = args[++i];
                        break;
                    case "--init":
                        initMode = true;
                        initThemesPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "wallpapers");
                        break;
                    case "--once":
                        runOnce = true;
                        break;
                    case "--help":
                    case "-h":
                        PrintHelp();
                        return 0;
                    case "--version":
                        Console.WriteLine("Desktop Profiles Xfce 1.0.0 (Core " +
                            typeof(ImageResolver).Assembly.GetName().Version + ")");
                        return 0;
                }
            }

            if (initMode)
                return RunInit(configPath, initThemesPath);

            // Load config
            var configProvider = new ConfigProvider();
            var config = configProvider.Load(configPath);

            if (config == null)
            {
                Console.Error.WriteLine($"[Desktop Profiles] Config not found: {configPath}");
                Console.Error.WriteLine("  Run: desktop-profiles-xfce --init [themes-path]");
                return 1;
            }

            Console.WriteLine($"[Desktop Profiles] Platform: Xfce (event-driven)");
            Console.WriteLine($"  Config: {configPath}");
            Console.WriteLine($"  Themes path: {config.ThemesPath}");
            Console.WriteLine($"  Context mappings: {config.ContextMappings.Count}");

            // Create Xfce platform adapters
            var monitorProvider = new XfceMonitorProvider();
            var contextProvider = new XfceDesktopContextProvider();
            var imageResolver = new ImageResolver();
            var themeScanner = new ThemeScanner();

            // Scan themes
            var themes = themeScanner.ScanThemes(config.ThemesPath);
            Console.WriteLine($"  Discovered themes: {themes.Count}");

            if (themes.Count == 0)
            {
                Console.Error.WriteLine($"[Desktop Profiles] No themes found in: {config.ThemesPath}");
                Console.Error.WriteLine("  Expected: {themesPath}/{theme-name}/*-desktop-background-WxH.png");
                return 1;
            }

            foreach (var theme in themes)
                Console.WriteLine($"    - {theme.Name} ({theme.Wallpapers.Count} aspect ratios)");

            // Detect monitors
            var monitors = monitorProvider.GetMonitors();
            Console.WriteLine($"  Monitors: {monitors.Count}");
            foreach (var m in monitors)
                Console.WriteLine($"    - {m.Id}: {m.Width}x{m.Height} ({AspectRatioClassifier.Classify(m.Width, m.Height)})");

            // Detect workspaces
            int wsCount = contextProvider.GetContextCount();
            int currentWs = contextProvider.GetCurrentContextIndex();
            Console.WriteLine($"  Workspaces: {wsCount} (current: {currentWs})");

            // Pre-resolve wallpapers for all workspaces into a lookup table
            var wallpaperMap = BuildWallpaperMap(config, monitorProvider, contextProvider, imageResolver, themes);

            // Apply current workspace immediately
            ApplyWorkspace(currentWs, wallpaperMap, config);

            if (runOnce)
            {
                Console.WriteLine("[Desktop Profiles] Applied (--once mode, exiting)");
                return 0;
            }

            // Generate and run bash daemon (xfdesktop detects live xfconf changes)
            string daemonScript = GenerateDaemonScript(wallpaperMap, config, monitors);
            Console.WriteLine("[Desktop Profiles] Starting event-driven bash daemon...");
            RunBashDaemon(daemonScript);

            return 0;
        }

        private static int RunInit(string configPath, string themesPath)
        {
            var configProvider = new ConfigProvider();

            if (File.Exists(configPath))
            {
                Console.Error.WriteLine($"[Desktop Profiles] Config already exists: {configPath}");
                Console.Error.WriteLine("  Delete it first to regenerate.");
                return 1;
            }

            var config = configProvider.CreateDefault(themesPath);
            configProvider.Save(configPath, config);

            Console.WriteLine($"[Desktop Profiles] Config created: {configPath}");
            Console.WriteLine($"  Themes path: {themesPath}");
            Console.WriteLine("  Edit the config to assign themes to workspaces.");
            return 0;
        }

        /// <summary>
        /// Pre-resolves all workspace → monitor → wallpaper-path assignments at startup.
        /// This avoids doing theme scanning / resolution matching on every workspace switch.
        /// Key: workspace index → Dictionary(monitorId → imagePath)
        /// </summary>
        private static Dictionary<int, Dictionary<string, string>> BuildWallpaperMap(
            AppConfig config,
            IMonitorProvider monitorProvider,
            IDesktopContextProvider contextProvider,
            IImageResolver imageResolver,
            IReadOnlyList<WallpaperTheme> themes)
        {
            var monitors = monitorProvider.GetMonitors();
            int wsCount = contextProvider.GetContextCount();
            var map = new Dictionary<int, Dictionary<string, string>>();

            for (int ws = 0; ws < wsCount; ws++)
            {
                var resolution = imageResolver.Resolve(ws, monitors, config, themes);
                var assignments = new Dictionary<string, string>();

                foreach (var kvp in resolution.Assignments)
                {
                    if (kvp.Value != null && File.Exists(kvp.Value))
                        assignments[kvp.Key] = kvp.Value;
                }

                map[ws] = assignments;

                var mapping = config.ContextMappings.FirstOrDefault(m => m.ContextIndex == ws);
                string themeName = mapping?.ThemeName ?? "?";
                Console.WriteLine($"  WS{ws} ({themeName}): {assignments.Count} monitors");
                foreach (var kvp in assignments)
                    Console.WriteLine($"    {kvp.Key} → {Path.GetFileName(kvp.Value)}");
            }

            return map;
        }

        /// <summary>
        /// Applies wallpaper for a specific workspace on all monitors.
        /// Uses xfconf-query to set the CURRENT workspace's last-image property.
        /// </summary>
        private static void ApplyWorkspace(
            int workspaceIndex,
            Dictionary<int, Dictionary<string, string>> wallpaperMap,
            AppConfig config)
        {
            if (!wallpaperMap.TryGetValue(workspaceIndex, out var assignments) || assignments.Count == 0)
            {
                Console.WriteLine($"  WS{workspaceIndex}: no wallpaper assignment");
                return;
            }

            var setter = new XfceWallpaperSetter(workspaceIndex);
            setter.SetPerMonitor(assignments);
        }

        /// <summary>
        /// Discovers all xfconf monitor property names from the xfce4-desktop channel.
        /// Returns names like: monitorDP-0, monitorHDMI-0, monitor0, monitoreDP-1-1 etc.
        /// </summary>
        private static List<string> GetXfconfMonitorNames()
        {
            try
            {
                var psi = new ProcessStartInfo("xfconf-query")
                {
                    Arguments = "-c xfce4-desktop -l",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null) return new List<string>();
                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit();

                    var names = new HashSet<string>();
                    foreach (var line in output.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (!trimmed.Contains("/workspace") || !trimmed.Contains("/last-image"))
                            continue;

                        var parts = trimmed.Split('/');
                        if (parts.Length >= 6 && parts[3].StartsWith("monitor"))
                            names.Add(parts[3]);
                    }

                    return names.OrderBy(n => n).ToList();
                }
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Maps an xfconf monitor property name to a physical monitor ID from xrandr.
        /// Example: "monitorDP-0" → "DP-0", "monitor0" → monitors[0].Id
        /// </summary>
        private static string ResolveXfconfToPhysical(
            string xfconfName,
            IReadOnlyList<MonitorInfo> monitors)
        {
            string stripped = xfconfName.Substring("monitor".Length);

            // Direct match: monitorDP-0 → DP-0
            foreach (var m in monitors)
            {
                if (m.Id == stripped)
                    return m.Id;
            }

            // Index match: monitor0 → monitors[0]
            if (int.TryParse(stripped, out int idx) && idx >= 0 && idx < monitors.Count)
                return monitors[idx].Id;

            // Case-insensitive match
            foreach (var m in monitors)
            {
                if (string.Equals(stripped, m.Id, StringComparison.OrdinalIgnoreCase))
                    return m.Id;
            }

            // Partial/prefix match: monitorHDMI-1-0 → HDMI-0 (matches "HDMI" prefix)
            // Try matching connector type (DP, HDMI, eDP, VGA, DVI, etc.)
            foreach (var m in monitors)
            {
                string connectorType = new string(m.Id.TakeWhile(c => char.IsLetter(c) || c == '-').ToArray()).TrimEnd('-');
                string strippedType = new string(stripped.TakeWhile(c => char.IsLetter(c) || c == '-').ToArray()).TrimEnd('-');
                if (!string.IsNullOrEmpty(connectorType) && !string.IsNullOrEmpty(strippedType) &&
                    string.Equals(connectorType, strippedType, StringComparison.OrdinalIgnoreCase))
                    return m.Id;
            }

            return null;
        }

        /// <summary>
        /// Generates a self-contained bash daemon script that handles workspace switching.
        ///
        /// Key insight: xfdesktop 4.18 detects live xfconf property changes and updates
        /// the wallpaper immediately — no reload needed. However, it does NOT automatically
        /// switch wallpapers when changing workspaces.
        ///
        /// Strategy: On workspace switch, set ALL workspace paths (ws0–ws3) on ALL monitors
        /// to the same wallpaper image. This guarantees a value-change event because the
        /// previous switch set them to a different theme. No clearing/invalidation needed.
        /// </summary>
        private static string GenerateDaemonScript(
            Dictionary<int, Dictionary<string, string>> wallpaperMap,
            AppConfig config,
            IReadOnlyList<MonitorInfo> monitors)
        {
            // Discover all xfconf monitor property names
            var xfconfMonNames = GetXfconfMonitorNames();

            // Ensure standard "monitorXXX" entries exist for active monitors
            foreach (var m in monitors)
            {
                string xfName = "monitor" + m.Id;
                if (!xfconfMonNames.Contains(xfName))
                    xfconfMonNames.Add(xfName);
            }

            // Group xfconf names by physical monitor they correspond to
            var monitorGroups = new Dictionary<string, List<string>>();
            string defaultPhysId = monitors.Count > 0 ? monitors[0].Id : "unknown";

            foreach (var xfName in xfconfMonNames)
            {
                string physId = ResolveXfconfToPhysical(xfName, monitors) ?? defaultPhysId;
                if (!monitorGroups.ContainsKey(physId))
                    monitorGroups[physId] = new List<string>();
                monitorGroups[physId].Add(xfName);
            }

            Console.WriteLine($"  xfconf monitor paths: {xfconfMonNames.Count} ({monitorGroups.Count} groups)");
            foreach (var g in monitorGroups)
                Console.WriteLine($"    {g.Key}: {string.Join(", ", g.Value)}");

            int wsCount = wallpaperMap.Count;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("#!/bin/bash");
            sb.AppendLine("# Desktop Profiles Xfce — workspace wallpaper daemon");
            sb.AppendLine("# Event-driven via xprop -spy (zero CPU when idle)");
            sb.AppendLine("# xfdesktop detects live xfconf changes — no reload needed");
            sb.AppendLine();
            sb.AppendLine("export DISPLAY=:0.0");
            sb.AppendLine("export DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$(id -u)/bus");
            sb.AppendLine();
            sb.AppendLine("LAST_WS=-1");
            sb.AppendLine();

            // Emit monitor group variables
            foreach (var group in monitorGroups)
            {
                string varName = "MONS_" + group.Key.Replace("-", "_").Replace(".", "_");
                sb.AppendLine(varName + "=\"" + string.Join(" ", group.Value) + "\"");
            }
            sb.AppendLine();

            // set_all helper: sets ALL workspace paths (ws0..N) for a monitor group to one image
            sb.AppendLine("# Sets ALL workspace paths for a monitor group to the same image");
            sb.AppendLine("set_all() {");
            sb.AppendLine("  local mons=\"$1\"");
            sb.AppendLine("  local img=\"$2\"");
            sb.AppendLine("  for mon in $mons; do");
            sb.AppendLine("    for ws in " + string.Join(" ", Enumerable.Range(0, wsCount)) + "; do");
            sb.AppendLine("      xfconf-query -c xfce4-desktop -p \"/backdrop/screen0/${mon}/workspace${ws}/last-image\" -s \"$img\" 2>/dev/null &");
            sb.AppendLine("    done");
            sb.AppendLine("  done");
            sb.AppendLine("}");
            sb.AppendLine();

            // Apply function — sets all workspace paths on all monitors to the target theme
            sb.AppendLine("apply_ws() {");
            sb.AppendLine("  local target=$1");
            sb.AppendLine("  case $target in");

            foreach (var wsKvp in wallpaperMap)
            {
                int ws = wsKvp.Key;
                var assignments = wsKvp.Value;
                var mapping = config.ContextMappings.FirstOrDefault(m => m.ContextIndex == ws);
                string themeName = mapping?.ThemeName ?? "?";

                sb.AppendLine("    " + ws + ")");
                sb.AppendLine("      echo \"[$(date +%H:%M:%S)] WS" + ws + " → " + themeName + "\"");

                foreach (var group in monitorGroups)
                {
                    string physId = group.Key;
                    string varName = "MONS_" + physId.Replace("-", "_").Replace(".", "_");
                    string imgPath = assignments.ContainsKey(physId)
                        ? assignments[physId]
                        : assignments.Values.FirstOrDefault() ?? "";

                    sb.AppendLine("      set_all \"$" + varName + "\" \"" + imgPath + "\"");
                }

                sb.AppendLine("      wait");
                sb.AppendLine("      ;;");
            }

            sb.AppendLine("  esac");
            sb.AppendLine("}");
            sb.AppendLine();

            // Apply current workspace at startup
            sb.AppendLine("echo \"=== Desktop Profiles daemon started ===\"");
            sb.AppendLine("CURRENT_WS=$(xprop -root _NET_CURRENT_DESKTOP | awk '{print $NF}')");
            sb.AppendLine("apply_ws $CURRENT_WS");
            sb.AppendLine("LAST_WS=$CURRENT_WS");
            sb.AppendLine();

            // Watch loop
            sb.AppendLine("# Watch for workspace changes (event-driven, zero CPU idle)");
            sb.AppendLine("echo \"Watching for workspace switches...\"");
            sb.AppendLine("xprop -root -spy _NET_CURRENT_DESKTOP | while read line; do");
            sb.AppendLine("  ws=$(echo \"$line\" | awk '{print $NF}')");
            sb.AppendLine("  if [ \"$ws\" != \"$LAST_WS\" ]; then");
            sb.AppendLine("    apply_ws $ws");
            sb.AppendLine("    LAST_WS=$ws");
            sb.AppendLine("  fi");
            sb.AppendLine("done");

            return sb.ToString();
        }

        /// <summary>
        /// Runs the generated bash daemon script.
        /// Forwards Ctrl+C to the bash process for clean shutdown.
        /// </summary>
        private static void RunBashDaemon(string script)
        {
            // Write script to temp file
            string scriptPath = Path.Combine(Path.GetTempPath(), "desktop-profiles-daemon.sh");
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = scriptPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                {
                    Console.Error.WriteLine("[Desktop Profiles] Failed to start bash daemon");
                    return;
                }

                // Forward stdout
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) Console.WriteLine(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) Console.Error.WriteLine(e.Data);
                };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    try { process.Kill(true); } catch { }
                    Console.WriteLine("\n[Desktop Profiles] Shutting down...");
                };

                process.WaitForExit();
            }

            // Cleanup
            try { File.Delete(Path.Combine(Path.GetTempPath(), "desktop-profiles-daemon.sh")); } catch { }
        }

        private static (byte r, byte g, byte b) ParseHexColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 7)
                return (11, 16, 32);

            try
            {
                byte r = Convert.ToByte(hex.Substring(1, 2), 16);
                byte g = Convert.ToByte(hex.Substring(3, 2), 16);
                byte b = Convert.ToByte(hex.Substring(5, 2), 16);
                return (r, g, b);
            }
            catch
            {
                return (11, 16, 32);
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine(@"Desktop Profiles Xfce — workspace-aware wallpaper daemon

Usage:
  desktop-profiles-xfce                        Run daemon with default config
  desktop-profiles-xfce --config <path>        Use custom config file
  desktop-profiles-xfce --init [themes-path]   Generate default config
  desktop-profiles-xfce --once                 Apply wallpaper once and exit
  desktop-profiles-xfce --help                 Show this help
  desktop-profiles-xfce --version              Show version

How it works:
  Uses xprop -spy (event-driven) to detect workspace changes instantly.
  Zero CPU usage when idle. Immediate wallpaper switch on workspace change.
  Also detects monitor hotplug every 10 seconds.

Config location (default):
  ~/.config/desktop-profiles/config.json

Theme structure:
  {themesPath}/{theme-name}/*-desktop-background-{W}x{H}.png

Examples:
  desktop-profiles-xfce --init ~/Utveckling/desktop-profiles/wallpapers
  desktop-profiles-xfce --once
  desktop-profiles-xfce --config ~/dp.json

By Evrion Tech Solutions AB — https://evrion.se");
        }
    }
}
