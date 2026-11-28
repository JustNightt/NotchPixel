using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using OpenPixel.Core;

namespace OpenPixel.UI
{
    // ========================================================================
    // Floating Web Drawer (WebView2 / Edge engine) matching OpenPixel's
    // dark rounded vertical browser window directly below the top resting pill.
    // ========================================================================
    public sealed class WebViewWindow : Form
    {
        private static readonly Color Bg = Color.FromArgb(255, 11, 11, 12);
        private static readonly Color BarBg = Color.FromArgb(255, 11, 11, 12);
        private static readonly Color PillBg = Color.FromArgb(255, 24, 24, 27);
        private static readonly Color PillBorder = Color.FromArgb(255, 38, 38, 43);

        private readonly GlobalAppState _state;
        private readonly WebView2 _view = new();
        private readonly TextBox _urlBox = new();
        private readonly Panel _urlPill = new();
        private string _pendingUrl = "";
        private bool _ready;
        private bool _fallbackShown;

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public WebViewWindow(GlobalAppState state)
        {
            _state = state;
            Text = "OpenPixel Web";
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            MinimumSize = new Size(380, 420);
            Size = new Size(500, 640);
            Rectangle scr = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Top + 58);

            // Compact 40px dark chrome bar: Back | Pill URL Bar | Copy, Pop-out, Reload, Close
            var bar = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = BarBg, Padding = new Padding(8, 6, 8, 6) };
            bar.MouseDown += DragWindow;
            bar.MouseDoubleClick += (_, _) =>
            {
                WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            };

            var btnBack = MakeBarButton("\u2190", "Back", () =>
            {
                try { if (_ready && _view.CoreWebView2.CanGoBack) _view.CoreWebView2.GoBack(); } catch { }
            });
            btnBack.Dock = DockStyle.Left;

            var rightButtons = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                AutoSize = true,
                BackColor = Color.Transparent,
                Padding = new Padding(0)
            };
            rightButtons.Controls.Add(MakeBarButton("\u2715", "Close", Close));
            rightButtons.Controls.Add(MakeBarButton("\u21BB", "Reload", Reload));
            rightButtons.Controls.Add(MakeBarButton("\u2197", "Open in browser", OpenInBrowser));
            rightButtons.Controls.Add(MakeBarButton("\u29C9", "Copy link", CopyUrl));

            _urlPill.Dock = DockStyle.Fill;
            _urlPill.BackColor = Color.Transparent;
            _urlPill.Padding = new Padding(12, 4, 12, 4);
            _urlPill.Margin = new Padding(6, 0, 6, 0);
            _urlPill.Paint += (_, pe) =>
            {
                pe.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new RectangleF(1, 1, _urlPill.Width - 3, _urlPill.Height - 3);
                using var gp = RoundedRect(r, r.Height * 0.5f);
                using var br = new SolidBrush(PillBg);
                using var pen = new Pen(PillBorder, 1.0f);
                pe.Graphics.FillPath(br, gp);
                pe.Graphics.DrawPath(pen, gp);
            };
            _urlPill.MouseDown += DragWindow;

            _urlBox.Dock = DockStyle.Fill;
            _urlBox.BorderStyle = BorderStyle.None;
            _urlBox.BackColor = PillBg;
            _urlBox.ForeColor = Color.FromArgb(255, 226, 228, 234);
            _urlBox.Font = new Font("Segoe UI", 8.75f);
            _urlBox.KeyDown += (_, ke) =>
            {
                if (ke.KeyCode == Keys.Enter)
                {
                    ke.SuppressKeyPress = true;
                    string raw = _urlBox.Text.Trim();
                    if (!string.IsNullOrEmpty(raw))
                    {
                        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        {
                            raw = raw.Contains('.') && !raw.Contains(' ')
                                ? "https://" + raw
                                : "https://www.google.com/search?q=" + Uri.EscapeDataString(raw);
                        }
                        Navigate(raw);
                    }
                }
            };
            _urlPill.Controls.Add(_urlBox);

            // Hairline separator beneath chrome bar
            var sep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(255, 26, 26, 30) };

            bar.Controls.Add(_urlPill);
            bar.Controls.Add(rightButtons);
            bar.Controls.Add(btnBack);

            _view.Dock = DockStyle.Fill;
            _view.DefaultBackgroundColor = Bg;

            Controls.Add(_view);
            Controls.Add(sep);
            Controls.Add(bar);

            Resize += (_, _) => ApplyRoundedRegion();
            Load += async (_, _) => await InitAsync();
            FormClosed += (_, _) => _state.WebDrawerVisible = false;
        }

        private static GraphicsPath RoundedRect(RectangleF r, float rad)
        {
            float d = Math.Max(2f, Math.Min(rad * 2f, Math.Min(r.Width, r.Height)));
            var gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static Button MakeBarButton(string glyph, string tip, Action onClick)
        {
            var button = new Button
            {
                Text = glyph,
                Size = new Size(28, 28),
                Margin = new Padding(2, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.FromArgb(220, 214, 216, 224),
                BackColor = Color.Transparent,
                TabStop = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 255, 255, 255);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(58, 255, 255, 255);
            button.Font = new Font("Segoe UI", 9.5f);
            button.Click += (_, _) => onClick();
            new ToolTip().SetToolTip(button, tip);
            return button;
        }

        private void DragWindow(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            ReleaseCapture();
            SendMessage(Handle, 0xA1, new IntPtr(2), IntPtr.Zero);
        }

        private void ApplyRoundedRegion()
        {
            try
            {
                if (!IsHandleCreated) return;
                Region = WindowState == FormWindowState.Maximized
                    ? null
                    : Region.FromHrgn(CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 22, 22));
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                ApplyRoundedRegion();
                int preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
            }
            catch { }
        }

        public void Navigate(string url, string title = "")
        {
            _pendingUrl = string.IsNullOrWhiteSpace(url)
                ? (_state.PinnedSites.Count > 0 ? _state.PinnedSites[0].Url : "https://www.youtube.com")
                : url;

            if (!string.IsNullOrWhiteSpace(title)) Text = title;
            _urlBox.Text = _pendingUrl;

            if (_ready)
            {
                try { _view.CoreWebView2.Navigate(_pendingUrl); } catch { }
            }
        }

        public void Present(Point? location = null)
        {
            if (location.HasValue) Location = location.Value;
            if (!Visible) Show();
            Activate();
            BringToFront();
        }

        private async Task InitAsync()
        {
            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPixel", "WebView2");
            try
            {
                Directory.CreateDirectory(dataFolder);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
                await _view.EnsureCoreWebView2Async(environment);

                CoreWebView2 core = _view.CoreWebView2;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;
                core.Settings.IsZoomControlEnabled = true;
                core.NewWindowRequested += (_, args) =>
                {
                    args.Handled = true;
                    Navigate(args.Uri);
                };
                core.DocumentTitleChanged += (_, _) =>
                {
                    string title = core.DocumentTitle;
                    if (!string.IsNullOrWhiteSpace(title)) Text = title;
                };
                core.NavigationCompleted += (_, _) => _urlBox.Text = core.Source;

                _ready = true;
                if (_pendingUrl.Length > 0) core.Navigate(_pendingUrl);
            }
            catch
            {
                if (_fallbackShown) return;
                _fallbackShown = true;
                _urlBox.Text = "WebView2 runtime missing";
                OpenInBrowser();
            }
        }

        private void CopyUrl()
        {
            try
            {
                string url = string.IsNullOrWhiteSpace(_urlBox.Text) ? _pendingUrl : _urlBox.Text;
                if (!string.IsNullOrWhiteSpace(url)) Clipboard.SetText(url);
            }
            catch { }
        }

        private void Reload()
        {
            try
            {
                if (_ready) _view.CoreWebView2.Reload();
                else if (_pendingUrl.Length > 0) Navigate(_pendingUrl);
            }
            catch { }
        }

        private void OpenInBrowser()
        {
            string url = string.IsNullOrWhiteSpace(_pendingUrl) ? _urlBox.Text : _pendingUrl;
            try
            {
                if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
