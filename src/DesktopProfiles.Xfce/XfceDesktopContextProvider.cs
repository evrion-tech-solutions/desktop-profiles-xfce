using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DesktopProfiles.Core.Abstractions;

namespace DesktopProfiles.Xfce
{
    /// <summary>
    /// Provides the current Xfce workspace index via EWMH X11 properties.
    /// 
    /// Uses xprop to query _NET_CURRENT_DESKTOP and _NET_NUMBER_OF_DESKTOPS.
    /// These are EWMH/ICCCM standards supported by Xfce's xfwm4 window manager.
    /// 
    /// Workspace count can be set via xfconf-query on the xfwm4 channel.
    /// </summary>
    public class XfceDesktopContextProvider : IDesktopContextProvider
    {
        private int _cachedCount = -1;
        private DateTime _countCacheExpiry = DateTime.MinValue;
        private static readonly TimeSpan CountCacheTtl = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Gets the zero-based index of the currently active workspace.
        /// </summary>
        public int GetCurrentContextIndex()
        {
            // xprop -root _NET_CURRENT_DESKTOP → "_NET_CURRENT_DESKTOP(CARDINAL) = 0"
            string output = RunCommand("xprop", "-root _NET_CURRENT_DESKTOP");
            if (output != null)
            {
                var match = Regex.Match(output, @"=\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int index))
                    return index;
            }

            // Fallback: wmctrl -d
            string wmctrlOutput = RunCommand("wmctrl", "-d");
            if (wmctrlOutput != null)
            {
                foreach (string line in wmctrlOutput.Split('\n'))
                {
                    if (line.Contains("*"))
                    {
                        var idxMatch = Regex.Match(line.TrimStart(), @"^(\d+)");
                        if (idxMatch.Success && int.TryParse(idxMatch.Groups[1].Value, out int idx))
                            return idx;
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Gets the total number of workspaces.
        /// </summary>
        public int GetContextCount()
        {
            if (_cachedCount > 0 && DateTime.UtcNow < _countCacheExpiry)
                return _cachedCount;

            // xprop -root _NET_NUMBER_OF_DESKTOPS → "_NET_NUMBER_OF_DESKTOPS(CARDINAL) = 4"
            string output = RunCommand("xprop", "-root _NET_NUMBER_OF_DESKTOPS");
            if (output != null)
            {
                var match = Regex.Match(output, @"=\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int count))
                {
                    _cachedCount = count;
                    _countCacheExpiry = DateTime.UtcNow + CountCacheTtl;
                    return count;
                }
            }

            // Fallback: wmctrl -d line count
            string wmctrlOutput = RunCommand("wmctrl", "-d");
            if (wmctrlOutput != null)
            {
                int lineCount = 0;
                foreach (string line in wmctrlOutput.Split('\n'))
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        lineCount++;
                }
                if (lineCount > 0)
                {
                    _cachedCount = lineCount;
                    _countCacheExpiry = DateTime.UtcNow + CountCacheTtl;
                    return lineCount;
                }
            }

            return 4; // Xfce default
        }

        /// <summary>
        /// Sets the total number of Xfce workspaces via xfconf-query.
        /// </summary>
        public bool SetContextCount(int count)
        {
            if (count < 1) count = 1;
            if (count > 36) count = 36;

            try
            {
                string result = RunCommand("xfconf-query",
                    $"-c xfwm4 -p /general/workspace_count -s {count}");

                _cachedCount = -1;
                _countCacheExpiry = DateTime.MinValue;

                return result != null;
            }
            catch
            {
                return false;
            }
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
