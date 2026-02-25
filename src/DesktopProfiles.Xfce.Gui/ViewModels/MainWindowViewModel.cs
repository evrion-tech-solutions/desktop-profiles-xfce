using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DesktopProfiles.Core;
using DesktopProfiles.Core.Abstractions;
using DesktopProfiles.Core.Models;
using DesktopProfiles.Xfce;

namespace DesktopProfiles.Xfce.Gui.ViewModels
{
    // ─────────────────────────────────────────────────────
    //  MVVM Helpers
    // ─────────────────────────────────────────────────────

    public class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;
        private readonly Func<object, bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : (Func<object, bool>)null) { }

        public RelayCommand(Action<object> execute, Func<object, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object parameter) => _execute(parameter);
    }

    // ─────────────────────────────────────────────────────
    //  Desktop Assignment Row ViewModel
    // ─────────────────────────────────────────────────────

    public class DesktopAssignmentVM : ViewModelBase
    {
        private string _selectedThemeName;
        private Bitmap _previewImage;
        private int _desktopIndex;

        public int DesktopIndex
        {
            get => _desktopIndex;
            set
            {
                if (SetProperty(ref _desktopIndex, value))
                    OnPropertyChanged(nameof(DisplayLabel));
            }
        }
        public string DisplayLabel => string.Format("Workspace {0}", DesktopIndex + 1);

        public string SelectedThemeName
        {
            get => _selectedThemeName;
            set
            {
                if (SetProperty(ref _selectedThemeName, value))
                    OnThemeChanged?.Invoke(this);
            }
        }

        public Bitmap PreviewImage
        {
            get => _previewImage;
            set => SetProperty(ref _previewImage, value);
        }

        public Action<DesktopAssignmentVM> OnThemeChanged { get; set; }
    }

    // ─────────────────────────────────────────────────────
    //  Detected Monitor display model
    // ─────────────────────────────────────────────────────

    public class DetectedMonitor
    {
        public int Index { get; set; }
        public string DevicePath { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public string AspectRatio { get; set; } = "";
        public string DisplayName => string.Format("Monitor {0}", Index + 1);
        public string Resolution => Width + " × " + Height;
    }

    // ─────────────────────────────────────────────────────
    //  Main ViewModel — Xfce edition
    // ─────────────────────────────────────────────────────

    public class MainWindowViewModel : ViewModelBase
    {
        private readonly ConfigProvider _configProvider;
        private readonly ThemeScanner _themeScanner;
        private readonly IMonitorProvider _monitorProvider;
        private readonly IDesktopContextProvider _contextProvider;
        private readonly IImageResolver _imageResolver;

        private AppConfig _config;
        private List<WallpaperTheme> _coreThemes = new List<WallpaperTheme>();
        private string _configPath;

        // Daemon thread
        private CancellationTokenSource _cts;
        private Thread _daemonThread;

        // Cached xfconf monitor groups: physicalId → [xfconf names]
        private Dictionary<string, List<string>> _monitorGroups = new Dictionary<string, List<string>>();

        // Observable properties
        private string _statusMessage = "Loading...";
        private string _wallpapersBasePath;
        private bool _applyOnContextChange = true;
        private bool _applyOnMonitorChange = true;
        private int _debounceMs = 100;
        private bool _isMonitoringActive;
        private string _monitoringButtonText = "▶ Start Monitoring";
        private bool _minimizeToTray = true;
        private bool _autoStart = true;
        private bool _trayIconVisible;

        // Collections
        public ObservableCollection<DetectedMonitor> Monitors { get; } = new ObservableCollection<DetectedMonitor>();
        public ObservableCollection<DesktopAssignmentVM> DesktopAssignments { get; } = new ObservableCollection<DesktopAssignmentVM>();
        public ObservableCollection<string> ThemeNames { get; } = new ObservableCollection<string>();

        // Bindable properties
        public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }
        public string WallpapersBasePath { get => _wallpapersBasePath; set => SetProperty(ref _wallpapersBasePath, value); }
        public bool ApplyOnContextChange { get => _applyOnContextChange; set => SetProperty(ref _applyOnContextChange, value); }
        public bool ApplyOnMonitorChange { get => _applyOnMonitorChange; set => SetProperty(ref _applyOnMonitorChange, value); }
        public int DebounceMs { get => _debounceMs; set => SetProperty(ref _debounceMs, value); }
        public bool IsMonitoringActive { get => _isMonitoringActive; set => SetProperty(ref _isMonitoringActive, value); }
        public string MonitoringButtonText { get => _monitoringButtonText; set => SetProperty(ref _monitoringButtonText, value); }
        public bool MinimizeToTray { get => _minimizeToTray; set => SetProperty(ref _minimizeToTray, value); }
        public bool AutoStart
        {
            get => _autoStart;
            set
            {
                if (SetProperty(ref _autoStart, value))
                    UpdateAutoStartDesktopFile(value);
            }
        }
        public bool TrayIconVisible { get => _trayIconVisible; set => SetProperty(ref _trayIconVisible, value); }

        // Commands
        public ICommand SaveAndApplyCommand { get; }
        public ICommand RefreshMonitorsCommand { get; }
        public ICommand AddDesktopCommand { get; }
        public ICommand RemoveDesktopCommand { get; }
        public ICommand ToggleMonitoringCommand { get; }
        public ICommand ApplyNowCommand { get; }
        public ICommand ShowWindowCommand { get; }
        public ICommand QuitCommand { get; }

        // Event to request the view layer to show/hide/quit
        public event Action RequestShowWindow;
        public event Action RequestQuit;

        public MainWindowViewModel()
        {
            _configProvider = new ConfigProvider();
            _themeScanner = new ThemeScanner();
            _monitorProvider = new XfceMonitorProvider();
            _contextProvider = new XfceDesktopContextProvider();
            _imageResolver = new ImageResolver();

            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "desktop-profiles", "config.json");

            // Wire up commands
            SaveAndApplyCommand = new RelayCommand(SaveAndApply);
            RefreshMonitorsCommand = new RelayCommand(RefreshMonitors);
            AddDesktopCommand = new RelayCommand(AddDesktop);
            RemoveDesktopCommand = new RelayCommand(p => RemoveDesktop(p));
            ToggleMonitoringCommand = new RelayCommand(ToggleMonitoring);
            ApplyNowCommand = new RelayCommand(ApplyNow);
            ShowWindowCommand = new RelayCommand(() => RequestShowWindow?.Invoke());
            QuitCommand = new RelayCommand(() => RequestQuit?.Invoke());

            Initialize();
        }

        // ─────────────────────────────────────────────
        //  Initialization
        // ─────────────────────────────────────────────

        private void Initialize()
        {
            _config = _configProvider.Load(_configPath);
            if (_config == null)
            {
                string defaultThemesPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "wallpapers");
                _config = _configProvider.CreateDefault(defaultThemesPath);
            }

            WallpapersBasePath = _config.ThemesPath;
            ApplyOnContextChange = _config.Behavior.ApplyOnContextChange;
            ApplyOnMonitorChange = _config.Behavior.ApplyOnMonitorChange;
            DebounceMs = _config.Behavior.DebounceMs;
            _minimizeToTray = _config.Appearance.MinimizeToTray;
            _autoStart = _config.Appearance.AutoStart;
            OnPropertyChanged(nameof(MinimizeToTray));
            OnPropertyChanged(nameof(AutoStart));

            ScanThemes();

            // Sync workspace count from Xfce
            int xfceCount = _contextProvider.GetContextCount();
            int configCount = _config.ContextMappings.Count;
            int targetCount = Math.Max(xfceCount, configCount);
            if (targetCount < 1) targetCount = 1;

            for (int i = 0; i < targetCount; i++)
            {
                var mapping = _config.ContextMappings.FirstOrDefault(m => m.ContextIndex == i);
                string theme = mapping?.ThemeName ?? ThemeNames.FirstOrDefault() ?? "";
                var vm = CreateAssignmentVM(i, theme);
                DesktopAssignments.Add(vm);
            }

            // Sync Xfce workspace count if needed
            if (targetCount != xfceCount)
                _contextProvider.SetContextCount(targetCount);

            RefreshMonitors();
            RefreshMonitorGroups();

            StatusMessage = string.Format("{0} monitors, {1} workspaces, {2} themes",
                Monitors.Count, targetCount, _coreThemes.Count);

            // Auto-start monitoring
            if (ApplyOnContextChange)
                StartMonitoring();
        }

        // ─────────────────────────────────────────────
        //  Theme Management
        // ─────────────────────────────────────────────

        private void ScanThemes()
        {
            _coreThemes = _themeScanner.ScanThemes(WallpapersBasePath);
            ThemeNames.Clear();
            foreach (var theme in _coreThemes)
                ThemeNames.Add(theme.Name);
        }

        private DesktopAssignmentVM CreateAssignmentVM(int index, string themeName)
        {
            if (!ThemeNames.Contains(themeName) && ThemeNames.Count > 0)
                themeName = ThemeNames.First();

            var vm = new DesktopAssignmentVM
            {
                DesktopIndex = index,
                SelectedThemeName = themeName,
                OnThemeChanged = UpdateDesktopPreview
            };

            UpdateDesktopPreview(vm);
            return vm;
        }

        private void UpdateDesktopPreview(DesktopAssignmentVM vm)
        {
            var theme = _coreThemes.FirstOrDefault(t => t.Name == vm.SelectedThemeName);
            if (theme != null && !string.IsNullOrEmpty(theme.PreviewImagePath) && File.Exists(theme.PreviewImagePath))
            {
                try
                {
                    using (var stream = File.OpenRead(theme.PreviewImagePath))
                    {
                        vm.PreviewImage = Bitmap.DecodeToWidth(stream, 320);
                    }
                }
                catch
                {
                    vm.PreviewImage = null;
                }
            }
            else
            {
                vm.PreviewImage = null;
            }
        }

        // ─────────────────────────────────────────────
        //  Monitor Detection
        // ─────────────────────────────────────────────

        private void RefreshMonitors()
        {
            Monitors.Clear();
            try
            {
                var rawMonitors = _monitorProvider.GetMonitors();
                foreach (var m in rawMonitors)
                {
                    Monitors.Add(new DetectedMonitor
                    {
                        Index = m.Index,
                        DevicePath = m.Id,
                        Width = m.Width,
                        Height = m.Height,
                        AspectRatio = AspectRatioClassifier.Classify(m.Width, m.Height)
                    });
                }
            }
            catch (Exception ex)
            {
                StatusMessage = "Monitor detection failed: " + ex.Message;
            }
        }

        /// <summary>
        /// Discovers xfconf monitor names and groups them by physical monitor.
        /// This enables the set-all-workspace-paths strategy for live wallpaper switching.
        /// </summary>
        private void RefreshMonitorGroups()
        {
            var coreMonitors = Monitors.Select(m => new MonitorInfo
            {
                Id = m.DevicePath,
                Index = m.Index,
                Width = m.Width,
                Height = m.Height
            }).ToList();

            var xfconfNames = XfceWallpaperSetter.GetXfconfMonitorNames();
            _monitorGroups = XfceWallpaperSetter.GroupByPhysicalMonitor(xfconfNames, coreMonitors);
        }

        // ─────────────────────────────────────────────
        //  Desktop Assignments
        // ─────────────────────────────────────────────

        private void AddDesktop()
        {
            int nextIndex = DesktopAssignments.Count > 0
                ? DesktopAssignments.Max(d => d.DesktopIndex) + 1
                : 0;

            string defaultTheme = ThemeNames.FirstOrDefault() ?? "";
            var vm = CreateAssignmentVM(nextIndex, defaultTheme);
            DesktopAssignments.Add(vm);

            SyncWorkspaceCountToXfce();
        }

        private void RemoveDesktop(object parameter)
        {
            if (parameter is DesktopAssignmentVM vm && DesktopAssignments.Count > 1)
            {
                DesktopAssignments.Remove(vm);

                for (int i = 0; i < DesktopAssignments.Count; i++)
                    DesktopAssignments[i].DesktopIndex = i;

                SyncWorkspaceCountToXfce();
            }
        }

        private void SyncWorkspaceCountToXfce()
        {
            int desired = DesktopAssignments.Count;
            int actual = _contextProvider.GetContextCount();
            if (desired != actual)
            {
                bool ok = _contextProvider.SetContextCount(desired);
                StatusMessage = ok
                    ? $"Xfce workspaces set to {desired}"
                    : "Could not change Xfce workspace count";
            }
        }

        /// <summary>
        /// Called from daemon thread when Xfce workspace count changes.
        /// </summary>
        private void SyncAssignmentsFromXfce(int xfceCount)
        {
            if (xfceCount == DesktopAssignments.Count) return;

            if (xfceCount > DesktopAssignments.Count)
            {
                while (DesktopAssignments.Count < xfceCount)
                {
                    int idx = DesktopAssignments.Count;
                    string theme = ThemeNames.FirstOrDefault() ?? "";
                    DesktopAssignments.Add(CreateAssignmentVM(idx, theme));
                }
                StatusMessage = $"Synced: {xfceCount} workspaces detected";
            }
            else
            {
                while (DesktopAssignments.Count > xfceCount && DesktopAssignments.Count > 1)
                    DesktopAssignments.RemoveAt(DesktopAssignments.Count - 1);
                StatusMessage = $"Synced: {xfceCount} workspaces detected";
            }
        }

        // ─────────────────────────────────────────────
        //  Wallpaper Application
        // ─────────────────────────────────────────────

        /// <summary>
        /// Applies wallpapers for ALL workspaces at once using the set-all-paths strategy.
        /// Sets xfconf properties for every workspace × monitor combination.
        /// xfdesktop detects live value changes — no reload needed.
        /// </summary>
        private void ApplyAllWorkspaces()
        {
            var coreConfig = BuildCoreConfig();
            var coreMonitors = Monitors.Select(m => new MonitorInfo
            {
                Id = m.DevicePath,
                Index = m.Index,
                Width = m.Width,
                Height = m.Height
            }).ToList();

            if (coreMonitors.Count == 0) return;

            // Refresh monitor groups in case monitors changed
            if (_monitorGroups.Count == 0)
                RefreshMonitorGroups();

            // Build fresh wallpaper map
            _wallpaperMap.Clear();

            foreach (var assignment in DesktopAssignments)
            {
                int ws = assignment.DesktopIndex;
                var resolution = _imageResolver.Resolve(ws, coreMonitors, coreConfig, _coreThemes);

                var validAssignments = new Dictionary<string, string>();
                foreach (var kvp in resolution.Assignments)
                {
                    if (kvp.Value != null && File.Exists(kvp.Value))
                        validAssignments[kvp.Key] = kvp.Value;
                }

                _wallpaperMap[ws] = validAssignments;
            }

            // Apply current workspace theme to all paths (proven live-switch strategy)
            int currentWs = _contextProvider.GetCurrentContextIndex();
            if (_wallpaperMap.TryGetValue(currentWs, out var currentAssignments) && currentAssignments.Count > 0)
            {
                int wsCount = _contextProvider.GetContextCount();
                XfceWallpaperSetter.SetAllWorkspacePaths(currentAssignments, wsCount, _monitorGroups);
            }

            Dispatcher.UIThread.Post(() =>
            {
                StatusMessage = string.Format("Applied {0} workspaces ({1} monitors)",
                    DesktopAssignments.Count, coreMonitors.Count);
            });
        }

        /// <summary>
        /// Applies wallpaper for a single workspace using the set-all-paths strategy.
        /// Sets ALL workspace paths on ALL xfconf monitor entries to the target theme,
        /// which makes xfdesktop detect the value change and update live.
        /// </summary>
        private void ApplyWorkspace(int workspaceIndex)
        {
            if (!_wallpaperMap.TryGetValue(workspaceIndex, out var assignments) || assignments.Count == 0)
            {
                // No cached assignment — compute it
                var coreConfig = BuildCoreConfig();
                var coreMonitors = Monitors.Select(m => new MonitorInfo
                {
                    Id = m.DevicePath,
                    Index = m.Index,
                    Width = m.Width,
                    Height = m.Height
                }).ToList();

                if (coreMonitors.Count == 0) return;

                var resolution = _imageResolver.Resolve(workspaceIndex, coreMonitors, coreConfig, _coreThemes);
                assignments = new Dictionary<string, string>();
                foreach (var kvp in resolution.Assignments)
                {
                    if (kvp.Value != null && File.Exists(kvp.Value))
                        assignments[kvp.Key] = kvp.Value;
                }
                _wallpaperMap[workspaceIndex] = assignments;
            }

            if (assignments.Count > 0 && _monitorGroups.Count > 0)
            {
                int wsCount = _contextProvider.GetContextCount();
                XfceWallpaperSetter.SetAllWorkspacePaths(assignments, wsCount, _monitorGroups);
            }
        }

        // Pre-resolved wallpaper map: workspace → (monitorId → imagePath)
        private Dictionary<int, Dictionary<string, string>> _wallpaperMap = new Dictionary<int, Dictionary<string, string>>();

        private AppConfig BuildCoreConfig()
        {
            return new AppConfig
            {
                SchemaVersion = 1,
                ThemesPath = WallpapersBasePath,
                ContextMappings = DesktopAssignments.Select(d => new ContextMapping
                {
                    ContextIndex = d.DesktopIndex,
                    ThemeName = d.SelectedThemeName ?? ""
                }).ToList(),
                FallbackOrder = _config.FallbackOrder,
                FallbackColor = _config.FallbackColor ?? "#0B1020",
                Behavior = new BehaviorConfig
                {
                    ApplyOnContextChange = ApplyOnContextChange,
                    ApplyOnMonitorChange = ApplyOnMonitorChange,
                    DebounceMs = DebounceMs
                }
            };
        }

        private void ApplyCurrentWallpaper()
        {
            ApplyAllWorkspaces();
        }

        // ─────────────────────────────────────────────
        //  Background Monitoring (xprop -spy + monitor hotplug)
        // ─────────────────────────────────────────────

        private Process _xpropProcess;

        private void StartMonitoring()
        {
            StopMonitoring();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Apply all workspaces initially
            ApplyAllWorkspaces();

            int lastWs = _contextProvider.GetCurrentContextIndex();

            // Start xprop -spy for event-driven workspace detection
            _xpropProcess = StartXpropSpy();
            _xpropProcess.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data) || ct.IsCancellationRequested) return;

                var match = Regex.Match(e.Data, @"=\s*(\d+)");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int newWs))
                {
                    if (newWs != lastWs && ApplyOnContextChange)
                    {
                        lastWs = newWs;
                        var assignment = DesktopAssignments.FirstOrDefault(d => d.DesktopIndex == newWs);
                        string themeName = assignment?.SelectedThemeName ?? "?";

                        ApplyWorkspace(newWs);

                        Dispatcher.UIThread.Post(() =>
                        {
                            StatusMessage = string.Format("WS{0} ({1})", newWs + 1, themeName);
                        });
                    }
                }
            };
            _xpropProcess.BeginOutputReadLine();

            // Background thread for periodic monitor hotplug + workspace count checks
            _daemonThread = new Thread(() =>
            {
                int lastWsCount = _contextProvider.GetContextCount();
                int lastMonitorCount = Monitors.Count;

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        Thread.Sleep(10000);

                        // Restart xprop if it died
                        if (_xpropProcess != null && _xpropProcess.HasExited)
                        {
                            _xpropProcess = StartXpropSpy();
                            _xpropProcess.OutputDataReceived += (sender, e) =>
                            {
                                if (string.IsNullOrEmpty(e.Data) || ct.IsCancellationRequested) return;
                                var match = Regex.Match(e.Data, @"=\s*(\d+)");
                                if (match.Success && int.TryParse(match.Groups[1].Value, out int newWs))
                                {
                                    if (newWs != lastWs && ApplyOnContextChange)
                                    {
                                        lastWs = newWs;
                                        ApplyWorkspace(newWs);
                                        var a = DesktopAssignments.FirstOrDefault(d => d.DesktopIndex == newWs);
                                        Dispatcher.UIThread.Post(() => StatusMessage = string.Format("WS{0} ({1})", newWs + 1, a?.SelectedThemeName ?? "?"));
                                    }
                                }
                            };
                            _xpropProcess.BeginOutputReadLine();
                        }

                        // Monitor hotplug
                        try
                        {
                            var currentMonitors = _monitorProvider.GetMonitors();
                            if (currentMonitors.Count != lastMonitorCount && ApplyOnMonitorChange)
                            {
                                lastMonitorCount = currentMonitors.Count;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    RefreshMonitors();
                                    RefreshMonitorGroups();
                                    ApplyAllWorkspaces();
                                });
                            }
                        }
                        catch { /* ignore */ }

                        // Workspace count change
                        try
                        {
                            int currentWsCount = _contextProvider.GetContextCount();
                            if (currentWsCount != lastWsCount)
                            {
                                lastWsCount = currentWsCount;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    SyncAssignmentsFromXfce(currentWsCount);
                                    ApplyAllWorkspaces();
                                });
                            }
                        }
                        catch { /* ignore */ }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Dispatcher.UIThread.Post(() => StatusMessage = "Error: " + ex.Message);
                        Thread.Sleep(2000);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "DPXfceDaemon",
                Priority = ThreadPriority.BelowNormal
            };

            _daemonThread.Start();
            IsMonitoringActive = true;
            MonitoringButtonText = "■ Stop Monitoring";
            StatusMessage = "Monitoring (xprop event-driven)...";
        }

        /// <summary>
        /// Starts xprop -spy to watch workspace changes (event-driven, zero CPU idle).
        /// </summary>
        private static Process StartXpropSpy()
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xprop",
                Arguments = "-root -spy _NET_CURRENT_DESKTOP",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start xprop -spy");

            return process;
        }

        private void StopMonitoring()
        {
            // Kill xprop -spy process first
            if (_xpropProcess != null && !_xpropProcess.HasExited)
            {
                try { _xpropProcess.Kill(); } catch { }
                _xpropProcess.Dispose();
                _xpropProcess = null;
            }

            if (_cts != null)
            {
                _cts.Cancel();
                _daemonThread?.Join(3000);
                _cts.Dispose();
                _cts = null;
            }
            _daemonThread = null;
            IsMonitoringActive = false;
            MonitoringButtonText = "▶ Start Monitoring";
        }

        private void ToggleMonitoring()
        {
            if (IsMonitoringActive)
            {
                StopMonitoring();
                StatusMessage = "Monitoring stopped";
            }
            else
            {
                StartMonitoring();
            }
        }

        // ─────────────────────────────────────────────
        //  Commands Implementation
        // ─────────────────────────────────────────────

        private void SaveAndApply()
        {
            try
            {
                _config.ThemesPath = WallpapersBasePath;
                _config.Behavior.ApplyOnContextChange = ApplyOnContextChange;
                _config.Behavior.ApplyOnMonitorChange = ApplyOnMonitorChange;
                _config.Behavior.DebounceMs = DebounceMs;
                _config.Appearance.MinimizeToTray = MinimizeToTray;
                _config.Appearance.AutoStart = AutoStart;

                _config.ContextMappings = DesktopAssignments.Select(d => new ContextMapping
                {
                    ContextIndex = d.DesktopIndex,
                    ThemeName = d.SelectedThemeName ?? ""
                }).ToList();

                _configProvider.Save(_configPath, _config);

                // Re-scan themes — preserve selections
                var savedSelections = DesktopAssignments
                    .Select(d => (d.DesktopIndex, d.SelectedThemeName))
                    .ToList();

                ScanThemes();

                foreach (var (idx, theme) in savedSelections)
                {
                    var row = DesktopAssignments.FirstOrDefault(d => d.DesktopIndex == idx);
                    if (row != null)
                    {
                        row.SelectedThemeName = ThemeNames.Contains(theme) ? theme : ThemeNames.FirstOrDefault();
                    }
                }

                ApplyCurrentWallpaper();

                if (ApplyOnContextChange)
                    StartMonitoring();
                else
                    StopMonitoring();

                StatusMessage = "Settings saved and applied";
            }
            catch (Exception ex)
            {
                StatusMessage = "Error: " + ex.Message;
            }
        }

        private void ApplyNow()
        {
            try
            {
                ApplyCurrentWallpaper();
            }
            catch (Exception ex)
            {
                StatusMessage = "Error: " + ex.Message;
            }
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
            catch { return (11, 16, 32); }
        }

        public void ShowTrayIcon(bool visible)
        {
            TrayIconVisible = visible;
        }

        // ─────────────────────────────────────────────
        //  .desktop file helpers
        // ─────────────────────────────────────────────

        private static readonly string AutoStartDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".config", "autostart");

        private static readonly string ApplicationsDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                         ".local", "share", "applications");

        private static readonly string DesktopFileName = "desktop-profiles-xfce.desktop";

        private static string AutoStartFilePath => Path.Combine(AutoStartDir, DesktopFileName);
        private static string ApplicationsFilePath => Path.Combine(ApplicationsDir, DesktopFileName);

        private static string GetInstalledIconPath()
        {
            string exeDir = Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");
            string srcIcon = string.IsNullOrEmpty(exeDir)
                ? null
                : Path.Combine(exeDir, "Assets", "app-icon.png");

            if (srcIcon == null || !File.Exists(srcIcon))
                srcIcon = Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon.png");

            string iconsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "icons");
            string destIcon = Path.Combine(iconsDir, "desktop-profiles.png");

            try
            {
                if (srcIcon != null && File.Exists(srcIcon))
                {
                    if (!Directory.Exists(iconsDir))
                        Directory.CreateDirectory(iconsDir);
                    File.Copy(srcIcon, destIcon, overwrite: true);
                    return destIcon;
                }
            }
            catch { /* non-critical */ }

            return File.Exists(destIcon) ? destIcon : "desktop-profiles";
        }

        private static string GetExePath()
        {
            return System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                   ?? "desktop-profiles-xfce-gui";
        }

        private void UpdateAutoStartDesktopFile(bool enable)
        {
            try
            {
                if (enable)
                {
                    WriteDesktopFile(AutoStartDir, AutoStartFilePath, GetExePath(), GetInstalledIconPath(),
                        extraKeys: "StartupNotify=false\nX-XFCE-Autostart-Override=true",
                        execSuffix: " --minimized");
                }
                else
                {
                    if (File.Exists(AutoStartFilePath))
                        File.Delete(AutoStartFilePath);
                }
            }
            catch { /* non-critical */ }
        }

        public void InstallApplicationDesktopFile()
        {
            try
            {
                WriteDesktopFile(ApplicationsDir, ApplicationsFilePath, GetExePath(), GetInstalledIconPath());
            }
            catch { /* non-critical */ }
        }

        private static void WriteDesktopFile(string dir, string path, string exePath, string iconPath,
            string extraKeys = null, string execSuffix = null)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string content = string.Join("\n",
                "[Desktop Entry]",
                "Type=Application",
                "Name=Desktop Profiles",
                "GenericName=Wallpaper Manager",
                "Comment=Per-monitor per-workspace wallpaper management for Xfce",
                "Exec=" + exePath + (execSuffix ?? ""),
                "Icon=" + iconPath,
                "Terminal=false",
                "Categories=Utility;Settings;DesktopSettings;",
                extraKeys ?? "",
                "");

            File.WriteAllText(path, content);
        }

        public void Cleanup()
        {
            StopMonitoring();
        }
    }
}
