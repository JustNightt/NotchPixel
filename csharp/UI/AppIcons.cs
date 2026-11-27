using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenPixel.UI
{
    // ========================================================================
    // Real application icons for the bottom notch taskbar.
    //
    // Pinned entries resolve the icon of the .lnk target through the shell;
    // running windows ask the window itself (WM_GETICON), then its class, then
    // its executable. If none of those yield an icon the caller draws a neutral
    // placeholder - it is never replaced with an invented logo.
    // ========================================================================
    public static class AppIcons
    {
        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_LARGEICON = 0x000000000;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const int WM_GETICON = 0x007F;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL2 = 2;
        private const int GCLP_HICON = -14;
        private const int GCLP_HICONSM = -34;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

        private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new();

        public static Bitmap ForShortcut(string shortcutOrExePath)
        {
            if (string.IsNullOrWhiteSpace(shortcutOrExePath)) return null;
            lock (Sync)
            {
                if (Cache.TryGetValue(shortcutOrExePath, out Bitmap cached)) return cached;
            }

            Bitmap bitmap = ExtractFromShell(shortcutOrExePath);
            lock (Sync)
            {
                if (Cache.Count > 128)
                {
                    foreach (Bitmap b in Cache.Values) b.Dispose();
                    Cache.Clear();
                }
                Cache[shortcutOrExePath] = bitmap;
            }
            return bitmap;
        }

        public static Bitmap ForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return null;
            try
            {
                IntPtr icon = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_BIG), IntPtr.Zero);
                if (icon == IntPtr.Zero) icon = GetClassLongPtrW(hwnd, GCLP_HICON);
                if (icon == IntPtr.Zero) icon = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_SMALL2), IntPtr.Zero);
                if (icon == IntPtr.Zero) icon = SendMessageW(hwnd, WM_GETICON, new IntPtr(ICON_SMALL), IntPtr.Zero);
                if (icon == IntPtr.Zero) icon = GetClassLongPtrW(hwnd, GCLP_HICONSM);
                if (icon == IntPtr.Zero) return null;

                using Icon managed = Icon.FromHandle(icon);
                using Bitmap raw = managed.ToBitmap();
                return Normalise(raw);
            }
            catch
            {
                return null;
            }
        }

        // Resolves the executable behind a .lnk through the shell, then takes
        // its icon. Running processes are preferred because a shortcut's icon
        // can be stale after an update.
        public static Bitmap ForPinnedShortcut(string shortcutPath, IntPtr runningWindow)
        {
            if (runningWindow != IntPtr.Zero)
            {
                Bitmap live = ForWindow(runningWindow);
                if (live != null) return live;
            }
            return ForShortcut(shortcutPath);
        }

        public static Bitmap ForProcessId(uint pid)
        {
            if (pid == 0) return null;
            try
            {
                using Process p = Process.GetProcessById((int)pid);
                string path = p.MainModule?.FileName;
                return string.IsNullOrEmpty(path) ? null : ForShortcut(path);
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap ExtractFromShell(string path)
        {
            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;
                IntPtr result = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                {
                    flags = SHGFI_ICON | SHGFI_SMALLICON;
                    result = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
                }
                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

                try
                {
                    using Icon icon = Icon.FromHandle(info.hIcon);
                    using Bitmap raw = icon.ToBitmap();
                    return Normalise(raw);
                }
                finally
                {
                    DestroyIcon(info.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        // 32x32 ARGB with a transparent background and clean edges, cached once.
        private static Bitmap Normalise(Bitmap source)
        {
            const int size = 32;
            var target = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(target))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return target;
        }
    }
}
