// Entry point. With no arguments it runs the small always-on tray app. With arguments it runs a
// short-lived worker that does the heavy work (decode, convert, install, check) and exits, so the
// always-on part stays small.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LosslessWallpaper
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();
            if (args.Length == 0)
            {
                bool created;
                using (var m = new Mutex(true, @"Local\LosslessWallpaper", out created))
                {
                    if (!created)
                    {
                        MessageBox.Show("Lossless Wallpaper is already running. Look for its icon in the notification area.", "Lossless Wallpaper");
                        return 0;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new TrayApp());
                }
                return 0;
            }

            if (args[0] == "--quit")
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(TrayApp.QuitEventName, out ev)) { ev.Set(); ev.Dispose(); return 0; }
                return 1; // not running
            }

            var a = ParseArgs(args);
            var o = new List<string>();
            int code;
            try { code = RunWorker(a, o); }
            catch (Exception ex) { o.Add("ok=false"); o.Add("message=" + OneLine(ex.Message)); code = 1; }
            string result;
            if (a.TryGetValue("result", out result)) File.WriteAllLines(result, o.ToArray(), new UTF8Encoding(false));
            else foreach (var line in o) Console.WriteLine(line);
            return code;
        }

        static Dictionary<string, string> ParseArgs(string[] args)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string k = args[i].Substring(2);
                string v = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                d[k] = v;
            }
            return d;
        }

        static string OneLine(string s) { return (s ?? "").Replace("\r", " ").Replace("\n", " "); }

        static FitMode ParseFit(string s)
        {
            if (s == "crop") return FitMode.Crop;
            if (s == "fit") return FitMode.Fit;
            return FitMode.Exact;
        }

        static int RunWorker(Dictionary<string, string> a, List<string> o)
        {
            string v;
            if (a.TryGetValue("install", out v)) return Install(v, Get(a, "target", "wallpaper"), ParseFit(Get(a, "fit", "exact")), Get(a, "name", Path.GetFileName(v)), o);
            if (a.TryGetValue("preview", out v)) return Preview.Run(v, ParseFit(Get(a, "fit", "exact")), Get(a, "target", "wallpaper"), Get(a, "selftest", null), o);
            if (a.ContainsKey("undo")) return Undo(a.ContainsKey("original"), o);
            if (a.TryGetValue("check-lock", out v)) return CheckLock(v, new DateTime(long.Parse(Get(a, "since", "0"))), o);
            if (a.TryGetValue("convert", out v))
            {
                // Test helper: convert without installing.
                var s = Settings.Load();
                int W = int.Parse(Get(a, "width", "0")), H = int.Parse(Get(a, "height", "0"));
                if (W == 0) Native.PrimaryScreenPixels(out W, out H);
                var img = Imaging.Decode(v, s.AlphaBackground);
                var fitted = Imaging.FitTo(img, W, H, ParseFit(Get(a, "fit", "exact")), s.BarColor);
                Imaging.WritePng(fitted, a["out"]);
                var back = Imaging.Decode(a["out"], System.Drawing.Color.Black);
                o.Add("ok=true"); o.Add("format=" + img.Format); o.Add("pixelformat=" + img.SourcePixelFormat);
                o.Add("width=" + fitted.Width); o.Add("height=" + fitted.Height);
                o.Add("encodercheck=" + Imaging.Compare(fitted, back));
                foreach (var n in fitted.Notes) o.Add("note=" + n);
                return 0;
            }
            if (a.TryGetValue("compare", out v))
            {
                var x = Imaging.Decode(v, System.Drawing.Color.Black);
                var y = Imaging.Decode(a["with"], System.Drawing.Color.Black);
                var d = Imaging.Compare(x, y);
                o.Add("ok=" + (d.Zero ? "true" : "false")); o.Add("diff=" + d);
                return d.Zero ? 0 : 3;
            }
            if (a.ContainsKey("warnings")) { o.Add("ok=true"); foreach (var w in Warnings.Current()) o.Add("warning=" + w); return 0; }
            if (a.ContainsKey("screen"))
            {
                int W, H; Native.PrimaryScreenPixels(out W, out H);
                o.Add("ok=true"); o.Add("width=" + W); o.Add("height=" + H); o.Add("screens=" + Screen.AllScreens.Length);
                return 0;
            }
            o.Add("ok=false"); o.Add("message=Unknown command.");
            return 1;
        }

        static string Get(Dictionary<string, string> a, string k, string fallback)
        {
            string v; return a.TryGetValue(k, out v) ? v : fallback;
        }

        static int Install(string src, string target, FitMode fit, string name, List<string> o)
        {
            var s = Settings.Load();
            int W, H; Native.PrimaryScreenPixels(out W, out H);
            var img = Imaging.Decode(src, s.AlphaBackground);
            bool offSize = img.Width != W || img.Height != H;
            if (offSize && fit == FitMode.Exact)
            {
                o.Add("ok=false"); o.Add("refused=true");
                o.Add("message=Image is " + img.Width + "x" + img.Height + " but the screen is " + W + "x" + H + ". True lossless mode needs an exact match.");
                return 2;
            }
            var fitted = Imaging.FitTo(img, W, H, fit, s.BarColor);

            string tmp = Path.Combine(Settings.LibraryDir, "incoming-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Directory.CreateDirectory(Settings.LibraryDir);
                Imaging.WritePng(fitted, tmp);
            }
            catch (UnauthorizedAccessException)
            {
                throw new UnauthorizedAccessException("Windows blocked writing to " + Settings.LibraryDir +
                    ". If Controlled Folder Access (ransomware protection) is on, allow LosslessWallpaper.exe in Windows Security > Virus & threat protection > Ransomware protection.");
            }
            var back = Imaging.Decode(tmp, System.Drawing.Color.Black);
            var self = Imaging.Compare(fitted, back);
            if (!self.Zero) { File.Delete(tmp); throw new InvalidDataException("The app's own PNG did not match (" + self + "). Nothing was installed."); }
            string id = Library.HashFile(tmp);
            string final = Path.Combine(Settings.LibraryDir, id + ".png");
            if (File.Exists(final)) File.Delete(tmp); else File.Move(tmp, final);

            string mode = offSize ? (fit == FitMode.Crop ? "scaled (crop)" : "scaled (fit)") : (img.Lossy ? "converted" : "lossless");
            o.Add("id=" + id); o.Add("file=" + final); o.Add("name=" + OneLine(name)); o.Add("mode=" + mode);
            o.Add("width=" + W); o.Add("height=" + H);
            foreach (var n in fitted.Notes) o.Add("note=" + OneLine(n));

            // target "none" = save for later: the checked PNG goes into the library and nothing is applied.
            bool wp = target == "wallpaper" || target == "both";
            bool lk = target == "lockscreen" || target == "both";
            var checks = new List<CheckResult>();
            // Re-applying what is already set changes nothing, so it gets no backup (keeps undo useful).
            bool wpSame = wp && string.Equals(Installer.CurrentWallpaperPath(), final, StringComparison.OrdinalIgnoreCase);
            bool lkSame = lk && Installer.CurrentLockPath() != null && string.Equals(Installer.CurrentLockPath(), final, StringComparison.OrdinalIgnoreCase);
            if ((wp || lk) && !((!wp || wpSame) && (!lk || lkSame)))
                o.Add("snapshot=" + Installer.Snapshot(wp && !wpSame, lk && !lkSame));
            if (wp || lk)
            {
                o.Add("since=" + DateTime.Now.AddSeconds(-5).Ticks);
                if (wp) checks.Add(Installer.SetWallpaper(final, fitted));
                if (lk) checks.Add(Installer.SetLockScreen(final, fitted));
            }
            foreach (var c in checks) o.Add("check=" + OneLine(c.ToString()));
            bool pass = checks.All(c => c.Pass);
            bool pending = checks.Any(c => c.Pending) && checks.All(c => c.Pass || c.Pending);
            o.Add("pass=" + (pass ? "true" : "false")); o.Add("pending=" + (pending ? "true" : "false"));
            o.Add("target=" + target);
            foreach (var w in Warnings.Current()) o.Add("warning=" + OneLine(w));
            o.Insert(0, "ok=true");
            return 0;
        }

        static int Undo(bool original, List<string> o)
        {
            string snap = original ? Installer.OriginalSnapshot() : Installer.LatestSnapshot();
            if (snap == null)
            {
                o.Add("ok=false");
                o.Add("message=" + (original ? "No record of your setup from before the app was used." : "Nothing to undo."));
                return 1;
            }
            var log = new List<string>();
            bool ok = Installer.Undo(snap, log);
            o.Add("ok=true");
            o.Add("complete=" + (ok ? "true" : "false"));
            foreach (var l in log) o.Add("line=" + OneLine(l));
            return 0;
        }

        static int CheckLock(string png, DateTime since, List<string> o)
        {
            var expected = Imaging.Decode(png, System.Drawing.Color.Black);
            var c = Installer.CheckLockScreen(expected, since);
            o.Add("ok=true"); o.Add("check=" + OneLine(c.ToString()));
            o.Add("pass=" + (c.Pass ? "true" : "false")); o.Add("pending=" + (c.Pending ? "true" : "false"));
            return 0;
        }
    }
}
