// Per-user settings in %LOCALAPPDATA%\LosslessWallpaper\settings.ini (key=value).
// Your personal setup lives only in this file, never in the source code.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

namespace LosslessWallpaper
{
    public sealed class Settings
    {
        public string DropFolder;
        public string Mode = "lossless";        // "lossless" refuses off-size images; "scaling" offers crop / fit
        public string LastTarget = "wallpaper"; // wallpaper | lockscreen | both
        public string LastFit = "crop";         // crop | fit
        public Color BarColor = Color.Black;
        public Color AlphaBackground = Color.Black;

        public static string AppDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LosslessWallpaper"); }
        }
        // Installed copies live under Pictures, not AppData: on the test PC Windows silently refused
        // to render wallpapers from AppData\Local and AppData\Roaming (2026-11-18), while the same
        // file under Pictures rendered with 0 differing pixels. The drop folder is watched
        // non-recursively, so this subfolder is never picked up as a new drop.
        public static string LibraryDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), @"Lossless Wallpaper\Installed"); }
        }
        public static string UndoDir { get { return Path.Combine(AppDir, "undo"); } }
        static string FilePath { get { return Path.Combine(AppDir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            s.DropFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Lossless Wallpaper");
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0 || line.TrimStart().StartsWith("#")) continue;
                        string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                        if (k == "DropFolder" && v.Length > 0) s.DropFolder = Environment.ExpandEnvironmentVariables(v);
                        else if (k == "Mode" && (v == "lossless" || v == "scaling")) s.Mode = v;
                        else if (k == "LastTarget" && (v == "wallpaper" || v == "lockscreen" || v == "both")) s.LastTarget = v;
                        else if (k == "LastFit" && (v == "crop" || v == "fit")) s.LastFit = v;
                        else if (k == "BarColor") s.BarColor = ParseColor(v, s.BarColor);
                        else if (k == "AlphaBackground") s.AlphaBackground = ParseColor(v, s.AlphaBackground);
                    }
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(AppDir);
            var lines = new List<string>();
            lines.Add("# Lossless Wallpaper settings");
            lines.Add("DropFolder=" + DropFolder);
            lines.Add("Mode=" + Mode);
            lines.Add("LastTarget=" + LastTarget);
            lines.Add("LastFit=" + LastFit);
            lines.Add("BarColor=" + ToHex(BarColor));
            lines.Add("AlphaBackground=" + ToHex(AlphaBackground));
            File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
        }

        static Color ParseColor(string v, Color fallback)
        {
            v = v.TrimStart('#');
            int rgb;
            if (v.Length == 6 && int.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out rgb))
                return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return fallback;
        }

        static string ToHex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }
    }
}
