// Full-screen preview: the image exactly as it would be installed, shown 1:1 on the main screen.
// Hold Shift to see what Windows shows now (the current wallpaper, or the current lock screen);
// Esc, Enter, Space or a click closes it. It changes nothing: no wallpaper, lock screen or file.
// It runs in a short-lived worker process, like installs, so the tray app stays small.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LosslessWallpaper
{
    static class Preview
    {
        public static int Run(string src, FitMode fit, string target, string selftest, List<string> o)
        {
            Bitmap next = null, old = null;
            try
            {
                var s = Settings.Load();
                int W, H; Native.PrimaryScreenPixels(out W, out H);
                var img = Imaging.Decode(src, s.AlphaBackground);
                // An off-size image with no scaling chosen yet is shown cropped, as the pop-up's default.
                if ((img.Width != W || img.Height != H) && fit == FitMode.Exact) fit = FitMode.Crop;
                var fitted = Imaging.FitTo(img, W, H, fit, s.BarColor);
                next = Imaging.ToBitmap(fitted);
                string label;
                old = Current(target, W, H, s, out label);
                Theme.Refresh();
                using (var f = new PreviewForm(next, old, label, W, H, target))
                {
                    f.SelfTestDir = selftest;
                    Application.Run(f);
                    o.Add("ok=true");
                    o.AddRange(f.Results);
                }
                return 0;
            }
            catch (Exception ex)
            {
                if (selftest == null)
                    MessageBox.Show("Could not show the preview: " + ex.Message, "Lossless Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                throw;
            }
            finally
            {
                if (next != null) next.Dispose();
                if (old != null) old.Dispose();
            }
        }

        // What Windows shows now, at screen size. Returns null (with a label saying so) when it
        // can't be read. When the stored image is not screen-sized, Windows scales it with its own
        // filter, so the app's version of that is close but not exact; the label says "approximate".
        static Bitmap Current(string target, int W, int H, Settings s, out string label)
        {
            bool lockScreen = target == "lockscreen";
            string what = lockScreen ? "Current lock screen" : "Current wallpaper";
            try
            {
                RawImage cur;
                FitMode mode = FitMode.Crop;
                if (lockScreen)
                {
                    string tmp = Path.Combine(Path.GetTempPath(), "lw-lock-" + Guid.NewGuid().ToString("N") + ".img");
                    try
                    {
                        if (!Installer.SaveCurrentLockImage(tmp)) { label = what + " could not be read"; return null; }
                        cur = Imaging.Decode(tmp, Color.Black);
                    }
                    finally { try { File.Delete(tmp); } catch (Exception) { } }
                }
                else if (Installer.CurrentWallpaperPath().Length == 0)
                {
                    label = what + " (solid colour)";
                    var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(bmp)) g.Clear(DesktopColour());
                    return bmp;
                }
                else
                {
                    cur = Imaging.Decode(Installer.TranscodedPath, Color.Black);
                    string style = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallpaperStyle", "10") as string;
                    if (style == "6") mode = FitMode.Fit;
                }
                bool scaled = cur.Width != W || cur.Height != H;
                label = what + (scaled ? " (approximate: Windows scales it)" : "");
                return Imaging.ToBitmap(Imaging.FitTo(cur, W, H, mode, Color.Black));
            }
            catch (Exception) { label = what + " could not be read"; return null; }
        }

        static Color DesktopColour()
        {
            try
            {
                var v = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Colors", "Background", "0 0 0") as string;
                var p = (v ?? "0 0 0").Split(' ');
                return Color.FromArgb(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]));
            }
            catch (Exception) { return Color.Black; }
        }
    }

    sealed class PreviewForm : Form
    {
        readonly Bitmap next, old;
        readonly string oldLabel, newLabel;
        readonly Timer timer = new Timer { Interval = 100 };
        readonly Font font;
        bool showOld, cursorHidden;
        string tag;
        DateTime hintUntil, tagUntil;
        Point lastMouse;
        public string SelfTestDir;
        public readonly List<string> Results = new List<string>();

        public PreviewForm(Bitmap next, Bitmap old, string oldLabel, int W, int H, string target)
        {
            this.next = next; this.old = old; this.oldLabel = oldLabel;
            newLabel = "New " + (target == "lockscreen" ? "lock screen" : "wallpaper");
            Text = "Preview - Lossless Wallpaper";
            AccessibleName = "Full-screen preview";
            AccessibleDescription = "Shows the image as it would be installed. Hold Shift to see what you have now. Press Escape to close.";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(Screen.PrimaryScreen.Bounds.Location, new Size(W, H));
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.Black;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);
            font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.SizeInPoints * 1.15f);
            hintUntil = DateTime.Now.AddSeconds(3);
            timer.Tick += delegate { Tick(); };
            Shown += delegate { SetForegroundWindow(Handle); Activate(); timer.Start(); if (SelfTestDir != null) BeginInvoke((MethodInvoker)SelfTest); };
        }

        Bitmap Showing { get { return showOld && old != null ? old : next; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var b = Showing;
            if (showOld && old == null) g.Clear(Color.Black);
            else
            {
                // 1:1, no resampling: every screen pixel is the image's pixel.
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(b, new Rectangle(0, 0, b.Width, b.Height), 0, 0, b.Width, b.Height, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            if (SelfTestDir != null) return;
            DateTime now = DateTime.Now;
            if (now < hintUntil)
                Pill(g, (old == null ? oldLabel + "   ·   " : "Hold Shift to compare with what you have now   ·   ") + "Esc to close", false);
            if (tag != null && now < tagUntil) Pill(g, tag, true);
            else if (showOld && old == null) Pill(g, oldLabel, true);
        }

        // A small dark label; it fades away so it never stays over the pixels being judged.
        void Pill(Graphics g, string text, bool top)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Size t = TextRenderer.MeasureText(text, font);
            int padX = Dpi.S(16), padY = Dpi.S(8);
            var r = new Rectangle(0, 0, t.Width + padX * 2, t.Height + padY * 2);
            r.X = top ? Dpi.S(24) : (ClientSize.Width - r.Width) / 2;
            r.Y = top ? Dpi.S(24) : ClientSize.Height - r.Height - Dpi.S(48);
            Color fill = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(200, 20, 20, 21);
            Color ink = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(237, 237, 237);
            Theme.FillRound(g, fill, SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(40, 255, 255, 255), r, r.Height / 2f);
            TextRenderer.DrawText(g, text, font, r, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        bool overlayShown;
        void Tick()
        {
            DateTime now = DateTime.Now;
            bool want = now < hintUntil || (tag != null && now < tagUntil) || (showOld && old == null);
            if (overlayShown && !want) Invalidate();      // a label just expired: repaint without it
            overlayShown = want;
            if (!want && !cursorHidden && SelfTestDir == null) { Cursor.Hide(); cursorHidden = true; }
        }

        void Swap(bool toOld)
        {
            if (showOld == toOld) return;
            showOld = toOld;
            tag = toOld ? (old == null ? null : oldLabel) : newLabel;
            tagUntil = DateTime.Now.AddMilliseconds(1200);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey) Swap(true);
            else if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) Close();
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ShiftKey) Swap(false);
            base.OnKeyUp(e);
        }

        // Shift released while another window had focus: show the new image again.
        protected override void OnDeactivate(EventArgs e) { Swap(false); base.OnDeactivate(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (Math.Abs(e.X - lastMouse.X) + Math.Abs(e.Y - lastMouse.Y) > 4)
            {
                lastMouse = e.Location;
                if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
                bool was = DateTime.Now < hintUntil;
                hintUntil = DateTime.Now.AddSeconds(2.5);
                if (!was) Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e) { Close(); base.OnMouseClick(e); }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (cursorHidden) { Cursor.Show(); cursorHidden = false; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); font.Dispose(); }
            base.Dispose(disposing);
        }

        // ---------- test helper (--selftest <folder>) ----------
        // Captures what actually reached the screen, then sends Shift down and up through the
        // window's own key messages, capturing after each, and saves the expected images beside.
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        void SelfTest()
        {
            Directory.CreateDirectory(SelfTestDir);
            next.Save(Path.Combine(SelfTestDir, "expected-new.png"), ImageFormat.Png);
            if (old != null) old.Save(Path.Combine(SelfTestDir, "expected-old.png"), ImageFormat.Png);
            Results.Add("oldlabel=" + oldLabel);
            Shot("shown-new.png");
            SendMessage(Handle, 0x0100, (IntPtr)0x10, IntPtr.Zero);        // WM_KEYDOWN, Shift
            Shot("shown-old.png");
            SendMessage(Handle, 0x0101, (IntPtr)0x10, (IntPtr)unchecked((int)0xC0000001));   // WM_KEYUP
            Shot("shown-new-again.png");
            Results.Add("activewindow=" + (Form.ActiveForm == this));
            Close();
        }

        void Shot(string name)
        {
            Refresh();
            DateTime until = DateTime.Now.AddMilliseconds(600);
            while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
            using (var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(Left, Top, 0, 0, Size);
                bmp.Save(Path.Combine(SelfTestDir, name), ImageFormat.Png);
            }
            Results.Add("shot=" + name);
        }
    }
}
