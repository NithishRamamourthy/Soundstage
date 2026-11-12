using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Soundstage.UI;

public partial class MainWindow : Window
{
    readonly MainViewModel _viewModel = new();
    readonly TrayIcon _tray;
    readonly Hotkeys _hotkeys;
    bool _exiting;
    bool _shutdown;

    /// <summary>Shows the Headphones page with sample values (layout snapshots only).</summary>
    internal void PreviewHeadphones()
    {
        _viewModel.Page = 1;
        _viewModel.Headphones.ShowPreview();
    }

    /// <summary>Shows the Settings page (layout snapshots only).</summary>
    internal void PreviewSettings() => _viewModel.Page = 2;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        SourceInitialized += (_, _) => Chrome.Apply(this);

        Loaded += (_, _) =>
        {
            if (UpdateChecker.UpdateAvailable) ShowUpdateLink();
            UpdateChecker.Changed += () => Dispatcher.Invoke(ShowUpdateLink);
        };

        _tray = new TrayIcon(_viewModel);
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += ExitApp;

        _hotkeys = new Hotkeys();
        _hotkeys.Bind('S', () => _viewModel.IsOn = !_viewModel.IsOn);
        _hotkeys.Bind('R', _viewModel.Recenter);
        _hotkeys.Bind('N', _viewModel.Headphones.ToggleNoiseMode);
        _viewModel.ApplyHotkeys = on => _hotkeys.Enable(on);
        bool registered = _hotkeys.Enable(_viewModel.HotkeysEnabled);
        _viewModel.HotkeyConflict = !registered && _viewModel.HotkeysEnabled;

        Closing += OnClosing;
        Closed += OnClosed;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        // Coming back from Windows sound settings: device list or layouts may have changed.
        Activated += (_, _) => _viewModel.RefreshDevices();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || !_viewModel.CloseToTray) return;
        e.Cancel = true;
        Hide();
        _tray.ShowStillRunningHint();
    }

    void OnClosed(object? sender, EventArgs e)
    {
        if (_shutdown) return;
        _shutdown = true;
        _hotkeys.Dispose();
        _tray.Dispose();
        _viewModel.Shutdown();
    }

    void ExitApp()
    {
        _exiting = true;
        _shutdown = true;
        _hotkeys.Dispose();
        _tray.Dispose();
        _viewModel.Shutdown();
        Close();
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph == null) return;
        MaximizeGlyph.Data = Geometry.Parse(WindowState == WindowState.Maximized
            ? "M2,4 H8 V10 H2 Z M4,2 H10 V8 H4 Z"
            : "M2,2 H10 V10 H2 Z");
    }

    void ShowUpdateLink()
    {
        if (UpdateLink == null || !UpdateChecker.UpdateAvailable) return;
        UpdateLink.Text = "Update available: v" + UpdateChecker.LatestVersion;
        UpdateLink.Visibility = Visibility.Visible;
    }

    void UpdateLink_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (string.IsNullOrEmpty(UpdateChecker.ReleaseUrl)) return;
        try { Process.Start(new ProcessStartInfo(UpdateChecker.ReleaseUrl) { UseShellExecute = true }); }
        catch { }
    }
}
