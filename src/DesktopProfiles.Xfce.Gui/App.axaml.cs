using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DesktopProfiles.Xfce.Gui.ViewModels;
using DesktopProfiles.Xfce.Gui.Views;

namespace DesktopProfiles.Xfce.Gui
{
    public partial class App : Application
    {
        private MainWindowViewModel _vm;
        private Window _mainWindow;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _vm = new MainWindowViewModel();
                _mainWindow = new MainWindow { DataContext = _vm };

                DataContext = _vm;

                desktop.MainWindow = _mainWindow;
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                _vm.ShowTrayIcon(true);
                _vm.InstallApplicationDesktopFile();

                _vm.RequestShowWindow += ShowFromTray;
                _vm.RequestQuit += () =>
                {
                    _vm.ShowTrayIcon(false);
                    _vm.Cleanup();
                    desktop.Shutdown();
                };

                // Intercept window close → minimize to tray
                _mainWindow.Closing += (_, e) =>
                {
                    if (_vm.MinimizeToTray && _vm.IsMonitoringActive)
                    {
                        e.Cancel = true;
                        _mainWindow.Hide();
                    }
                    else
                    {
                        _vm.ShowTrayIcon(false);
                        _vm.Cleanup();
                        desktop.Shutdown();
                    }
                };

                // Handle --minimized startup
                string[] args = Environment.GetCommandLineArgs();
                if (args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
                {
                    _mainWindow.Opened += (_, _) =>
                    {
                        _mainWindow.Hide();
                    };
                }
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void ShowFromTray()
        {
            if (_mainWindow != null)
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
        }
    }
}
