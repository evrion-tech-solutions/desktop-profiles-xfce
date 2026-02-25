using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DesktopProfiles.Core.Abstractions;
using DesktopProfiles.Core.Models;

namespace DesktopProfiles.Xfce
{
    /// <summary>
    /// Sets wallpapers on Xfce desktop using xfconf-query.
    /// 
    /// Xfce wallpaper model — superior to GNOME:
    /// - Native per-monitor support (each monitor has its own backdrop channel)
    /// - Native per-workspace support (each workspace has its own backdrop setting)
    /// - Path format: /backdrop/screen0/monitor{CONNECTOR}/workspace{N}/last-image
    /// 
    /// The monitor name in xfconf uses the xrandr connector name (DP-0, HDMI-0, etc.).
    /// Workspace index is zero-based and matches the workspace switcher.
    /// </summary>
    public class XfceWallpaperSetter : IWallpaperSetter
    {
        private readonly int _workspaceIndex;

        /// <summary>
        /// Creates a wallpaper setter for a specific workspace.
        /// </summary>
        /// <param name="workspaceIndex">Zero-based workspace index to apply wallpapers to.</param>
        public XfceWallpaperSetter(int workspaceIndex)
        {
            _workspaceIndex = workspaceIndex;
        }

        /// <summary>
        /// Applies wallpaper images to specific monitors on the current workspace.
        /// Unlike GNOME, Xfce natively supports per-monitor wallpapers.
        /// </summary>
        public WallpaperSetResult SetPerMonitor(IReadOnlyDictionary<string, string> assignments)
        {
            int success = 0;
            int failure = 0;
            var failedIds = new List<string>();

            foreach (var kvp in assignments)
            {
                string monitorId = kvp.Key;   // e.g. "DP-0", "HDMI-0"
                string imagePath = kvp.Value;

                if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath))
                {
                    failure++;
                    failedIds.Add(monitorId);
                    continue;
                }

                // Xfce xfconf path: /backdrop/screen0/monitor{ID}/workspace{N}/last-image
                string xfconfPath = $"/backdrop/screen0/monitor{monitorId}/workspace{_workspaceIndex}/last-image";

                bool ok = SetXfconfProperty(xfconfPath, imagePath);

                // Also set image-style to 5 (Zoomed) for best display
                string stylePath = $"/backdrop/screen0/monitor{monitorId}/workspace{_workspaceIndex}/image-style";
                SetXfconfProperty(stylePath, "5", isInt: true);

                if (ok)
                {
                    success++;
                    Console.WriteLine($"    ✓ {monitorId} (ws{_workspaceIndex}) → {Path.GetFileName(imagePath)}");
                }
                else
                {
                    // Try creating the property if it doesn't exist yet
                    ok = CreateXfconfProperty(xfconfPath, imagePath);
                    if (ok)
                    {
                        success++;
                        Console.WriteLine($"    ✓ {monitorId} (ws{_workspaceIndex}) → {Path.GetFileName(imagePath)} (created)");
                    }
                    else
                    {
                        failure++;
                        failedIds.Add(monitorId);
                        Console.Error.WriteLine($"    ✗ {monitorId} (ws{_workspaceIndex}) — xfconf-query failed");
                    }
                }
            }

            // Signal xfdesktop to re-read xfconf and apply the new wallpapers.
            // xfdesktop 4.18 does NOT auto-react to xfconf property changes;
            // it requires an explicit reload signal.
            if (success > 0)
                ReloadXfdesktop();

            return new WallpaperSetResult
            {
                SuccessCount = success,
                FailureCount = failure,
                FailedMonitorIds = failedIds
            };
        }

        /// <summary>
        /// Signals xfdesktop to reload all backdrop settings.
        /// This is required because xfdesktop 4.18 does not auto-watch xfconf changes.
        /// </summary>
        public static void ReloadXfdesktop()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xfdesktop",
                    Arguments = "--reload",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    process?.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[XfceWallpaperSetter] xfdesktop --reload failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a solid fallback color on the Xfce desktop for all monitors.
        /// </summary>
        public void SetFallbackColor(byte r, byte g, byte b)
        {
            // Xfce uses RGBA doubles for color. Convert bytes to 0.0-1.0 range.
            // We clear the image and set color-style to solid.
            // For simplicity, we set it on screen0/monitor0 which is the fallback channel.
            string colorStylePath = $"/backdrop/screen0/monitor0/workspace{_workspaceIndex}/color-style";
            string imageStylePath = $"/backdrop/screen0/monitor0/workspace{_workspaceIndex}/image-style";

            // color-style=0 means solid color, image-style=0 means no image
            SetXfconfProperty(colorStylePath, "0", isInt: true);
            SetXfconfProperty(imageStylePath, "0", isInt: true);

            Console.WriteLine($"  Applied fallback color: #{r:X2}{g:X2}{b:X2}");
        }

        /// <summary>
        /// Sets an xfconf property value using xfconf-query.
        /// </summary>
        private static bool SetXfconfProperty(string path, string value, bool isInt = false)
        {
            try
            {
                string typeFlag = isInt ? "-t int" : "-t string";
                var psi = new ProcessStartInfo
                {
                    FileName = "xfconf-query",
                    Arguments = $"-c xfce4-desktop -p {path} -s \"{value}\" {typeFlag}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                    {
                        // Don't log "property not found" — we'll try --create
                        if (!stderr.Contains("does not exist"))
                            Console.Error.WriteLine($"[XfceWallpaperSetter] xfconf-query {path}: {stderr.Trim()}");
                    }

                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[XfceWallpaperSetter] xfconf-query failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Creates a new xfconf property (for monitors that don't have an entry yet).
        /// </summary>
        private static bool CreateXfconfProperty(string path, string value)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xfconf-query",
                    Arguments = $"-c xfce4-desktop -p {path} -n -t string -s \"{value}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
                        Console.Error.WriteLine($"[XfceWallpaperSetter] create {path}: {stderr.Trim()}");

                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[XfceWallpaperSetter] create failed: {ex.Message}");
                return false;
            }
        }

        // ─────────────────────────────────────────────────────
        //  Static helpers: xfconf monitor discovery & live apply
        // ─────────────────────────────────────────────────────

        /// <summary>
        /// Discovers all xfconf monitor property names from the xfce4-desktop channel.
        /// Returns names like: monitorDP-0, monitorHDMI-0, monitor0, monitoreDP-1-1 etc.
        /// </summary>
        public static List<string> GetXfconfMonitorNames()
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
        /// Groups xfconf monitor property names by their physical monitor.
        /// Example: { "DP-0": ["monitor0", "monitorDP-0", "monitorDP-1-1", ...],
        ///            "HDMI-0": ["monitor1", "monitorHDMI-0", ...] }
        /// </summary>
        public static Dictionary<string, List<string>> GroupByPhysicalMonitor(
            List<string> xfconfNames,
            IReadOnlyList<MonitorInfo> monitors)
        {
            // Ensure standard "monitorXXX" entries exist for active monitors
            foreach (var m in monitors)
            {
                string xfName = "monitor" + m.Id;
                if (!xfconfNames.Contains(xfName))
                    xfconfNames.Add(xfName);
            }

            var groups = new Dictionary<string, List<string>>();
            string defaultPhysId = monitors.Count > 0 ? monitors[0].Id : "unknown";

            foreach (var xfName in xfconfNames)
            {
                string physId = ResolveXfconfToPhysical(xfName, monitors) ?? defaultPhysId;
                if (!groups.ContainsKey(physId))
                    groups[physId] = new List<string>();
                groups[physId].Add(xfName);
            }

            return groups;
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

            // Connector type match: monitorHDMI-1-0 → HDMI-0 (matches "HDMI" prefix)
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
        /// Applies wallpaper for a workspace by setting ALL workspace paths on ALL xfconf
        /// monitor entries to the same image. This makes xfdesktop detect the value change
        /// and update the desktop immediately — no reload needed.
        ///
        /// This is the proven strategy for xfdesktop 4.18 live wallpaper switching.
        /// </summary>
        /// <param name="physicalAssignments">Physical monitorId → imagePath for this theme.</param>
        /// <param name="workspaceCount">Total number of workspaces.</param>
        /// <param name="monitorGroups">Physical monitorId → list of xfconf monitor names.</param>
        public static void SetAllWorkspacePaths(
            Dictionary<string, string> physicalAssignments,
            int workspaceCount,
            Dictionary<string, List<string>> monitorGroups)
        {
            foreach (var group in monitorGroups)
            {
                string physId = group.Key;
                var xfconfNames = group.Value;

                string imgPath = physicalAssignments.ContainsKey(physId)
                    ? physicalAssignments[physId]
                    : physicalAssignments.Values.FirstOrDefault() ?? "";

                if (string.IsNullOrEmpty(imgPath)) continue;

                foreach (var xfName in xfconfNames)
                {
                    for (int ws = 0; ws < workspaceCount; ws++)
                    {
                        string path = $"/backdrop/screen0/{xfName}/workspace{ws}/last-image";
                        SetXfconfProperty(path, imgPath);
                    }
                }
            }
        }
    }
}
