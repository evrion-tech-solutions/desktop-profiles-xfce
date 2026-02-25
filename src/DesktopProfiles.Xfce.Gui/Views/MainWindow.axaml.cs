using System.Reflection;
using Avalonia.Controls;

#nullable enable

namespace DesktopProfiles.Xfce.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        var label = this.FindControl<TextBlock>("VersionLabel");
        if (label != null && ver != null)
            label.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build} (Xfce)";
    }
}
