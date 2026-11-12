// Windows 11 window chrome for the OLED shell: dark title bar, rounded corners,
// a black DWM border and a maximise size that respects the taskbar / work area.
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Soundstage.UI;

public static class Chrome
{
    const int WM_GETMINMAXINFO = 0x0024;
    const uint MONITOR_DEFAULTTONEAREST = 2;

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_BORDER_COLOR = 34;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Applies the OLED window treatment. Call once the window has a handle.</summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int round = 2;   // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        int none = 1;    // DWMSBT_NONE: keep the surface pure black for OLED
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

        int black = 0x000000;   // COLORREF, 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref black, sizeof(int));

        if (HwndSource.FromHwnd(hwnd) is { } source) source.AddHook(WindowProc);
    }

    static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        MonitorInfo info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        MinMaxInfo mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // A custom-chrome window otherwise maximises over the taskbar. Constrain it
        // to the monitor's work area in monitor-relative coordinates.
        mmi.MaxPosition.X = info.Work.Left - info.Monitor.Left;
        mmi.MaxPosition.Y = info.Work.Top - info.Monitor.Top;
        mmi.MaxSize.X = info.Work.Right - info.Work.Left;
        mmi.MaxSize.Y = info.Work.Bottom - info.Work.Top;
        mmi.MaxTrackSize.X = Math.Max(mmi.MaxTrackSize.X, mmi.MaxSize.X);
        mmi.MaxTrackSize.Y = Math.Max(mmi.MaxTrackSize.Y, mmi.MaxSize.Y);
        Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }
}
