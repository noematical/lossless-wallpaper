using System;
using System.Runtime.InteropServices;

namespace LosslessWallpaper
{
    static class Native
    {
        public const int SPI_SETDESKWALLPAPER = 0x0014;
        public const int SPIF_UPDATEINIFILE_SENDCHANGE = 3;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfo(int action, int param, string vparam, int winIni);

        [DllImport("user32.dll")]
        static extern IntPtr SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

        // Hands memory used by a closed window back to Windows, so the tray app stays small.
        public static void TrimMemory()
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1)); }
            catch (Exception) { }
        }

        public static void MakeDpiAware()
        {
            // The manifest already declares this; the call covers the case where it is ignored.
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-2)) != IntPtr.Zero) return; } // SYSTEM_AWARE
            catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (Exception) { }
        }

        // Physical pixels of the primary display. EnumDisplaySettings is documented as not
        // taking part in DPI virtualisation, so this is right even at 150% scale.
        public static void PrimaryScreenPixels(out int width, out int height)
        {
            var dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (EnumDisplaySettings(null, -1, ref dm)) { width = dm.dmPelsWidth; height = dm.dmPelsHeight; return; }
            var b = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            width = b.Width; height = b.Height;
        }
    }
}
