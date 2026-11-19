// History of installed images. Each entry is the app's own PNG copy plus what was done with it.
// Only the always-running app writes history.json; install workers just return results.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace LosslessWallpaper
{
    [DataContract]
    public sealed class Entry
    {
        [DataMember] public string Id = "";
        [DataMember] public string File = "";
        [DataMember] public string OriginalName = "";
        [DataMember] public int Width;
        [DataMember] public int Height;
        [DataMember] public string Mode = "";        // lossless | scaled (crop) | scaled (fit) | converted
        [DataMember] public string Added = "";
        [DataMember] public string LastUsed = "";
        [DataMember] public string LastTarget = "";
        [DataMember] public string LastCheck = "";
        [DataMember] public bool LastPass;
        [DataMember] public int LockChecks;      // re-checks tried after unlock; gives up after a few
    }

    public static class Library
    {
        static string HistoryPath { get { return Path.Combine(Settings.AppDir, "history.json"); } }

        // A damaged history file is kept aside as history.json.bad-<time> instead of being
        // silently replaced by an empty list on the next save.
        public static List<Entry> Load()
        {
            if (!System.IO.File.Exists(HistoryPath)) return new List<Entry>();
            try
            {
                using (var fs = System.IO.File.OpenRead(HistoryPath))
                {
                    var list = (List<Entry>)new DataContractJsonSerializer(typeof(List<Entry>)).ReadObject(fs);
                    return list ?? new List<Entry>();
                }
            }
            catch (Exception)
            {
                try { System.IO.File.Move(HistoryPath, HistoryPath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch (Exception) { }
                return new List<Entry>();
            }
        }

        public static void Save(List<Entry> list)
        {
            Directory.CreateDirectory(Settings.AppDir);
            string tmp = HistoryPath + ".tmp";
            using (var fs = System.IO.File.Create(tmp)) new DataContractJsonSerializer(typeof(List<Entry>)).WriteObject(fs, list);
            if (System.IO.File.Exists(HistoryPath)) System.IO.File.Replace(tmp, HistoryPath, null);
            else System.IO.File.Move(tmp, HistoryPath);
        }

        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = System.IO.File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        // Adds or updates an entry. The same output image (same hash) never gets a second entry.
        public static Entry Upsert(List<Entry> list, Entry incoming)
        {
            var e = list.FirstOrDefault(x => x.Id == incoming.Id);
            if (e == null) { list.Insert(0, incoming); return incoming; }
            e.LastUsed = incoming.LastUsed; e.LastTarget = incoming.LastTarget;
            e.LastCheck = incoming.LastCheck; e.LastPass = incoming.LastPass;
            if (string.IsNullOrEmpty(e.OriginalName)) e.OriginalName = incoming.OriginalName;
            list.Remove(e); list.Insert(0, e);
            return e;
        }

        // Sends the app's copy to the Recycle Bin. Refuses the image the wallpaper currently uses,
        // because Windows keeps pointing at that file.
        public static string Delete(List<Entry> list, Entry e)
        {
            if (string.Equals(Installer.CurrentWallpaperPath(), e.File, StringComparison.OrdinalIgnoreCase))
                return "This image is your current wallpaper, so it can't be deleted. Set another wallpaper first.";
            // The lock screen needs no such guard: Windows keeps its own copy of that image.
            try
            {
                if (System.IO.File.Exists(e.File))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(e.File,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (OperationCanceledException) { return "Deleting was cancelled."; }
            catch (Exception ex) { return "Could not delete the file: " + ex.Message; }
            list.Remove(e);
            return null;
        }
    }
}
