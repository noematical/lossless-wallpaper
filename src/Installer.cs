// Installs an exact-size PNG as wallpaper and/or lock screen, backs up the previous state first,
// and checks the files Windows actually renders from. Current user only; never needs admin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.UserProfile;

namespace LosslessWallpaper
{
    public sealed class CheckResult
    {
        public string Target = "";
        public bool Pass;
        public bool Pending;
        public string Detail = "";
        public override string ToString() { return Target + ": " + (Pass ? "PASS" : (Pending ? "PENDING" : "FAIL")) + " - " + Detail; }
    }

    public static class Installer
    {
        const string DesktopKey = @"HKEY_CURRENT_USER\Control Panel\Desktop";

        public static string TranscodedPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper"); }
        }

        public static string CurrentWallpaperPath()
        {
            return (Registry.GetValue(DesktopKey, "WallPaper", "") as string) ?? "";
        }

        public static string CurrentLockPath()
        {
            try { var u = LockScreen.OriginalImageFile; return u != null && u.IsFile ? u.LocalPath : null; }
            catch (Exception) { return null; }
        }

        // ---------- wallpaper ----------
        public static CheckResult SetWallpaper(string png, RawImage expected)
        {
            Registry.SetValue(DesktopKey, "WallpaperStyle", "10");
            Registry.SetValue(DesktopKey, "TileWallpaper", "0");
            if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, png, Native.SPIF_UPDATEINIFILE_SENDCHANGE))
                return Fail("wallpaper", "Windows refused the change (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + "). A policy may lock the wallpaper.");
            return CheckWallpaper(png, expected, 8);
        }

        public static CheckResult CheckWallpaper(string png, RawImage expected, int waitSeconds)
        {
            var r = new CheckResult(); r.Target = "wallpaper";
            if (!string.Equals(CurrentWallpaperPath(), png, StringComparison.OrdinalIgnoreCase))
            { r.Detail = "Windows is pointing at a different file: " + CurrentWallpaperPath(); return r; }
            string last = "TranscodedWallpaper not found.";
            DateTime until = DateTime.Now.AddSeconds(waitSeconds);
            do
            {
                try
                {
                    string fmt = Imaging.SniffFile(TranscodedPath);
                    var got = Imaging.Decode(TranscodedPath, System.Drawing.Color.Black);
                    var d = Imaging.Compare(expected, got);
                    last = "file Windows draws from is " + fmt + " " + got.Width + "x" + got.Height + ", " + d;
                    if (d.Zero && fmt == "PNG") { r.Pass = true; r.Detail = last; return r; }
                }
                catch (Exception ex) { last = "could not read TranscodedWallpaper: " + ex.Message; }
                Thread.Sleep(500);
            } while (DateTime.Now < until);
            r.Detail = last;
            return r;
        }

        // ---------- WinRT without the SDK ----------
        // Windows' built-in compiler can't use the AsTask helpers (they need the SDK's union
        // metadata), so WinRT async calls are waited for through their own interfaces.
        static readonly TimeSpan WinRtDeadline = TimeSpan.FromSeconds(60);

        static T Wait<T>(IAsyncOperation<T> op)
        {
            var until = DateTime.Now + WinRtDeadline;
            while (op.Status == AsyncStatus.Started)
            {
                if (DateTime.Now > until) { op.Cancel(); throw new TimeoutException("Windows did not answer within 60 seconds."); }
                Thread.Sleep(10);
            }
            if (op.Status != AsyncStatus.Completed) throw new InvalidOperationException(op.ErrorCode != null ? op.ErrorCode.Message : op.Status.ToString());
            return op.GetResults();
        }

        static void Wait(IAsyncAction a)
        {
            var until = DateTime.Now + WinRtDeadline;
            while (a.Status == AsyncStatus.Started)
            {
                if (DateTime.Now > until) { a.Cancel(); throw new TimeoutException("Windows did not answer within 60 seconds."); }
                Thread.Sleep(10);
            }
            if (a.Status != AsyncStatus.Completed) throw new InvalidOperationException(a.ErrorCode != null ? a.ErrorCode.Message : a.Status.ToString());
            a.GetResults();
        }

        static void SetLockImage(string path)
        {
            StorageFile f = Wait(StorageFile.GetFileFromPathAsync(path));
            Wait(LockScreen.SetImageFileAsync(f));
        }

        static void SaveLockImage(string dest)
        {
            IRandomAccessStream s = LockScreen.GetImageStream();
            using (var reader = new DataReader(s.GetInputStreamAt(0)))
            {
                uint n = Wait<uint>(reader.LoadAsync((uint)s.Size));
                var buf = new byte[n];
                reader.ReadBytes(buf);
                File.WriteAllBytes(dest, buf);
            }
        }

        // Writes the lock screen image Windows has now to "dest" (read only; used by the preview).
        public static bool SaveCurrentLockImage(string dest)
        {
            try { SaveLockImage(dest); return true; }
            catch (Exception) { return false; }
        }

        // ---------- lock screen ----------
        public static CheckResult SetLockScreen(string png, RawImage expected)
        {
            try { SetLockImage(png); }
            catch (Exception ex)
            {
                return Fail("lock screen", "Windows refused the change (" + ex.Message + "). A policy may lock the lock screen.");
            }
            return CheckLockScreen(expected, DateTime.Now.AddSeconds(-5));
        }

        public static string LockScreenFolder()
        {
            try
            {
                string sid = WindowsIdentity.GetCurrent().User.Value;
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\SystemData", sid, "ReadOnly");
                if (!Directory.Exists(root)) return null;
                var dirs = new DirectoryInfo(root).GetDirectories("LockScreen_*");
                if (dirs.Length == 0) return null;
                return dirs.OrderByDescending(d => d.LastWriteTime).First().FullName;
            }
            catch (Exception) { return null; }
        }

        // Stored copy is checked straight away. The copy Windows actually draws is only made the
        // next time the PC locks, so until then the result is PENDING, not PASS.
        public static CheckResult CheckLockScreen(RawImage expected, DateTime installedAt)
        {
            var r = new CheckResult(); r.Target = "lock screen";
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "lw-lock-" + Guid.NewGuid().ToString("N"));
                SaveLockImage(tmp);
                string fmt = Imaging.SniffFile(tmp);
                var stored = Imaging.Decode(tmp, System.Drawing.Color.Black);
                File.Delete(tmp);
                var d = Imaging.Compare(expected, stored);
                if (!d.Zero || fmt != "PNG") { r.Detail = "stored copy is " + fmt + ", " + d; return r; }
                r.Detail = "stored copy PNG, " + d;
            }
            catch (Exception ex) { r.Detail = "could not read the stored lock screen: " + ex.Message; return r; }

            string folder = LockScreenFolder();
            string drawn = folder == null ? null : Path.Combine(folder, "LockScreen___" + expected.Width + "_" + expected.Height + "_notdimmed.jpg");
            if (drawn == null || !File.Exists(drawn) || File.GetLastWriteTime(drawn) < installedAt)
            {
                r.Pending = true;
                r.Detail += "; the copy Windows draws is made the next time you lock the PC (Win+L), then the app checks it.";
                return r;
            }
            try
            {
                string fmt = Imaging.SniffFile(drawn);
                var got = Imaging.Decode(drawn, System.Drawing.Color.Black);
                var d = Imaging.Compare(expected, got);
                r.Pass = d.Zero && fmt == "PNG";
                r.Detail += "; drawn copy " + fmt + ", " + d;
            }
            catch (Exception ex) { r.Detail += "; could not read the drawn copy: " + ex.Message; }
            return r;
        }

        static CheckResult Fail(string target, string why) { var r = new CheckResult(); r.Target = target; r.Detail = why; return r; }

        // ---------- backup and undo ----------
        // Every install that changes something first saves what was there. Snapshots are written
        // to a ".partial" folder and renamed when complete, so a failed backup never blocks undo.
        // The very first snapshot is also kept as "original": the setup from before the app was
        // used, which "Restore what I had before" brings back and pruning never deletes.
        public const string OriginalName = "original";

        public static string Snapshot(bool wallpaper, bool lockscreen)
        {
            Directory.CreateDirectory(Settings.UndoDir);
            string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            string dir = Path.Combine(Settings.UndoDir, name + ".partial");
            Directory.CreateDirectory(dir);
            var ini = new List<string>();
            if (wallpaper)
            {
                string wp = CurrentWallpaperPath();
                ini.Add("WallPaper=" + wp);
                ini.Add("WallpaperStyle=" + (Registry.GetValue(DesktopKey, "WallpaperStyle", "") as string));
                ini.Add("TileWallpaper=" + (Registry.GetValue(DesktopKey, "TileWallpaper", "") as string));
                if (wp.Length == 0) ini.Add("WallpaperNone=true"); // solid colour or no picture
                else
                {
                    string src = File.Exists(wp) ? wp : (File.Exists(TranscodedPath) ? TranscodedPath : null);
                    if (src != null)
                    {
                        string ext = ExtFor(Imaging.SniffFile(src));
                        File.Copy(src, Path.Combine(dir, "wallpaper" + ext));
                        ini.Add("WallpaperCopy=wallpaper" + ext);
                    }
                }
            }
            if (lockscreen)
            {
                try
                {
                    string tmp = Path.Combine(dir, "lockscreen.bin");
                    SaveLockImage(tmp);
                    string ext = ExtFor(Imaging.SniffFile(tmp));
                    File.Move(tmp, Path.Combine(dir, "lockscreen" + ext));
                    ini.Add("LockCopy=lockscreen" + ext);
                    var orig = LockScreen.OriginalImageFile;
                    if (orig != null && orig.IsFile) ini.Add("LockOriginal=" + orig.LocalPath);
                }
                catch (Exception ex) { ini.Add("LockError=" + ex.Message.Replace("\r", " ").Replace("\n", " ")); }
            }
            File.WriteAllLines(Path.Combine(dir, "state.ini"), ini.ToArray(), new UTF8Encoding(false));

            string final = Path.Combine(Settings.UndoDir, name);
            Directory.Move(dir, final);
            string original = Path.Combine(Settings.UndoDir, OriginalName);
            if (!Directory.Exists(original)) CopyDir(final, original);
            Prune();
            return final;
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to + ".partial");
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to + ".partial", Path.GetFileName(f)));
            Directory.Move(to + ".partial", to);
        }

        static string ExtFor(string fmt)
        {
            if (fmt == "PNG") return ".png";
            if (fmt == "JPEG") return ".jpg";
            if (fmt == "BMP") return ".bmp";
            return ".img";
        }

        // Timestamped snapshots only; "original" is kept forever. Incomplete ones are removed.
        static IEnumerable<DirectoryInfo> Snapshots()
        {
            if (!Directory.Exists(Settings.UndoDir)) return new DirectoryInfo[0];
            var all = new DirectoryInfo(Settings.UndoDir).GetDirectories();
            foreach (var d in all.Where(x => x.Name.EndsWith(".partial") || (x.Name != OriginalName && !File.Exists(Path.Combine(x.FullName, "state.ini")))))
            {
                try { d.Delete(true); } catch (Exception) { }
            }
            return all.Where(x => x.Name != OriginalName && !x.Name.EndsWith(".partial") && File.Exists(Path.Combine(x.FullName, "state.ini")))
                      .OrderByDescending(x => x.Name).ToList();
        }

        static void Prune()
        {
            foreach (var d in Snapshots().Skip(20)) { try { d.Delete(true); } catch (Exception) { } }
        }

        public static string LatestSnapshot()
        {
            var d = Snapshots().FirstOrDefault();
            return d == null ? null : d.FullName;
        }

        public static string OriginalSnapshot()
        {
            string d = Path.Combine(Settings.UndoDir, OriginalName);
            return File.Exists(Path.Combine(d, "state.ini")) ? d : null;
        }

        // Restores the state saved in a snapshot. The snapshot is deleted only when every part was
        // restored (and never for "original"), so a failed undo can be tried again.
        public static bool Undo(string dir, List<string> log)
        {
            bool allOk = true;
            var kv = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "state.ini")))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) kv[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            string v;
            if (kv.TryGetValue("WallPaper", out v))
            {
                try
                {
                    string target = v;
                    string copy;
                    if (kv.ContainsKey("WallpaperNone")) target = "";
                    else if (kv.TryGetValue("WallpaperCopy", out copy))
                    {
                        string saved = Path.Combine(dir, copy);
                        if (!File.Exists(target) || !SameBytes(target, saved))
                        {
                            // The original file moved or changed: keep the saved copy where Windows can render it.
                            Directory.CreateDirectory(Settings.LibraryDir);
                            target = Path.Combine(Settings.LibraryDir, "restored-" + Path.GetFileName(dir) + Path.GetExtension(copy));
                            File.Copy(saved, target, true);
                        }
                    }
                    else if (!File.Exists(target)) throw new FileNotFoundException("no saved copy of the previous wallpaper, and " + target + " is gone");
                    string style, tile;
                    if (kv.TryGetValue("WallpaperStyle", out style) && style.Length > 0) Registry.SetValue(DesktopKey, "WallpaperStyle", style);
                    if (kv.TryGetValue("TileWallpaper", out tile) && tile.Length > 0) Registry.SetValue(DesktopKey, "TileWallpaper", tile);
                    // An empty path removes the picture, which brings back a solid-colour background.
                    if (!Native.SystemParametersInfo(Native.SPI_SETDESKWALLPAPER, 0, target, Native.SPIF_UPDATEINIFILE_SENDCHANGE))
                        throw new InvalidOperationException("Windows refused the change");
                    log.Add("wallpaper restored: " + (target.Length == 0 ? "(no picture / solid colour)" : target));
                }
                catch (Exception ex) { allOk = false; log.Add("wallpaper restore FAILED: " + ex.Message); }
            }
            if (kv.TryGetValue("LockError", out v))
            {
                allOk = false;
                log.Add("lock screen NOT restored: it could not be backed up before the install (" + v + "). Set it again in Windows Settings > Personalization > Lock screen.");
            }
            if (kv.TryGetValue("LockCopy", out v))
            {
                try
                {
                    // Point Windows back at the original file when it is still there and unchanged;
                    // otherwise keep the saved copy in the library.
                    string saved = Path.Combine(dir, v), orig, target;
                    if (kv.TryGetValue("LockOriginal", out orig) && File.Exists(orig) && SameDecodedPixels(orig, saved)) target = orig;
                    else
                    {
                        Directory.CreateDirectory(Settings.LibraryDir);
                        target = Path.Combine(Settings.LibraryDir, "restored-lock-" + Path.GetFileName(dir) + Path.GetExtension(v));
                        File.Copy(saved, target, true);
                    }
                    SetLockImage(target);
                    log.Add("lock screen restored: " + target);
                }
                catch (Exception ex) { allOk = false; log.Add("lock screen restore FAILED: " + ex.Message); }
            }
            if (allOk && Path.GetFileName(dir) != OriginalName) { try { Directory.Delete(dir, true); } catch (Exception) { } }
            if (!allOk) log.Add("The backup was kept, so you can try Undo again.");
            return allOk;
        }

        // Windows may re-encode the lock screen copy it hands back, so compare pixels, not bytes.
        static bool SameDecodedPixels(string a, string b)
        {
            try { return Imaging.Compare(Imaging.Decode(a, System.Drawing.Color.Black), Imaging.Decode(b, System.Drawing.Color.Black)).Zero; }
            catch (Exception) { return false; }
        }

        static bool SameBytes(string a, string b)
        {
            var fa = new FileInfo(a); var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
        }
    }
}
