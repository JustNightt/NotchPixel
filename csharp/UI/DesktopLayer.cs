using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // Desktop layer host.
    //
    // Windows' "Show Desktop" (Win+D) hides every top-level window, which used
    // to take the desktop widgets and the live wallpaper with it. Parenting
    // those windows to the WorkerW that sits behind the desktop icons keeps
    // them on the wallpaper layer: they stay under normal windows, survive
    // Win+D, and never float above a fullscreen app.
    //
    // Explorer restarts destroy WorkerW, so the host is re-resolved lazily and
    // every caller falls back to HWND_BOTTOM top-level behaviour if it is gone.
    // ========================================================================
    public static class DesktopLayer
    {
        private const uint GW_HWNDNEXT = 2;
        private const uint SMTO_NORMAL = 0x0000;
        private const int WM_SPAWN_WORKER = 0x052C;
        private const int GWL_STYLE = -16;
        private const int GWLP_HWNDPARENT = -8;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_POPUP = unchecked((long)0x80000000L);
        private const int SW_SHOWNOACTIVATE = 4;
        private const uint SWP_FLAGS = 0x0013; // NOACTIVATE | NOMOVE | NOSIZE
        private static readonly IntPtr HWND_BOTTOM = new(1);
        private static readonly IntPtr HWND_TOP = new(0);

        private static IntPtr _host = IntPtr.Zero;
        private static IntPtr _progman = IntPtr.Zero;
        private static IntPtr _wallpaperHwnd = IntPtr.Zero;
        private static readonly HashSet<IntPtr> _captureAffinityApplied = new();
        private static long _lastProbeMs;

        public static bool IsAttached => _host != IntPtr.Zero && Win32Layered.IsWindow(_host);

        public static IntPtr ProgmanHost
        {
            get
            {
                if (_progman != IntPtr.Zero && Win32Layered.IsWindow(_progman)) return _progman;
                _progman = Win32Layered.FindWindowW("Progman", null);
                return _progman;
            }
        }

        // Re-resolves the WorkerW whenever the cached one disappears.
        public static IntPtr Host
        {
            get
            {
                if (IsAttached) return _host;
                if (Environment.TickCount64 - _lastProbeMs < 2000) return IntPtr.Zero;
                _lastProbeMs = Environment.TickCount64;
                _host = ResolveWorkerW();
                return _host;
            }
        }

        private static IntPtr ResolveWorkerW()
        {
            try
            {
                IntPtr progman = Win32Layered.FindWindowW("Progman", null);
                if (progman == IntPtr.Zero) return IntPtr.Zero;

                // Ask Explorer to split Progman into Progman + WorkerW. This is the
                // documented message Lively/Rainmeter use; it is harmless when the
                // shell has already spawned one.
                _ = Win32Layered.SendMessageTimeoutW(progman, WM_SPAWN_WORKER, new UIntPtr(0x0D), IntPtr.Zero,
                                                     SMTO_NORMAL, 1000, out _);
                _ = Win32Layered.SendMessageTimeoutW(progman, WM_SPAWN_WORKER, new UIntPtr(0x0D), new IntPtr(1),
                                                     SMTO_NORMAL, 1000, out _);

                IntPtr shellView = Win32Layered.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView == IntPtr.Zero)
                {
                    // On multi-monitor or after an Explorer restart the DefView can
                    // live under a different top-level window; find it, then take
                    // the WorkerW that follows it in Z-order.
                    IntPtr found = IntPtr.Zero;
                    Win32Layered.EnumWindows((hwnd, _) =>
                    {
                        IntPtr view = Win32Layered.FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (view == IntPtr.Zero) return true;
                        found = hwnd;
                        return false;
                    }, IntPtr.Zero);
                    if (found != IntPtr.Zero)
                        shellView = Win32Layered.FindWindowExW(found, IntPtr.Zero, "SHELLDLL_DefView", null);
                }

                if (shellView != IntPtr.Zero)
                {
                    IntPtr owner = Win32Layered.GetParent(shellView);
                    if (owner == IntPtr.Zero) owner = progman;
                    IntPtr sibling = Win32Layered.GetWindow(owner, GW_HWNDNEXT);
                    while (sibling != IntPtr.Zero)
                    {
                        var cls = new StringBuilder(64);
                        if (Win32Layered.GetClassNameW(sibling, cls, cls.Capacity) > 0 &&
                            cls.ToString().Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
                            return sibling;
                        sibling = Win32Layered.GetWindow(sibling, GW_HWNDNEXT);
                    }
                }

                // No WorkerW: fall back to Progman itself, which is still the
                // desktop layer (widgets simply sit above the wallpaper).
                return progman;
            }
            catch (Exception ex)
            {
                ConfigManager.LogDiagnostic("workerw-resolve: " + ex);
                return IntPtr.Zero;
            }
        }

        // Desktop-layer attachment:
        //   * Live video wallpaper (`asWallpaper: true`) is parented into WorkerW so
        //     it sits behind desktop icons.
        //   * Desktop widgets (`asWallpaper: false`) remain top-level layered windows
        //     owned by Progman (`GWLP_HWNDPARENT`) and kept at `HWND_BOTTOM`. On
        //     Windows 11, WS_CHILD+WS_EX_LAYERED inside WorkerW can vanish behind
        //     SHELLDLL_DefView and miss pointer input, whereas Progman-owned
        //     HWND_BOTTOM widgets stay visible, draggable, below active windows,
        //     and survive Win+D (Show Desktop).
        public static bool Attach(IntPtr hwnd, bool asWallpaper = false)
        {
            if (hwnd == IntPtr.Zero || !Win32Layered.IsWindow(hwnd)) return false;
            try
            {
                if (!asWallpaper)
                {
                    if (Win32Layered.GetParent(hwnd) != IntPtr.Zero)
                        Win32Layered.SetParent(hwnd, IntPtr.Zero);

                    long style = Win32Layered.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                    long popupStyle = (style & ~WS_CHILD) | WS_POPUP;
                    if (popupStyle != style)
                        Win32Layered.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(popupStyle));

                    IntPtr progman = ProgmanHost;
                    if (progman != IntPtr.Zero)
                    {
                        Win32Layered.SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, progman);
                    }

                    if (_captureAffinityApplied.Add(hwnd))
                    {
                        try { WindowsSystemHooks.ApplyWindowCaptureAffinity(hwnd, GlobalAppState.Instance); } catch { }
                    }

                    Win32Layered.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_FLAGS);
                    Win32Layered.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    return progman != IntPtr.Zero;
                }

                IntPtr host = Host;
                if (host != IntPtr.Zero)
                {
                    if (Win32Layered.GetParent(hwnd) != host)
                    {
                        Win32Layered.SetParent(hwnd, host);
                    }

                    long style = Win32Layered.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                    long childStyle = (style & ~WS_POPUP) | WS_CHILD;
                    if (childStyle != style)
                    {
                        Win32Layered.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(childStyle));
                    }

                    _wallpaperHwnd = hwnd;
                    Win32Layered.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_FLAGS);

                    if (_captureAffinityApplied.Add(hwnd))
                    {
                        try { WindowsSystemHooks.ApplyWindowCaptureAffinity(hwnd, GlobalAppState.Instance); } catch { }
                    }

                    Win32Layered.ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                    return true;
                }
                Win32Layered.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_FLAGS);
                return false;
            }
            catch
            {
                return false;
            }
        }

        // Brings a window back to the normal top-level layer (used when the
        // widget is unpinned or the shell goes away).
        public static void Detach(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32Layered.IsWindow(hwnd)) return;
            try
            {
                if (Win32Layered.GetParent(hwnd) != IntPtr.Zero)
                    Win32Layered.SetParent(hwnd, IntPtr.Zero);
                Win32Layered.SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, IntPtr.Zero);
            }
            catch { }
        }

        // Called on WM_SETTINGCHANGE / WM_DISPLAYCHANGE so a new monitor layout
        // or a restarted Explorer re-hosts the widgets instead of stranding them.
        public static void Reset()
        {
            _host = IntPtr.Zero;
            _progman = IntPtr.Zero;
            _lastProbeMs = 0;
        }
    }
}
