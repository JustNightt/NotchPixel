using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using OpenPixel.Core;
using OpenPixel.Rendering;

namespace OpenPixel.UI
{
    // One dock entry: a pinned shortcut, a running window, or both.
    public sealed class TaskbarApp
    {
        public string Label = "";
        public string LaunchPath = "";
        public string ProcessName = "";
        public IntPtr Window = IntPtr.Zero;
        public uint ProcessId = 0;
        public bool IsActive = false;
        public int UnreadCount = 0;
        public Color Accent = Color.FromArgb(255, 78, 179, 255);

        public bool Running => Window != IntPtr.Zero;
        public string Initial => string.IsNullOrEmpty(Label) ? "?" : Label.Substring(0, 1).ToUpperInvariant();
    }

    // ========================================================================
    // Mirrors the real Windows taskbar: pinned shortcuts from Explorer's pinned
    // folder merged with visible top-level app windows. Icons are resolved from
    // the live window first, then the executable behind the shortcut.
    // ========================================================================
    public static class TaskbarApps
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(255, 78, 179, 255),
            Color.FromArgb(255, 114, 137, 218),
            Color.FromArgb(255, 250, 189, 47),
            Color.FromArgb(255, 46, 204, 113),
            Color.FromArgb(255, 255, 143, 177),
            Color.FromArgb(255, 195, 166, 255),
            Color.FromArgb(255, 255, 179, 138)
        };

        private static readonly List<TaskbarApp> Cached = new();
        private static long _lastRefreshMs;

        public static IReadOnlyList<TaskbarApp> Pinned()
        {
            var list = new List<TaskbarApp>();
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(dir)) return list;
                foreach (string file in Directory.GetFiles(dir, "*.lnk"))
                {
                    string label = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    list.Add(new TaskbarApp { Label = label, LaunchPath = file, Accent = AccentFor(label) });
                }
            }
            catch { }
            return list;
        }

        private static bool MatchesPinned(TaskbarApp pinned, string procName, string winTitle)
        {
            if (pinned == null) return false;
            string pLabel = pinned.Label ?? "";
            if (!string.IsNullOrEmpty(procName))
            {
                if (pLabel.Equals(procName, StringComparison.OrdinalIgnoreCase)) return true;
                if (pLabel.Contains(procName, StringComparison.OrdinalIgnoreCase) ||
                    procName.Contains(pLabel.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            if (!string.IsNullOrEmpty(winTitle) &&
                winTitle.Contains(pLabel, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        private static int CountUnreadNotifications(string label, string procName)
        {
            var state = GlobalAppState.Instance;
            int count = 0;
            lock (state.SyncRoot)
            {
                foreach (var n in state.Notifications)
                {
                    if (string.IsNullOrEmpty(n.AppName)) continue;
                    if ((!string.IsNullOrEmpty(label) && n.AppName.Contains(label, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrEmpty(procName) && n.AppName.Contains(procName, StringComparison.OrdinalIgnoreCase)))
                    {
                        ++count;
                    }
                }
            }
            return count;
        }

        public static void Refresh()
        {
            if (Environment.TickCount64 - _lastRefreshMs < 1500) return;
            _lastRefreshMs = Environment.TickCount64;
            Cached.Clear();

            var pinnedList = new List<TaskbarApp>(Pinned());
            foreach (TaskbarApp pinned in pinnedList) Cached.Add(pinned);

            var seenProcesses = new HashSet<int>();
            IntPtr fg = Win32Layered.GetForegroundWindow();

            try
            {
                int ownPid = Environment.ProcessId;
                Win32Layered.EnumWindows((hwnd, _) =>
                {
                    try
                    {
                        if (!Win32Layered.IsWindowVisible(hwnd)) return true;
                        if (Win32Layered.GetWindowTextLengthW(hwnd) <= 0) return true;

                        long exStyle = Win32Layered.GetWindowLongPtr(hwnd, -20).ToInt64();
                        if ((exStyle & 0x80L) != 0) return true;        // WS_EX_TOOLWINDOW
                        if ((exStyle & 0x08000000L) != 0) return true;  // WS_EX_NOACTIVATE

                        Win32Layered.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == 0 || (int)pid == ownPid) return true;
                        if (!seenProcesses.Add((int)pid)) return true;

                        var title = new StringBuilder(192);
                        Win32Layered.GetWindowTextW(hwnd, title, title.Capacity);
                        string label = title.ToString();
                        if (string.IsNullOrWhiteSpace(label)) return true;

                        string procName = "";
                        try
                        {
                            using Process proc = Process.GetProcessById((int)pid);
                            procName = proc.ProcessName ?? "";
                        }
                        catch { }

                        bool isFg = hwnd == fg;
                        TaskbarApp match = pinnedList.Find(p => p.Window == IntPtr.Zero && MatchesPinned(p, procName, label));
                        if (match != null)
                        {
                            match.Window = hwnd;
                            match.ProcessId = pid;
                            match.ProcessName = procName;
                            match.IsActive = isFg;
                            return true;
                        }

                        Cached.Add(new TaskbarApp
                        {
                            Label = label.Length > 26 ? label.Substring(0, 26) : label,
                            ProcessName = procName,
                            Window = hwnd,
                            ProcessId = pid,
                            IsActive = isFg,
                            Accent = AccentFor(label)
                        });
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            foreach (TaskbarApp app in Cached)
            {
                app.UnreadCount = CountUnreadNotifications(app.Label, app.ProcessName);
            }

            // Keep pinned apps first (with live running status merged in), then unpinned running apps.
            Cached.Sort((a, b) =>
            {
                bool aPinned = !string.IsNullOrEmpty(a.LaunchPath);
                bool bPinned = !string.IsNullOrEmpty(b.LaunchPath);
                if (aPinned != bPinned) return bPinned.CompareTo(aPinned);
                if (a.Running != b.Running) return b.Running.CompareTo(a.Running);
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
        }

        public static List<TaskbarApp> DockItems(int max)
        {
            var result = new List<TaskbarApp>(max);
            for (int i = 0; i < Cached.Count && result.Count < max; ++i) result.Add(Cached[i]);
            return result;
        }

        public static List<TaskbarApp> All(int max)
        {
            var result = new List<TaskbarApp>(Math.Min(max, Cached.Count));
            for (int i = 0; i < Cached.Count && result.Count < max; ++i) result.Add(Cached[i]);
            return result;
        }

        public static void Activate(TaskbarApp app)
        {
            try
            {
                if (app.Running)
                {
                    if (Win32Layered.IsIconic(app.Window)) Win32Layered.ShowWindow(app.Window, 9);
                    Win32Layered.SetForegroundWindow(app.Window);
                    return;
                }
                if (!string.IsNullOrEmpty(app.LaunchPath))
                {
                    Process.Start(new ProcessStartInfo(app.LaunchPath) { UseShellExecute = true });
                }
            }
            catch { }
        }

        private static Color AccentFor(string label)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in label) hash = hash * 31 + char.ToUpperInvariant(c);
                return Palette[Math.Abs(hash) % Palette.Length];
            }
        }
    }
}
