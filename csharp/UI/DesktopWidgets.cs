using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // Desktop widget surface.
    //
    // One window per widget, parented to the desktop layer (WorkerW) so it
    // stays under normal windows and survives Show Desktop. Each WidgetType has
    // its own renderer - no shared "PC stats" fallback - and the interactive
    // ones register hit regions instead of guessing from mouse coordinates.
    // ========================================================================
    public sealed class DesktopWidgetForm : Form
    {
        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        public WidgetType WidgetType { get; }
        public int StyleMode { get; set; } = 0; // 0 glass, 1 dark, 2 light, 3 text only
        public Color CustomAccent { get; set; } = Color.FromArgb(255, 239, 237, 232);
        public int ScalePercent { get; private set; } = 100;

        private bool _isDragging;
        private float _hoverX = -9999.0f;
        private float _hoverY = -9999.0f;
        private int _dragOffsetX;
        private int _dragOffsetY;
        private int _screenX;
        private int _screenY;
        private bool _layerAttached;
        private int _layerOriginX;
        private int _layerOriginY;

        private Bitmap _photo;
        private string _photoPath = "";
        private long _photoScanMs;
        private List<string> _photoFiles = new();
        private int _photoIndex;

        private static readonly object BatterySync = new();
        private static List<BatteryReading> _batteries = new();
        private static long _batteryScanMs;
        private static bool _batteryScanInFlight;

        private readonly float[] _sparkCpu = new float[TelemetryHistory.Capacity];
        private readonly float[] _sparkGpu = new float[TelemetryHistory.Capacity];
        private readonly float[] _sparkRam = new float[TelemetryHistory.Capacity];
        private readonly float[] _sparkDown = new float[TelemetryHistory.Capacity];
        private readonly float[] _sparkUp = new float[TelemetryHistory.Capacity];

        private sealed class BatteryReading
        {
            public string Name = "Battery";
            public int Percent;
            public bool Charging;
            public bool HasPercent;
        }

        public DesktopWidgetForm(GlobalAppState state, WidgetType type, int x, int y)
        {
            _state = state;
            WidgetType = type;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            _screenX = x;
            _screenY = y;
            Bounds = new Rectangle(x, y, BaseWidth(type), BaseHeight(type));
            if (type == WidgetType.DayClock || type == WidgetType.AnalogClock || type == WidgetType.PlainWeather)
                StyleMode = 3; // Float directly on wallpaper matching NotchPixel Day Clock/Analog/Plain Weather
            CustomAccent = Color.FromArgb(_state.Settings.DayClockAccentColorArgb);
            UpdateStockWidget();
        }

        private static int BaseWidth(WidgetType t) => t switch
        {
            WidgetType.DayClock => 520,
            WidgetType.AnalogClock => 200,
            WidgetType.Calendar => 276,
            WidgetType.TodoList => 248,
            WidgetType.Photos => 264,
            WidgetType.PcStats => 288,
            WidgetType.StockPrices => 250,
            WidgetType.Batteries => 244,
            WidgetType.FocusTimer => 214,
            WidgetType.CustomTile => 228,
            WidgetType.Clock => 214,
            WidgetType.WorldClock => 224,
            _ => 214
        };

        private static int BaseHeight(WidgetType t) => t switch
        {
            WidgetType.DayClock => 310,
            WidgetType.AnalogClock => 200,
            WidgetType.Calendar => 222,
            WidgetType.TodoList => 178,
            WidgetType.Photos => 178,
            WidgetType.PcStats => 152,
            WidgetType.StockPrices => 152,
            WidgetType.Batteries => 148,
            WidgetType.FocusTimer => 136,
            WidgetType.CustomTile => 122,
            WidgetType.Clock => 116,
            WidgetType.WorldClock => 122,
            _ => 122
        };

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000; // LAYERED | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TryAttachToDesktopLayer();
        }

        // WinForms re-applies its own window style after OnHandleCreated, which
        // resets the child parenting. Re-attach once the form is fully shown.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AttachAndRender();
        }

        private string _lastRefreshKey = "";

        // The 1 Hz tick used to repaint every widget (and re-capture the glass)
        // even when nothing visible changed. Widgets that only show minutes are
        // repainted once a minute; live gauges still update every second.
        public void RefreshIfStale()
        {
            string minute = DateTime.Now.ToString("yyyyMMddHHmm");
            string key = WidgetType switch
            {
                WidgetType.FocusTimer or WidgetType.PcStats or WidgetType.Batteries
                    => minute + ":" + DateTime.Now.Second,
                _ => minute
            };
            if (key == _lastRefreshKey) return;
            _lastRefreshKey = key;
            RenderNow();
        }

        public void AttachAndRender()
        {
            if (IsDisposed || !IsHandleCreated) return;
            TryAttachToDesktopLayer();
            PositionWindow();
            RenderNow();
        }

        // WorkerW can move to (0,0) on every shell build we target; if it does
        // not, staying top-level at HWND_BOTTOM is safer than mis-parenting and
        // jumping the widget across the screen.
        private void TryAttachToDesktopLayer()
        {
            if (IsDisposed || !IsHandleCreated) return;
            _layerOriginX = 0;
            _layerOriginY = 0;
            _layerAttached = DesktopLayer.Attach(Handle, asWallpaper: false);
            PositionWindow();
        }

        // _screenX/_screenY are authoritative. WinForms raises LocationChanged while
        // it re-parents/resets styles during Show, and copying those transient values
        // poisoned the position (widgets ended up off-screen). Dragging updates the
        // screen coordinates explicitly instead.

        public void ApplyScale(int percent)
        {
            ScalePercent = Math.Clamp(percent, 60, 200);
            float k = ScalePercent / 100.0f;
            int cx = _screenX + Width / 2;
            int cy = _screenY + Height / 2;
            int w = Math.Max(120, (int)MathF.Round(BaseWidth(WidgetType) * k));
            int h = Math.Max(80, (int)MathF.Round(BaseHeight(WidgetType) * k));
            Width = w;
            Height = h;
            _screenX = cx - w / 2;
            _screenY = cy - h / 2;
            PositionWindow();
            RenderNow();
            AuxiliaryWindows.NotifyWidgetLayoutChanged(_state, this);
        }

        private void PositionWindow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            Point at = PresentPoint();
            Win32Layered.SetWindowPos(Handle, new IntPtr(1), at.X, at.Y, Width, Height, 0x0010 | 0x0001);
        }

        // A layered child of WorkerW positions itself in the parent's client area,
        // not in screen coordinates. Using screen coordinates here drew every
        // attached widget off its intended spot (or off-screen entirely).
        private Point PresentPoint()
        {
            return new Point(_screenX, _screenY);
        }

        public bool SavePreview(string path) => _renderer.SaveSurfacePng(path);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_WINDOWPOSCHANGING = 0x0046;
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_SETTINGCHANGE = 0x001A;
            if (m.Msg == WM_WINDOWPOSCHANGING && !_isDragging)
            {
                var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                wp.hwndInsertAfter = new IntPtr(1); // HWND_BOTTOM: stay under active windows
                wp.flags &= ~0x0004u; // Clear SWP_NOZORDER so bottom placement holds
                Marshal.StructureToPtr(wp, m.LParam, false);
            }
            if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
            {
                // A monitor change or Explorer restart invalidates both the layer
                // host and the widget's position; re-host, then repaint.
                DesktopLayer.Reset();
                _layerAttached = false;
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    TryAttachToDesktopLayer();
                    PositionWindow();
                    RenderNow();
                }));
            }
            base.WndProc(ref m);
        }

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (StyleMode == 0 && !_state.Settings.FallbackBlackOnGlassCrash)
                Win32Layered.SetWindowDisplayAffinity(Handle, Win32Layered.WDA_EXCLUDEFROMCAPTURE);
            else
                WindowsSystemHooks.ApplyWindowCaptureAffinity(Handle, _state);
            PositionWindow();

            try
            {
                if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
                _renderer.ClearHitRegions();

                var box = new RectangleF(2, 2, Width - 4, Height - 4);
                bool noBackground = StyleMode == 3;
                bool drewGlass = false;
                if (!noBackground)
                {
                    using GraphicsPath clipPath = RoundedPath(box, 18.0f);
                    Region prevClip = _renderer.G.Clip;
                    _renderer.G.SetClip(clipPath, CombineMode.Replace);
                    if (StyleMode == 0 && !_state.Settings.DrawWithProcessor)
                    {
                        drewGlass = _renderer.RenderLiquidGlassBackdrop(
                            new Rectangle(_screenX, _screenY, Width, Height),
                            2, 2, Width - 4, Height - 4, _state.Settings.GlassBlurRadius,
                            _state.Settings.Theme == SurfaceTheme.CandyGlass);
                    }
                    if (!drewGlass)
                    {
                        Color bg = StyleMode == 2
                            ? Color.FromArgb(242, 247, 247, 245)
                            : Color.FromArgb(238, 11, 11, 12);
                        _renderer.FillRoundedRect(box, 18, bg);
                    }
                    _renderer.G.Clip = prevClip;
                    _renderer.DrawRoundedRect(box, 18, Color.FromArgb(StyleMode == 2 ? (byte)40 : (byte)56, 255, 255, 255), 1.0f);
                }

                Color ink = StyleMode == 2 ? Color.FromArgb(255, 12, 12, 14) : CustomAccent;
                Color muted = StyleMode == 2 ? Color.FromArgb(255, 82, 82, 88) : Color.FromArgb(214, 235, 233, 228);
                Color faint = StyleMode == 2 ? Color.FromArgb(255, 128, 128, 134) : Color.FromArgb(140, 235, 233, 228);
                Color accent = ThemeSpec.Get(_state.Settings.Theme).Accent;
                if (StyleMode != 0) accent = ThemeSpec.AdaptiveInkForBackground(ink);

                DateTime now = DateTime.Now;
                switch (WidgetType)
                {
                    case WidgetType.DayClock: DrawDayClock(now, ink, muted, faint); break;
                    case WidgetType.AnalogClock: DrawAnalogClock(now, ink, muted, accent); break;
                    case WidgetType.Clock: DrawClock(now, ink, muted); break;
                    case WidgetType.WorldClock: DrawWorldClock(now, ink, muted); break;
                    case WidgetType.MinimalWeather:
                    case WidgetType.PlainWeather: DrawWeather(now, ink, muted); break;
                    case WidgetType.Calendar: DrawCalendar(now, ink, muted, faint, accent); break;
                    case WidgetType.TodoList: DrawTodo(ink, muted, faint, accent); break;
                    case WidgetType.Photos: DrawPhotos(ink, muted, accent); break;
                    case WidgetType.Batteries: DrawBatteries(ink, muted, faint, accent); break;
                    case WidgetType.FocusTimer: DrawFocusTimer(ink, muted, accent); break;
                    case WidgetType.PcStats: DrawPcStats(ink, muted, faint); break;
                    case WidgetType.StockPrices: DrawStocks(ink, muted, faint, accent); break;
                    case WidgetType.CustomTile: DrawCustomTile(ink, muted, faint, accent); break;
                    default: DrawPcStats(ink, muted, faint); break;
                }

                _renderer.EndLayeredFrame(Handle, PresentPoint(), noBackground ? (byte)255 : (byte)246);
            }
            catch
            {
                // A bad frame must never take down the desktop: black pill, not a crash.
                try
                {
                    if (_renderer.BeginLayeredFrame(Width, Height, 1.0f))
                    {
                        _renderer.FillRoundedRect(new RectangleF(2, 2, Width - 4, Height - 4), 18, Color.FromArgb(255, 11, 11, 12));
                        _renderer.EndLayeredFrame(Handle, PresentPoint(), 246);
                    }
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------
        // Day Clock
        // ------------------------------------------------------------------
        // Native C# port of Mond Desktop Clock v5.1 (zero Windhawk dependency):
        // Crisp geometric weekday stencil with drop shadow, formatted date & time
        // rows with tabular digits and drop shadows, plus optional media/weather/recycle bin.
        private void DrawDayClock(DateTime now, Color ink, Color muted, Color faint)
        {
            float k = ScalePercent / 100.0f;
            float pad = 20.0f * k;
            float innerW = Width - pad * 2.0f;
            float y = 12.0f * k;
            float shadowOff = 2.0f * k;
            Color shadowCol = Color.FromArgb(145, 0, 0, 0);
            var s = _state.Settings;

            if (s.DayClockShowWeekday)
            {
                string weekday = now.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture).ToUpperInvariant();
                float tracking = Math.Clamp(s.DayClockLetterSpacing, 6, 28) * k * 1.15f;
                float byWidth = StencilFont.WidthOf(weekday, 1.0f, tracking) <= 0.01f
                    ? innerW
                    : (innerW - (weekday.Length - 1) * tracking) / Math.Max(0.01f, StencilFont.WidthOf(weekday, 1.0f, 0.0f));
                float cap = Math.Clamp(byWidth, 24.0f * k, 56.0f * k);
                if (StencilFont.SupportsAll(weekday))
                {
                    // Drop shadow matching Mond Desktop Clock dayShadowOffset=2
                    StencilFont.Draw(_renderer.G, weekday, Width * 0.5f + shadowOff, y + shadowOff, cap, tracking, shadowCol, 0.94f);
                    StencilFont.Draw(_renderer.G, weekday, Width * 0.5f, y, cap, tracking, ink, 0.96f);
                }
                else
                {
                    _renderer.DrawTrackedText(weekday, new RectangleF(pad + shadowOff, y + shadowOff, innerW, cap * 1.15f), cap * 0.92f, false, shadowCol, tracking);
                    _renderer.DrawTrackedText(weekday, new RectangleF(pad, y, innerW, cap * 1.15f), cap * 0.92f, false, ink, tracking);
                }
                y += cap + 14.0f * k;
            }

            float smallShadow = 1.0f * k;
            if (s.DayClockShowDate)
            {
                string dateStr = now.ToString("dd MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                _renderer.DrawTrackedText(dateStr, new RectangleF(pad + smallShadow, y + smallShadow, innerW, 20.0f * k), 13.0f * k, true, shadowCol, 2.0f * k);
                _renderer.DrawTrackedText(dateStr, new RectangleF(pad, y, innerW, 20.0f * k), 13.0f * k, true, ink, 2.0f * k);
                y += 23.0f * k;
            }

            if (s.DayClockShowTime)
            {
                string timeText = s.Use24HourClock
                    ? now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
                    : now.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
                string dashTime = s.DayClockShowDashes ? $"- {timeText} -" : timeText;
                _renderer.DrawTrackedText(dashTime, new RectangleF(pad + smallShadow, y + smallShadow, innerW, 22.0f * k), 13.5f * k, true, shadowCol, 1.6f * k);
                _renderer.DrawTrackedText(dashTime, new RectangleF(pad, y, innerW, 22.0f * k), 13.5f * k, true, ink, 1.6f * k);
                y += 24.0f * k;
            }

            if (s.DayClockShowNowPlaying)
            {
                Color accentCol = Color.FromArgb(s.DayClockAccentColorArgb);
                if (accentCol.A < 30) accentCol = Color.FromArgb(255, 232, 138, 42); // warm Mond amber default
                var m = _state.Media;
                if (m.HasSession)
                {
                    y += 8.0f * k;
                    float barY = y + 8.0f * k;
                    float timeW = 46.0f * k;
                    float trackL = pad + timeW + 6.0f * k;
                    float trackR = Width - pad - timeW - 6.0f * k;
                    float trackW = Math.Max(40.0f * k, trackR - trackL);
                    float prog = m.DurationSec > 0.1 ? Math.Clamp((float)(m.PositionSec / m.DurationSec), 0.0f, 1.0f) : 0.0f;

                    _renderer.DrawTextStr(IslandTabsRenderer.FormatTimeSec(m.PositionSec),
                        new RectangleF(pad, barY - 8.0f * k, timeW, 16.0f * k), 8.5f * k, true, ink, mono: true, hAlign: StringAlignment.Far);
                    _renderer.DrawTextStr("-" + IslandTabsRenderer.FormatTimeSec(Math.Max(0.0, m.DurationSec - m.PositionSec)),
                        new RectangleF(trackR + 6.0f * k, barY - 8.0f * k, timeW, 16.0f * k), 8.5f * k, true, muted, mono: true, hAlign: StringAlignment.Near);

                    _renderer.DrawLine(trackL, barY, trackR, barY, Color.FromArgb(75, ink.R, ink.G, ink.B), 1.4f * k);
                    float knobX = trackL + trackW * prog;
                    _renderer.DrawLine(trackL, barY, knobX, barY, ink, 1.8f * k);
                    _renderer.FillCircle(knobX, barY, 5.5f * k, Color.FromArgb(65, ink.R, ink.G, ink.B));
                    _renderer.FillCircle(knobX, barY, 2.8f * k, ink);
                    y += 22.0f * k;

                    string artistLine = (m.Artist ?? "").ToUpperInvariant();
                    string titleLine = (m.Title ?? "").ToUpperInvariant();
                    _renderer.DrawTrackedText(artistLine, new RectangleF(pad, y, innerW, 15.0f * k), 8.5f * k, false, muted, 1.0f * k);
                    y += 14.0f * k;
                    _renderer.DrawTrackedText(titleLine, new RectangleF(pad, y, innerW, 16.0f * k), 9.0f * k, true, ink, 0.9f * k);
                    y += 22.0f * k;

                    // Outlined Mond-style transport controls in customizable DayClock accent color
                    float midX = Width * 0.5f;
                    float btnY = y + 4.0f * k;
                    _renderer.DrawLucideIcon("skip-back", midX - 28.0f * k, btnY, 13.0f * k, accentCol, 1.4f * k);
                    _renderer.DrawLucideIcon(m.IsPlaying ? "pause" : "play", midX, btnY, 14.0f * k, accentCol, 1.5f * k);
                    _renderer.DrawLucideIcon("skip-forward", midX + 28.0f * k, btnY, 13.0f * k, accentCol, 1.4f * k);
                    _renderer.AddHitRegion("dc-prev", new RectangleF(midX - 40.0f * k, btnY - 12.0f * k, 24.0f * k, 24.0f * k),
                        () => WindowsSystemHooks.MediaPreviousTrack(_state));
                    _renderer.AddHitRegion("dc-play", new RectangleF(midX - 13.0f * k, btnY - 12.0f * k, 26.0f * k, 24.0f * k),
                        () => WindowsSystemHooks.MediaTogglePlayPause(_state));
                    _renderer.AddHitRegion("dc-next", new RectangleF(midX + 16.0f * k, btnY - 12.0f * k, 24.0f * k, 24.0f * k),
                        () => WindowsSystemHooks.MediaNextTrack(_state));
                    y += 28.0f * k;
                }
            }

            // Bottom Mond row: Weather (left) + Recycle Bin (right), both individually toggleable
            if (s.DayClockShowWeather || s.DayClockShowRecycleBin)
            {
                Color accentCol = Color.FromArgb(s.DayClockAccentColorArgb);
                if (accentCol.A < 30) accentCol = Color.FromArgb(255, 232, 138, 42);
                float botY = Math.Max(y + 10.0f * k, Height - 58.0f * k);

                if (s.DayClockShowWeather)
                {
                    float sunCx = pad + 26.0f * k;
                    float sunCy = botY + 22.0f * k;
                    _renderer.FillCircle(sunCx, sunCy, 17.5f * k, Color.FromArgb(255, 255, 210, 20));
                    // Crisp soft white cloud overlapping bottom-left of sun
                    _renderer.FillCircle(sunCx - 11.0f * k, sunCy + 11.0f * k, 7.5f * k, Color.FromArgb(245, 250, 250, 252));
                    _renderer.FillCircle(sunCx - 4.0f * k, sunCy + 9.0f * k, 8.5f * k, Color.FromArgb(245, 250, 250, 252));
                    _renderer.FillCircle(sunCx + 3.0f * k, sunCy + 12.0f * k, 6.5f * k, Color.FromArgb(245, 250, 250, 252));

                    string city = string.IsNullOrWhiteSpace(s.WeatherCity) ? "LOCAL" : s.WeatherCity.ToUpperInvariant();
                    int tempF = (int)Math.Round(s.WeatherTempC * 9.0 / 5.0 + 32.0);
                    _renderer.DrawTextStr(city, new RectangleF(sunCx + 24.0f * k, botY + 8.0f * k, 150.0f * k, 18.0f * k),
                        11.5f * k, true, ink, hAlign: StringAlignment.Near);
                    _renderer.DrawTextStr($"FAIR {tempF}°F", new RectangleF(sunCx + 24.0f * k, botY + 25.0f * k, 150.0f * k, 16.0f * k),
                        9.0f * k, false, muted, hAlign: StringAlignment.Near);
                }

                if (s.DayClockShowRecycleBin)
                {
                    var (binItems, binBytes) = QueryRecycleBinStats();
                    string filesStr = binItems >= 1000 ? $"FILES {binItems / 1000.0:0.#}K" : $"FILES {binItems}";
                    string sizeStr = binBytes >= 1024L * 1024L * 1024L
                        ? $"SIZE {binBytes / (1024.0 * 1024.0 * 1024.0):0.#} GB"
                        : $"SIZE {Math.Max(0, binBytes / (1024.0 * 1024.0)):0.#} MB";
                    float binIconX = Width - pad - 18.0f * k;
                    float binTextR = binIconX - 18.0f * k;
                    _renderer.DrawTextStr("RECYCLE BIN", new RectangleF(binTextR - 140.0f * k, botY + 2.0f * k, 140.0f * k, 15.0f * k),
                        10.0f * k, true, ink, hAlign: StringAlignment.Far);
                    _renderer.DrawTextStr(filesStr, new RectangleF(binTextR - 140.0f * k, botY + 17.0f * k, 140.0f * k, 13.0f * k),
                        8.5f * k, false, muted, hAlign: StringAlignment.Far);
                    _renderer.DrawTextStr(sizeStr, new RectangleF(binTextR - 140.0f * k, botY + 30.0f * k, 140.0f * k, 13.0f * k),
                        8.5f * k, false, muted, hAlign: StringAlignment.Far);
                    _renderer.DrawLucideIcon("trash", binIconX, botY + 22.0f * k, 22.0f * k, accentCol, 1.8f * k);
                    _renderer.AddHitRegion("dc-bin", new RectangleF(binTextR - 120.0f * k, botY, 156.0f * k, 46.0f * k), () =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true }); }
                        catch { }
                    });
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHQUERYRBINFO
        {
            public int cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBinW(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

        private static (long Items, long Bytes) QueryRecycleBinStats()
        {
            try
            {
                var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBinW(null, ref info) == 0)
                    return (Math.Max(0, info.i64NumItems), Math.Max(0, info.i64Size));
            }
            catch { }
            return (0, 0);
        }

        // ------------------------------------------------------------------
        // Clocks
        // ------------------------------------------------------------------
        private void DrawAnalogClock(DateTime now, Color ink, Color muted, Color accent)
        {
            float cx = Width * 0.5f;
            float cy = Height * 0.5f;
            float r = Math.Min(Width, Height) * 0.40f;
            float k = ScalePercent / 100.0f;

            _renderer.DrawCircle(cx, cy, r, Color.FromArgb(70, ink.R, ink.G, ink.B), 1.4f * k);
            for (int i = 0; i < 12; ++i)
            {
                double a = i * Math.PI / 6.0 - Math.PI / 2.0;
                float inner = i % 3 == 0 ? r * 0.80f : r * 0.87f;
                _renderer.DrawLine(
                    cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                    cx + (float)Math.Cos(a) * r * 0.94f, cy + (float)Math.Sin(a) * r * 0.94f,
                    i % 3 == 0 ? ink : muted, i % 3 == 0 ? 2.0f * k : 1.0f * k);
            }

            double hourAngle = ((now.Hour % 12) + now.Minute / 60.0) * Math.PI / 6.0 - Math.PI / 2.0;
            double minuteAngle = (now.Minute + now.Second / 60.0) * Math.PI / 30.0 - Math.PI / 2.0;
            double secondAngle = now.Second * Math.PI / 30.0 - Math.PI / 2.0;

            _renderer.DrawLine(cx, cy,
                cx + (float)Math.Cos(hourAngle) * r * 0.50f,
                cy + (float)Math.Sin(hourAngle) * r * 0.50f, ink, 4.0f * k);
            _renderer.DrawLine(cx, cy,
                cx + (float)Math.Cos(minuteAngle) * r * 0.72f,
                cy + (float)Math.Sin(minuteAngle) * r * 0.72f, ink, 2.6f * k);
            _renderer.DrawLine(cx, cy,
                cx + (float)Math.Cos(secondAngle) * r * 0.80f,
                cy + (float)Math.Sin(secondAngle) * r * 0.80f, accent, 1.2f * k);
            _renderer.FillCircle(cx, cy, 3.2f * k, ink);

            _renderer.DrawTextStr(now.ToString("tt"), new RectangleF(cx - r, cy + r * 0.22f, r * 2, 18), 9.0f * k, true, muted, hAlign: StringAlignment.Center);
        }

        private void DrawClock(DateTime now, Color ink, Color muted)
        {
            float k = ScalePercent / 100.0f;
            string t = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
            _renderer.DrawTextStr(t, new RectangleF(18, 14, Width - 36, 46), 30.0f * k, true, ink, mono: true);
            _renderer.DrawTextStr(now.ToString("dddd, d MMMM"), new RectangleF(18, 62, Width - 36, 24), 11.0f * k, false, muted);
        }

        private void DrawWorldClock(DateTime now, Color ink, Color muted)
        {
            float k = ScalePercent / 100.0f;
            string local = _state.Settings.Use24HourClock ? now.ToString("HH:mm") : now.ToString("h:mm tt");
            DateTime world = DateTime.UtcNow.AddHours(_state.Settings.WorldClockOffsetHours);
            string remote = _state.Settings.Use24HourClock ? world.ToString("HH:mm") : world.ToString("h:mm tt");

            _renderer.DrawTextStr("LOCAL", new RectangleF(18, 12, Width - 36, 16), 8.5f * k, true, muted, mono: true);
            _renderer.DrawTextStr(local, new RectangleF(18, 26, Width - 36, 34), 22.0f * k, true, ink, mono: true);
            _renderer.DrawLine(18, Height * 0.55f, Width - 18, Height * 0.55f, Color.FromArgb(40, ink.R, ink.G, ink.B), 1.0f);
            _renderer.DrawTextStr(_state.Settings.WorldClockCity.ToUpperInvariant(), new RectangleF(18, Height * 0.56f, Width - 36, 16), 8.5f * k, true, muted, mono: true);
            _renderer.DrawTextStr(remote, new RectangleF(18, Height * 0.56f + 16, Width - 36, 30), 19.0f * k, true, ink, mono: true);
        }

        private void DrawWeather(DateTime now, Color ink, Color muted)
        {
            float k = ScalePercent / 100.0f;
            bool night = now.Hour < 6 || now.Hour >= 19;
            float iconCx = 34.0f;
            float iconCy = 40.0f;
            if (night)
            {
                // Draw crescent moon cleanly without leaving an opaque black circle on transparent background
                using var moonPath = new GraphicsPath();
                float r = 11.5f * k;
                moonPath.AddArc(iconCx - r, iconCy - r, r * 2.0f, r * 2.0f, 40, 260);
                moonPath.AddArc(iconCx - r * 0.35f, iconCy - r * 1.15f, r * 1.7f, r * 1.7f, 270, -170);
                moonPath.CloseFigure();
                using var moonBrush = new SolidBrush(ink);
                _renderer.G.FillPath(moonBrush, moonPath);
            }
            else
            {
                _renderer.FillCircle(iconCx, iconCy, 9.0f * k, ink);
                for (int i = 0; i < 8; ++i)
                {
                    double a = i * Math.PI / 4.0;
                    _renderer.DrawLine(
                        iconCx + (float)Math.Cos(a) * 13.0f * k, iconCy + (float)Math.Sin(a) * 13.0f * k,
                        iconCx + (float)Math.Cos(a) * 18.0f * k, iconCy + (float)Math.Sin(a) * 18.0f * k, ink, 1.6f * k);
                }
            }

            _renderer.DrawTextStr($"{_state.Settings.WeatherTempC}\u00B0", new RectangleF(62, 12, Width - 80, 48), 30.0f * k, true, ink);
            _renderer.DrawTextStr(_state.Settings.WeatherCity, new RectangleF(64, 58, Width - 82, 20), 10.5f * k, false, muted);
            _renderer.DrawTextStr($"H {_state.Settings.WeatherHighC}\u00B0   L {_state.Settings.WeatherLowC}\u00B0", new RectangleF(18, Height - 30, Width - 36, 20), 9.5f * k, false, muted, mono: true);
        }

        private void DrawCalendar(DateTime now, Color ink, Color muted, Color faint, Color accent)
        {
            float k = ScalePercent / 100.0f;
            float padX = 18.0f * k;
            float innerW = Width - padX * 2.0f;

            // Clean Apple-style header: Accent month name on left, subtle year pill on right
            _renderer.DrawTextStr(now.ToString("MMMM"), new RectangleF(padX, 12.0f * k, innerW * 0.68f, 24.0f * k), 14.5f * k, true, accent);
            _renderer.DrawTextStr(now.ToString("yyyy"), new RectangleF(padX + innerW * 0.65f, 14.0f * k, innerW * 0.35f, 20.0f * k), 11.0f * k, false, muted, hAlign: StringAlignment.Far);

            // Subtle divider hairline
            _renderer.DrawLine(padX, 40.0f * k, Width - padX, 40.0f * k, Color.FromArgb(26, 255, 255, 255), 1.0f);

            string[] headers = { "M", "T", "W", "T", "F", "S", "S" };
            float dowY = 46.0f * k;
            float gridTop = 64.0f * k;
            float cellW = innerW / 7.0f;
            float cellH = (Height - gridTop - 12.0f * k) / 6.0f;
            for (int i = 0; i < 7; ++i)
            {
                Color dowCol = i >= 5 ? Color.FromArgb(145, accent.R, accent.G, accent.B) : faint;
                _renderer.DrawTextStr(headers[i], new RectangleF(padX + i * cellW, dowY, cellW, 15.0f * k), 8.5f * k, true, dowCol, hAlign: StringAlignment.Center);
            }

            DateTime first = new(now.Year, now.Month, 1);
            int offset = ((int)first.DayOfWeek + 6) % 7; // Monday-first
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            for (int d = 1; d <= days; ++d)
            {
                int slot = offset + d - 1;
                int col = slot % 7;
                int row = slot / 7;
                if (row > 5) break;
                float cx = padX + col * cellW + cellW * 0.5f;
                float cy = gridTop + row * cellH + cellH * 0.5f;
                var cell = new RectangleF(padX + col * cellW, gridTop + row * cellH, cellW, cellH);
                bool today = d == now.Day;
                if (today)
                {
                    float rad = Math.Min(cellW, cellH) * 0.42f;
                    _renderer.FillCircle(cx, cy, rad, accent);
                }
                Color dayCol = today
                    ? ThemeSpec.AdaptiveInkForBackground(accent)
                    : (col >= 5 ? muted : ink);
                _renderer.DrawTextStr(d.ToString(), cell, 10.0f * k, today, dayCol, hAlign: StringAlignment.Center);
            }
        }

        // ------------------------------------------------------------------
        // To-Do
        // ------------------------------------------------------------------
        private void DrawTodo(Color ink, Color muted, Color faint, Color accent)
        {
            float k = ScalePercent / 100.0f;
            int total = _state.TodoList.Count;

            _renderer.DrawTextStr("To do", new RectangleF(16 * k, 12 * k, 56 * k, 20 * k), 13.0f * k, true, ink);
            _renderer.DrawTextStr(total.ToString(), new RectangleF(62 * k, 12 * k, 32 * k, 20 * k), 13.0f * k, false, muted);

            float plusCx = Width - 24.0f * k, plusCy = 22.0f * k;
            var plusHit = new RectangleF(plusCx - 12 * k, plusCy - 12 * k, 24 * k, 24 * k);
            bool plusHov = _renderer.IsHovered(plusHit, _hoverX, _hoverY);
            _renderer.FillCircle(plusCx, plusCy, 11.0f * k, Color.FromArgb(plusHov ? 46 : 24, 255, 255, 255));
            _renderer.DrawLucideIcon("plus", plusCx, plusCy, 11.0f * k, ink);
            _renderer.AddHitRegion("todo-add", plusHit, OpenTodoWindow);

            if (total == 0)
            {
                _renderer.DrawTextStr("Right-click or tap + to add tasks", new RectangleF(16 * k, 52 * k, Width - 32 * k, 24 * k), 11.0f * k, false, muted, hAlign: StringAlignment.Center);
                return;
            }

            float y = 40.0f * k;
            float rowH = 26.0f * k;
            int shown = Math.Min(5, total);
            for (int i = 0; i < shown; ++i)
            {
                TodoItem item = _state.TodoList[i];
                var row = new RectangleF(10 * k, y, Width - 20 * k, rowH - 2 * k);
                bool hover = _renderer.IsHovered(row, _hoverX, _hoverY);
                if (hover) _renderer.FillRoundedRect(row, 10 * k, Color.FromArgb(20, 255, 255, 255));

                float cx = row.X + 14.0f * k;
                float cy = row.Y + row.Height * 0.5f;
                if (item.Done)
                {
                    _renderer.FillCircle(cx, cy, 6.2f * k, accent);
                    _renderer.DrawLucideIcon("check", cx, cy, 8.5f * k, ThemeSpec.AdaptiveInkForBackground(accent), 1.9f * k);
                }
                else
                {
                    _renderer.DrawCircle(cx, cy, 5.8f * k, Color.FromArgb(135, ink.R, ink.G, ink.B), 1.4f * k);
                }

                string label = item.Text.Length > 32 ? item.Text.Substring(0, 31) + "\u2026" : item.Text;
                _renderer.DrawTextStr(label, new RectangleF(row.X + 28 * k, row.Y, row.Width - 34 * k, row.Height), 12.0f * k, false,
                    item.Done ? faint : ink);

                int index = i;
                _renderer.AddHitRegion($"todo-{i}", row, () =>
                {
                    lock (_state.SyncRoot)
                    {
                        if (index < _state.TodoList.Count) _state.TodoList[index].Done = !_state.TodoList[index].Done;
                    }
                    RenderNow();
                });
                y += rowH;
            }

            if (total > shown)
            {
                _renderer.DrawTextStr($"+{total - shown} more", new RectangleF(16 * k, Height - 20.0f * k, Width - 32 * k, 16 * k), 10.0f * k, false, faint);
            }
            // Left-click drags the To-Do widget; right-click opens menu / editor.
        }

        private static TodoListWindow _todoWindow;

        private void OpenTodoWindow()
        {
            if (_todoWindow != null && !_todoWindow.IsDisposed)
            {
                _todoWindow.Activate();
                return;
            }
            _todoWindow = new TodoListWindow(_state, RenderNow);
            _todoWindow.Show();
        }

        // ------------------------------------------------------------------
        // Photos
        // ------------------------------------------------------------------
        private void DrawPhotos(Color ink, Color muted, Color accent)
        {
            float k = ScalePercent / 100.0f;
            if (_photo == null || Environment.TickCount64 - _photoScanMs > 20000)
            {
                LoadNextPhoto();
                _photoScanMs = Environment.TickCount64;
            }

            var area = new RectangleF(6, 6, Width - 12, Height - 12);
            if (_photo != null)
            {
                try
                {
                    Graphics g = _renderer.G;
                    using var path = RoundedPath(area, 14);
                    Region old = g.Clip;
                    g.SetClip(path, CombineMode.Replace);
                    // Cover-fit so portrait photos do not letterbox inside the tile.
                    float scale = Math.Max(area.Width / _photo.Width, area.Height / _photo.Height);
                    float w = _photo.Width * scale;
                    float h = _photo.Height * scale;
                    g.DrawImage(_photo, area.X + (area.Width - w) * 0.5f, area.Y + (area.Height - h) * 0.5f, w, h);
                    g.Clip = old;
                }
                catch { }
                _renderer.DrawTextStr(Path.GetFileNameWithoutExtension(_photoPath),
                    new RectangleF(16, Height - 32.0f * k, Width - 100, 20), 8.5f * k, false, Color.FromArgb(210, 255, 255, 255));
            }
            else
            {
                _renderer.DrawLucideIcon("folder", Width * 0.5f, Height * 0.42f, 30.0f * k, muted);
                _renderer.DrawTextStr("Choose a folder in Settings", new RectangleF(16, Height * 0.58f, Width - 32, 20), 9.0f * k, false, muted, hAlign: StringAlignment.Center);
            }

            _renderer.AddHitRegion("photo-next", new RectangleF(2, 2, Width - 4, Height - 4), () =>
            {
                LoadNextPhoto();
                RenderNow();
            });
        }

        private void LoadNextPhoto()
        {
            try
            {
                string folder = _state.Settings.PhotosFolder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                    folder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

                if (_photoFiles.Count == 0 || _photo == null)
                {
                    _photoFiles = new List<string>();
                    foreach (string pattern in new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp" })
                    {
                        try { _photoFiles.AddRange(Directory.GetFiles(folder, pattern)); } catch { }
                    }
                    _photoFiles.Sort(StringComparer.OrdinalIgnoreCase);
                }
                if (_photoFiles.Count == 0) return;

                if (_photoIndex >= _photoFiles.Count) _photoIndex = 0;
                string next = _photoFiles[_photoIndex++];

                Bitmap loaded = null;
                try
                {
                    // Load through a stream so the file is not locked while shown.
                    using FileStream fs = new(next, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var temp = Image.FromStream(fs);
                    loaded = new Bitmap(temp);
                }
                catch { loaded = null; }

                if (loaded != null)
                {
                    _photo?.Dispose();
                    _photo = loaded;
                    _photoPath = next;
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Batteries
        // ------------------------------------------------------------------
        private void DrawBatteries(Color ink, Color muted, Color faint, Color accent)
        {
            float k = ScalePercent / 100.0f;
            EnsureBatteryScan();
            _renderer.DrawTextStr("BATTERIES", new RectangleF(16, 10, Width - 32, 20), 9.5f * k, true, ink, mono: true);

            List<BatteryReading> readings;
            lock (BatterySync) readings = new List<BatteryReading>(_batteries);

            if (readings.Count == 0)
            {
                bool hasBattery = SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Unknown;
                if (!hasBattery)
                {
                    _renderer.DrawTextStr("No battery on this PC", new RectangleF(16, 60, Width - 32, 20), 9.5f * k, false, muted, hAlign: StringAlignment.Center);
                    return;
                }
                int pct = (int)Math.Round(SystemInformation.PowerStatus.BatteryLifePercent * 100.0f);
                readings.Add(new BatteryReading
                {
                    Name = "System battery",
                    Percent = Math.Clamp(pct, 0, 100),
                    Charging = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online,
                    HasPercent = pct >= 0
                });
            }

            float y = 36.0f * k;
            int shown = Math.Min(3, readings.Count);
            for (int i = 0; i < shown; ++i)
            {
                BatteryReading bat = readings[i];
                _renderer.DrawTextStr(bat.Name.Length > 22 ? bat.Name.Substring(0, 21) + "\u2026" : bat.Name,
                    new RectangleF(16, y, Width - 80, 16), 8.8f * k, false, muted);
                _renderer.DrawTextStr(bat.HasPercent ? $"{bat.Percent}%" : "\u2014",
                    new RectangleF(Width - 68, y, 52, 16), 9.0f * k, true, ink, mono: true, hAlign: StringAlignment.Far);

                var bar = new RectangleF(16, y + 18.0f * k, Width - 32.0f, 5.0f * k);
                _renderer.FillRoundedRect(bar, 2.5f, Color.FromArgb(36, 255, 255, 255));
                if (bat.HasPercent)
                {
                    Color fill = bat.Charging ? accent : (bat.Percent <= 20 ? Color.FromArgb(255, 255, 69, 58) : ink);
                    _renderer.FillRoundedRect(new RectangleF(bar.X, bar.Y, Math.Max(3.0f, bar.Width * bat.Percent / 100.0f), bar.Height), 2.5f, fill);
                }
                if (bat.Charging) _renderer.DrawTextStr("\u26A1", new RectangleF(Width - 26, y - 1, 16, 16), 9.0f * k, false, accent);
                y += 34.0f * k;
            }
        }

        private static void EnsureBatteryScan()
        {
            if (_batteryScanInFlight || Environment.TickCount64 - _batteryScanMs < 30000) return;
            _batteryScanInFlight = true;
            _batteryScanMs = Environment.TickCount64;
            _ = Task.Run(() =>
            {
                var found = new List<BatteryReading>();
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        "SELECT Name, EstimatedChargeRemaining, BatteryStatus FROM Win32_Battery");
                    foreach (ManagementBaseObject mo in searcher.Get())
                    {
                        try
                        {
                            var reading = new BatteryReading
                            {
                                Name = mo["Name"]?.ToString() ?? "Battery",
                                Charging = Convert.ToInt32(mo["BatteryStatus"] ?? 0) == 2
                            };
                            if (mo["EstimatedChargeRemaining"] != null)
                            {
                                reading.Percent = Math.Clamp(Convert.ToInt32(mo["EstimatedChargeRemaining"]), 0, 100);
                                reading.HasPercent = true;
                            }
                            found.Add(reading);
                        }
                        catch { }
                    }
                }
                catch { }
                finally
                {
                    lock (BatterySync)
                    {
                        _batteries = found;
                        _batteryScanInFlight = false;
                    }
                }
            });
        }

        // ------------------------------------------------------------------
        // Focus timer
        // ------------------------------------------------------------------
        private void DrawFocusTimer(Color ink, Color muted, Color accent)
        {
            float k = ScalePercent / 100.0f;
            int remaining = Math.Max(0, _state.FocusTimerRemainingSec);
            string label = IslandTabsRenderer.FormatTimeSec(remaining);

            _renderer.DrawTextStr("FOCUS", new RectangleF(16, 10, Width - 32, 18), 9.0f * k, true, muted, mono: true);
            _renderer.DrawTextStr(label, new RectangleF(16, 28, Width - 32, 44), 28.0f * k, true, ink, mono: true, hAlign: StringAlignment.Center);

            var button = new RectangleF(Width * 0.5f - 30.0f * k, Height - 44.0f * k, 60.0f * k, 30.0f * k);
            _renderer.FillRoundedRect(button, 15.0f * k, accent);
            _renderer.DrawLucideIcon(_state.FocusTimerRunning ? "pause" : "play",
                button.X + button.Width * 0.5f, button.Y + button.Height * 0.5f, 12.0f * k,
                ThemeSpec.AdaptiveInkForBackground(accent));
            _renderer.AddHitRegion("focus-toggle", button, () =>
            {
                if (!_state.FocusTimerRunning && _state.FocusTimerRemainingSec <= 0) _state.FocusTimerRemainingSec = 25 * 60;
                _state.FocusTimerRunning = !_state.FocusTimerRunning;
                RenderNow();
            });

            var reset = new RectangleF(button.Right + 8, button.Y, 30.0f * k, button.Height);
            _renderer.FillRoundedRect(reset, 10.0f * k, Color.FromArgb(34, 255, 255, 255));
            _renderer.DrawTextStr("\u21BA", reset, 13.0f * k, true, muted, hAlign: StringAlignment.Center);
            _renderer.AddHitRegion("focus-reset", reset, () =>
            {
                _state.FocusTimerRemainingSec = 25 * 60;
                _state.FocusTimerRunning = false;
                RenderNow();
            });
        }

        // ------------------------------------------------------------------
        // PC stats
        // ------------------------------------------------------------------
        private void DrawPcStats(Color ink, Color muted, Color faint)
        {
            float k = ScalePercent / 100.0f;
            TelemetryHistory.Copy("cpu", _sparkCpu);
            TelemetryHistory.Copy("gpu", _sparkGpu);
            TelemetryHistory.Copy("ram", _sparkRam);
            TelemetryHistory.Copy("down", _sparkDown);
            TelemetryHistory.Copy("up", _sparkUp);

            HardwareTelemetry t = _state.Telemetry;
            float y = 12.0f * k;
            SparkRow("CPU", t.CpuPercent, _sparkCpu, Color.FromArgb(255, 120, 190, 255), ink, muted, y, 100.0f); y += 32.0f * k;
            if (t.GpuPercentAvailable)
            {
                SparkRow("GPU", t.GpuPercent, _sparkGpu, Color.FromArgb(255, 150, 230, 170), ink, muted, y, 100.0f);
            }
            else
            {
                _renderer.DrawTextStr("GPU", new RectangleF(16, y, 34, 16), 8.5f * k, true, muted, mono: true);
                _renderer.DrawTextStr("\u2014", new RectangleF(54, y, Width - 110, 16), 8.5f * k, false, faint, mono: true, hAlign: StringAlignment.Far);
            }
            y += 32.0f * k;
            SparkRow("RAM", t.RamPercent, _sparkRam, Color.FromArgb(255, 220, 180, 255), ink, muted, y, 100.0f);

            float netY = Height - 26.0f * k;
            _renderer.DrawLine(16, netY - 8.0f * k, Width - 16, netY - 8.0f * k, Color.FromArgb(32, ink.R, ink.G, ink.B), 1.0f);
            _renderer.DrawTextStr($"\u2193 {FormatRate(t.NetDownKbps)}", new RectangleF(16, netY, Width * 0.5f - 20, 16), 8.5f * k, false, muted, mono: true);
            _renderer.DrawTextStr($"\u2191 {FormatRate(t.NetUpKbps)}", new RectangleF(Width * 0.5f, netY, Width * 0.5f - 16, 16), 8.5f * k, false, muted, mono: true, hAlign: StringAlignment.Far);

            if (t.CpuTempAvailable)
            {
                _renderer.DrawTextStr($"{t.CpuTempC:0}\u00B0C", new RectangleF(Width - 70, 12, 54, 16), 8.0f * k, false, faint, mono: true, hAlign: StringAlignment.Far);
            }
        }

        private void SparkRow(string label, float value, float[] data, Color line, Color ink, Color muted, float y, float max)
        {
            float k = ScalePercent / 100.0f;
            _renderer.DrawTextStr(label, new RectangleF(16, y, 34, 16), 8.5f * k, true, muted, mono: true);
            _renderer.DrawTextStr($"{(int)Math.Round(value)}%", new RectangleF(52, y, 40, 16), 8.5f * k, true, ink, mono: true);
            DrawSparkline(new RectangleF(96, y - 3.0f * k, Width - 112.0f, 20.0f * k), data, line, max);
        }

        private void DrawSparkline(RectangleF r, float[] data, Color color, float max)
        {
            if (r.Width < 10 || r.Height < 4) return;
            Graphics g = _renderer.G;
            int n = TelemetryHistory.Count;
            if (n < 2) return;

            using var path = new GraphicsPath();
            float stepX = r.Width / Math.Max(1, n - 1);
            for (int i = 0; i < n; ++i)
            {
                float v = Math.Clamp(data[i], 0.0f, max) / max;
                float x = r.X + i * stepX;
                float y = r.Bottom - v * r.Height;
                if (i == 0) path.AddLine(x, y, x, y);
                else path.AddLine(r.X + (i - 1) * stepX, r.Bottom - Math.Clamp(data[i - 1], 0.0f, max) / max * r.Height, x, y);
            }
            using var pen = new Pen(color, 1.3f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(pen, path);
        }

        private static string FormatRate(float kbps)
        {
            if (kbps >= 1024.0f) return $"{kbps / 1024.0f:0.0} MB/s";
            if (kbps <= 0.01f) return "0 KB/s";
            return $"{kbps:0} KB/s";
        }

        // ------------------------------------------------------------------
        // Stocks
        // ------------------------------------------------------------------
        private void DrawStocks(Color ink, Color muted, Color faint, Color accent)
        {
            float k = ScalePercent / 100.0f;
            _renderer.DrawTextStr("MARKETS", new RectangleF(16, 9, Width - 32, 18), 9.0f * k, true, muted, mono: true);
            _renderer.DrawTextStr(StockQuotes.IsStale ? "..." : "LIVE",
                new RectangleF(Width - 60, 9, 44, 18), 8.0f * k, false,
                StockQuotes.IsStale ? faint : accent, mono: true, hAlign: StringAlignment.Far);

            var symbols = _state.Settings.StockSymbols;
            float y = 32.0f * k;
            int shown = Math.Min(3, symbols?.Count ?? 0);
            for (int i = 0; i < shown; ++i)
            {
                string symbol = symbols[i];
                StockQuotes.TryGet(symbol, out StockQuote quote);
                string price = quote != null ? StockQuotes.FormatPrice(quote) : "\u2014";
                string change = quote != null && quote.Valid
                    ? (quote.ChangePercent >= 0 ? "+" : "") + quote.ChangePercent.ToString("0.00") + "%"
                    : "";
                Color changeColor = quote != null && quote.Valid && quote.ChangePercent < 0
                    ? Color.FromArgb(255, 255, 105, 97)
                    : Color.FromArgb(255, 120, 220, 150);

                _renderer.DrawTextStr(symbol.ToUpperInvariant(), new RectangleF(16, y, 70, 18), 9.5f * k, true, ink, mono: true);
                _renderer.DrawTextStr(price, new RectangleF(Width - 130, y, 72, 18), 9.5f * k, true, ink, mono: true, hAlign: StringAlignment.Far);
                if (change.Length > 0)
                    _renderer.DrawTextStr(change, new RectangleF(Width - 56, y, 40, 18), 8.5f * k, false, changeColor, mono: true, hAlign: StringAlignment.Far);
                y += 30.0f * k;
            }

            if (shown == 0)
            {
                _renderer.DrawTextStr("Add symbols in Settings", new RectangleF(16, 56, Width - 32, 20), 9.0f * k, false, muted, hAlign: StringAlignment.Center);
            }

            _renderer.AddHitRegion("stocks-refresh", new RectangleF(2, 2, Width - 4, Height - 4), () =>
            {
                StockQuotes.Refresh(_state.Settings.StockSymbols);
                RenderNow();
            });
        }

        private void UpdateStockWidget()
        {
            StockQuotes.Refresh(_state.Settings.StockSymbols);
        }

        // ------------------------------------------------------------------
        // Script tile
        // ------------------------------------------------------------------
        private void DrawCustomTile(Color ink, Color muted, Color faint, Color accent)
        {
            float k = ScalePercent / 100.0f;
            if (_state.CustomTiles.Count == 0)
            {
                _renderer.DrawTextStr("\u25A2", new RectangleF(16, 18, Width - 32, 34), 24.0f * k, false, faint, hAlign: StringAlignment.Center);
                _renderer.DrawTextStr("Local tile", new RectangleF(16, 60, Width - 32, 18), 10.0f * k, true, ink, hAlign: StringAlignment.Center);
                _renderer.DrawTextStr("POST /tile to 127.0.0.1:47823", new RectangleF(16, 80, Width - 32, 18), 8.0f * k, false, muted, hAlign: StringAlignment.Center);
                return;
            }

            CustomTileData tile = _state.CustomTiles[0];
            _renderer.FillCircle(24, 24, 4.0f * k, accent);
            _renderer.DrawTextStr(tile.Title.ToUpperInvariant(), new RectangleF(36, 14, Width - 56, 20), 9.0f * k, true, muted, mono: true);
            _renderer.DrawTextStr(tile.Value, new RectangleF(16, 38, Width - 32, 42), 26.0f * k, true, ink, mono: true);
            if (!string.IsNullOrEmpty(tile.Subtitle))
                _renderer.DrawTextStr(tile.Subtitle, new RectangleF(16, Height - 28.0f * k, Width - 32, 18), 8.5f * k, false, faint);

            _renderer.AddHitRegion("tile-cycle", new RectangleF(2, 2, Width - 4, Height - 4), () =>
            {
                if (_state.CustomTiles.Count > 1)
                {
                    lock (_state.SyncRoot)
                    {
                        CustomTileData first = _state.CustomTiles[0];
                        _state.CustomTiles.RemoveAt(0);
                        _state.CustomTiles.Add(first);
                    }
                }
                RenderNow();
            });
        }

        private static GraphicsPath RoundedPath(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Max(2.0f, radius * 2.0f);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ------------------------------------------------------------------
        // Interaction: drag anywhere, controls first
        // ------------------------------------------------------------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                ShowWidgetContextMenu();
                return;
            }

            if (e.Button == MouseButtons.Left)
            {
                try
                {
                    if (_renderer.DispatchClick(e.X, e.Y)) return;
                }
                catch { }

                if (_state.Settings.LockDesktopWidgets) return;
                Point cursor = Cursor.Position;
                _dragOffsetX = cursor.X - _screenX;
                _dragOffsetY = cursor.Y - _screenY;
                _isDragging = true;
                Capture = true;
                ScreenSnapGuideForm.ShowFor(Screen.FromPoint(cursor).Bounds, false, false);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverX = e.X;
            _hoverY = e.Y;

            if (_isDragging && !_state.Settings.LockDesktopWidgets)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    _isDragging = false;
                    Capture = false;
                    ScreenSnapGuideForm.HideGuide();
                    return;
                }

                // Zero-lag native move + smooth center-snap with '+' crosshair guide
                Point cursor = Cursor.Position;
                int nx = cursor.X - _dragOffsetX;
                int ny = cursor.Y - _dragOffsetY;

                Rectangle mon = Screen.FromPoint(cursor).Bounds;
                int centerX = mon.Left + (mon.Width - Width) / 2;
                int centerY = mon.Top + (mon.Height - Height) / 2;
                const int snapThreshold = 14;
                bool snapX = Math.Abs(nx - centerX) <= snapThreshold;
                bool snapY = Math.Abs(ny - centerY) <= snapThreshold;
                if (snapX) nx = centerX;
                if (snapY) ny = centerY;

                ScreenSnapGuideForm.ShowFor(mon, snapX, snapY);

                if (nx == _screenX && ny == _screenY) return;
                _screenX = nx;
                _screenY = ny;
                PositionWindow(); // Native SetWindowPos only — zero per-pixel GDI repaint lag!
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && _isDragging)
            {
                _isDragging = false;
                Capture = false;
                ScreenSnapGuideForm.HideGuide();
                // Refresh glass backdrop once on drop and persist position
                RenderNow();
                AuxiliaryWindows.NotifyWidgetLayoutChanged(_state, this);
                ConfigManager.SaveAtomic(_state);
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_state.Settings.LockDesktopWidgets) return;
            ApplyScale(ScalePercent + (e.Delta > 0 ? 10 : -10));
        }

        private void ShowWidgetContextMenu()
        {
            var menu = new ContextMenuStrip();

            var lockItem = new ToolStripMenuItem("Lock widgets in place") { Checked = _state.Settings.LockDesktopWidgets };
            lockItem.Click += (_, _) =>
            {
                _state.Settings.LockDesktopWidgets = !_state.Settings.LockDesktopWidgets;
                ConfigManager.SaveAtomic(_state);
                RenderNow();
            };
            menu.Items.Add(lockItem);

            var scale = new ToolStripMenuItem($"Scale / Size ({ScalePercent}%)");
            foreach (int pct in new[] { 75, 100, 125, 150 })
            {
                var item = new ToolStripMenuItem($"{pct}%") { Checked = ScalePercent == pct };
                item.Click += (_, _) => ApplyScale(pct);
                scale.DropDownItems.Add(item);
            }
            menu.Items.Add(scale);
            menu.Items.Add(new ToolStripSeparator());

            var theme = new ToolStripMenuItem("Theme / Colour");
            theme.DropDownItems.Add("Liquid Glass (samples wallpaper)", null, (_, _) => { StyleMode = 0; RenderNow(); });
            theme.DropDownItems.Add("Dark surface", null, (_, _) => { StyleMode = 1; RenderNow(); });
            theme.DropDownItems.Add("Light surface", null, (_, _) => { StyleMode = 2; RenderNow(); });
            theme.DropDownItems.Add("Text only (no background)", null, (_, _) => { StyleMode = 3; RenderNow(); });
            theme.DropDownItems.Add(new ToolStripSeparator());
            var colour = new ToolStripMenuItem("Choose colour...");
            colour.Click += (_, _) =>
            {
                using var cd = new ColorDialog { Color = CustomAccent, FullOpen = true };
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    CustomAccent = cd.Color;
                    _state.Settings.DayClockAccentColorArgb = cd.Color.ToArgb();
                    ConfigManager.SaveAtomic(_state);
                    RenderNow();
                }
            };
            theme.DropDownItems.Add(colour);
            menu.Items.Add(theme);

            if (WidgetType == WidgetType.DayClock)
            {
                menu.Items.Add(new ToolStripSeparator());
                var s = _state.Settings;
                void Persist(Func<bool> get, Action<bool> set, string label)
                {
                    var item = new ToolStripMenuItem(label) { Checked = get() };
                    item.Click += (_, _) =>
                    {
                        set(!get());
                        item.Checked = get();
                        ConfigManager.SaveAtomic(_state);
                        RenderNow();
                    };
                    menu.Items.Add(item);
                }
                Persist(() => s.DayClockShowWeekday, v => s.DayClockShowWeekday = v, "Show day name");
                Persist(() => s.DayClockShowDate, v => s.DayClockShowDate = v, "Show date");
                Persist(() => s.DayClockShowTime, v => s.DayClockShowTime = v, "Show time");
                Persist(() => s.DayClockShowDashes, v => s.DayClockShowDashes = v, "Dashes around the time");
                Persist(() => s.DayClockShowNowPlaying, v => s.DayClockShowNowPlaying = v, "Show now playing");
                Persist(() => s.DayClockShowWeather, v => s.DayClockShowWeather = v, "Show weather");
                Persist(() => s.DayClockShowRecycleBin, v => s.DayClockShowRecycleBin = v, "Show Recycle Bin");

                var tracking = new ToolStripMenuItem($"Letter spacing: {s.DayClockLetterSpacing} px");
                var wider = new ToolStripMenuItem("Wider tracking");
                wider.Click += (_, _) =>
                {
                    s.DayClockLetterSpacing = Math.Min(22, s.DayClockLetterSpacing + 2);
                    ConfigManager.SaveAtomic(_state);
                    RenderNow();
                    tracking.Text = $"Letter spacing: {s.DayClockLetterSpacing} px";
                };
                var tighter = new ToolStripMenuItem("Tighter tracking");
                tighter.Click += (_, _) =>
                {
                    s.DayClockLetterSpacing = Math.Max(0, s.DayClockLetterSpacing - 2);
                    ConfigManager.SaveAtomic(_state);
                    RenderNow();
                    tracking.Text = $"Letter spacing: {s.DayClockLetterSpacing} px";
                };
                tracking.DropDownItems.Add(wider);
                tracking.DropDownItems.Add(tighter);
                menu.Items.Add(tracking);
            }

            if (WidgetType == WidgetType.TodoList)
            {
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("Edit tasks...", null, (_, _) => OpenTodoWindow());
            }

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Unpin from desktop", null, (_, _) => AuxiliaryWindows.ToggleDesktopWidget(_state, WidgetType));
            menu.Show(Cursor.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Close() destroys the handle first; reading Handle here would
                // recreate it on a disposed form.
                try { if (IsHandleCreated) DesktopLayer.Detach(Handle); } catch { }
                _renderer.Dispose();
                _photo?.Dispose();
                _photo = null;
            }
            base.Dispose(disposing);
        }
    }

    // ========================================================================
    // Full to-do list, opened by clicking a To-Do widget.
    // ========================================================================
    public sealed class TodoListWindow : Form
    {
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private readonly GlobalAppState _state;
        private readonly Action _onChanged;
        private readonly CheckedListBox _list;
        private readonly TextBox _entry;

        public TodoListWindow(GlobalAppState state, Action onChanged)
        {
            _state = state;
            _onChanged = onChanged;

            ThemeSpec theme = ThemeSpec.Get(_state.Settings.Theme);
            bool light = theme.IsLight;
            Color bgWin = light ? Color.FromArgb(245, 245, 248) : (theme.IsGlass ? Color.FromArgb(14, 15, 20) : theme.Fill);
            Color bgHdr = light ? Color.FromArgb(234, 235, 240) : Color.FromArgb(Math.Min(255, bgWin.R + 8), Math.Min(255, bgWin.G + 8), Math.Min(255, bgWin.B + 10));
            Color bgList = light ? Color.White : Color.FromArgb(Math.Min(255, bgWin.R + 12), Math.Min(255, bgWin.G + 12), Math.Min(255, bgWin.B + 16));
            Color fgMain = light ? Color.FromArgb(16, 18, 22) : Color.FromArgb(242, 242, 246);
            Color accentCol = _state.Settings.AccentColorArgb != 0
                ? Color.FromArgb(255, Color.FromArgb(_state.Settings.AccentColorArgb))
                : theme.Accent;

            Text = "To-Do";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = bgWin;
            ForeColor = fgMain;
            ClientSize = new Size(368, 438);
            TopMost = true;
            KeyPreview = true;
            Padding = new Padding(1);

            Paint += (_, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(48, 255, 255, 255), 1.2f);
                pe.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            };
            HandleCreated += (_, _) =>
            {
                try { Region = Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 22, 22)); } catch { }
            };

            // Draggable theme-matched header bar with Close button
            var header = new Panel { Left = 0, Top = 0, Width = Width, Height = 44, BackColor = bgHdr };
            header.MouseDown += DragBar;
            var title = new Label
            {
                Text = "To-Do",
                Left = 18,
                Top = 11,
                Width = 220,
                Height = 22,
                ForeColor = fgMain,
                Font = Rendering.UiFonts.Get(12.0f, true)
            };
            title.MouseDown += DragBar;
            header.Controls.Add(title);

            var btnClose = new Button
            {
                Text = "\u00D7",
                Left = Width - 38,
                Top = 8,
                Width = 28,
                Height = 28,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = fgMain,
                Font = Rendering.UiFonts.Get(13.0f, false),
                Cursor = Cursors.Hand
            };
            btnClose.FlatAppearance.BorderSize = 0;
            btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(210, 255, 69, 58);
            btnClose.Click += (_, _) => Close();
            header.Controls.Add(btnClose);
            Controls.Add(header);

            _list = new CheckedListBox
            {
                Left = 16,
                Top = 56,
                Width = 336,
                Height = 278,
                BackColor = bgList,
                ForeColor = fgMain,
                BorderStyle = BorderStyle.None,
                CheckOnClick = true,
                Font = Rendering.UiFonts.Get(10.5f, false)
            };
            _list.ItemCheck += (_, e) =>
            {
                if (e.Index < 0 || e.Index >= _state.TodoList.Count) return;
                _state.TodoList[e.Index].Done = e.NewValue == CheckState.Checked;
                ConfigManager.SaveAtomic(_state);
                BeginInvoke(new Action(() => _onChanged?.Invoke()));
            };
            Controls.Add(_list);

            _entry = new TextBox
            {
                Left = 16,
                Top = 346,
                Width = 252,
                BackColor = bgList,
                ForeColor = fgMain,
                BorderStyle = BorderStyle.FixedSingle,
                Font = Rendering.UiFonts.Get(10.5f, false),
                PlaceholderText = "Add a task..."
            };
            _entry.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    AddCurrent();
                    e.SuppressKeyPress = true;
                }
            };
            Controls.Add(_entry);

            var add = new Button
            {
                Text = "Add",
                Left = 276,
                Top = 345,
                Width = 76,
                Height = 28,
                BackColor = accentCol,
                ForeColor = ThemeSpec.AdaptiveInkForBackground(accentCol),
                FlatStyle = FlatStyle.Flat,
                Font = Rendering.UiFonts.Get(10.0f, true),
                Cursor = Cursors.Hand
            };
            add.FlatAppearance.BorderSize = 0;
            add.Click += (_, _) => AddCurrent();
            Controls.Add(add);

            var remove = new Button
            {
                Text = "Remove selected",
                Left = 16,
                Top = 388,
                Width = 152,
                Height = 30,
                BackColor = bgHdr,
                ForeColor = fgMain,
                FlatStyle = FlatStyle.Flat,
                Font = Rendering.UiFonts.Get(9.5f, false),
                Cursor = Cursors.Hand
            };
            remove.FlatAppearance.BorderSize = 0;
            remove.Click += (_, _) =>
            {
                int i = _list.SelectedIndex;
                if (i < 0 || i >= _state.TodoList.Count) return;
                _state.TodoList.RemoveAt(i);
                ConfigManager.SaveAtomic(_state);
                RefreshList();
                _onChanged?.Invoke();
            };
            Controls.Add(remove);

            RefreshList();
        }

        private void DragBar(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }

        private void AddCurrent()
        {
            string text = _entry.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            _state.TodoList.Add(new TodoItem { Text = text });
            _entry.Clear();
            ConfigManager.SaveAtomic(_state);
            RefreshList();
            _onChanged?.Invoke();
        }

        private void RefreshList()
        {
            _list.BeginUpdate();
            _list.Items.Clear();
            lock (_state.SyncRoot)
            {
                foreach (TodoItem item in _state.TodoList)
                    _list.Items.Add(item.Text, item.Done);
            }
            _list.EndUpdate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Close();
        }
    }

    // ========================================================================
    // Full-screen translucent '+' center-snap crosshair shown while dragging
    // any desktop widget so you can align smoothly to exact screen center.
    // ========================================================================
    internal sealed class ScreenSnapGuideForm : Form
    {
        private static ScreenSnapGuideForm _inst;
        private readonly LiquidGlassRenderer _renderer = new();
        private bool _snapX;
        private bool _snapY;

        private ScreenSnapGuideForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080 | 0x00000020 | 0x08000000; // LAYERED | TOOLWINDOW | TRANSPARENT | NOACTIVATE
                return cp;
            }
        }

        public static void ShowFor(Rectangle monitorBounds, bool snapX, bool snapY)
        {
            try
            {
                if (_inst == null || _inst.IsDisposed) _inst = new ScreenSnapGuideForm();
                if (_inst.Bounds != monitorBounds) _inst.Bounds = monitorBounds;
                bool changed = !_inst.Visible || _inst._snapX != snapX || _inst._snapY != snapY;
                _inst._snapX = snapX;
                _inst._snapY = snapY;
                if (!_inst.Visible) _inst.Show();
                if (changed) _inst.RenderGuide();
            }
            catch { }
        }

        public static void HideGuide()
        {
            try
            {
                if (_inst != null && !_inst.IsDisposed && _inst.Visible) _inst.Hide();
            }
            catch { }
        }

        private void RenderGuide()
        {
            if (!_renderer.BeginLayeredFrame(Width, Height, 1.0f)) return;
            float cx = Width * 0.5f;
            float cy = Height * 0.5f;
            Color vCol = _snapX ? Color.FromArgb(220, 43, 228, 234) : Color.FromArgb(65, 255, 255, 255);
            Color hCol = _snapY ? Color.FromArgb(220, 43, 228, 234) : Color.FromArgb(65, 255, 255, 255);
            _renderer.DrawLine(cx, 0, cx, Height, vCol, _snapX ? 1.6f : 1.0f);
            _renderer.DrawLine(0, cy, Width, cy, hCol, _snapY ? 1.6f : 1.0f);
            // Crisp '+' center marker
            Color plusCol = (_snapX || _snapY) ? Color.FromArgb(245, 43, 228, 234) : Color.FromArgb(140, 255, 255, 255);
            _renderer.DrawLine(cx - 10, cy, cx + 10, cy, plusCol, 2.0f);
            _renderer.DrawLine(cx, cy - 10, cx, cy + 10, plusCol, 2.0f);
            _renderer.EndLayeredFrame(Handle, Location, 255);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }
}
