// Pieces of the app that live outside the window: the notification-area icon,
// system-wide keyboard shortcuts and the "start with Windows" registration.
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Soundstage.UI;

/// <summary>The icon next to the clock, with quick actions.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    readonly MainViewModel _vm;
    readonly Forms.ToolStripMenuItem _spatial, _nc, _ambient, _off, _recentre;
    bool _hintShown;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    public TrayIcon(MainViewModel vm)
    {
        _vm = vm;
        _spatial = new Forms.ToolStripMenuItem("Spatial audio", null, (_, _) => _vm.IsOn = !_vm.IsOn);
        _recentre = new Forms.ToolStripMenuItem("Recentre", null, (_, _) => _vm.Recenter());
        _nc = new Forms.ToolStripMenuItem("Noise cancelling", null, (_, _) => _vm.Headphones.SetNoiseMode(Engine.NoiseMode.NoiseCancelling));
        _ambient = new Forms.ToolStripMenuItem("Ambient sound", null, (_, _) => _vm.Headphones.SetNoiseMode(Engine.NoiseMode.Ambient));
        _off = new Forms.ToolStripMenuItem("Noise control off", null, (_, _) => _vm.Headphones.SetNoiseMode(Engine.NoiseMode.Off));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem("Open Soundstage", null, (_, _) => OpenRequested?.Invoke()) { Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_spatial);
        menu.Items.Add(_recentre);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_nc);
        menu.Items.Add(_ambient);
        menu.Items.Add(_off);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));
        menu.Opening += (_, _) => Sync();

        System.Drawing.Icon? icon = null;
        try { if (Environment.ProcessPath is { } path) icon = System.Drawing.Icon.ExtractAssociatedIcon(path); }
        catch (Exception) { }

        _icon = new Forms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "Soundstage",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(); };
    }

    void Sync()
    {
        var hp = _vm.Headphones;
        _spatial.Checked = _vm.IsOn;
        _recentre.Enabled = _vm.IsRunning;
        bool noise = hp.IsConnected && hp.NoiseSupported;
        _nc.Enabled = _ambient.Enabled = _off.Enabled = noise;
        _nc.Checked = noise && hp.NoiseCancelling;
        _ambient.Checked = noise && hp.Ambient;
        _off.Checked = noise && hp.NoiseOff;
    }

    /// <summary>Tells the user, once, that closing the window left the app running.</summary>
    public void ShowStillRunningHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "Soundstage is still running",
            "Spatial audio keeps working. Right-click this icon for quick actions or to exit.", Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

/// <summary>Keyboard shortcuts that work while another app has focus.</summary>
public sealed class Hotkeys : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    bool _registered;

    public Hotkeys()
    {
        // A message-only window, so shortcuts work even while the main window is hidden.
        _source = new HwndSource(new HwndSourceParameters("SoundstageHotkeys") { ParentWindow = HWND_MESSAGE, Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    /// <summary>Binds Ctrl+Alt+key. Call before Enable.</summary>
    public void Bind(char key, Action action) => _actions[char.ToUpperInvariant(key)] = action;

    /// <summary>Turns the shortcuts on or off. Returns false if another app holds one of them.</summary>
    public bool Enable(bool on)
    {
        bool all = true;
        if (on && !_registered)
        {
            foreach (int key in _actions.Keys)
                all &= RegisterHotKey(_source.Handle, key, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, (uint)key);
            _registered = true;
        }
        else if (!on && _registered)
        {
            foreach (int key in _actions.Keys) UnregisterHotKey(_source.Handle, key);
            _registered = false;
        }
        return all;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Enable(false);
        _source.Dispose();
    }
}

/// <summary>Registers the app to start when the user signs in (current user only).</summary>
public static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "Soundstage";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } path) key.SetValue(ValueName, $"\"{path}\" --tray");
        else key.DeleteValue(ValueName, false);
    }
}
