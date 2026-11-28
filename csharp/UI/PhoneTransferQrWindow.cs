using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using OpenPixel.Core;
using OpenPixel.Rendering;
using OpenPixel.Services;

namespace OpenPixel.UI
{
    // ========================================================================
    // "Send to your phone" QR transfer popover card positioned beside the island.
    // Displays a white-backed QR code framed by 4 white corner brackets,
    // shelf & clipboard transfer instructions, and a full-width white Done pill.
    // ========================================================================
    public sealed class PhoneTransferQrWindow : Form
    {
        private static PhoneTransferQrWindow _active;

        private const float BaseW = 330.0f;
        private const float BaseH = 498.0f;

        private readonly GlobalAppState _state;
        private readonly LiquidGlassRenderer _renderer = new();
        private float _uiScale = 1.0f;
        private float _mouseX = -1000.0f;
        private float _mouseY = -1000.0f;
        private string _lastHoverId = "";
        private bool _showAppInstallQr = false; // Default: no-app direct file transfer QR

        public static void ShowFor(GlobalAppState state, Rectangle? islandScreenRect = null)
        {
            if (state == null) return;
            try
            {
                if (_active != null && !_active.IsDisposed)
                {
                    _active.PositionNear(islandScreenRect);
                    if (!_active.Visible) _active.Show();
                    _active.Activate();
                    _active.RenderNow();
                    return;
                }
                _active = new PhoneTransferQrWindow(state);
                _active.PositionNear(islandScreenRect);
                _active.Show();
                _active.Activate();
                _active.RenderNow();
            }
            catch { }
        }

        public static void HideActive()
        {
            try
            {
                if (_active != null && !_active.IsDisposed)
                {
                    _active.Hide();
                }
            }
            catch { }
        }

        private PhoneTransferQrWindow(GlobalAppState state)
        {
            _state = state;
            Text = "Send to your phone";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;

            float dpi = 1.0f;
            try
            {
                using var g = Graphics.FromHwnd(IntPtr.Zero);
                dpi = Math.Clamp(g.DpiX / 96.0f, 1.0f, 2.5f);
            }
            catch { }
            _uiScale = dpi;
            Size = new Size((int)MathF.Round(BaseW * _uiScale), (int)MathF.Round(BaseH * _uiScale));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        private void PositionNear(Rectangle? islandRect)
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1366, 768);
            int x, y;
            if (islandRect.HasValue)
            {
                x = islandRect.Value.Right + 14;
                if (x + Width > work.Right - 12) x = islandRect.Value.Left - Width - 14;
                if (x < work.Left + 12) x = work.Left + (work.Width - Width) / 2;
                y = Math.Max(work.Top + 12, islandRect.Value.Top + 6);
            }
            else
            {
                x = work.Left + (work.Width - Width) / 2 + 230;
                if (x + Width > work.Right - 12) x = work.Right - Width - 18;
                y = work.Top + 18;
            }
            Location = new Point(x, y);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                ClosePopover();
                e.Handled = true;
            }
        }

        private void ClosePopover()
        {
            lock (_state.SyncRoot) _state.SendToPhoneQrActive = false;
            Hide();
        }

        public void RenderNow()
        {
            if (IsDisposed || !IsHandleCreated || !Visible) return;
            lock (_state.SyncRoot)
            {
                if (!_renderer.BeginLayeredFrame(Width, Height, _uiScale)) return;
                _renderer.ClearHitRegions();

                float lw = Width / _uiScale;
                float lh = Height / _uiScale;
                ThemeSpec theme = ThemeSpec.Get(_state.Settings.Theme);
                bool light = theme.IsLight;

                Color bg = light ? Color.FromArgb(250, 246, 246, 244) : Color.FromArgb(250, 14, 14, 16);
                Color border = light ? Color.FromArgb(40, 12, 12, 14) : Color.FromArgb(42, 255, 255, 255);
                Color textPrimary = light ? Color.FromArgb(255, 16, 16, 18) : Color.FromArgb(255, 245, 245, 247);
                Color textMuted = light ? Color.FromArgb(255, 96, 96, 102) : Color.FromArgb(255, 148, 148, 154);

                var card = new RectangleF(0.5f, 0.5f, lw - 1.0f, lh - 1.0f);
                bool useGlass = _state.Settings.Theme == SurfaceTheme.LiquidGlass || _state.Settings.Theme == SurfaceTheme.CandyGlass || _state.Settings.Theme == SurfaceTheme.Acrylic;
                bool drewGlass = false;
                if (useGlass)
                {
                    using var gp = new GraphicsPath();
                    float d = 36.0f;
                    gp.AddArc(card.X, card.Y, d, d, 180, 90);
                    gp.AddArc(card.Right - d, card.Y, d, d, 270, 90);
                    gp.AddArc(card.Right - d, card.Bottom - d, d, d, 0, 90);
                    gp.AddArc(card.X, card.Bottom - d, d, d, 90, 90);
                    gp.CloseFigure();
                    Region prevC = _renderer.G.Clip;
                    _renderer.G.SetClip(gp, CombineMode.Replace);
                    // The glass shape has to use this card's own 18 DIP radius and no
                    // corner-concentrated specular: with the old auto radius every
                    // corner sat outside the lens shape and rendered as a dark smudge.
                    drewGlass = _renderer.RenderLiquidGlassBackdrop(Bounds, 0, 0, lw, lh,
                        _state.Settings.GlassBlurRadius, _state.Settings.Theme,
                        cornerRadiusPx: 18.0f * _uiScale, cornerSheen: 0.0f);
                    if (drewGlass)
                        _renderer.FillRoundedRect(card, 18.0f, Color.FromArgb(132, 12, 12, 16));
                    _renderer.G.Clip = prevC;
                }
                if (!drewGlass) _renderer.FillRoundedRect(card, 18.0f, bg);
                _renderer.DrawRoundedRect(card, 18.0f, border, 1.0f);

                // Header: "Send to your phone" + circular X close button
                _renderer.DrawTextStr("Send to your phone", new RectangleF(20, 16, lw - 64, 24), 12.2f, true, textPrimary);
                var closeCircle = new RectangleF(lw - 42, 14, 26, 26);
                bool closeHov = _renderer.IsHovered(closeCircle, _mouseX, _mouseY);
                _renderer.FillCircle(closeCircle.X + 13, closeCircle.Y + 13, 13.0f,
                    closeHov ? Color.FromArgb(52, 255, 255, 255) : Color.FromArgb(24, 255, 255, 255));
                using (var pen = new Pen(textPrimary, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    float cx = closeCircle.X + 13, cy = closeCircle.Y + 13;
                    _renderer.G.DrawLine(pen, cx - 4, cy - 4, cx + 4, cy + 4);
                    _renderer.G.DrawLine(pen, cx + 4, cy - 4, cx - 4, cy + 4);
                }
                _renderer.AddHitRegion("qr-close", closeCircle, ClosePopover);

                // Mode toggle: [ Files (No App) ] | [ Android App ]
                var modeTrack = new RectangleF(20, 48, lw - 40, 28);
                _renderer.FillRoundedRect(modeTrack, 14.0f, light ? Color.FromArgb(20, 12, 12, 14) : Color.FromArgb(24, 255, 255, 255));
                float halfW = (modeTrack.Width - 4.0f) * 0.5f;
                var filesTab = new RectangleF(modeTrack.X + 2.0f, modeTrack.Y + 2.0f, halfW, 24.0f);
                var appTab = new RectangleF(filesTab.Right, modeTrack.Y + 2.0f, halfW, 24.0f);
                Color activePill = light ? Color.FromArgb(255, 20, 20, 24) : Color.FromArgb(255, 242, 242, 246);
                Color activeInk = light ? Color.White : Color.FromArgb(255, 16, 16, 18);
                _renderer.FillRoundedRect(_showAppInstallQr ? appTab : filesTab, 12.0f, activePill);
                _renderer.DrawTextStr("Files (No App)", filesTab, 9.2f, !_showAppInstallQr, !_showAppInstallQr ? activeInk : textMuted, hAlign: StringAlignment.Center);
                _renderer.DrawTextStr("Android App", appTab, 9.2f, _showAppInstallQr, _showAppInstallQr ? activeInk : textMuted, hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("qr-mode-files", filesTab, () => { _showAppInstallQr = false; RenderNow(); });
                _renderer.AddHitRegion("qr-mode-app", appTab, () => { _showAppInstallQr = true; RenderNow(); });

                // White-backed QR card framed by 4 crisp corner brackets
                float qrCardSize = 180.0f;
                float qrCardX = (lw - qrCardSize) * 0.5f;
                float qrCardY = 90.0f;
                var qrCard = new RectangleF(qrCardX, qrCardY, qrCardSize, qrCardSize);
                _renderer.FillRoundedRect(qrCard, 16.0f, Color.White);

                string shelfUrl = LocalNetworkServers.BuildShelfShareUrl(_state);
                string qrPayload = _showAppInstallQr ? LocalNetworkServers.AndroidAppDownloadUrl : shelfUrl;
                _renderer.DrawQrCode(qrPayload,
                    qrCard.X + 14, qrCard.Y + 14, qrCardSize - 28, Color.Black, Color.White);

                // 4 crisp corner brackets framing the QR card
                DrawCornerBrackets(new RectangleF(qrCard.X - 6, qrCard.Y - 6, qrCard.Width + 12, qrCard.Height + 12), 16.0f,
                    light ? Color.FromArgb(255, 24, 24, 28) : Color.White);

                // Bold headline + helper copy
                float ty = qrCard.Bottom + 16.0f;
                string headline = _showAppInstallQr
                    ? "Scan with your phone's camera to install the OpenPixel app."
                    : "Scan with your phone's camera to download PC files (no app needed).";
                _renderer.DrawTextStr(headline, new RectangleF(20, ty, lw - 40, 36), 11.2f, true, textPrimary, noWrap: false);
                ty += 38.0f;

                if (!_showAppInstallQr)
                {
                    _renderer.DrawTextStr(shelfUrl, new RectangleF(20, ty, lw - 40, 16), 8.6f, false, textMuted, mono: true);
                    ty += 20.0f;
                }

                string shelfLine = _state.ShelfFiles.Count == 0
                    ? "No files selected yet. Pick files below or drop them on the island."
                    : $"{_state.ShelfFiles.Count} file{(_state.ShelfFiles.Count == 1 ? "" : "s")} ready on this PC for your phone to download.";
                _renderer.DrawTextStr(shelfLine, new RectangleF(20, ty, lw - 40, 30), 9.2f, false, textMuted, noWrap: false);
                ty += 32.0f;

                string clipToggle = _state.SendToPhoneIncludeClipboard
                    ? "Copied text is shared on the phone page too. Turn off"
                    : "Copied text sharing is off. Turn on";
                var clipToggleRect = new RectangleF(20, ty, lw - 40, 26);
                _renderer.DrawTextStr(clipToggle, clipToggleRect, 9.0f, true, textPrimary, noWrap: false);
                _renderer.AddHitRegion("qr-clip-toggle", clipToggleRect, () =>
                {
                    _state.SendToPhoneIncludeClipboard = !_state.SendToPhoneIncludeClipboard;
                    RenderNow();
                });

                // "+ Pick files from PC" button above Done
                var pickRect = new RectangleF(20, lh - 92, lw - 40, 32);
                bool pickHov = _renderer.IsHovered(pickRect, _mouseX, _mouseY);
                _renderer.FillRoundedRect(pickRect, 16.0f,
                    light ? Color.FromArgb(pickHov ? 36 : 22, 12, 12, 14) : Color.FromArgb(pickHov ? 46 : 28, 255, 255, 255));
                _renderer.DrawTextStr("+ Pick files from PC", pickRect, 10.0f, true, textPrimary, hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("qr-pick-files", pickRect, PickFilesFromPc);

                // Full-width white pill "Done" button at bottom
                var doneRect = new RectangleF(20, lh - 50, lw - 40, 34);
                bool doneHov = _renderer.IsHovered(doneRect, _mouseX, _mouseY);
                _renderer.FillRoundedRect(doneRect, 17.0f, doneHov ? Color.White : Color.FromArgb(255, 237, 237, 237));
                _renderer.DrawTextStr("Done", doneRect, 10.6f, true, Color.FromArgb(255, 16, 16, 18), hAlign: StringAlignment.Center);
                _renderer.AddHitRegion("qr-done", doneRect, ClosePopover);

                _lastHoverId = _renderer.HoverId(_mouseX, _mouseY);
                _renderer.EndLayeredFrame(Handle, Location, 252);
            }
        }

        private void PickFilesFromPc()
        {
            try
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Pick files to share via QR code",
                    Multiselect = true,
                    CheckFileExists = true
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                lock (_state.SyncRoot)
                {
                    foreach (string fullPath in dlg.FileNames)
                    {
                        if (string.IsNullOrWhiteSpace(fullPath) || !System.IO.File.Exists(fullPath)) continue;
                        if (_state.ShelfFiles.Exists(f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        string ext = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
                        long size = 0;
                        try { size = new System.IO.FileInfo(fullPath).Length; } catch { }
                        _state.ShelfFiles.Insert(0, new ShelfFileItem
                        {
                            FullPath = fullPath,
                            DisplayName = System.IO.Path.GetFileName(fullPath),
                            Extension = ext,
                            FileSize = size,
                            IsImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"
                        });
                        if (_state.ShelfFiles.Count > 12) _state.ShelfFiles.RemoveAt(_state.ShelfFiles.Count - 1);
                    }
                }
                ConfigManager.FlushPersistentCollectionsIfChanged(_state);
                RenderNow();
            }
            catch { }
        }

        private void DrawCornerBrackets(RectangleF r, float len, Color color)
        {
            using var pen = new Pen(color, 2.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            Graphics g = _renderer.G;
            // Top-left
            g.DrawLine(pen, r.Left, r.Top + len, r.Left, r.Top);
            g.DrawLine(pen, r.Left, r.Top, r.Left + len, r.Top);
            // Top-right
            g.DrawLine(pen, r.Right - len, r.Top, r.Right, r.Top);
            g.DrawLine(pen, r.Right, r.Top, r.Right, r.Top + len);
            // Bottom-left
            g.DrawLine(pen, r.Left, r.Bottom - len, r.Left, r.Bottom);
            g.DrawLine(pen, r.Left, r.Bottom, r.Left + len, r.Bottom);
            // Bottom-right
            g.DrawLine(pen, r.Right - len, r.Bottom, r.Right, r.Bottom);
            g.DrawLine(pen, r.Right, r.Bottom, r.Right, r.Bottom - len);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _mouseX = e.X / _uiScale;
            _mouseY = e.Y / _uiScale;
            if (_renderer.DispatchClick(_mouseX, _mouseY)) RenderNow();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _mouseX = e.X / _uiScale;
            _mouseY = e.Y / _uiScale;
            // Hover is the only pointer-driven state on this card, so a move only
            // needs a frame when it enters or leaves a control - not on every
            // WM_MOUSEMOVE, each of which used to re-blur the whole glass card.
            if (_renderer.HoverId(_mouseX, _mouseY) != _lastHoverId) RenderNow();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_lastHoverId.Length == 0) return;
            _mouseX = -1000.0f;
            _mouseY = -1000.0f;
            RenderNow();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _renderer.Dispose();
            base.Dispose(disposing);
        }
    }
}
