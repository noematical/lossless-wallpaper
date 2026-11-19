// Things outside the app that can stop the screen from showing the exact pixels.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace LosslessWallpaper
{
    public static class Warnings
    {
        public static List<string> Current()
        {
            var w = new List<string>();
            if (AsusPixelShiftOn())
                w.Add("ASUS OLED Care Pixel Shift is on. The file Windows draws from is lossless, but every few minutes ASUS shows a shifted, JPEG-compressed copy instead. " +
                      "To see the exact pixels, turn Pixel Shift off in MyASUS (Device Settings > Audio & Visual > ASUS OLED Care). It protects OLED screens from burn-in, so this is your choice.");
            try
            {
                if (System.Windows.Forms.Screen.AllScreens.Length > 1)
                    w.Add("More than one screen is connected. The image is exact for the main screen; Windows scales it on the others.");
            }
            catch (Exception) { }
            if (PolicyValue(@"Software\Microsoft\Windows\CurrentVersion\Policies\System", "Wallpaper"))
                w.Add("A policy (usually set by an organisation) controls the wallpaper, so Windows may undo changes.");
            if (PolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "LockScreenImage") || PolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoChangingLockScreen"))
                w.Add("A policy (usually set by an organisation) controls the lock screen, so it may not change.");
            return w;
        }

        public static bool AsusPixelShiftOn()
        {
            try
            {
                if (Process.GetProcessesByName("AsusOLEDShifter").Length == 0) return false;
                string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                foreach (var dir in Directory.GetDirectories(packages, "B9ECED6F.ASUSPCAssistant_*"))
                {
                    string ini = Path.Combine(dir, @"LocalState\AsusOLEDShifter\config.ini");
                    if (!File.Exists(ini)) continue;
                    foreach (var line in File.ReadAllLines(ini))
                        if (line.Trim().StartsWith("RunningState=", StringComparison.OrdinalIgnoreCase))
                            return line.Trim().EndsWith("true", StringComparison.OrdinalIgnoreCase);
                }
                return true; // running, state unknown
            }
            catch (Exception) { return false; }
        }

        // Not used: the wallpaper history value (BackgroundHistoryPath0) is not updated by
        // SystemParametersInfo (measured 2026-11-18), so it can't show what is on screen.

        static bool PolicyValue(string subkey, string name)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(subkey))
                        if (k != null && k.GetValue(name) != null) return true;
                }
                catch (Exception) { }
            }
            return false;
        }
    }
}
