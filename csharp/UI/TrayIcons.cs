using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenPixel.UI
{
    // ========================================================================
    // Real notification-area icons.
    //
    // Reads Explorer's tray toolbar (ToolbarWindow32 under TrayNotifyWnd),
    // pulls each button's image-list index and converts the shared HICON into
    // a bitmap. Every step is guarded: if this Windows build hides the toolbar
    // or blocks cross-process reads the snapshot is simply empty and the
    // taskbar renders without tray icons - never with invented ones.
    // ========================================================================
    public static class TrayIcons
    {
        private const int TB_BUTTONCOUNT = 0x0418;
        private const int TB_GETBUTTON = 0x0417;
        private const int TB_GETIMAGELIST = 0x0431;
        private const int TBSTATE_HIDDEN = 0x08;
        private const int BTNS_SEP = 0x01;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;
        private const uint ILD_TRANSPARENT = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct TBBUTTON
        {
            public int iBitmap;
            public int idCommand;
            public byte fsState;
            public byte fsStyle;
            public byte bReserved0;
            public byte bReserved1;
            public IntPtr dwData;
            public IntPtr iString;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string cls, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string name);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protect);
        [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
        [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, out TBBUTTON buffer, UIntPtr size, out UIntPtr read);
        [DllImport("comctl32.dll")] private static extern IntPtr ImageList_GetIcon(IntPtr himl, int index, uint flags);

        private static readonly object Sync = new();
        private static List<Bitmap> _cached = new();
        private static long _lastScanMs;
        private static int _scanning;

        public static IReadOnlyList<Bitmap> Snapshot()
        {
            lock (Sync)
            {
                if (Environment.TickCount64 - _lastScanMs > 5000 && Interlocked.CompareExchange(ref _scanning, 1, 0) == 0)
                {
                    _lastScanMs = Environment.TickCount64;
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            List<Bitmap> fresh = Read();
                            lock (Sync)
                            {
                                foreach (Bitmap b in _cached) b.Dispose();
                                _cached = fresh;
                            }
                        }
                        catch { }
                        finally { Interlocked.Exchange(ref _scanning, 0); }
                    });
                }
                return _cached;
            }
        }

        private static List<Bitmap> Read()
        {
            var icons = new List<Bitmap>();
            IntPtr toolbar = FindTrayToolbar();
            if (toolbar == IntPtr.Zero) return icons;

            uint pid;
            GetWindowThreadProcessId(toolbar, out pid);
            if (pid == 0) return icons;

            IntPtr process = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE, false, pid);
            if (process == IntPtr.Zero) return icons;

            IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, new UIntPtr(0x1000), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            try
            {
                if (remote == IntPtr.Zero) return icons;

                IntPtr himl = SendMessageW(toolbar, TB_GETIMAGELIST, IntPtr.Zero, IntPtr.Zero);
                if (himl == IntPtr.Zero) return icons;

                int count = SendMessageW(toolbar, TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero).ToInt32();
                count = Math.Min(count, 24);

                for (int i = 0; i < count && icons.Count < 8; ++i)
                {
                    if (SendMessageW(toolbar, TB_GETBUTTON, new IntPtr(i), remote) == IntPtr.Zero) continue;
                    if (!ReadProcessMemory(process, remote, out TBBUTTON button, new UIntPtr((uint)Marshal.SizeOf<TBBUTTON>()), out _)) continue;
                    if ((button.fsState & TBSTATE_HIDDEN) != 0) continue;
                    if ((button.fsStyle & BTNS_SEP) != 0) continue;
                    if (button.iBitmap < 0) continue;

                    IntPtr hIcon = ImageList_GetIcon(himl, button.iBitmap, ILD_TRANSPARENT);
                    if (hIcon == IntPtr.Zero) continue;
                    try
                    {
                        using Icon managed = Icon.FromHandle(hIcon);
                        using Bitmap raw = managed.ToBitmap();
                        icons.Add(Normalise(raw));
                    }
                    finally
                    {
                        DestroyIcon(hIcon);
                    }
                }
            }
            finally
            {
                if (remote != IntPtr.Zero) VirtualFreeEx(process, remote, UIntPtr.Zero, MEM_RELEASE);
                CloseHandle(process);
            }
            return icons;
        }

        private static IntPtr FindTrayToolbar()
        {
            IntPtr shell = FindWindowW("Shell_TrayWnd", null);
            if (shell == IntPtr.Zero) return IntPtr.Zero;

            IntPtr notify = FindWindowExW(shell, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return IntPtr.Zero;

            IntPtr pager = FindWindowExW(notify, IntPtr.Zero, "SysPager", null);
            IntPtr parent = pager != IntPtr.Zero ? pager : notify;
            IntPtr toolbar = FindWindowExW(parent, IntPtr.Zero, "ToolbarWindow32", null);
            if (toolbar != IntPtr.Zero) return toolbar;

            // Windows 11 moved some icons into a second overflow toolbar.
            IntPtr overflow = FindWindowW("NotifyIconOverflowWindow", null);
            if (overflow != IntPtr.Zero)
                toolbar = FindWindowExW(overflow, IntPtr.Zero, "ToolbarWindow32", null);
            return toolbar;
        }

        private static Bitmap Normalise(Bitmap source)
        {
            const int size = 22;
            var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(target))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return target;
        }
    }
}
