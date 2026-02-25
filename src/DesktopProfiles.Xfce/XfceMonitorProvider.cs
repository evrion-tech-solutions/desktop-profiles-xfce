using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DesktopProfiles.Core.Abstractions;
using DesktopProfiles.Core.Models;

namespace DesktopProfiles.Xfce
{
    /// <summary>
    /// Detects connected monitors by parsing xrandr output.
    /// Identical to GNOME version — xrandr is an X11 standard, not DE-specific.
    /// </summary>
    public class XfceMonitorProvider : IMonitorProvider
    {
        // Matches: "CONNECTOR connected [primary] WIDTHxHEIGHT+X+Y"
        private static readonly Regex XrandrPattern = new Regex(
            @"^(\S+)\s+connected\s+(?:primary\s+)?(\d+)x(\d+)\+",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public IReadOnlyList<MonitorInfo> GetMonitors()
        {
            var monitors = new List<MonitorInfo>();

            string xrandrOutput = RunCommand("xrandr", "--query");
            if (string.IsNullOrEmpty(xrandrOutput))
            {
                Console.Error.WriteLine("[XfceMonitorProvider] Could not detect monitors (xrandr failed)");
                return monitors;
            }

            var matches = XrandrPattern.Matches(xrandrOutput);
            int index = 0;

            foreach (Match match in matches)
            {
                string connector = match.Groups[1].Value;
                int width = int.Parse(match.Groups[2].Value);
                int height = int.Parse(match.Groups[3].Value);

                monitors.Add(new MonitorInfo
                {
                    Id = connector,
                    Index = index++,
                    Width = width,
                    Height = height
                });
            }

            return monitors;
        }

        private static string RunCommand(string command, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = command,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    return process.ExitCode == 0 ? output : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
