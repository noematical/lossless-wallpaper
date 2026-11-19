// The app's design language, in one place.
//
//   Product    Puts an image on the wallpaper or lock screen without changing a pixel, and
//              proves it with a pixel check.
//   Character  Exact, plain-spoken, calm.
//   World      A proofing bench: a neutral surface, the proof in the middle, registration marks
//              at its corners.
//   Moment     The picture of the screen. It carries all the colour; everything around it is
//              neutral graphite or paper.
//   Signature  Crop marks at the corners of that picture, in the colour of the check result.
//
// Rules that follow from it: one grey ramp (three text roles, hairlines at about 8% of the text
// colour), no accent colour in the chrome (the main button is the inverse of the surface),
// colour only for a check result (green exact / passed, amber scaled / waiting, red failed),
// regular weight everywhere with hierarchy from size and colour, a mono face for figures, every
// distance on a 4 px grid, radius 8 for controls and 12 for cards.
// The process and several values come from "unslop" by Damir Sokolovsky (@heydamir), CC BY 4.0.
//
// Accessibility: the drawn controls (Controls.cs) are still the standard RadioButton / CheckBox /
// Button / ListView underneath, so keyboard behaviour and what screen readers hear are unchanged;
// only their painting is replaced. In high-contrast mode nothing here paints: the standard
// controls draw themselves with the user's system colours. Colour is never the only signal.
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
    static class Theme
    {
        public static bool Dark, HC;
        public static Color Bg, Card, Footer, Border, Outline, Text, Text2, Muted, Disabled, Accent, OnAccent,
            BtnFill, BtnHover, BtnDown, BtnEdge, Subtle, Selected, Well, Track, Segment, Good, Caution, Bad;
        static Icon appIcon;

        static Theme() { Refresh(); }

        // Reads the current Windows settings. Windows opened afterwards use them.
        public static void Refresh() { Set(ReadFlag("AppsUseLightTheme") == 0, SystemInformation.HighContrast); }

        static int ReadFlag(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue(name);
                    return v is int ? (int)v : 1;
                }
            }
            catch (Exception) { return 1; }
        }

        public static Color Rgb(int v) { return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(255, (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        static void Set(bool dark, bool hc)
        {
            HC = hc; Dark = dark && !hc;
            if (hc)
            {
                // The user's high-contrast colours, untouched.
                Bg = Card = Footer = BtnFill = BtnHover = BtnDown = Subtle = Well = Track = Segment = SystemColors.Control;
                Text = Text2 = Muted = Border = Outline = BtnEdge = Good = Caution = Bad = SystemColors.ControlText;
                Disabled = SystemColors.GrayText; Accent = Selected = SystemColors.Highlight; OnAccent = SystemColors.HighlightText;
            }
            else if (Dark)
            {
                Bg = Rgb(0x141415); Card = Rgb(0x19191B); Footer = Track = Well = Rgb(0x0F0F10);
                Segment = Selected = BtnFill = Rgb(0x232325); Subtle = BtnDown = Rgb(0x1E1E20); BtnHover = Rgb(0x2A2A2D);
                Text = Accent = Rgb(0xEDEDED); Text2 = Rgb(0xA1A1A6); Muted = Outline = Rgb(0x85858B); Disabled = Rgb(0x5C5C61);
                OnAccent = Rgb(0x141415);
                Good = Rgb(0x4CC38A); Caution = Rgb(0xE2A336); Bad = Rgb(0xE5484D);
                Border = Mix(Card, Text, 0.08); BtnEdge = Mix(BtnFill, Text, 0.10);
            }
            else
            {
                Bg = Rgb(0xFAFAFA); Card = Segment = BtnFill = Rgb(0xFFFFFF); Footer = Selected = BtnHover = Rgb(0xF4F4F5);
                Track = Well = Rgb(0xF0F0F1); Subtle = Rgb(0xF7F7F8); BtnDown = Rgb(0xEDEDEE);
                Text = Accent = Rgb(0x171717); Text2 = Rgb(0x525252); Muted = Outline = Rgb(0x737373); Disabled = Rgb(0xA3A3A3);
                OnAccent = Rgb(0xFFFFFF);
                Good = Rgb(0x16A34A); Caution = Rgb(0xB45309); Bad = Rgb(0xDC2626);
                Border = Mix(Card, Text, 0.10); BtnEdge = Mix(Card, Text, 0.15);
            }
            if (appIcon != null) { appIcon.Dispose(); appIcon = null; }
        }

        // ---------- motion ----------
        [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")]
        static extern bool SpiGetBool(int action, int param, out bool value, int winIni);

        // False when the user turned animations off in Windows (Accessibility > Visual effects).
        public static bool Animate
        {
            get
            {
                if (HC || SystemInformation.TerminalServerSession) return false;
                try { bool on; return SpiGetBool(0x1042, 0, out on, 0) ? on : true; }   // SPI_GETCLIENTAREAANIMATION
                catch (Exception) { return true; }
            }
        }

        // ---------- fonts ----------
        // One weight (regular) everywhere; hierarchy comes from size and colour. Body text is the
        // system message font (the user's choice). Titles use Windows 11's display cut of it when
        // it is there. Figures (pixel sizes, dates) use a mono face so they line up.
        public static Font Display(float points)
        {
            using (Font b = SystemFonts.MessageBoxFont)
            {
                if (b.FontFamily.Name == "Segoe UI" && !HC)
                {
                    Font f = Try("Segoe UI Variable Display", points);
                    if (f != null) return f;
                }
                return new Font(b.FontFamily, points, FontStyle.Regular);
            }
        }

        public static Font Mono(float points)
        {
            return Try("Cascadia Mono SemiLight", points) ?? Try("Consolas", points) ?? Try("Cascadia Mono", points) ?? new Font(FontFamily.GenericMonospace, points);
        }

        static Font Try(string family, float points)
        {
            try
            {
                var f = new Font(family, points);
                if (f.Name == family) return f;
                f.Dispose();
            }
            catch (Exception) { }
            return null;
        }

        // ---------- window frame ----------
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // Window colours, plus (Windows 11) a title bar in the same colour and rounded corners.
        // On Windows 10 the calls that don't exist fail quietly.
        public static void Chrome(Form f)
        {
            f.BackColor = Bg; f.ForeColor = Text;
            f.Icon = AppIcon();
            f.HandleCreated += delegate { Frame(f.Handle, true); };
        }

        static void Frame(IntPtr h, bool titleBar)
        {
            if (HC) return;
            try
            {
                int on = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);   // dark title bar
                int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);                                    // rounded corners
                if (titleBar)
                {
                    int cap = ColorRef(Bg); DwmSetWindowAttribute(h, 35, ref cap, 4);                         // title bar colour
                    int txt = ColorRef(Text); DwmSetWindowAttribute(h, 36, ref txt, 4);                       // title text colour
                    int edge = ColorRef(Dark ? Border : BtnEdge); DwmSetWindowAttribute(h, 34, ref edge, 4);  // window border
                }
            }
            catch (Exception) { }
        }

        // Dark scroll bars for a standard control in dark mode.
        public static void Scrollbars(Control c)
        {
            c.HandleCreated += delegate
            {
                if (!Dark) return;
                try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
            };
        }

        // Keeps a menu in the current colours each time it opens.
        public static void HookMenu(ContextMenuStrip m)
        {
            m.HandleCreated += delegate { Frame(m.Handle, false); };
            m.Opening += delegate { StyleMenu(m); };
            StyleMenu(m);
        }

        static void StyleMenu(ContextMenuStrip m)
        {
            if (HC) { m.RenderMode = ToolStripRenderMode.System; return; }
            if (!(m.Renderer is MenuRenderer)) m.Renderer = new MenuRenderer();
            m.BackColor = Card; m.ForeColor = Text;
            m.Padding = new Padding(0, Dpi.S(4), 0, Dpi.S(4));
            foreach (ToolStripItem it in m.Items)
                if (it is ToolStripMenuItem) it.Padding = new Padding(0, Dpi.S(4), 0, Dpi.S(4));
            if (m.IsHandleCreated) Frame(m.Handle, false);
        }

        // ---------- drawing helpers ----------
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = radius * 2;
            if (d <= 0 || d > r.Width || d > r.Height) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color fill, Color edge, RectangleF r, float radius)
        {
            using (var path = Round(r, radius))
            {
                if (fill != Color.Empty) using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (edge != Color.Empty) using (var p = new Pen(edge, 1f)) g.DrawPath(p, path);
            }
        }

        public static void PaintCard(Control c, Graphics g)
        {
            Color around = c.Parent != null ? c.Parent.BackColor : Bg;
            g.Clear(around);
            if (HC)
            {
                using (var p = new Pen(Border)) g.DrawRectangle(p, 0, 0, c.Width - 1, c.Height - 1);
                return;
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillRound(g, c.BackColor, Border, new RectangleF(0, 0, c.Width - 1, c.Height - 1), Dpi.S(12));
        }

        // The keyboard-focus mark: a strong outline around the control.
        public static void FocusRing(Graphics g, RectangleF r, float radius)
        {
            using (var pen = new Pen(Text, 2f)) using (var path = Round(r, radius)) g.DrawPath(pen, path);
        }

        // A status dot (the word next to it always says the same thing).
        public static void Dot(Graphics g, Color c, float x, float centreY)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int d = Dpi.S(8);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x, centreY - d / 2f, d, d);
            g.Restore(state);
        }

        // ---------- the mark: a screen with a tick ----------
        // Small sizes (tray, title bar): line drawing, light or dark lines to suit the surface.
        static Bitmap Glyph(int size, bool lightSurface)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.Transparent);
                float u = size / 16f;
                Color line = lightSurface ? Rgb(0x171717) : Rgb(0xEDEDED);
                Color tick = lightSurface ? Rgb(0x16A34A) : Rgb(0x4CC38A);
                using (var pen = new Pen(line, Math.Max(1f, 1.2f * u)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    using (var path = Round(new RectangleF(1.5f * u, 2.5f * u, 13f * u, 9f * u), 1.8f * u)) g.DrawPath(pen, path);
                    g.DrawLine(pen, 8f * u, 11.5f * u, 8f * u, 13.5f * u);
                    g.DrawLine(pen, 5.5f * u, 13.5f * u, 10.5f * u, 13.5f * u);
                }
                using (var pen = new Pen(tick, 1.7f * u))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(5f * u, 7f * u), new PointF(7.2f * u, 9.1f * u), new PointF(11.2f * u, 5f * u) });
                }
            }
            return bmp;
        }

        // Large sizes (taskbar, Alt+Tab, the exe file): the same mark on a flat graphite tile.
        static Bitmap Tile(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.Clear(Color.Transparent);
                float u = size / 64f;
                var tile = new RectangleF(2 * u, 2 * u, 60 * u, 60 * u);
                using (var path = Round(tile, 14 * u))
                {
                    using (var b = new SolidBrush(Rgb(0x19191B))) g.FillPath(b, path);
                    using (var p = new Pen(Rgb(0x2E2E30), Math.Max(1f, u))) g.DrawPath(p, path);
                }
                using (var pen = new Pen(Rgb(0xEDEDED), 3.2f * u))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    using (var path = Round(new RectangleF(12 * u, 15 * u, 40 * u, 27 * u), 5 * u)) g.DrawPath(pen, path);
                    g.DrawLine(pen, 32 * u, 42 * u, 32 * u, 48.5f * u);
                    g.DrawLine(pen, 24 * u, 48.5f * u, 40 * u, 48.5f * u);
                }
                using (var pen = new Pen(Rgb(0x4CC38A), 5.2f * u))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] { new PointF(23.5f * u, 28.5f * u), new PointF(29.5f * u, 34.5f * u), new PointF(41 * u, 22 * u) });
                }
            }
            return bmp;
        }

        static Icon FromBitmap(Bitmap bmp)
        {
            IntPtr h = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(h).Clone(); }
            finally { DestroyIcon(h); }
        }

        public static Icon TrayIcon()
        {
            bool light = HC ? SystemColors.Window.GetBrightness() > 0.5f : ReadFlag("SystemUsesLightTheme") != 0;
            using (var bmp = Glyph(Math.Max(16, SystemInformation.SmallIconSize.Width), light)) return FromBitmap(bmp);
        }

        // One icon holding every size: line drawings for the title bar, tiles for taskbar and Alt+Tab.
        static Icon AppIcon()
        {
            if (appIcon == null)
            {
                bool light = HC ? SystemColors.ActiveCaption.GetBrightness() > 0.5f : !Dark;
                using (var ms = new MemoryStream(IcoBytes(light))) appIcon = new Icon(ms);
            }
            return appIcon;
        }

        // One icon image in the classic format: header, 32-bit pixels bottom-up, empty mask.
        static byte[] Dib(Bitmap bmp)
        {
            int s = bmp.Width, maskRow = ((s + 31) / 32) * 4;
            using (var o = new MemoryStream())
            using (var w = new BinaryWriter(o))
            {
                w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32); w.Write(0);
                w.Write(s * s * 4 + maskRow * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[s * 4];
                    for (int y = s - 1; y >= 0; y--)
                    {
                        Marshal.Copy(new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, row.Length);
                        w.Write(row);
                    }
                }
                finally { bmp.UnlockBits(data); }
                w.Write(new byte[maskRow * s]);
                w.Flush();
                return o.ToArray();
            }
        }

        // The bytes of an .ico file. src\app.ico (the exe's icon, see build.cmd) is this, written
        // once with lightSurface = true.
        public static byte[] IcoBytes(bool lightSurface)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = s <= 24 ? Glyph(s, lightSurface) : Tile(s))
                {
                    if (s < 256) { images.Add(Dib(bmp)); continue; }
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                }
            using (var o = new MemoryStream())
            using (var w = new BinaryWriter(o))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
                w.Flush();
                return o.ToArray();
            }
        }
    }

    // The tray menu in the window colours.
    sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.BtnEdge; } }
        public override Color MenuItemBorder { get { return Theme.Selected; } }
        public override Color MenuItemSelected { get { return Theme.Selected; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Disabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(e.Graphics, Theme.Selected, Color.Empty, new RectangleF(Dpi.S(4), 1, e.Item.Width - Dpi.S(8) - 1, e.Item.Height - 3), Dpi.S(4));
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            float s = Math.Min(r.Width, r.Height), x = r.X + (r.Width - s) / 2f + Dpi.S(2), y = r.Y + (r.Height - s) / 2f;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Theme.Text2, Math.Max(1.5f, s / 10f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                e.Graphics.DrawLines(pen, new[] { new PointF(x + s * 0.20f, y + s * 0.52f), new PointF(x + s * 0.42f, y + s * 0.73f), new PointF(x + s * 0.82f, y + s * 0.28f) });
            }
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }
    }
}
