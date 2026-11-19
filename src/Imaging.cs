// Image core: sniff, decode to raw 8-bit BGR, fit to screen, write PNG, compare.
// "Lossless" here means: the stored sample values are preserved exactly. Colour profiles and
// gamma chunks are ignored on purpose, so the numbers in the file are the numbers Windows gets.
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GdiColor = System.Drawing.Color;

namespace LosslessWallpaper
{
    public enum FitMode { Exact, Crop, Fit }

    public sealed class RawImage
    {
        public int Width;
        public int Height;
        public byte[] Bgr;                 // 3 bytes per pixel, rows packed (stride = Width * 3)
        public string Format = "";         // from the file signature, not the extension
        public string SourcePixelFormat = "";
        public List<string> Notes = new List<string>();
        public bool Lossy;                 // true if converting the input changed sample values
    }

    public sealed class Diff
    {
        public long Differing;
        public int MaxDelta;
        public bool SizeMismatch;
        public bool Zero { get { return !SizeMismatch && Differing == 0; } }
        public override string ToString()
        {
            if (SizeMismatch) return "size mismatch";
            return Differing + " differing pixels, largest difference " + MaxDelta;
        }
    }

    public static class Imaging
    {
        public const long MaxFileBytes = 300L * 1024 * 1024;

        public static string Sniff(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "PNG";
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "JPEG";
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "BMP";
            if (b.Length >= 4 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8') return "GIF";
            if (b.Length >= 4 && ((b[0] == 'I' && b[1] == 'I' && b[2] == 42 && b[3] == 0) || (b[0] == 'M' && b[1] == 'M' && b[2] == 0 && b[3] == 42))) return "TIFF";
            if (b.Length >= 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P') return "WebP";
            if (b.Length >= 12 && b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return "HEIF/AVIF";
            return null;
        }

        public static string SniffFile(string path)
        {
            var head = new byte[16];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) n = fs.Read(head, 0, head.Length);
            if (n < head.Length) Array.Resize(ref head, n);
            return Sniff(head);
        }

        public static RawImage Decode(string path, GdiColor alphaBackground)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes) throw new InvalidDataException("File is larger than 300 MB.");
            byte[] file = File.ReadAllBytes(path);
            string fmt = Sniff(file);
            if (fmt == null) throw new InvalidDataException("Not a recognised image file (checked the first bytes, not the extension).");

            BitmapFrame frame;
            int frameCount;
            using (var ms = new MemoryStream(file))
            {
                BitmapDecoder dec;
                try
                {
                    dec = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(fmt + " file could not be decoded by Windows (" + ex.Message + "). A codec may be missing.");
                }
                frameCount = dec.Frames.Count;
                frame = dec.Frames[0];
            }
            if (frameCount > 1) throw new InvalidDataException("Animated or multi-page images are not supported.");

            var img = new RawImage();
            img.Format = fmt;
            BitmapSource source = ApplyOrientation(frame, img);
            img.Width = source.PixelWidth;
            img.Height = source.PixelHeight;
            PixelFormat pf = source.Format;
            img.SourcePixelFormat = pf.ToString();
            if (fmt == "JPEG" || fmt == "WebP" || fmt == "HEIF/AVIF") img.Notes.Add(fmt + " input: it was already compressed before it reached the app; the app adds no further loss.");
            try { if (frame.ColorContexts != null && frame.ColorContexts.Count > 0) img.Notes.Add("Embedded colour profile ignored: the stored pixel values are kept as they are."); }
            catch (Exception) { }

            if (pf == PixelFormats.Rgba128Float || pf == PixelFormats.Rgb128Float || pf == PixelFormats.Prgba128Float || pf == PixelFormats.Gray32Float)
                throw new InvalidDataException("HDR / floating-point images are not supported.");

            if (pf == PixelFormats.Rgb48 || pf == PixelFormats.Rgba64 || pf == PixelFormats.Prgba64 || pf == PixelFormats.Gray16)
                From16Bit(source, img, alphaBackground);
            else
            {
                if (!ExactTo8Bit(pf)) { img.Lossy = true; img.Notes.Add("Converted from " + pf + " to 8-bit RGB; this changes some values."); }
                From8Bit(source, img, alphaBackground);
            }
            return img;
        }

        // Formats whose conversion to 8-bit BGRA keeps every value (an allow-list, so anything
        // unexpected is labelled as converted rather than lossless).
        static bool ExactTo8Bit(PixelFormat pf)
        {
            return pf == PixelFormats.Bgr24 || pf == PixelFormats.Rgb24 || pf == PixelFormats.Bgr32 || pf == PixelFormats.Bgra32 ||
                   pf == PixelFormats.Gray8 || pf == PixelFormats.Gray4 || pf == PixelFormats.Gray2 || pf == PixelFormats.BlackWhite ||
                   pf == PixelFormats.Indexed1 || pf == PixelFormats.Indexed2 || pf == PixelFormats.Indexed4 || pf == PixelFormats.Indexed8;
        }

        // Camera photos store their rotation in an EXIF tag instead of rotating the pixels.
        // Rotations by multiples of 90 degrees and flips move pixels without changing them.
        static BitmapSource ApplyOrientation(BitmapFrame frame, RawImage img)
        {
            int o = 1;
            try
            {
                var md = frame.Metadata as BitmapMetadata;
                if (md != null)
                {
                    object v = null;
                    foreach (var q in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
                    {
                        try { if (md.ContainsQuery(q)) { v = md.GetQuery(q); break; } } catch (Exception) { }
                    }
                    if (v != null) o = Convert.ToInt32(v);
                }
            }
            catch (Exception) { }
            if (o < 2 || o > 8) return frame;
            var tg = new TransformGroup();
            if (o == 2 || o == 4 || o == 5 || o == 7) tg.Children.Add(new ScaleTransform(o == 4 ? 1 : -1, o == 4 ? -1 : 1));
            if (o == 3) tg.Children.Add(new RotateTransform(180));
            // EXIF: 6 = rotate 90 clockwise, 8 = rotate 270, 5 = mirror then 270, 7 = mirror then 90.
            if (o == 6 || o == 7) tg.Children.Add(new RotateTransform(90));
            if (o == 5 || o == 8) tg.Children.Add(new RotateTransform(270));
            img.Notes.Add("Turned upright using the photo's orientation tag (" + o + ").");
            return new TransformedBitmap(frame, tg);
        }

        static byte To8(int v16) { return (byte)((v16 * 255 + 32767) / 65535); }

        static void From16Bit(BitmapSource src, RawImage img, GdiColor bg)
        {
            int w = img.Width, h = img.Height;
            PixelFormat pf = src.Format;
            if (pf == PixelFormats.Prgba64) { src = new FormatConvertedBitmap(src, PixelFormats.Rgba64, null, 0); pf = PixelFormats.Rgba64; }
            int ch = pf == PixelFormats.Gray16 ? 1 : (pf == PixelFormats.Rgb48 ? 3 : 4);
            var buf = new ushort[w * h * ch];
            src.CopyPixels(buf, w * ch * 2, 0);
            img.Bgr = new byte[w * h * 3];
            bool alphaUsed = false;
            for (int i = 0, o = 0; i < buf.Length; i += ch, o += 3)
            {
                int r, g, b;
                if (ch == 1) { r = g = b = To8(buf[i]); }
                else { r = To8(buf[i]); g = To8(buf[i + 1]); b = To8(buf[i + 2]); }
                if (ch == 4)
                {
                    int a = To8(buf[i + 3]);
                    if (a < 255) { alphaUsed = true; r = Blend(r, bg.R, a); g = Blend(g, bg.G, a); b = Blend(b, bg.B, a); }
                }
                img.Bgr[o] = (byte)b; img.Bgr[o + 1] = (byte)g; img.Bgr[o + 2] = (byte)r;
            }
            img.Lossy = true;
            img.Notes.Add("16-bit image converted to 8-bit (Windows would otherwise turn it into a lossy JPEG).");
            if (alphaUsed) img.Notes.Add("Transparent areas flattened onto the background colour.");
        }

        static void From8Bit(BitmapSource src, RawImage img, GdiColor bg)
        {
            int w = img.Width, h = img.Height;
            var conv = src.Format == PixelFormats.Bgra32 ? src : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            var buf = new byte[w * h * 4];
            conv.CopyPixels(buf, w * 4, 0);
            img.Bgr = new byte[w * h * 3];
            bool alphaUsed = false;
            for (int i = 0, o = 0; i < buf.Length; i += 4, o += 3)
            {
                int b = buf[i], g = buf[i + 1], r = buf[i + 2], a = buf[i + 3];
                if (a < 255) { alphaUsed = true; r = Blend(r, bg.R, a); g = Blend(g, bg.G, a); b = Blend(b, bg.B, a); }
                img.Bgr[o] = (byte)b; img.Bgr[o + 1] = (byte)g; img.Bgr[o + 2] = (byte)r;
            }
            if (alphaUsed) { img.Lossy = true; img.Notes.Add("Transparent areas flattened onto the background colour."); }
        }

        static int Blend(int c, int bg, int a) { return (c * a + bg * (255 - a) + 127) / 255; }

        // Returns the image unchanged when it already matches; otherwise resamples with one fixed
        // filter (GDI+ HighQualityBicubic, edge pixels mirrored) so results are repeatable.
        public static RawImage FitTo(RawImage src, int W, int H, FitMode mode, GdiColor bars)
        {
            if (src.Width == W && src.Height == H) return src;
            if (mode == FitMode.Exact) throw new InvalidOperationException("Image is " + src.Width + "x" + src.Height + ", screen is " + W + "x" + H + ".");

            var outImg = new RawImage();
            outImg.Width = W; outImg.Height = H; outImg.Format = src.Format; outImg.SourcePixelFormat = src.SourcePixelFormat;
            outImg.Notes.AddRange(src.Notes);
            outImg.Lossy = true;
            outImg.Notes.Add("Scaled from " + src.Width + "x" + src.Height + " (" + (mode == FitMode.Crop ? "crop to fill" : "fit with bars") + ", high-quality bicubic).");

            using (var s = ToBitmap(src))
            using (var d = new System.Drawing.Bitmap(W, H, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            using (var g = System.Drawing.Graphics.FromImage(d))
            using (var attr = new System.Drawing.Imaging.ImageAttributes())
            {
                attr.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g.Clear(bars);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                double sx = (double)W / src.Width, sy = (double)H / src.Height;
                if (mode == FitMode.Crop)
                {
                    double scale = Math.Max(sx, sy);
                    float cw = (float)(W / scale), chh = (float)(H / scale);
                    float cx = (src.Width - cw) / 2f, cy = (src.Height - chh) / 2f;
                    g.DrawImage(s, new System.Drawing.Rectangle(0, 0, W, H), cx, cy, cw, chh, System.Drawing.GraphicsUnit.Pixel, attr);
                }
                else
                {
                    double scale = Math.Min(sx, sy);
                    int dw = Math.Max(1, (int)Math.Round(src.Width * scale)), dh = Math.Max(1, (int)Math.Round(src.Height * scale));
                    int dx = (W - dw) / 2, dy = (H - dh) / 2;
                    g.DrawImage(s, new System.Drawing.Rectangle(dx, dy, dw, dh), 0, 0, src.Width, src.Height, System.Drawing.GraphicsUnit.Pixel, attr);
                }
                outImg.Bgr = FromBitmap(d);
            }
            return outImg;
        }

        public static System.Drawing.Bitmap ToBitmap(RawImage img)
        {
            var bmp = new System.Drawing.Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, img.Width, img.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            int row = img.Width * 3;
            for (int y = 0; y < img.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(img.Bgr, y * row, data.Scan0 + y * data.Stride, row);
            bmp.UnlockBits(data);
            return bmp;
        }

        static byte[] FromBitmap(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height, row = w * 3;
            var outBuf = new byte[row * h];
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, outBuf, y * row, row);
            bmp.UnlockBits(data);
            return outBuf;
        }

        public static void WritePng(RawImage img, string path)
        {
            var src = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgr24, null, img.Bgr, img.Width * 3);
            var enc = new PngBitmapEncoder();
            enc.Interlace = PngInterlaceOption.Off;
            enc.Frames.Add(BitmapFrame.Create(src));
            byte[] png;
            using (var ms = new MemoryStream()) { enc.Save(ms); png = StripAncillaryChunks(ms.ToArray()); }
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, png);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        // Windows' PNG encoder adds sRGB, gAMA and pHYs chunks. Keep only the chunks that carry
        // pixels, so nothing in the file can tell a reader to adjust the values.
        static byte[] StripAncillaryChunks(byte[] png)
        {
            var outp = new MemoryStream();
            outp.Write(png, 0, 8);
            int i = 8;
            while (i + 12 <= png.Length)
            {
                int len = (png[i] << 24) | (png[i + 1] << 16) | (png[i + 2] << 8) | png[i + 3];
                string kind = System.Text.Encoding.ASCII.GetString(png, i + 4, 4);
                int total = 12 + len;
                if (kind == "IHDR" || kind == "PLTE" || kind == "IDAT" || kind == "IEND") outp.Write(png, i, total);
                i += total;
            }
            return outp.ToArray();
        }

        public static Diff Compare(RawImage a, RawImage b)
        {
            var d = new Diff();
            if (a.Width != b.Width || a.Height != b.Height) { d.SizeMismatch = true; return d; }
            for (int i = 0; i < a.Bgr.Length; i += 3)
            {
                int m = Math.Max(Math.Abs(a.Bgr[i] - b.Bgr[i]), Math.Max(Math.Abs(a.Bgr[i + 1] - b.Bgr[i + 1]), Math.Abs(a.Bgr[i + 2] - b.Bgr[i + 2])));
                if (m > 0) { d.Differing++; if (m > d.MaxDelta) d.MaxDelta = m; }
            }
            return d;
        }
    }
}
