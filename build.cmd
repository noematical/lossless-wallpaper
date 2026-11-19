@echo off
rem Builds LosslessWallpaper.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
rem Nothing to install or download. Output: out\LosslessWallpaper.exe
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (echo .NET Framework 4.8 compiler not found. & exit /b 1)
set WM=%WINDIR%\System32\WinMetadata
set GAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL
if not exist "%~dp0out" mkdir "%~dp0out"
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /warn:4 ^
  /out:"%~dp0out\LosslessWallpaper.exe" /win32manifest:"%~dp0src\app.manifest" /win32icon:"%~dp0src\app.ico" ^
  /lib:"%FW%\WPF" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Runtime.Serialization.dll /r:Microsoft.VisualBasic.dll ^
  /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.System.winmd" ^
  /r:"%GAC%\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll" ^
  "%~dp0src\*.cs"
if errorlevel 1 exit /b 1
echo Built "%~dp0out\LosslessWallpaper.exe"
