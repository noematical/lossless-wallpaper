// The always-on part: tray icon, watched folder, arrival pop-up and history window.
// Accessibility: everything works by keyboard (Tab, arrow keys, Alt+underlined letter, Enter,
// Esc), uses standard Windows controls that screen readers can read, uses the system font and
// system colours, and nothing times out. This process never loads WPF or WinRT; installs run in
// a short-lived worker (the same exe with arguments).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LosslessWallpaper
{
    static class Dpi
    {
        static float scale = -1;
        // Scales a size given for 100% (96 DPI) to the screen's scale.
        public static int S(int px)
        {
            if (scale < 0)
            {
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f; }
                catch (Exception) { scale = 1f; }
            }
            return (int)Math.Round(px * scale);
        }
    }

    sealed class WorkerResult
    {
        public Dictionary<string, string> Values = new Dictionary<string, string>();
        public List<string> Notes = new List<string>(), Checks = new List<string>(), Warnings = new List<string>(), Lines = new List<string>();
        public string this[string k] { get { string v; return Values.TryGetValue(k, out v) ? v : ""; } }
        public bool Ok { get { return this["ok"] == "true"; } }

        public static WorkerResult Failed(string message)
        {
            var r = new WorkerResult(); r.Values["ok"] = "false"; r.Values["message"] = message; return r;
        }

        public static WorkerResult Read(string path)
        {
            if (!File.Exists(path)) return Failed("The worker ended without a result.");
            var r = new WorkerResult();
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                if (k == "note") r.Notes.Add(v);
                else if (k == "check") r.Checks.Add(v);
                else if (k == "warning") r.Warnings.Add(v);
                else if (k == "line") r.Lines.Add(v);
                else r.Values[k] = v;
            }
            try { File.Delete(path); } catch (Exception) { }
            return r;
        }
    }

    sealed class TrayApp : ApplicationContext
    {
        public const string QuitEventName = @"Local\LosslessWallpaper.Quit";
        const int MaxLockChecks = 5;
        const int WorkerTimeoutMs = 3 * 60 * 1000;

        readonly Settings settings = Settings.Load();
        readonly NotifyIcon icon = new NotifyIcon();
        readonly Control ui = new Control();          // marshals background events to the UI thread
        readonly Queue<string> queue = new Queue<string>();
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        FileSystemWatcher watcher;
        List<Entry> history = Library.Load();
        HistoryForm historyForm;
        bool busy;          // a worker is running
        bool dialogOpen;    // the arrival pop-up is open
        int workerSeq;
        string lastInstalled;
        readonly System.Windows.Forms.Timer driftTimer = new System.Windows.Forms.Timer();
        ToolStripMenuItem modeLossless, modeScaling, startup;

        public TrayApp()
        {
            ui.CreateControl();
            Directory.CreateDirectory(settings.DropFolder);
            settings.Save();

            icon.Icon = Theme.TrayIcon();
            icon.Text = "Lossless Wallpaper";
            var menu = new ContextMenuStrip();
            menu.Items.Add("Open &history", null, delegate { ShowHistory(); });
            menu.Items.Add("Open &drop folder", null, delegate { Process.Start("explorer.exe", "\"" + settings.DropFolder + "\""); });
            menu.Items.Add(new ToolStripSeparator());
            modeLossless = new ToolStripMenuItem("True &lossless (refuse images that are not screen-sized)", null, delegate { SetMode("lossless"); });
            modeScaling = new ToolStripMenuItem("Allow &scaling (offer crop or fit)", null, delegate { SetMode("scaling"); });
            menu.Items.Add(modeLossless); menu.Items.Add(modeScaling);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("&Undo last change", null, delegate { RunUndo(false); });
            menu.Items.Add("&Restore what I had before this app", null, delegate { RunUndo(true); });
            startup = new ToolStripMenuItem("Start when I &sign in", null, delegate { ToggleStartup(); });
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("E&xit", null, delegate { ExitApp(); });
            menu.Opening += delegate { RefreshMenu(); };   // picks up changes made in Task Manager
            Theme.HookMenu(menu);
            icon.ContextMenuStrip = menu;
            // One left click opens the history (right click still opens the menu).
            icon.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowHistory(); };
            icon.BalloonTipClicked += delegate { ShowHistory(); };
            RefreshMenu();
            icon.Visible = true;

            StartWatcher();

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            driftTimer.Interval = 30 * 60 * 1000;
            driftTimer.Tick += delegate { CheckDrift(); };
            driftTimer.Start();

            // "LosslessWallpaper.exe --quit" signals this event so the app can be closed cleanly.
            var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
            ThreadPool.RegisterWaitForSingleObject(quit, delegate { ui.BeginInvoke((MethodInvoker)ExitApp); }, null, -1, true);

            icon.ShowBalloonTip(5000, "Lossless Wallpaper is running", "Drop images into " + settings.DropFolder, ToolTipIcon.Info);
            Native.TrimMemory();
        }

        void Notify(string title, string body, ToolTipIcon kind) { icon.ShowBalloonTip(10000, title, string.IsNullOrEmpty(body) ? " " : body, kind); }

        // ---------- menu ----------
        void SetMode(string m) { settings.Mode = m; settings.Save(); RefreshMenu(); }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        // Task Manager's Startup apps page lists the Run value and records "Disabled" separately,
        // under StartupApproved (first byte odd = disabled). The app reads and writes both, so its
        // switch, the tray menu and Task Manager always agree.
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        public bool StartupOn()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                if (k == null || k.GetValue("LosslessWallpaper") == null) return false;
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
            {
                var b = k == null ? null : k.GetValue("LosslessWallpaper") as byte[];
                if (b != null && b.Length > 0 && (b[0] & 1) == 1) return false;
            }
            return true;
        }
        public void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("LosslessWallpaper", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("LosslessWallpaper", false);
            }
            // No entry here means "enabled"; removing it clears a "Disabled" set in Task Manager.
            using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                if (k != null) k.DeleteValue("LosslessWallpaper", false);
            RefreshMenu();
            if (historyForm != null && !historyForm.IsDisposed) historyForm.ShowStartup(StartupOn());
        }
        void ToggleStartup() { SetStartup(!StartupOn()); }
        void RefreshMenu()
        {
            modeLossless.Checked = settings.Mode == "lossless";
            modeScaling.Checked = settings.Mode == "scaling";
            startup.Checked = StartupOn();
        }

        // Light / dark, accent colour or high contrast changed: windows opened from now on, the
        // menu and the tray icon follow.
        void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category != UserPreferenceCategory.General && e.Category != UserPreferenceCategory.Color &&
                e.Category != UserPreferenceCategory.VisualStyle && e.Category != UserPreferenceCategory.Accessibility) return;
            ui.BeginInvoke((MethodInvoker)delegate
            {
                Theme.Refresh();
                var old = icon.Icon;
                icon.Icon = Theme.TrayIcon();
                if (old != null) old.Dispose();
            });
        }

        void ExitApp()
        {
            if (watcher != null) watcher.EnableRaisingEvents = false;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            icon.Visible = false;
            ExitThread();
        }

        // ---------- arrivals ----------
        static readonly string[] SkipExt = { ".tmp", ".crdownload", ".part", ".partial", ".download", ".ini", ".lnk", ".db" };

        void StartWatcher()
        {
            if (watcher != null) { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
            Directory.CreateDirectory(settings.DropFolder);
            watcher = new FileSystemWatcher(settings.DropFolder);
            watcher.IncludeSubdirectories = false;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite;
            watcher.Created += OnArrived;
            watcher.Changed += OnArrived;   // a file replaced under the same name
            watcher.Renamed += delegate(object s, RenamedEventArgs e) { OnArrived(s, e); };
            // Too many files at once, or the folder was removed: start again and look at what is there now.
            watcher.Error += delegate { ui.BeginInvoke((MethodInvoker)delegate { StartWatcher(); Rescan(); }); };
            watcher.EnableRaisingEvents = true;
        }

        void Rescan()
        {
            foreach (var f in Directory.GetFiles(settings.DropFolder))
                OnArrived(null, new FileSystemEventArgs(WatcherChangeTypes.Created, settings.DropFolder, Path.GetFileName(f)));
        }

        enum Ready { Yes, Skip, GaveUp }

        void OnArrived(object sender, FileSystemEventArgs e)
        {
            string path = e.FullPath;
            if (SkipExt.Contains(Path.GetExtension(path).ToLowerInvariant())) return;
            if (Directory.Exists(path)) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Ready r = WaitUntilReady(path);
                ui.BeginInvoke((MethodInvoker)delegate
                {
                    if (r == Ready.GaveUp) { Notify("Could not open " + Path.GetFileName(path), "It was still being copied or another program had it open. Drop it again when it is ready.", ToolTipIcon.Warning); return; }
                    if (r != Ready.Yes) return;
                    string key;
                    try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks + "|" + new FileInfo(path).Length; }
                    catch (Exception) { return; }
                    if (seen.Contains(key) || queue.Contains(path)) return;
                    seen.Add(key);
                    queue.Enqueue(path);
                    Pump();
                });
            });
        }

        // Waits until the file has finished copying: it can be opened exclusively and its size has
        // stopped changing. Gives up after about two minutes.
        static Ready WaitUntilReady(string path)
        {
            long last = -1;
            for (int i = 0; i < 240; i++)
            {
                try
                {
                    if (!File.Exists(path)) return Ready.Skip;
                    if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System)) != 0) return Ready.Skip;
                    long len;
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) len = fs.Length;
                    if (len > 0 && len == last) return Ready.Yes;
                    last = len;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { return Ready.GaveUp; }
                Thread.Sleep(500);
            }
            return Ready.GaveUp;
        }

        // One thing at a time: never a second pop-up or worker while one is open or running.
        void Pump()
        {
            if (busy || dialogOpen || queue.Count == 0) return;
            string path = queue.Dequeue();
            if (!File.Exists(path)) { Pump(); return; }

            Size? size = ProbeSize(path);
            int W, H; Native.PrimaryScreenPixels(out W, out H);
            dialogOpen = true;
            string target = null, fit = null;
            using (var form = new ChoiceForm(path, size, new Size(W, H), settings))
            {
                if (form.ShowDialog() == DialogResult.OK) { target = form.SaveOnly ? "none" : form.Target; fit = form.Fit; }
                form.WaitForPreview();   // the preview reader has let go of the file before a worker starts
            }
            dialogOpen = false;
            Native.TrimMemory();
            if (target == null) { Pump(); return; }
            if (target != "none") settings.LastTarget = target;   // saving remembers no "install as" choice
            if (fit != "exact") settings.LastFit = fit;
            settings.Save();
            StartWorker(new[] { "--install", path, "--target", target, "--fit", fit, "--name", Path.GetFileName(path) }, OnInstallDone);
        }

        // Reads only the header through GDI+, which the tray app already has loaded, and allows for a
        // camera rotation tag. Returns null for formats GDI+ can't read (for example WebP); the worker decides.
        static Size? ProbeSize(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var img = Image.FromStream(fs, false, false))
                {
                    Size s = img.Size;
                    if (img.PropertyIdList.Contains(0x0112))
                    {
                        var p = img.GetPropertyItem(0x0112);
                        int o = p.Value.Length >= 2 ? BitConverter.ToUInt16(p.Value, 0) : 1;
                        if (o >= 5 && o <= 8) s = new Size(s.Height, s.Width);
                    }
                    return s;
                }
            }
            catch (Exception) { return null; }
        }

        bool StartWorker(string[] args, Action<WorkerResult> done)
        {
            if (busy) return false;
            busy = true;
            int seq = ++workerSeq;
            string result = Path.Combine(Path.GetTempPath(), "lw-result-" + Guid.NewGuid().ToString("N") + ".txt");
            var all = args.Concat(new[] { "--result", result }).Select(Quote);
            var p = new Process();
            p.StartInfo = new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", all));
            p.StartInfo.UseShellExecute = false;
            p.EnableRaisingEvents = true;

            var timeout = new System.Windows.Forms.Timer { Interval = WorkerTimeoutMs };
            bool finished = false;
            Action<WorkerResult> finish = delegate(WorkerResult r)
            {
                if (finished) return;            // the first of "exited" and "timed out" wins
                finished = true;
                timeout.Stop(); timeout.Dispose();
                if (seq == workerSeq) busy = false;
                done(r);
                Pump();
            };
            timeout.Tick += delegate
            {
                // Report and move on; the worker is left to finish or fail on its own (nothing is killed).
                finish(WorkerResult.Failed("Windows did not finish within 3 minutes. Check the result in the history later, or try again."));
            };
            p.Exited += delegate
            {
                ui.BeginInvoke((MethodInvoker)delegate { p.Dispose(); finish(WorkerResult.Read(result)); });
            };
            try { p.Start(); timeout.Start(); }
            catch (Exception ex)
            {
                busy = false; timeout.Dispose();
                Notify("Could not start the installer", ex.Message, ToolTipIcon.Error);
                return false;
            }
            return true;
        }

        static string Quote(string s) { return "\"" + s.Replace("\"", "") + "\""; }

        // Full-screen preview in its own short-lived process (see Preview.cs). One at a time;
        // it reads files only, so it may run while an install is busy.
        static Process previewProc;
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
        public static void StartPreview(string file, string fit, string target)
        {
            if (previewProc != null)
            {
                bool running;
                try { running = !previewProc.HasExited; } catch (Exception) { running = false; }
                if (running) return;
                previewProc.Dispose(); previewProc = null;
            }
            var all = new[] { "--preview", file, "--fit", fit, "--target", target }.Select(Quote);
            try
            {
                AllowSetForegroundWindow(-1);   // so the preview gets the keyboard (Shift, Esc) at once

                var psi = new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", all));
                psi.UseShellExecute = false;
                previewProc = Process.Start(psi);
            }
            catch (Exception ex) { MessageBox.Show("Could not open the preview: " + ex.Message, "Lossless Wallpaper"); }
        }

        void OnInstallDone(WorkerResult r)
        {
            if (!r.Ok)
            {
                Notify(r["refused"] == "true" ? "Not installed: wrong size" : "Not installed", r["message"], ToolTipIcon.Warning);
                return;
            }
            if (r["target"] == "none")
            {
                // Saved for later. An image already in the history keeps its earlier record.
                var known = history.FirstOrDefault(x => x.Id == r["id"]);
                if (known != null)
                {
                    Notify("Already in your history", (known.OriginalName.Length > 0 ? known.OriginalName : r["name"]) + " is saved there. Open the history to apply it.", ToolTipIcon.Info);
                    return;
                }
                var s = new Entry();
                s.Id = r["id"]; s.File = r["file"]; s.OriginalName = r["name"]; s.Mode = r["mode"];
                int sw, sh; int.TryParse(r["width"], out sw); int.TryParse(r["height"], out sh); s.Width = sw; s.Height = sh;
                s.Added = s.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                s.LastTarget = "none";
                s.LastCheck = string.Join(" | ", r.Notes.Select(n => "note: " + n).ToArray());
                s.LastPass = false;
                Library.Upsert(history, s);
                Library.Save(history);
                ReloadHistoryWindow();
                Notify("Saved", r["name"] + " is in your history. Open it there to preview or set it.", ToolTipIcon.Info);
                return;
            }
            var e = new Entry();
            e.Id = r["id"]; e.File = r["file"]; e.OriginalName = r["name"]; e.Mode = r["mode"];
            int w, h; int.TryParse(r["width"], out w); int.TryParse(r["height"], out h); e.Width = w; e.Height = h;
            e.Added = e.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            e.LastTarget = r["target"];
            var parts = new List<string>(r.Checks);
            parts.AddRange(r.Notes.Select(n => "note: " + n));
            e.LastCheck = string.Join(" | ", parts.ToArray());
            e.LastPass = r["pass"] == "true";
            e.LockChecks = 0;
            bool lockSet = e.LastTarget == "lockscreen" || e.LastTarget == "both";
            if (lockSet)
            {
                // A newer lock screen replaces any older one still waiting for its check.
                foreach (var old in history.Where(x => x.Id != e.Id && x.LastCheck.Contains("lock screen: PENDING")))
                    old.LastCheck = old.LastCheck.Replace("lock screen: PENDING", "lock screen: SUPERSEDED (replaced before it could be checked)");
            }
            if (r["pending"] == "true") e.LastCheck += " | since=" + r["since"];
            Library.Upsert(history, e);
            Library.Save(history);
            if (e.LastTarget == "wallpaper" || e.LastTarget == "both") lastInstalled = e.File;
            ReloadHistoryWindow();

            string title = e.LastPass ? "Installed: check passed" : (r["pending"] == "true" ? "Installed: lock screen check waits for your next lock" : "Installed, but the check failed");
            string body = string.Join("\n", r.Checks.ToArray());
            if (r.Warnings.Count > 0) body += "\n" + r.Warnings.Count + " warning(s): open the history to read them.";
            Notify(title, body, e.LastPass || r["pending"] == "true" ? ToolTipIcon.Info : ToolTipIcon.Error);
        }

        // ---------- history actions ----------
        void ShowHistory()
        {
            if (historyForm == null || historyForm.IsDisposed)
            {
                historyForm = new HistoryForm(this);
                historyForm.FormClosed += delegate { historyForm = null; Native.TrimMemory(); };
                historyForm.Reload(history);
            }
            historyForm.Show();
            if (historyForm.WindowState == FormWindowState.Minimized) historyForm.WindowState = FormWindowState.Normal;
            historyForm.Activate();
        }

        void ReloadHistoryWindow() { if (historyForm != null && !historyForm.IsDisposed) historyForm.Reload(history); }

        bool Busy()
        {
            if (!busy && !dialogOpen) return false;
            MessageBox.Show("Another install or check is still running. Try again in a moment.", "Lossless Wallpaper");
            return true;
        }

        public void Apply(Entry e, string target)
        {
            if (Busy()) return;
            if (!File.Exists(e.File)) { MessageBox.Show("The app's copy of this image is missing.", "Lossless Wallpaper"); return; }
            StartWorker(new[] { "--install", e.File, "--target", target, "--fit", "exact", "--name", e.OriginalName }, OnInstallDone);
        }

        public void Delete(Entry e)
        {
            string why = Library.Delete(history, e);
            if (why != null) { MessageBox.Show(why, "Lossless Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Library.Save(history);
            ReloadHistoryWindow();
        }

        public void RunUndo(bool original)
        {
            if (Busy()) return;
            if (original && MessageBox.Show("Restore the wallpaper and lock screen you had before you first used this app?", "Lossless Wallpaper",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var args = original ? new[] { "--undo", "--original" } : new[] { "--undo" };
            StartWorker(args, delegate(WorkerResult r)
            {
                lastInstalled = null;   // our own undo is not "something else" changing the wallpaper
                string body = r.Ok ? string.Join("\n", r.Lines.ToArray()) : r["message"];
                string title = !r.Ok ? "Nothing undone" : (r["complete"] == "true" ? "Undone" : "Undo incomplete");
                Notify(title, body, r.Ok && r["complete"] == "true" ? ToolTipIcon.Info : ToolTipIcon.Warning);
            });
        }

        // ---------- after lock / unlock ----------
        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionUnlock) return;
            ui.BeginInvoke((MethodInvoker)delegate { RecheckPending(); CheckDrift(); });
        }

        void RecheckPending()
        {
            if (busy || dialogOpen) return;
            // Only the newest lock screen install can still be the one on screen.
            var e = history.FirstOrDefault(x => x.LastCheck.Contains("lock screen: PENDING"));
            if (e == null) return;
            if (++e.LockChecks > MaxLockChecks)
            {
                e.LastCheck = e.LastCheck.Replace("lock screen: PENDING", "lock screen: NOT CHECKED (Windows' drawn copy could not be found)");
                Library.Save(history); ReloadHistoryWindow();
                return;
            }
            Library.Save(history);
            int i = e.LastCheck.IndexOf("since=");
            string since = i >= 0 ? e.LastCheck.Substring(i + 6).Trim() : "0";
            StartWorker(new[] { "--check-lock", e.File, "--since", since }, delegate(WorkerResult r)
            {
                if (!r.Ok || r["pending"] == "true") return;
                var parts = e.LastCheck.Split(new[] { " | " }, StringSplitOptions.None)
                    .Where(p => !p.StartsWith("lock screen:") && !p.StartsWith("since=")).ToList();
                parts.InsertRange(0, r.Checks);
                e.LastCheck = string.Join(" | ", parts.ToArray());
                e.LastPass = !parts.Any(p => p.Contains(": FAIL"));
                Library.Save(history);
                ReloadHistoryWindow();
                Notify(r["pass"] == "true" ? "Lock screen check passed" : "Lock screen check failed", string.Join("\n", r.Checks.ToArray()),
                    r["pass"] == "true" ? ToolTipIcon.Info : ToolTipIcon.Error);
            });
        }

        void CheckDrift()
        {
            if (lastInstalled == null) return;
            string now = Installer.CurrentWallpaperPath();
            if (!string.Equals(now, lastInstalled, StringComparison.OrdinalIgnoreCase))
            {
                Notify("Your wallpaper was changed by something else", "Now: " + now, ToolTipIcon.Warning);
                lastInstalled = null;
            }
        }
    }

    // The arrival pop-up. Left: the picture of the screen, showing how the image will sit on it.
    // Right: the image's name and sizes, a one-line status, "Install as" and, for images that are
    // not screen-sized, "Scaling". Bottom: Install / Cancel.
    sealed class ChoiceForm : Form
    {
        readonly RadioButton rWall, rLock, rBoth, rCrop, rFit;
        readonly CheckBox allowScale;
        readonly Control sizeGroup;
        readonly bool offSize;
        readonly bool sizeKnown;
        readonly Settings settings;
        readonly ScreenPreview hero = new ScreenPreview();
        readonly ManualResetEvent previewDone = new ManualResetEvent(true);
        readonly Font nameFont, mono;
        bool shown;
        // True when the user chose "Save for later": prepare and keep the image, apply nothing.
        public bool SaveOnly { get; private set; }

        public string Target { get { return rLock.Checked ? "lockscreen" : (rBoth.Checked ? "both" : "wallpaper"); } }
        public string Fit
        {
            get
            {
                if (!sizeGroup.Enabled) return "exact";
                return rFit.Checked ? "fit" : "crop";
            }
        }

        public ChoiceForm(string path, Size? size, Size screen, Settings s)
        {
            settings = s;
            string name = Path.GetFileName(path);
            Text = "Install image - Lossless Wallpaper";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;   // sizes below are scaled explicitly with Dpi.S
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;   // centred in OnLoad
            ShowInTaskbar = true;
            TopMost = true;
            Theme.Chrome(this);
            nameFont = Theme.Display(Font.SizeInPoints * 1.75f);
            mono = Theme.Mono(Font.SizeInPoints);
            int pad = Dpi.S(24), nudge = Dpi.S(2), tab = 0;

            sizeKnown = size.HasValue;
            offSize = !sizeKnown || size.Value != screen;

            // Two columns, placed by hand on a 4 px grid (the widths are fixed, so the heights
            // follow from the text).
            int lw = Dpi.S(400), gap = Dpi.S(32), rw = Dpi.S(408), top = Dpi.S(16), m = ScreenPreview.Inset;
            var root = new Panel { Location = Point.Empty };
            Controls.Add(root);

            // ----- left: the screen -----
            double shape = (double)screen.Height / Math.Max(1, screen.Width);
            int hw = lw, hh = (int)Math.Round(lw * shape), tallest = Dpi.S(328);
            if (hh > tallest) { hh = tallest; hw = Math.Max(Dpi.S(120), (int)Math.Round(tallest / shape)); }
            hero.SetBounds(pad + (lw - hw) / 2 - m, top - m, hw + m * 2, hh + m * 2);
            hero.AccessibleName = "Preview of " + name;
            root.Controls.Add(hero);
            int ly = top + hh;

            // ----- right: what arrived -----
            var col = new Panel { TabIndex = 0 };
            col.SetBounds(pad + lw + gap - nudge, top, rw + nudge * 2, 10);
            root.Controls.Add(col);
            int x = nudge, y = 0;
            var title = new Label { Text = name, Font = nameFont, UseMnemonic = false, AutoEllipsis = true, AutoSize = false };
            title.SetBounds(0, y, rw + nudge, nameFont.Height + Dpi.S(4));
            col.Controls.Add(title);
            y += title.Height + Dpi.S(8);
            // The two sizes, one above the other, so a match (or a mismatch) shows at a glance.
            int figures = Dpi.S(64);
            y = Fact(col, "Image", sizeKnown ? size.Value.Width + " \u00D7 " + size.Value.Height : "not known yet", sizeKnown, figures, y, rw) + Dpi.S(4);
            y = Fact(col, "Screen", screen.Width + " \u00D7 " + screen.Height, true, figures, y, rw) + Dpi.S(12);
            var status = new DotLabel();
            if (!sizeKnown) { status.Text = "Image size unknown until it is decoded"; status.Dot = Theme.Muted; }
            else if (offSize) { status.Text = "Not screen-sized: needs scaling, so not lossless"; status.Dot = Theme.Caution; }
            else { status.Text = "Exact match: can be installed losslessly"; status.Dot = Theme.Good; }
            y = Place(col, status, 0, y, rw);

            // ----- install as -----
            y = Place(col, Section("Install as"), 0, y + Dpi.S(24), rw) + Dpi.S(8);
            var track = new SegTrack { AccessibleRole = AccessibleRole.Grouping, AccessibleName = "Install as", TabIndex = tab++ };
            track.SetBounds(x, y, rw, Dpi.S(44));
            rWall = new SegRadio { Text = "&Wallpaper" };
            rLock = new SegRadio { Text = "&Lock screen" };
            rBoth = new SegRadio { Text = "&Both" };
            track.Controls.AddRange(new Control[] { rWall, rLock, rBoth });
            track.Arrange();
            col.Controls.Add(track);
            y += track.Height;
            if (s.LastTarget == "lockscreen") rLock.Checked = true; else if (s.LastTarget == "both") rBoth.Checked = true; else rWall.Checked = true;

            // ----- scaling (only for images that are not screen-sized) -----
            int ch = Dpi.S(60);
            var sp = new Panel { TabIndex = tab + 1 };
            rCrop = new OptionCard { Text = "&Crop to fill the screen (no bars)", Bars = false };
            rFit = new OptionCard { Text = "&Fit inside the screen (bars at the edges)", Bars = true };
            rCrop.SetBounds(0, 0, rw, ch); rFit.SetBounds(0, ch + Dpi.S(8), rw, ch);
            sp.Controls.Add(rCrop); sp.Controls.Add(rFit);
            sizeGroup = sp;
            if (s.LastFit == "fit") rFit.Checked = true; else rCrop.Checked = true;

            if (offSize)
            {
                y = Place(col, Section("Scaling"), 0, y + Dpi.S(24), rw) + Dpi.S(8);
                if (s.Mode == "lossless")
                {
                    allowScale = new CheckEx
                    {
                        TabIndex = tab,
                        Text = sizeKnown ? "&Allow scaling for this image" : "&Allow scaling if it turns out not to be screen-sized"
                    };
                    allowScale.CheckedChanged += delegate { sizeGroup.Enabled = allowScale.Checked; };
                    y = Place(col, allowScale, x - Dpi.S(4), y - Dpi.S(4), rw + Dpi.S(4));
                    var why = new Label { ForeColor = Theme.Muted, UseMnemonic = false, Text = "True lossless mode refuses images that are not screen-sized" };
                    y = Place(col, why, Dpi.S(24), y - Dpi.S(2), rw - Dpi.S(24)) + Dpi.S(12);
                    sizeGroup.Enabled = false;
                }
                sp.SetBounds(x, y, rw, ch * 2 + Dpi.S(8));
                col.Controls.Add(sp);
                y += sp.Height;
                // Under the picture; it stays readable while the choices are switched off.
                var note = new Label
                {
                    ForeColor = Theme.Text2,
                    Text = "Scaling changes pixels, so this install is labelled \"scaled\", not lossless. The app still checks that Windows adds no further loss."
                };
                ly = Place(root, note, pad - nudge, top + hh + Dpi.S(20), lw);
            }
            else sizeGroup.Enabled = false;
            tab += 2;
            col.Height = y;
            if (y < hh) col.Top = top + (hh - y) / 2;      // few choices: centre them beside the picture

            // ----- buttons -----
            y = Math.Max(ly, col.Bottom) + pad;
            int full = pad + lw + gap + rw + pad;
            var foot = new Panel { BackColor = Theme.Footer, TabIndex = 1 };
            foot.SetBounds(0, y, full, Dpi.S(36) + Dpi.S(16) * 2);
            foot.Paint += delegate(object o, PaintEventArgs e) { using (var p = new Pen(Theme.Border)) e.Graphics.DrawLine(p, 0, 0, foot.Width, 0); };
            root.Controls.Add(foot);
            var ok = new ButtonEx { Text = "&Install", DialogResult = DialogResult.OK, Primary = true, TabIndex = 0 };
            var save = new ButtonEx { Text = "&Save for later", DialogResult = DialogResult.OK, TabIndex = 1, AccessibleDescription = "Prepare and check the image and keep it in the history without applying it" };
            var cancel = new ButtonEx { Text = "Cancel", DialogResult = DialogResult.Cancel, TabIndex = 2 };
            save.Click += delegate { SaveOnly = true; };
            // Preview: full screen, exactly as it would be installed (cropped or fitted as chosen).
            var preview = new ButtonEx { Text = "&Preview", TabIndex = 3, AccessibleDescription = "Show the image full screen as it would be installed. Hold Shift to compare with what you have now." };
            preview.Click += delegate { TrayApp.StartPreview(path, offSize ? (rFit.Checked ? "fit" : "crop") : Fit, Target); };
            foot.Controls.Add(ok); foot.Controls.Add(save); foot.Controls.Add(cancel); foot.Controls.Add(preview);
            int halo = ButtonEx.Halo;
            int bw = Math.Max(Dpi.S(104) + halo * 2, Math.Max(ok.PreferredSize.Width, cancel.PreferredSize.Width)), bh = ok.PreferredSize.Height;
            int sw = Math.Max(bw, save.PreferredSize.Width);
            ok.AutoSize = save.AutoSize = cancel.AutoSize = preview.AutoSize = false;
            cancel.SetBounds(foot.Width - pad + halo - bw, (foot.Height - bh) / 2, bw, bh);
            ok.SetBounds(cancel.Left - Dpi.S(8) + halo * 2 - bw, cancel.Top, bw, bh);
            // Secondary action on the left of the footer, away from Install.
            save.SetBounds(pad - halo, cancel.Top, sw, bh);
            preview.SetBounds(save.Right - halo * 2 + Dpi.S(8), cancel.Top, bw, bh);
            y += foot.Height;

            root.Size = new Size(full, y);
            ClientSize = root.Size;

            rCrop.CheckedChanged += delegate { ShowFraming(true); };
            rFit.CheckedChanged += delegate { ShowFraming(true); };
            if (allowScale != null) allowScale.CheckedChanged += delegate { ShowFraming(true); };
            ShowFraming(false);

            AcceptButton = ok; CancelButton = cancel;
            // A known off-size image in True lossless mode can't be installed until scaling is
            // allowed, so Install stays off instead of failing after Enter.
            if (allowScale != null && sizeKnown)
            {
                ok.Enabled = save.Enabled = false;
                allowScale.CheckedChanged += delegate { ok.Enabled = save.Enabled = allowScale.Checked; };
                Shown += delegate { Activate(); allowScale.Focus(); };
            }
            else Shown += delegate { Activate(); ok.Focus(); };
            Shown += delegate { shown = true; StartPreview(path, size); };
        }

        static Label Section(string text) { return new Label { Text = text, ForeColor = Theme.Text2, UseMnemonic = false }; }

        // One "label  figure" line; figures start in one column and use the mono face.
        int Fact(Control parent, string label, string value, bool figure, int column, int y, int width)
        {
            var a = new Label { Text = label, ForeColor = Theme.Text2, UseMnemonic = false };
            var b = new Label { Text = value, UseMnemonic = false };
            if (figure) b.Font = mono; else b.ForeColor = Theme.Text2;
            int bottom = Place(parent, b, column, y, width - column);
            Place(parent, a, 0, y, column);
            a.Top = y + (b.Height - a.Height) / 2;
            return bottom;
        }

        // Adds a control at (x, y), as wide as its text needs up to "width"; returns its bottom.
        static int Place(Control parent, Control c, int x, int y, int width)
        {
            parent.Controls.Add(c);
            c.MaximumSize = new Size(width, 0);
            Size want = c.GetPreferredSize(new Size(width, 0));
            c.AutoSize = false;
            c.SetBounds(x, y, Math.Min(width, want.Width), want.Height);
            return c.Bottom;
        }

        // The picture of the screen follows the choices: exact, cropped, fitted with bars, or (while
        // scaling is not allowed, or the size is not known) the whole image, not yet placed. Its
        // crop marks take the colour of the result: green exact, amber scaled, grey undecided.
        void ShowFraming(bool glide)
        {
            Framing f; string says; Color marks = Theme.Caution;
            if (!offSize) { f = Framing.Exact; says = "Exact match"; marks = Theme.Good; }
            else if (!sizeKnown) { f = Framing.Loose; says = "No preview"; marks = Theme.Muted; }
            else if (!sizeGroup.Enabled) { f = Framing.Loose; says = "Not screen-sized, not placed yet"; marks = Theme.Muted; }
            else if (rFit.Checked) { f = Framing.Fit; says = "Scaled to fit, with bars"; }
            else { f = Framing.Crop; says = "Scaled and cropped to fill"; }
            hero.Frame(f, settings.BarColor, marks, says, glide && shown);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + Math.Max(0, (wa.Height - Height) / 2));
        }

        // The preview is read on a background thread after the window is up, so a big image never
        // delays the pop-up. Skipped when GDI+ can't read the image or it is very large.
        void StartPreview(string path, Size? size)
        {
            if (!size.HasValue || (long)size.Value.Width * size.Value.Height > 40000000L) return;
            int w = hero.Width - ScreenPreview.Inset * 2, h = hero.Height - ScreenPreview.Inset * 2;
            previewDone.Reset();
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap b = null;
                try { b = Pictures.Load(path, w, h, true); }
                finally { previewDone.Set(); }
                if (b == null) return;
                try { BeginInvoke((MethodInvoker)delegate { if (IsDisposed) b.Dispose(); else hero.SetImage(b); }); }
                catch (Exception) { b.Dispose(); }   // the window closed first
            });
        }

        public void WaitForPreview() { previewDone.WaitOne(3000); }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                nameFont.Dispose(); mono.Dispose();
                if (sizeGroup.Parent == null) sizeGroup.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // The history window: what this app installed and how each pixel check went. A table on the
    // left (a standard list underneath), the selected image and its full check result on the right.
    sealed class HistoryForm : Form
    {
        readonly TrayApp app;
        readonly ListEx list = new ListEx();
        readonly Label summary = new Label();
        readonly DotLabel calm = new DotLabel();
        readonly CardFlow banner = new CardFlow();
        readonly TableLayoutPanel layout;
        readonly CardPanel detailCard = new CardPanel();
        readonly ScreenPreview hero = new ScreenPreview();
        readonly TextBox details = new TextBox();
        readonly ButtonEx previewButton = new ButtonEx();
        readonly CheckEx startup = new CheckEx();
        bool syncing;

        public void ShowStartup(bool on)
        {
            syncing = true;
            try { startup.Checked = on; } finally { syncing = false; }
        }
        readonly ImageList rowHeight = new ImageList();
        readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>();
        readonly HashSet<string> asked = new HashSet<string>();
        readonly Font titleFont, mono;
        readonly int pad = Dpi.S(24);
        string shownId;
        double shape = 0.625;     // height / width of the selected image
        bool sizing;
        volatile bool closed;

        const TextFormatFlags CellFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        static Size ThumbSize { get { return new Size(Dpi.S(64), Dpi.S(40)); } }

        public HistoryForm(TrayApp owner)
        {
            app = owner;
            Text = "History - Lossless Wallpaper";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;   // sizes below are scaled explicitly with Dpi.S
            StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(Dpi.S(1320), wa.Width), Math.Min(Dpi.S(760), wa.Height));
            MinimumSize = new Size(Math.Min(Dpi.S(1240), wa.Width), Math.Min(Dpi.S(560), wa.Height));
            Theme.Chrome(this);
            titleFont = Theme.Display(Font.SizeInPoints * 2.4f);
            mono = Theme.Mono(Font.SizeInPoints);

            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(pad, Dpi.S(8), pad, Dpi.S(20)) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Dpi.S(352)));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Title, with the counts as quiet text under it.
            var heading = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, Dpi.S(16)) };
            heading.Controls.Add(new Label { Text = "History", Font = titleFont, AutoSize = true, UseMnemonic = false, Margin = new Padding(-Dpi.S(4), 0, 0, 0) });
            summary.ForeColor = Theme.Text2; summary.AutoSize = true; summary.UseMnemonic = false; summary.Margin = new Padding(-Dpi.S(2), Dpi.S(4), 0, 0);
            heading.Controls.Add(summary);
            layout.Controls.Add(heading, 0, 0);

            // Top right, beside the title: the same setting as the tray menu's "Start when I sign
            // in" and Task Manager's Startup apps page.
            startup.Text = "Start when I &sign in";
            startup.AccessibleDescription = "Start Lossless Wallpaper in the background when you sign in to Windows. Also listed in Task Manager under Startup apps.";
            startup.Checked = app.StartupOn();
            startup.CheckedChanged += delegate { if (!syncing && startup.Checked != app.StartupOn()) app.SetStartup(startup.Checked); };
            startup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            startup.Margin = new Padding(0, Dpi.S(8), 0, 0);
            layout.Controls.Add(startup, 1, 0);
            Activated += delegate { ShowStartup(app.StartupOn()); };   // it may have been changed in Task Manager

            // What could stop the screen from showing the exact pixels: one quiet line when there
            // is nothing, a card with one entry per warning when there is something.
            var notes = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, Dpi.S(16)) };
            calm.ForeColor = Theme.Text2; calm.Margin = Padding.Empty;
            banner.FlowDirection = FlowDirection.TopDown; banner.WrapContents = false;
            banner.AutoSize = true; banner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            banner.Padding = new Padding(Dpi.S(16), Dpi.S(12), Dpi.S(16), Dpi.S(12)); banner.Margin = Padding.Empty;
            banner.AccessibleRole = AccessibleRole.Grouping; banner.AccessibleName = "Warnings";
            notes.Controls.Add(calm); notes.Controls.Add(banner);
            layout.Controls.Add(notes, 0, 1);
            layout.SetColumnSpan(notes, 2);

            var listCard = new CardPanel { Dock = DockStyle.Fill, Padding = new Padding(Dpi.S(8)), Margin = Padding.Empty };
            list.View = View.Details; list.FullRowSelect = true; list.MultiSelect = false; list.HideSelection = false;
            list.Dock = DockStyle.Fill; list.AccessibleName = "Installed images";
            list.Columns.Add("Image", Dpi.S(260)); list.Columns.Add("Check", Dpi.S(152)); list.Columns.Add("Size", Dpi.S(108)); list.Columns.Add("Mode", Dpi.S(108));
            list.Columns.Add("Last set as", Dpi.S(96)); list.Columns.Add("Last used", Dpi.S(140));
            list.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Delete) DeleteSelected(); };
            list.SelectedIndexChanged += delegate { ShowDetails(); };
            if (!Theme.HC)
            {
                // Same ListView (keyboard, screen reader), drawn in the window's colours.
                list.BorderStyle = BorderStyle.None;
                list.BackColor = Theme.Card; list.ForeColor = Theme.Text;
                list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
                rowHeight.ImageSize = new Size(1, Dpi.S(56)); list.SmallImageList = rowHeight;
                list.OwnerDraw = true;
                list.DrawColumnHeader += DrawHeader;
                list.DrawSubItem += DrawCell;
                list.GotFocus += delegate { list.Invalidate(); };
                list.LostFocus += delegate { list.Invalidate(); };
                Theme.Scrollbars(list); list.CoverEmpty = Theme.Dark;
            }
            list.Resize += delegate { FitColumns(); };
            listCard.Controls.Add(list);
            layout.Controls.Add(listCard, 0, 2);

            // The selected image: its picture, and the full check result and notes as text that
            // keyboard and screen-reader users can read.
            detailCard.Dock = DockStyle.Fill; detailCard.Margin = new Padding(Dpi.S(16), 0, 0, 0);
            hero.AccessibleName = "Preview of the selected image";
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.None;
            details.AccessibleName = "Details of the selected image"; details.TabStop = true;
            if (!Theme.HC)
            {
                details.BorderStyle = BorderStyle.None;
                details.BackColor = Theme.Card; details.ForeColor = Theme.Text;
                Theme.Scrollbars(details);
            }
            previewButton.Text = "&Preview full screen";
            previewButton.AccessibleDescription = "Show the selected image full screen. Hold Shift to compare with what you have now.";
            previewButton.Click += delegate
            {
                var e = Selected();
                if (e == null) return;
                if (!File.Exists(e.File)) { MessageBox.Show("The app's copy of this image is missing.", "Lossless Wallpaper"); return; }
                TrayApp.StartPreview(e.File, "exact", e.LastTarget == "lockscreen" ? "lockscreen" : "wallpaper");
            };
            detailCard.Controls.Add(hero); detailCard.Controls.Add(previewButton); detailCard.Controls.Add(details);
            detailCard.Resize += delegate { ArrangeDetail(); };
            layout.Controls.Add(detailCard, 1, 2);

            int halo = ButtonEx.Halo;
            var bar = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill, Margin = new Padding(-halo, Dpi.S(16) - halo, -halo, -halo) };
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            var right = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
            AddButton(left, "Set as &wallpaper", delegate { ApplySelected("wallpaper"); });
            AddButton(left, "Set as &lock screen", delegate { ApplySelected("lockscreen"); });
            AddButton(left, "Set as &both", delegate { ApplySelected("both"); });
            AddButton(left, "&Delete...", delegate { DeleteSelected(); });
            AddButton(left, "Show &file", delegate { var e = Selected(); if (e != null) Process.Start("explorer.exe", "/select,\"" + e.File + "\""); });
            AddButton(right, "&Undo last change", delegate { app.RunUndo(false); });
            var close = AddButton(right, "Close", delegate { Close(); });
            close.Margin = Padding.Empty;
            CancelButton = close;
            bar.Controls.Add(left, 0, 0); bar.Controls.Add(right, 1, 0);
            layout.Controls.Add(bar, 0, 3);
            layout.SetColumnSpan(bar, 2);
            Controls.Add(layout);

            layout.Resize += delegate { FitNotes(); };
            Shown += delegate { list.Focus(); FitNotes(); ArrangeDetail(); };
        }

        static Button AddButton(FlowLayoutPanel p, string text, EventHandler click)
        {
            var b = new ButtonEx { Text = text, Margin = new Padding(0, 0, Math.Max(0, Dpi.S(8) - ButtonEx.Halo * 2), 0) };
            b.Click += click;
            p.Controls.Add(b);
            return b;
        }

        // ---------- warnings ----------
        void ShowWarnings(List<string> w)
        {
            calm.Text = "No warnings: nothing found that would change what your screen shows";
            calm.Dot = Theme.Good;
            calm.Visible = w.Count == 0;
            banner.Visible = w.Count > 0;
            banner.SuspendLayout();
            while (banner.Controls.Count > 0) banner.Controls[0].Dispose();
            for (int i = 0; i < w.Count; i++)
                banner.Controls.Add(new DotLabel { Text = w[i], Dot = Theme.Caution, AccessibleName = "Warning: " + w[i], Margin = new Padding(0, i == 0 ? 0 : Dpi.S(8), 0, 0) });
            banner.ResumeLayout();
            FitNotes();
        }

        // The warning text wraps at the window's width.
        void FitNotes()
        {
            int w = Math.Max(Dpi.S(240), layout.ClientSize.Width - pad * 2);
            banner.MinimumSize = new Size(w, 0); banner.MaximumSize = new Size(w, 0);
            foreach (Control c in banner.Controls) c.MaximumSize = new Size(w - Dpi.S(32), 0);
            calm.MaximumSize = new Size(w, 0);
        }

        // ---------- list drawing (not used in high-contrast mode) ----------
        static Color StatusColor(Entry e) { return IsSaved(e) ? Theme.Muted : e.LastCheck.Contains("PENDING") ? Theme.Caution : (e.LastPass ? Theme.Good : Theme.Bad); }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            var r = Rectangle.FromLTRB(e.Bounds.Left + Dpi.S(12), e.Bounds.Top, e.Bounds.Right - Dpi.S(4), e.Bounds.Bottom);
            TextRenderer.DrawText(g, e.Header.Text, list.Font, r, Theme.Muted, CellFlags);
        }

        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            var g = e.Graphics;
            Rectangle r = e.Bounds;
            using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, r);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected)
            {
                // Selection: one surface step up across the whole row, with a hairline. Keyboard
                // focus adds the same outline every other control gets.
                Rectangle row = e.Item.Bounds;
                var bar = new RectangleF(row.Left + 1, row.Top + 2, row.Width - 3, row.Height - 5);
                g.SetClip(r, CombineMode.Intersect);
                Theme.FillRound(g, Theme.Selected, Theme.BtnEdge, bar, Dpi.S(8));
                if (list.Focused && list.FocusCues) Theme.FocusRing(g, bar, Dpi.S(8));
            }
            int x = r.Left + Dpi.S(12);
            var entry = e.Item.Tag as Entry;
            if (e.ColumnIndex == 0)
            {
                // Thumbnail (a plain tile until it has been read).
                Size ts = ThumbSize;
                var tr = new RectangleF(x, r.Top + (r.Height - ts.Height) / 2f, ts.Width - 1, ts.Height - 1);
                Bitmap t;
                using (var path = Theme.Round(tr, Dpi.S(4)))
                {
                    using (var b = new SolidBrush(Theme.Well)) g.FillPath(b, path);
                    if (entry != null && thumbs.TryGetValue(entry.Id, out t) && r.Width > ts.Width + Dpi.S(12))
                    {
                        var inner = g.Save();
                        g.SetClip(path, CombineMode.Intersect);
                        g.DrawImageUnscaled(t, (int)tr.X, (int)tr.Y);
                        g.Restore(inner);
                    }
                    using (var p = new Pen(Theme.Border)) g.DrawPath(p, path);
                }
                x += ts.Width + Dpi.S(12);
            }
            g.Restore(state);
            if (e.ColumnIndex == 1 && entry != null)
            {
                // The result: a dot and the word. The word carries the meaning; the dot only helps scanning.
                Theme.Dot(g, StatusColor(entry), x, r.Top + r.Height / 2f);
                x += Dpi.S(16);
            }
            if (r.Right - Dpi.S(4) <= x) return;
            bool figure = e.ColumnIndex == 2 || e.ColumnIndex == 5;     // pixel size and date line up in mono
            TextRenderer.DrawText(g, e.SubItem.Text, figure ? mono : list.Font, Rectangle.FromLTRB(x, r.Top, r.Right - Dpi.S(4), r.Bottom),
                e.ColumnIndex <= 1 ? Theme.Text : Theme.Text2, CellFlags);
        }

        // The first column takes the width the others leave, so the table fills the card.
        void FitColumns()
        {
            if (sizing || list.Columns.Count == 0) return;
            sizing = true;
            try
            {
                int others = 0;
                for (int i = 1; i < list.Columns.Count; i++) others += list.Columns[i].Width;
                int w = Math.Max(Dpi.S(160), list.ClientSize.Width - others);
                if (list.Columns[0].Width != w) list.Columns[0].Width = w;
            }
            finally { sizing = false; }
        }

        // Picture on top (in the image's own shape, with room for its crop marks), text below.
        void ArrangeDetail()
        {
            int p = Dpi.S(24), m = ScreenPreview.Inset, w = detailCard.ClientSize.Width - p * 2;
            if (w <= 0) return;
            int h = Math.Max(Dpi.S(80), Math.Min((int)Math.Round(w * shape), Math.Max(Dpi.S(80), detailCard.ClientSize.Height / 2)));
            hero.SetBounds(p - m, p - m, w + m * 2, h + m * 2);
            int halo = ButtonEx.Halo;
            Size pb = previewButton.GetPreferredSize(Size.Empty);
            previewButton.AutoSize = false;
            previewButton.SetBounds(p - halo, p + h + Dpi.S(16) - halo, pb.Width, pb.Height);
            int top = previewButton.Bottom - halo + Dpi.S(16);
            details.SetBounds(p, top, w + Dpi.S(8), Math.Max(0, detailCard.ClientSize.Height - top - p));
            FitDetailsScroll();
        }

        // Shows the details' scroll bar only when the text is longer than the pane.
        void FitDetailsScroll()
        {
            if (sizing || !details.IsHandleCreated) return;
            sizing = true;
            try
            {
                int lines = details.GetLineFromCharIndex(details.TextLength) + 1;
                var want = lines * (details.Font.Height + 2) > details.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
                if (details.ScrollBars != want)
                {
                    details.ScrollBars = want;
                    // A scroll bar changes where lines wrap; count again once.
                    lines = details.GetLineFromCharIndex(details.TextLength) + 1;
                    if (want == ScrollBars.None && lines * (details.Font.Height + 2) > details.ClientSize.Height) details.ScrollBars = ScrollBars.Vertical;
                }
            }
            finally { sizing = false; }
        }

        static bool IsSaved(Entry e) { return e.LastTarget == "none"; }

        static string Status(Entry e)
        {
            if (IsSaved(e)) return "saved";
            if (e.LastCheck.Contains("PENDING")) return "waiting for next lock";
            return e.LastPass ? "passed" : "failed";
        }

        static string TargetText(string t)
        {
            if (t == "wallpaper") return "Wallpaper";
            if (t == "lockscreen") return "Lock screen";
            if (t == "both") return "Both";
            if (t == "none") return "—";      // saved, never set
            return t;
        }

        static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

        // Keeps the user's selection (by image id) when the list refreshes in the background.
        public void Reload(List<Entry> all)
        {
            var keep = Selected();
            string keepId = keep == null ? null : keep.Id;
            ShowWarnings(Warnings.Current());

            // "4 images, 2 passed, 1 waiting, 1 failed": the counts, as they are.
            int passed = 0, waiting = 0, failed = 0, saved = 0;
            foreach (var e in all) { string st = Status(e); if (IsSaved(e)) saved++; else if (st == "passed") passed++; else if (st == "failed") failed++; else waiting++; }
            var parts = new List<string>();
            parts.Add(all.Count == 1 ? "1 image" : all.Count + " images");
            if (saved > 0) parts.Add(saved + " saved");
            if (passed > 0) parts.Add(passed + " passed");
            if (waiting > 0) parts.Add(waiting + " waiting for next lock");
            if (failed > 0) parts.Add(failed + " failed");
            summary.Text = all.Count == 0 ? "No images yet" : string.Join("   \u00B7   ", parts.ToArray());

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var e in all)
            {
                var item = new ListViewItem(new[] { e.OriginalName, Cap(Status(e)), e.Width + " \u00D7 " + e.Height, Cap(e.Mode), TargetText(e.LastTarget), e.LastUsed });
                item.Tag = e;
                list.Items.Add(item);
            }
            list.EndUpdate();
            ListViewItem sel = null;
            foreach (ListViewItem it in list.Items) if (((Entry)it.Tag).Id == keepId) sel = it;
            if (sel == null && list.Items.Count > 0) sel = list.Items[0];
            if (sel != null) { sel.Selected = true; sel.Focused = true; sel.EnsureVisible(); }
            ShowDetails();
            LoadThumbs(all);
        }

        // Reads the row pictures one after another on a background thread (only while this
        // window is open; they are freed when it closes).
        void LoadThumbs(List<Entry> all)
        {
            if (Theme.HC) return;
            var todo = new List<KeyValuePair<string, string>>();
            foreach (var e in all) if (asked.Add(e.Id)) todo.Add(new KeyValuePair<string, string>(e.Id, e.File));
            if (todo.Count == 0) return;
            Size ts = ThumbSize;
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var job in todo)
                {
                    if (closed) return;
                    Bitmap big = Pictures.Load(job.Value, ts.Width, ts.Height, true);
                    if (big == null) continue;
                    Bitmap small;
                    using (big) small = Pictures.Cover(big, ts.Width, ts.Height);
                    string id = job.Key;
                    try { BeginInvoke((MethodInvoker)delegate { if (closed) { small.Dispose(); return; } thumbs[id] = small; list.Invalidate(); }); }
                    catch (Exception) { small.Dispose(); return; }   // the window closed first
                }
            });
        }

        void ShowDetails()
        {
            var e = Selected();
            if (e == null)
            {
                details.Text = list.Items.Count == 0 ? "Nothing installed yet. Drop an image into the drop folder to start (tray icon > Open drop folder)." : "";
                previewButton.Enabled = false;
                shownId = null;
                hero.SetImage(null);
                hero.Frame(Framing.Exact, Color.Black, Theme.Muted, "", false);
                FitDetailsScroll();
                return;
            }
            previewButton.Enabled = true;
            var lines = new List<string>();
            lines.Add(e.OriginalName + "  (" + e.Width + "x" + e.Height + ", " + e.Mode + ")");
            lines.Add(IsSaved(e) ? "Saved" : "Check: " + Status(e));
            foreach (var p in e.LastCheck.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries))
                if (!p.StartsWith("since=")) lines.Add("  " + p);
            lines.Add("File: " + e.File);
            details.Text = string.Join(Environment.NewLine, lines.ToArray());

            hero.Frame(Framing.Exact, Color.Black, StatusColor(e), IsSaved(e) ? "Saved" : "Check " + Status(e), false);
            if (shownId != e.Id)
            {
                shownId = e.Id;
                shape = e.Width > 0 && e.Height > 0 ? (double)e.Height / e.Width : 0.625;
                ArrangeDetail();
                // The row picture first (soft), then the sharp one when it has been read.
                Bitmap t;
                hero.SetImage(thumbs.TryGetValue(e.Id, out t) ? (Bitmap)t.Clone() : null);
                string id = e.Id, file = e.File;
                int w = hero.Width - ScreenPreview.Inset * 2, h = hero.Height - ScreenPreview.Inset * 2;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (closed) return;
                    Bitmap b = Pictures.Load(file, w, h, true);
                    if (b == null) return;
                    try { BeginInvoke((MethodInvoker)delegate { if (closed || shownId != id) b.Dispose(); else hero.SetImage(b); }); }
                    catch (Exception) { b.Dispose(); }
                });
            }
            FitDetailsScroll();
        }

        Entry Selected() { return list.SelectedItems.Count == 0 ? null : (Entry)list.SelectedItems[0].Tag; }

        void ApplySelected(string target) { var e = Selected(); if (e != null) app.Apply(e, target); }

        void DeleteSelected()
        {
            var e = Selected();
            if (e == null) return;
            if (MessageBox.Show("Delete \"" + e.OriginalName + "\"?\nIt goes to the Recycle Bin, so you can restore it from there (unless this drive has no Recycle Bin).", "Delete image",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                app.Delete(e);
        }

        protected override void Dispose(bool disposing)
        {
            closed = true;
            if (disposing)
            {
                titleFont.Dispose(); mono.Dispose(); rowHeight.Dispose();
                foreach (var b in thumbs.Values) b.Dispose();
                thumbs.Clear();
            }
            base.Dispose(disposing);
        }
    }
}
