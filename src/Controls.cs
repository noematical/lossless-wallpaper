// The drawn controls of the design language in Theme.cs. Each one is the standard Windows
// control underneath (same keyboard behaviour, same accessible name, role and state) with its
// painting replaced; in high-contrast mode the standard painting is used instead.
// Sizes sit on a 4 px grid; controls have radius 8, their containers 12 (8 + the 4 px between).
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LosslessWallpaper
{
    // One choice in a segmented row (a radio button drawn as a segment). The parent is a SegTrack.
    sealed class SegRadio : RadioButton
    {
        bool hot;
        public SegRadio() { AutoSize = false; Margin = Padding.Empty; TextAlign = ContentAlignment.MiddleCenter; }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Theme.HC) { base.OnPaint(e); return; }
            var g = e.Graphics;
            Color track = Parent != null ? Parent.BackColor : Theme.Track;
            g.Clear(track);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, Width - 1, Height - 1);
            // The chosen segment is one surface step up with a hairline; nothing else changes.
            if (Checked) Theme.FillRound(g, Enabled ? Theme.Segment : Theme.Mix(Theme.Segment, track, 0.5), Theme.BtnEdge, r, Dpi.S(8));
            else if (hot && Enabled) Theme.FillRound(g, Theme.Mix(track, Theme.Segment, 0.45), Color.Empty, r, Dpi.S(8));
            Color fore = !Enabled ? Theme.Disabled : (Checked || hot ? Theme.Text : Theme.Text2);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, flags);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), Dpi.S(8));
        }
    }

    // The rounded track that holds the segments.
    sealed class SegTrack : Panel
    {
        public SegTrack()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Track;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            if (Theme.HC) { using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1); return; }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(g, BackColor, Theme.Dark ? Theme.Border : Color.Empty, new RectangleF(0, 0, Width - 1, Height - 1), Dpi.S(12));
        }

        // Lays the segments out side by side at equal widths.
        public void Arrange()
        {
            int n = Controls.Count, pad = Dpi.S(4);
            if (n == 0) return;
            int w = (Width - pad * 2) / n;
            for (int i = 0; i < n; i++) Controls[i].SetBounds(pad + i * w, pad, i == n - 1 ? Width - pad * 2 - w * (n - 1) : w, Height - pad * 2);
        }
    }

    // A radio button drawn as a row with a small diagram: used for "crop" and "fit".
    // The text "Title (detail)" is shown on two lines; screen readers hear it whole.
    sealed class OptionCard : RadioButton
    {
        public bool Bars;      // diagram: true = fit with bars, false = crop to fill
        bool hot;
        public OptionCard() { AutoSize = false; Margin = Padding.Empty; }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Theme.HC) { base.OnPaint(e); return; }
            var g = e.Graphics;
            Color around = Parent != null ? Parent.BackColor : Theme.Bg;
            g.Clear(around);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = Enabled;
            // Chosen = one surface step up with a slightly stronger hairline. The radio mark says it too.
            Color fill = !en ? Theme.Mix(Theme.Card, around, 0.6) : (Checked ? Theme.Selected : (hot ? Theme.Subtle : Theme.Card));
            Theme.FillRound(g, fill, Checked && en ? Theme.BtnEdge : Theme.Border, new RectangleF(0, 0, Width - 1, Height - 1), Dpi.S(8));

            // Diagram: the screen (outline) and where the image lands (solid). Same stroke as every other mark.
            float u = Dpi.S(1), stroke = 1.5f * u, sx = 16 * u, sw = 40 * u, sh = 24 * u, sy = (Height - sh) / 2f;
            Color ink = en ? Theme.Muted : Theme.Disabled, solid = en ? Theme.Mix(fill, Theme.Text, 0.42) : Theme.Mix(fill, Theme.Text, 0.16);
            if (Bars)
            {
                using (var b = new SolidBrush(solid)) g.FillRectangle(b, sx + 10 * u, sy + 1, sw - 20 * u, sh - 2);
            }
            else
            {
                using (var b = new SolidBrush(solid)) g.FillRectangle(b, sx + 1, sy + 1, sw - 2, sh - 2);
                using (var pen = new Pen(ink, stroke))
                {
                    pen.DashStyle = DashStyle.Dot;
                    g.DrawLine(pen, sx + 1, sy - 4 * u, sx + sw - 1, sy - 4 * u); g.DrawLine(pen, sx + 1, sy + sh + 4 * u, sx + sw - 1, sy + sh + 4 * u);
                }
            }
            using (var pen = new Pen(ink, stroke)) using (var path = Theme.Round(new RectangleF(sx, sy, sw, sh), 2 * u)) g.DrawPath(pen, path);

            // Radio mark, right.
            int d = Dpi.S(16);
            var box = new RectangleF(Width - Dpi.S(16) - d, (Height - d) / 2f, d - 1, d - 1);
            if (Checked)
            {
                Theme.FillRound(g, en ? Theme.Accent : Theme.Disabled, Color.Empty, box, (d - 1) / 2f);
                float dot = d * 0.38f;
                using (var b = new SolidBrush(en ? Theme.OnAccent : fill)) g.FillEllipse(b, box.X + (box.Width - dot) / 2f, box.Y + (box.Height - dot) / 2f, dot, dot);
            }
            else Theme.FillRound(g, Color.Empty, en ? Theme.Outline : Theme.Disabled, box, (d - 1) / 2f);

            // Text: "Title (detail)" on two lines.
            string title = Text, detail = "";
            int cut = Text.IndexOf(" (");
            if (cut > 0) { title = Text.Substring(0, cut); detail = Text.Substring(cut + 2).TrimEnd(')'); if (detail.Length > 0) detail = char.ToUpper(detail[0]) + detail.Substring(1); }
            int tx = (int)(sx + sw) + Dpi.S(16), tw = (int)box.X - Dpi.S(8) - tx, lh = Font.Height;
            int top = (Height - (detail.Length > 0 ? lh * 2 : lh)) / 2;
            var flags = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);
            TextRenderer.DrawText(g, title, Font, new Rectangle(tx, top, tw, lh), en ? Theme.Text : Theme.Disabled, flags);
            if (detail.Length > 0) TextRenderer.DrawText(g, detail, Font, new Rectangle(tx, top + lh, tw, lh), en ? Theme.Text2 : Theme.Disabled, flags | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), Dpi.S(8));
        }
    }

    sealed class CheckEx : CheckBox
    {
        bool hot;
        const TextFormatFlags LabelFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
        public CheckEx() { AutoSize = true; Margin = Padding.Empty; }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }

        int Fixed { get { return Dpi.S(4) * 2 + Dpi.S(16) + Dpi.S(8); } }

        public override Size GetPreferredSize(Size proposed)
        {
            if (Theme.HC) return base.GetPreferredSize(proposed);
            int room = MaximumSize.Width > 0 ? MaximumSize.Width - Fixed : 100000;
            Size t = TextRenderer.MeasureText(Text, Font, new Size(room, 100000), LabelFlags);
            return new Size(Fixed + t.Width, Math.Max(Dpi.S(16), t.Height) + Dpi.S(4) * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Theme.HC) { base.OnPaint(e); return; }
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            bool en = Enabled;
            int d = Dpi.S(16), x = Dpi.S(4), room = Math.Max(1, Width - Fixed);
            Size t = TextRenderer.MeasureText(Text, Font, new Size(room, 100000), LabelFlags);
            int top = (Height - t.Height) / 2, line = Math.Min(t.Height, Font.Height);
            var box = new RectangleF(x, top + (line - d) / 2, d - 1, d - 1);
            if (Checked)
            {
                Theme.FillRound(g, en ? (hot ? Theme.Mix(Theme.Accent, BackColor, 0.12) : Theme.Accent) : Theme.Disabled, Color.Empty, box, Dpi.S(4));
                using (var pen = new Pen(en ? Theme.OnAccent : BackColor, 1.5f * Dpi.S(1)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] {
                        new PointF(box.X + box.Width * 0.26f, box.Y + box.Height * 0.52f),
                        new PointF(box.X + box.Width * 0.43f, box.Y + box.Height * 0.69f),
                        new PointF(box.X + box.Width * 0.75f, box.Y + box.Height * 0.34f) });
                }
            }
            else Theme.FillRound(g, hot && en ? Theme.Subtle : BackColor, en ? Theme.Outline : Theme.Disabled, box, Dpi.S(4));
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x + d + Dpi.S(8), top, room, t.Height), en ? Theme.Text : Theme.Disabled,
                LabelFlags | (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix));
            if (Focused && ShowFocusCues) Theme.FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), Dpi.S(8));
        }
    }

    // A standard Button, drawn. Primary = the main action (the inverse of the surface). The drawn
    // button sits Halo pixels inside the control, which leaves room for the focus outline.
    sealed class ButtonEx : Button
    {
        public bool Primary;
        bool hot, down;
        public static int Halo { get { return Theme.HC ? 0 : Dpi.S(4); } }
        public ButtonEx() { AutoSize = true; Margin = Padding.Empty; }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { down = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { down = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnLostFocus(EventArgs e) { down = false; base.OnLostFocus(e); }

        public override Size GetPreferredSize(Size proposed)
        {
            if (Theme.HC) return base.GetPreferredSize(proposed);
            Size t = TextRenderer.MeasureText(Text, Font, new Size(100000, 100000), TextFormatFlags.SingleLine);
            return new Size(Math.Max(Dpi.S(88), t.Width + Dpi.S(16) * 2) + Halo * 2, Dpi.S(36) + Halo * 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Theme.HC) { base.OnPaint(e); return; }
            var g = e.Graphics;
            Color around = Parent != null ? Parent.BackColor : Theme.Bg;
            g.Clear(around);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill, edge, text;
            if (Primary)
            {
                fill = !Enabled ? Theme.Mix(around, Theme.Text, 0.12) : (down ? Theme.Mix(Theme.Accent, around, 0.28) : (hot ? Theme.Mix(Theme.Accent, around, 0.14) : Theme.Accent));
                edge = Color.Empty;
                text = Enabled ? Theme.OnAccent : Theme.Mix(around, Theme.Text, 0.42);
            }
            else
            {
                fill = !Enabled ? Theme.Mix(Theme.BtnFill, around, 0.5) : (down ? Theme.BtnDown : (hot ? Theme.BtnHover : Theme.BtnFill));
                edge = Enabled ? Theme.BtnEdge : Theme.Mix(Theme.BtnEdge, around, 0.5);
                text = Enabled ? Theme.Text : Theme.Disabled;
            }
            int h = Halo;
            Theme.FillRound(g, fill, edge, new RectangleF(h, h, Width - 1 - h * 2, Height - 1 - h * 2), Dpi.S(8));
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                        (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, flags);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, new RectangleF(1, 1, Width - 3, Height - 3), Dpi.S(8) + h - 1);
        }
    }

    // Rounded panels that group related content.
    sealed class CardFlow : FlowLayoutPanel
    {
        public CardFlow()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintCard(this, e.Graphics); }
    }

    sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { Theme.PaintCard(this, e.Graphics); }
    }

    // A line of text with a small coloured dot in front (the colour is never the only signal:
    // the text says the same thing).
    sealed class DotLabel : Label
    {
        public Color Dot = Color.Empty;
        public DotLabel() { AutoSize = true; Padding = new Padding(Dpi.S(16), 0, 0, 0); UseMnemonic = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Dot == Color.Empty) return;
            Theme.Dot(e.Graphics, Dot, Dpi.S(2), Math.Min(Height, Font.Height) / 2f + 1);
        }
    }

    enum Framing { Exact, Crop, Fit, Loose }   // Loose: shown whole, not placed on the screen yet

    // The picture of the screen, and the one place in the app that carries colour. It has the
    // screen's shape, so "crop" really crops and "fit" really shows the bars (in the bar colour
    // from the settings). Crop marks sit at its corners, the way a printer marks the exact edge of
    // a proof; they take the colour of the check result. Changing the framing glides from one to
    // the other unless Windows' animations are turned off.
    sealed class ScreenPreview : Control
    {
        Bitmap image;
        Framing framing = Framing.Exact;
        Color ground = Color.Black, marks = Color.Empty;
        RectangleF from, to, cur;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Stopwatch watch = new Stopwatch();
        const int GlideMs = 300;

        // Room around the picture for the crop marks.
        public static int Inset { get { return Theme.HC ? 0 : Dpi.S(12); } }

        public ScreenPreview()
        {
            SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            AccessibleRole = AccessibleRole.Graphic;
            timer.Interval = 15;
            timer.Tick += delegate
            {
                // One curve for all motion: fast start, long soft landing.
                float x = Math.Min(1f, watch.ElapsedMilliseconds / (float)GlideMs), inv = 1 - x;
                float k = 1 - inv * inv * inv * inv;
                cur = new RectangleF(from.X + (to.X - from.X) * k, from.Y + (to.Y - from.Y) * k, from.Width + (to.Width - from.Width) * k, from.Height + (to.Height - from.Height) * k);
                if (x >= 1f) { timer.Stop(); cur = to; }
                Invalidate();
            };
        }

        // Takes ownership of the bitmap.
        public void SetImage(Bitmap b)
        {
            if (image != null) image.Dispose();
            image = b;
            timer.Stop();
            cur = to = Target();
            Invalidate();
        }

        // "description" is what a screen reader hears for the picture's state.
        public void Frame(Framing f, Color bars, Color markColor, string description, bool glide)
        {
            framing = f; ground = bars; marks = markColor;
            AccessibleDescription = description;
            to = Target();
            if (glide && image != null && IsHandleCreated && Theme.Animate && to != cur) { from = cur; watch.Restart(); timer.Start(); }
            else { timer.Stop(); cur = to; }
            Invalidate();
        }

        RectangleF Box { get { int m = Inset; return new RectangleF(m, m, Width - 1 - m * 2, Height - 1 - m * 2); } }

        RectangleF Target()
        {
            if (image == null) return RectangleF.Empty;
            RectangleF b = Box;
            float iw = image.Width, ih = image.Height;
            float k = framing == Framing.Crop || framing == Framing.Exact ? Math.Max(b.Width / iw, b.Height / ih) : Math.Min(b.Width / iw, b.Height / ih);
            if (framing == Framing.Loose) k *= 0.84f;
            return new RectangleF(b.X + (b.Width - iw * k) / 2f, b.Y + (b.Height - ih * k) / 2f, iw * k, ih * k);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); timer.Stop(); cur = to = Target(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); if (image != null) { image.Dispose(); image = null; } }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            RectangleF r = Box;
            if (r.Width < 4 || r.Height < 4) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, Theme.HC ? 0 : Dpi.S(4)))
            {
                using (var b = new SolidBrush(image != null && framing == Framing.Fit ? ground : Theme.Well)) g.FillPath(b, path);
                if (image != null)
                {
                    var state = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    g.InterpolationMode = timer.Enabled ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    using (var attr = new ImageAttributes())
                    {
                        attr.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(image, Rectangle.Round(cur), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attr);
                    }
                    g.Restore(state);
                }
                using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
            }
            if (Theme.HC || marks == Color.Empty) return;
            // Crop marks: two short lines outside each corner, in line with the picture's edges.
            g.SmoothingMode = SmoothingMode.None;
            int gap = Dpi.S(4), len = Dpi.S(8);
            int l = (int)r.Left, t = (int)r.Top, rt = (int)r.Right, bt = (int)r.Bottom;
            using (var p = new Pen(marks, Math.Max(1, Dpi.S(1))))
            {
                foreach (int x in new[] { l, rt }) { g.DrawLine(p, x, t - gap - len, x, t - gap); g.DrawLine(p, x, bt + gap, x, bt + gap + len); }
                foreach (int y in new[] { t, bt }) { g.DrawLine(p, l - gap - len, y, l - gap, y); g.DrawLine(p, rt + gap, y, rt + gap + len, y); }
            }
        }
    }

    static class Pictures
    {
        // Decodes the image once, scales it to the box (cover = fill the box, else fit inside it)
        // and frees the full-size pixels straight away. Applies a camera rotation tag. Returns
        // null for anything GDI+ can't read. Safe to call from a background thread.
        public static Bitmap Load(string path, int boxW, int boxH, bool cover)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs, false, false))
                {
                    int o = 1;
                    if (img.PropertyIdList.Contains(0x0112))
                    {
                        var p = img.GetPropertyItem(0x0112);
                        if (p.Value.Length >= 2) o = BitConverter.ToUInt16(p.Value, 0);
                    }
                    bool turned = o >= 5 && o <= 8;
                    int bw = turned ? boxH : boxW, bh = turned ? boxW : boxH;     // the box, as the stored pixels see it
                    double kx = (double)bw / img.Width, ky = (double)bh / img.Height;
                    double k = cover ? Math.Max(kx, ky) : Math.Min(kx, ky);
                    double area = img.Width * k * img.Height * k, cap = 4.0 * bw * bh;   // very long or tall images
                    if (area > cap) k *= Math.Sqrt(cap / area);
                    if (k > 1) k = 1;                                                    // never enlarge here
                    int w = Math.Max(1, (int)Math.Round(img.Width * k)), h = Math.Max(1, (int)Math.Round(img.Height * k));
                    var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        using (var attr = new ImageAttributes())
                        {
                            attr.SetWrapMode(WrapMode.TileFlipXY);   // no faded edge
                            g.DrawImage(img, new Rectangle(0, 0, w, h), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
                        }
                    }
                    RotateFlipType[] turn = { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX, RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX,
                                              RotateFlipType.Rotate90FlipX, RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
                    if (o >= 2 && o <= 8) bmp.RotateFlip(turn[o - 1]);
                    return bmp;
                }
            }
            catch (Exception) { return null; }
        }

        // A smaller copy, cropped to fill w x h.
        public static Bitmap Cover(Bitmap src, int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                float k = Math.Max((float)w / src.Width, (float)h / src.Height), sw = w / k, sh = h / k;
                using (var attr = new ImageAttributes())
                {
                    attr.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, w, h), (src.Width - sw) / 2f, (src.Height - sh) / 2f, sw, sh, GraphicsUnit.Pixel, attr);
                }
            }
            return bmp;
        }
    }

    sealed class ListEx : ListView
    {
        // In dark mode Windows' dark list theme (needed for dark scroll bars) rules faint column
        // lines down the empty part of the list. CoverEmpty paints that part plain again, as the
        // last step of the list's own drawing.
        public bool CoverEmpty;

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR { public IntPtr hwndFrom; public IntPtr idFrom; public int code; }
        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW { public NMHDR hdr; public int dwDrawStage; public IntPtr hdc; }

        public ListEx() { DoubleBuffered = true; }

        // Lets the owner draw the keyboard-focus outline only when Windows would show one.
        public bool FocusCues { get { return ShowFocusCues; } }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (!CoverEmpty || m.Msg != 0x204E || m.LParam == IntPtr.Zero) return;   // WM_NOTIFY sent back to the list
            var cd = (NMCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMCUSTOMDRAW));
            if (cd.hdr.code != -12 || cd.hdr.hwndFrom != Handle) return;               // NM_CUSTOMDRAW
            if (cd.dwDrawStage == 1) m.Result = new IntPtr(m.Result.ToInt64() | 0x10); // before painting: ask to be told when it is done
            else if (cd.dwDrawStage == 2 && cd.hdc != IntPtr.Zero)                     // after painting
            {
                int top = Items.Count > 0 ? Math.Max(0, Items[Items.Count - 1].Bounds.Bottom) : 0;
                if (top >= ClientSize.Height) return;
                using (var g = Graphics.FromHdc(cd.hdc))
                using (var b = new SolidBrush(BackColor))
                    g.FillRectangle(b, 0, top, ClientSize.Width, ClientSize.Height - top);
            }
        }
    }
}
