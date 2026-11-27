using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NotchPixel.E2E.Tests.Infrastructure;

public sealed record CliExecutionResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut,
    TimeSpan Elapsed);

public sealed record WindowSnapshot(
    nint Hwnd,
    uint Pid,
    string ClassName,
    string Title,
    Rectangle Bounds,
    bool Visible,
    uint DisplayAffinity,
    uint Dpi,
    bool IsTopMost,
    bool IsForeground);

public static class ProcessAndWindowDriver
{
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_MONITOR = 0x00000001;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    private const int SW_SHOW = 5;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const uint GW_HWNDNEXT = 2;

    private static readonly int[] DefaultNotchPixelPorts = { 47823, 47824, 47825, 47826 };

    public static Task<CliExecutionResult> RunCliAsync(
        string args,
        int timeoutMs = 15000,
        string? workingDirectory = null)
    {
        return RunProcessAsync(
            TestWorkspace.OpenPixelExePath,
            args,
            timeoutMs,
            workingDirectory ?? TestWorkspace.RepoRoot);
    }

    public static async Task<CliExecutionResult> RunProcessAsync(
        string fileName,
        string args,
        int timeoutMs = 15000,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory ?? TestWorkspace.RepoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (environmentVariables != null)
        {
            foreach (var kv in environmentVariables)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var outDone = new ManualResetEventSlim(false);
        using var errDone = new ManualResetEventSlim(false);

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) outDone.Set();
            else lock (stdout) stdout.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) errDone.Set();
            else lock (stderr) stderr.AppendLine(e.Data);
        };

        if (!proc.Start())
            throw new InvalidOperationException($"Failed to start process: {fileName} {args}");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool timedOut = false;
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch { }
        }

        outDone.Wait(1000);
        errDone.Wait(1000);
        sw.Stop();

        int exitCode = timedOut ? -1 : proc.ExitCode;
        string outStr;
        string errStr;
        lock (stdout) outStr = stdout.ToString();
        lock (stderr) errStr = stderr.ToString();

        return new CliExecutionResult(exitCode, outStr, errStr, timedOut, sw.Elapsed);
    }

    public static IReadOnlyList<int> GetRunningOpenPixelPids()
    {
        return Process.GetProcessesByName("OpenPixel")
            .Select(p =>
            {
                try { return p.Id; }
                catch { return -1; }
                finally { p.Dispose(); }
            })
            .Where(id => id > 0)
            .OrderBy(id => id)
            .ToArray();
    }

    public static IReadOnlyList<int> GetBoundNotchPixelPorts()
    {
        var props = IPGlobalProperties.GetIPGlobalProperties();
        var tcp = props.GetActiveTcpListeners().Select(ep => ep.Port);
        var udp = props.GetActiveUdpListeners().Select(ep => ep.Port);
        var active = new HashSet<int>(tcp.Concat(udp));
        return DefaultNotchPixelPorts.Where(active.Contains).ToArray();
    }

    public static void TerminateAllOpenPixelInstances()
    {
        foreach (Process proc in Process.GetProcessesByName("OpenPixel"))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            catch { }
            finally
            {
                proc.Dispose();
            }
        }
        RestoreWindowsNativeTaskbar();
    }

    public static async Task<GuiSession> StartGuiSessionAsync(
        int startupWaitMs = 2500,
        bool ensureExclusivePorts = true,
        int readinessTimeoutMs = 12000,
        string? extraArgs = null)
    {
        IReadOnlyList<int> preExisting = GetRunningOpenPixelPids();
        if (ensureExclusivePorts && (preExisting.Count > 0 || GetBoundNotchPixelPorts().Count > 0))
        {
            TerminateAllOpenPixelInstances();
            var portDeadline = DateTime.UtcNow.AddMilliseconds(3000);
            while (DateTime.UtcNow < portDeadline && GetBoundNotchPixelPorts().Count > 0)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = TestWorkspace.OpenPixelExePath,
            Arguments = extraArgs ?? string.Empty,
            WorkingDirectory = TestWorkspace.RepoRoot,
            UseShellExecute = false
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start GUI session: {TestWorkspace.OpenPixelExePath}");

        var session = new GuiSession(proc, preExisting);
        try
        {
            var sw = Stopwatch.StartNew();
            bool loopbackUp = false;
            while (sw.ElapsedMilliseconds < readinessTimeoutMs)
            {
                if (proc.HasExited)
                    throw new InvalidOperationException($"OpenPixel.exe exited prematurely during startup with code {proc.ExitCode}.");

                if (await TryProbeTcpPortAsync("127.0.0.1", 47823, 250).ConfigureAwait(false))
                {
                    loopbackUp = true;
                    break;
                }
                await Task.Delay(120).ConfigureAwait(false);
            }

            if (startupWaitMs > 0)
            {
                int remaining = Math.Max(0, startupWaitMs - (int)sw.ElapsedMilliseconds);
                if (remaining > 0)
                    await Task.Delay(remaining).ConfigureAwait(false);
            }

            if (!loopbackUp && proc.HasExited)
                throw new InvalidOperationException($"OpenPixel.exe exited during startup settle with code {proc.ExitCode}.");

            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private static async Task<bool> TryProbeTcpPortAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public sealed class GuiSession : IAsyncDisposable, IDisposable
    {
        private bool _disposed;

        public Process Process { get; }
        public int Pid { get; }
        public IReadOnlyList<int> PreExistingOpenPixelPids { get; }

        internal GuiSession(Process process, IReadOnlyList<int> preExistingPids)
        {
            Process = process;
            Pid = process.Id;
            PreExistingOpenPixelPids = preExistingPids;
        }

        public IReadOnlyList<WindowSnapshot> GetOwnedWindows(bool visibleOnly = false) =>
            EnumerateProcessWindows(Pid, visibleOnly);

        public async Task<IReadOnlyList<WindowSnapshot>> WaitForVisibleWindowsAsync(int minCount = 1, int timeoutMs = 8000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var wins = GetOwnedWindows(visibleOnly: true);
                if (wins.Count >= minCount) return wins;
                await Task.Delay(100).ConfigureAwait(false);
            }
            return GetOwnedWindows(visibleOnly: true);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(4000);
                }
            }
            catch { }
            finally
            {
                try { Process.Dispose(); } catch { }
                KillWatchdogsForPid(Pid);
                RestoreWindowsNativeTaskbar();
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static void KillWatchdogsForPid(int parentPid)
    {
        foreach (Process p in Process.GetProcessesByName("OpenPixel"))
        {
            try
            {
                string? cmd = TryGetProcessCommandLine(p.Id);
                if (!string.IsNullOrEmpty(cmd) &&
                    cmd.Contains("--watchdog", StringComparison.OrdinalIgnoreCase) &&
                    cmd.Contains(parentPid.ToString(), StringComparison.Ordinal))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(2000);
                }
            }
            catch { }
            finally
            {
                p.Dispose();
            }
        }
    }

    private static string? TryGetProcessCommandLine(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').CommandLine\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    // -------------------------------------------------------------------------
    // Native Windows Taskbar & Window Inspection Helpers
    // -------------------------------------------------------------------------

    public static bool IsNativeTaskbarVisible()
    {
        nint tray = FindWindow("Shell_TrayWnd", null);
        return tray != 0 && IsWindowVisible(tray);
    }

    public static void RestoreWindowsNativeTaskbar()
    {
        nint primary = FindWindow("Shell_TrayWnd", null);
        if (primary != 0)
        {
            ShowWindow(primary, SW_SHOW);
        }

        EnumWindows((hwnd, _) =>
        {
            string cls = GetWindowClassName(hwnd);
            if (cls.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            {
                ShowWindow(hwnd, SW_SHOW);
            }
            return true;
        }, 0);
    }

    public static IReadOnlyList<WindowSnapshot> EnumerateProcessWindows(int pid, bool visibleOnly = false)
    {
        var list = new List<WindowSnapshot>();
        var seen = new HashSet<nint>();
        nint fg = GetForegroundWindow();

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint wPid);
            if (wPid != (uint)pid) return true;
            AddWindow(list, seen, hwnd, wPid, IsWindowVisible(hwnd), visibleOnly, fg);
            return true;
        }, 0);

        // Desktop widgets and the live wallpaper are parented to WorkerW so they
        // survive Win+D. EnumWindows never returns child windows, so walk the
        // desktop hosts as well; the snapshot and every assertion stay identical.
        var hosts = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            string cls = GetWindowClassName(hwnd);
            if (cls.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase))
            {
                hosts.Add(hwnd);
            }
            return true;
        }, 0);

        foreach (nint host in hosts)
        {
            EnumChildWindows(host, (hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wPid);
                if (wPid != (uint)pid) return true;
                if (!TryGetWindowRect(hwnd, out Rectangle rect) || rect.Width <= 0 || rect.Height <= 0) return true;
                AddWindow(list, seen, hwnd, wPid, IsWindowVisible(hwnd), visibleOnly, fg);
                return true;
            }, 0);
        }

        return list;
    }

    private static void AddWindow(
        List<WindowSnapshot> list, HashSet<nint> seen, nint hwnd, uint pid,
        bool visible, bool visibleOnly, nint foreground)
    {
        if (visibleOnly && !visible) return;
        if (!seen.Add(hwnd)) return;
        list.Add(BuildSnapshot(hwnd, pid, visible, foreground));
    }

    // Deterministically brings a window to the foreground. SetForegroundWindow
    // is refused for a background process unless it shares an input queue with
    // the current foreground thread or a recent input event exists, so attach
    // to the foreground thread and fall back to the documented ALT-tap unlock.
    public static bool ForceForeground(nint hwnd)
    {
        if (hwnd == 0) return false;
        ShowWindow(hwnd, 9); // SW_RESTORE
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;

        nint fg = GetForegroundWindow();
        uint fgThread = fg != 0 ? GetWindowThreadProcessId(fg, out _) : 0;
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != thisThread &&
                        AttachThreadInput(thisThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, fgThread, false);
        }

        for (int i = 0; i < 15 && GetForegroundWindow() != hwnd; ++i)
        {
            SendInput(2, new[] { MakeKeyInput(0x12, false), MakeKeyInput(0x12, true) }, Marshal.SizeOf<INPUT>());
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(100);
        }
        return GetForegroundWindow() == hwnd;
    }

    public static IReadOnlyList<WindowSnapshot> EnumerateTopLevelWindows(bool visibleOnly = false)
    {
        var list = new List<WindowSnapshot>();
        nint fg = GetForegroundWindow();
        EnumWindows((hwnd, _) =>
        {
            bool visible = IsWindowVisible(hwnd);
            if (visibleOnly && !visible) return true;
            GetWindowThreadProcessId(hwnd, out uint wPid);
            list.Add(BuildSnapshot(hwnd, wPid, visible, fg));
            return true;
        }, 0);
        return list;
    }

    private static WindowSnapshot BuildSnapshot(nint hwnd, uint pid, bool visible, nint fg)
    {
        TryGetWindowRect(hwnd, out Rectangle rect);
        TryGetWindowDisplayAffinity(hwnd, out uint affinity);
        uint dpi = GetWindowDpi(hwnd);
        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        bool topMost = (exStyle & WS_EX_TOPMOST) != 0;
        return new WindowSnapshot(
            hwnd,
            pid,
            GetWindowClassName(hwnd),
            GetWindowTitle(hwnd),
            rect,
            visible,
            affinity,
            dpi,
            topMost,
            hwnd == fg);
    }

    public static bool TryGetWindowDisplayAffinity(nint hwnd, out uint affinity)
    {
        return GetWindowDisplayAffinity(hwnd, out affinity);
    }

    public static bool TryGetWindowRect(nint hwnd, out Rectangle rect)
    {
        if (GetWindowRect(hwnd, out RECT r))
        {
            rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }
        rect = Rectangle.Empty;
        return false;
    }

    public static nint GetForegroundWindowHandle() => GetForegroundWindow();

    public static bool IsWindowVisibleHandle(nint hwnd) => IsWindowVisible(hwnd);

    public static uint GetWindowDpi(nint hwnd)
    {
        try
        {
            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi : 96u;
        }
        catch
        {
            return 96u;
        }
    }

    public static bool IsProcessDpiAware(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        int hr = GetProcessDpiAwareness(proc.Handle, out int awareness);
        return hr == 0 && awareness >= 1; // SYSTEM_DPI_AWARE (1) or PER_MONITOR_DPI_AWARE (2)
    }

    public static int GetWindowZOrderIndex(nint targetHwnd)
    {
        // Desktop widgets/wallpaper are children of WorkerW, which has no entry in
        // the top-level Z chain. Their effective order is their root host's order.
        nint root = GetAncestor(targetHwnd, 2 /* GA_ROOT */);
        if (root == 0) root = targetHwnd;

        int index = 0;
        nint curr = GetTopWindow(0);
        while (curr != 0)
        {
            if (curr == root) return index;
            index++;
            curr = GetWindow(curr, GW_HWNDNEXT);
        }
        return -1;
    }

    public static void SimulateCrash(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        proc.Kill(entireProcessTree: false);
        proc.WaitForExit(3000);
    }

    public static void SuspendProcess(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        _ = NtSuspendProcess(proc.Handle);
    }

    public static void ResumeProcess(int pid)
    {
        using var proc = Process.GetProcessById(pid);
        _ = NtResumeProcess(proc.Handle);
    }

    // -------------------------------------------------------------------------
    // Input Simulation (Hotkeys & Mouse)
    // -------------------------------------------------------------------------

    public static void SendHotkey(uint modifiers, Keys key)
    {
        var down = new List<ushort>();
        if ((modifiers & MOD_CONTROL) != 0) down.Add((ushort)Keys.LControlKey);
        if ((modifiers & MOD_ALT) != 0) down.Add((ushort)Keys.LMenu);
        if ((modifiers & MOD_SHIFT) != 0) down.Add((ushort)Keys.LShiftKey);
        if ((modifiers & MOD_WIN) != 0) down.Add((ushort)Keys.LWin);
        down.Add((ushort)key);

        try
        {
            var inputs = new INPUT[down.Count];
            for (int i = 0; i < down.Count; i++)
            {
                inputs[i] = MakeKeyInput(down[i], keyUp: false);
            }
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            Thread.Sleep(40);
        }
        finally
        {
            var upInputs = new INPUT[down.Count];
            for (int i = 0; i < down.Count; i++)
            {
                upInputs[i] = MakeKeyInput(down[down.Count - 1 - i], keyUp: true);
            }
            SendInput((uint)upInputs.Length, upInputs, Marshal.SizeOf<INPUT>());
        }
    }

    public static void SendKeyPress(Keys key) => SendHotkey(0, key);

    public static void SimulateMouseMove(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
    }

    public static void SimulateMouseClick(int screenX, int screenY, bool rightClick = false)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(25);
        uint downFlag = rightClick ? 0x0008u : 0x0002u;
        uint upFlag = rightClick ? 0x0010u : 0x0004u;
        var inputs = new[]
        {
            MakeMouseInput(downFlag),
            MakeMouseInput(upFlag)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SimulateMouseDrag(int startX, int startY, int endX, int endY, int steps = 12, int stepDelayMs = 15)
    {
        SetCursorPos(startX, startY);
        Thread.Sleep(30);
        var down = new[] { MakeMouseInput(0x0002u) };
        SendInput(1, down, Marshal.SizeOf<INPUT>());
        try
        {
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                int x = (int)Math.Round(startX + (endX - startX) * t);
                int y = (int)Math.Round(startY + (endY - startY) * t);
                SetCursorPos(x, y);
                Thread.Sleep(stepDelayMs);
            }
        }
        finally
        {
            var up = new[] { MakeMouseInput(0x0004u) };
            SendInput(1, up, Marshal.SizeOf<INPUT>());
        }
    }

    // -------------------------------------------------------------------------
    // Windows Core Audio (IAudioEndpointVolume) COM Interop
    // -------------------------------------------------------------------------

    public static (float Scalar01, bool Muted, string EndpointId) GetSystemMasterVolumeAndMute()
    {
        return RunInMta(() =>
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out IMMDevice device);
                try
                {
                    device.GetId(out string endpointId);
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    device.Activate(ref iid, 23 /* CLSCTX_ALL */, 0, out object volObj);
                    var epVol = (IAudioEndpointVolume)volObj;
                    try
                    {
                        epVol.GetMasterVolumeLevelScalar(out float scalar);
                        epVol.GetMute(out bool muted);
                        return (scalar, muted, endpointId ?? string.Empty);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epVol);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        });
    }

    public static void SetSystemMasterVolume(float scalar01, bool? muted = null)
    {
        float clamped = Math.Clamp(scalar01, 0f, 1f);
        RunInMta(() =>
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                enumerator.GetDefaultAudioEndpoint(0, 1, out IMMDevice device);
                try
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    device.Activate(ref iid, 23, 0, out object volObj);
                    var epVol = (IAudioEndpointVolume)volObj;
                    try
                    {
                        Guid ctx = Guid.Empty;
                        epVol.SetMasterVolumeLevelScalar(clamped, ref ctx);
                        if (muted.HasValue)
                        {
                            epVol.SetMute(muted.Value, ref ctx);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(epVol);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
            return true;
        });
    }

    public static IDisposable CaptureAndRestoreAudioScope() => new AudioRestoreScope();

    private sealed class AudioRestoreScope : IDisposable
    {
        private readonly float _origScalar;
        private readonly bool _origMuted;
        private bool _disposed;

        public AudioRestoreScope()
        {
            (_origScalar, _origMuted, _) = GetSystemMasterVolumeAndMute();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                SetSystemMasterVolume(_origScalar, _origMuted);
            }
            catch { }
        }
    }

    private static T RunInMta<T>(Func<T> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
            return action();

        T? result = default;
        Exception? err = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { err = ex; }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(5000);
        if (err != null) throw err;
        return result!;
    }

    // -------------------------------------------------------------------------
    // Dummy Test Windows (Fullscreen & Normal) on dedicated STA thread
    // -------------------------------------------------------------------------

    public static DummyTestWindow CreateDummyFullscreenWindow(
        string title = "NotchPixelE2E_FullscreenDummy",
        int screenIndex = 0)
    {
        Screen[] screens = Screen.AllScreens;
        Screen target = (screenIndex >= 0 && screenIndex < screens.Length) ? screens[screenIndex] : Screen.PrimaryScreen!;
        return new DummyTestWindow(target.Bounds, title, borderless: true, topMost: false);
    }

    public static DummyTestWindow CreateDummyNormalWindow(
        Rectangle? bounds = null,
        string title = "NotchPixelE2E_NormalDummy",
        bool topMost = false)
    {
        Rectangle area = bounds ?? new Rectangle(120, 120, 640, 420);
        return new DummyTestWindow(area, title, borderless: false, topMost: topMost);
    }

    public sealed class DummyTestWindow : IDisposable
    {
        private readonly Thread _uiThread;
        private readonly ManualResetEventSlim _ready = new(false);
        private Form? _form;
        private bool _disposed;

        public nint Hwnd { get; private set; }
        public Rectangle Bounds { get; private set; }

        internal DummyTestWindow(Rectangle bounds, string title, bool borderless, bool topMost)
        {
            Bounds = bounds;
            _uiThread = new Thread(() =>
            {
                _form = new Form
                {
                    Text = title,
                    StartPosition = FormStartPosition.Manual,
                    Bounds = bounds,
                    FormBorderStyle = borderless ? FormBorderStyle.None : FormBorderStyle.Sizable,
                    TopMost = topMost,
                    ShowInTaskbar = true,
                    BackColor = Color.FromArgb(24, 28, 36)
                };
                _form.Shown += (_, _) =>
                {
                    Hwnd = _form.Handle;
                    Bounds = _form.Bounds;
                    ForceForeground(_form.Handle);
                    _ready.Set();
                };
                Application.Run(_form);
            });
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.IsBackground = true;
            _uiThread.Start();

            if (!_ready.Wait(5000))
                throw new TimeoutException($"Timed out creating dummy test window '{title}'.");
        }

        public void BringToFront()
        {
            if (_form == null || _form.IsDisposed) return;
            _form.Invoke(() =>
            {
                _form.WindowState = FormWindowState.Normal;
                _form.BringToFront();
                ForceForeground(_form.Handle);
            });
        }

        public void Minimize()
        {
            if (_form == null || _form.IsDisposed) return;
            _form.Invoke(() => _form.WindowState = FormWindowState.Minimized);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_form != null && !_form.IsDisposed && _form.IsHandleCreated)
                {
                    _form.BeginInvoke(() => _form.Close());
                }
                _uiThread.Join(3000);
            }
            catch { }
            finally
            {
                _ready.Dispose();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Win32 / COM Declarations
    // -------------------------------------------------------------------------

    private static string GetWindowClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    private static string GetWindowTitle(nint hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 2);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
    {
        type = 1, // INPUT_KEYBOARD
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? 0x0002u : 0u
            }
        }
    };

    private static INPUT MakeMouseInput(uint mouseFlags) => new()
    {
        type = 0, // INPUT_MOUSE
        U = new InputUnion
        {
            mi = new MOUSEINPUT { dwFlags = mouseFlags }
        }
    };

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(nint hWnd, out uint pdwAffinity);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("shcore.dll")]
    private static extern int GetProcessDpiAwareness(nint hprocess, out int value);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(nint processHandle);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(nint processHandle);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int dwStateMask, [MarshalAs(UnmanagedType.IUnknown)] out object ppDevices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        void RegisterEndpointNotificationCallback([MarshalAs(UnmanagedType.IUnknown)] object pClient);
        void UnregisterEndpointNotificationCallback([MarshalAs(UnmanagedType.IUnknown)] object pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint dwClsCtx, nint pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        void OpenPropertyStore(uint stgmAccess, [MarshalAs(UnmanagedType.IUnknown)] out object ppProperties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        void GetState(out uint pdwState);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify([MarshalAs(UnmanagedType.IUnknown)] object pNotify);
        void UnregisterControlChangeNotify([MarshalAs(UnmanagedType.IUnknown)] object pNotify);
        void GetChannelCount(out uint pnChannelCount);
        void SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        void SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        void GetMasterVolumeLevel(out float pfLevelDB);
        void GetMasterVolumeLevelScalar(out float pfLevel);
        void SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        void SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        void GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        void GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        void GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        void VolumeStepUp(ref Guid pguidEventContext);
        void VolumeStepDown(ref Guid pguidEventContext);
        void QueryHardwareSupport(out uint pdwHardwareSupportMask);
        void GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }
}
