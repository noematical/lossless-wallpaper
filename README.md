# Lossless Wallpaper

A small Windows tray app. Drop an image into a watched folder (`Pictures\Lossless Wallpaper`), choose whether it becomes the wallpaper, the lock screen or both (or save it for later), and the app installs it so that the file Windows draws from matches your image pixel for pixel. After every install it compares the pixels and shows you the result.

**What "lossless" means here.** The file Windows renders the wallpaper from has the same pixel values as your image, and the app checks this. It is not a claim about what the display panel emits. Display colour management, HDR and vendor features can still change what you see (see "Known limits").

## Requirements

- Windows 10 or 11 with .NET Framework 4.8 (included in Windows 11). Nothing to install.
- Current user only. The app never needs administrator rights, uses no network and sends no telemetry.

## Build from source

Run `build.cmd`. It uses the C# compiler that ships with Windows. The output is `out\LosslessWallpaper.exe`.

## Download

Unsigned binaries are published on the GitHub Releases page (`<repo-url>/releases`) together with SHA-256 hashes.

Because the binary is unsigned, Windows SmartScreen may show "Windows protected your PC". That is expected for an unsigned file. To verify what you downloaded, compare its hash with the one in the release notes:

```powershell
Get-FileHash .\LosslessWallpaper.exe -Algorithm SHA256
```

If you would rather not trust a binary, build it yourself from the source (see above).

## Usage

1. Start `LosslessWallpaper.exe`. An icon appears in the notification area.
2. Drop an image into `Pictures\Lossless Wallpaper` (the tray menu has "Open drop folder"). The folder is watched, not searched recursively. Only files that arrive while the app is running are picked up; to use a file that was already there, move it out and back in.
3. A pop-up shows a picture of your screen with the image placed on it, the image and screen sizes, and whether it is an exact match. Choose:
   - **Install as:** Wallpaper, Lock screen or Both.
   - If the image is not screen-sized and scaling is allowed: **Crop to fill** (no bars) or **Fit inside** (bars at the edges, bar colour from settings). The picture shows the result before you install.
   - **Install** (Enter) applies it now. **Save for later** (Alt+S) prepares and checks the image and adds it to the history without changing your wallpaper or lock screen. **Preview** (Alt+P) shows it full screen first (see below). **Cancel** (Esc) does nothing. Alt plus the underlined letter picks an option.
4. A tray notification reports whether the check passed (or that the image was saved). Clicking it opens the history.

### Preview

**Preview** (in the pop-up, and **Preview full screen** in the history) shows the image over your whole main screen, 1:1, exactly as it would be installed (cropped or fitted as chosen). **Hold Shift** to see what you have now (your current wallpaper, or your current lock screen when previewing a lock screen image) and let go to switch back. Esc, Enter, Space or a click closes it. A short hint appears at the start and fades so it doesn't cover the pixels; moving the mouse brings it back. The preview changes nothing.

Measured: the pixels that reached the screen in the preview were identical to the image the install would write (0 differing pixels, both 1:1 and cropped), and so was the "current" image while Shift was held. If your current wallpaper is not screen-sized, Windows scales it with its own filter, so that side of the comparison is close but not exact; the preview says "approximate" then. Desktop icons and the taskbar are not shown over the preview.

The windows follow your Windows light or dark setting and switch to system colours in high-contrast mode.

### Modes (tray menu)

- **True lossless:** refuses images that are not exactly the main screen's pixel size. The pop-up can still allow scaling for one image.
- **Allow scaling:** offers crop or fit for images that are not screen-sized. These installs are labelled "scaled", not lossless.

### History

"Open history" (or click the tray icon once) lists your images with a thumbnail, check result, size, mode (lossless, converted or scaled), where it was last set and when. The side panel shows a larger preview and the full check details.

- **Check results:** Passed, Failed, Waiting for next lock (the lock screen is checked after your next lock and unlock), or Saved (kept for later, not applied).
- **Actions:** preview the selected image full screen (the button under its picture), set it as wallpaper, lock screen or both (this is how you apply a saved image; it runs the normal check), delete it, show its file, or undo.
- Delete sends the app's copy to the Recycle Bin. The image currently used as wallpaper cannot be deleted.
- Saving an image that is already in the history keeps its earlier record.

### Undo

- **Undo last change** restores the previous wallpaper, lock screen and related settings from a backup taken before the last install. The last 20 backups are kept. Re-applying the image that is already set, and saving for later, make no backup because nothing changes.
- **Restore what I had before this app** goes back to the wallpaper and lock screen you had before you first used the app. That first backup is kept permanently. If you had a solid colour instead of a picture, that is restored too.

### Start at sign-in

"Start when I sign in" (top right of the history window, or in the tray menu) adds or removes a value in the current user's Run key (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`). The app then appears in Task Manager under Startup apps. Turning it off in Task Manager also unticks it in the app, and ticking it in the app clears a "Disabled" set in Task Manager.

### Quit

Use the tray menu's Exit, or run `LosslessWallpaper.exe --quit`.

## Accessibility

- Everything can be done from the keyboard (Enter, Esc, Alt shortcuts, standard tab order, visible focus outline).
- The controls are drawn to match the app's style but are standard Windows controls underneath, so screen readers get the usual buttons, radio buttons and check boxes. Checked through the Windows accessibility interface: the pop-up's buttons report as push buttons and can be pressed through it.
- In high-contrast mode the app stops custom drawing and uses the system colours.
- Text and layout scale with the display setting. Colour is never the only signal: every status also has a word.
- Nothing is timed.

Not yet tested with a real screen reader, a real high-contrast theme, or display scales other than 150%.

## What the check means

On install the app converts the image to an 8-bit PNG, decodes that file again and compares it with the intended pixels. It then sets the wallpaper (and/or lock screen) and compares the file Windows renders from with the same pixels. The wallpaper check reads `%APPDATA%\Microsoft\Windows\Themes\TranscodedWallpaper`.

### Measured results

Measured on one PC: Windows 11, one 2560x1600 OLED laptop screen at 150% scale, November 2026.

- An exact-size 8-bit PNG set as wallpaper: the file Windows renders from had 0 differing pixels, checked with the app's decoder (Windows Imaging Component) and independently with Pillow.
- Lock screen: Windows' stored copy had 0 differing pixels. The copy Windows actually draws is only created at the next lock, so the app re-checks it after you unlock. After a real lock and unlock, the drawn copy was PNG data (despite its `.jpg` name) with 0 differing pixels, checked by the app and independently with Pillow.
- Windows did not re-encode off-size 8-bit PNGs; it scales them at draw time. The app still does its own scaling with one fixed filter (GDI+ high-quality bicubic), so the result is known and checkable.
- 16-bit PNGs: Windows itself turned them into a lossy JPEG. The app converts them to 8-bit first (exact for values that are multiples of 257).
- Transparency is flattened by the app onto a background colour (black by default).
- Camera rotation tags (EXIF orientation 2–8) are applied, so photos install upright; checked against Pillow for all seven.
- Formats that can't be turned into 8-bit RGB exactly (for example CMYK JPEGs) are converted and labelled "converted", not lossless. Animated or multi-page images and HDR float formats are refused.
- Embedded colour profiles and gamma chunks are ignored on purpose: the stored sample values are kept. The PNG the app writes contains only IHDR, IDAT and IEND chunks.
- JPEG and WebP inputs are already lossy. The app adds no further loss after Windows decodes them, but different JPEG decoders can differ slightly on chroma-subsampled files (up to 2 levels measured on a smooth image; identical for 4:4:4 JPEGs).
- Windows did not render wallpapers from files under `%LOCALAPPDATA%` or `%APPDATA%` on the test PC (cause not determined), so installed copies are kept in `Pictures\Lossless Wallpaper\Installed`.
- The app must be DPI-aware: at 150% scale a non-DPI-aware program sees 1707x1067 instead of 2560x1600. The app declares itself DPI-aware.
- Footprint: the exe is about 160 KB (no installer, no dependencies). The idle tray app uses about 24 MB of private memory after start and about 30–34 MB after its windows have been used. Installs run in a short-lived second copy of the exe, so the tray app stays small.

### Known limits

- ASUS OLED laptops: "Pixel Shift" (MyASUS > Device Settings > Audio & Visual > ASUS OLED Care) replaces what is shown with a shifted, JPEG-compressed copy every few minutes. The app detects it and warns. It protects OLED screens from burn-in, so turning it off is your choice.
- The image is made exact for the main screen. With several screens, Windows shows it on all of them and scales it on the others (the app warns).
- If a policy controls the wallpaper or lock screen, Windows may undo changes (the app warns).
- The app checks every 30 minutes, and when you unlock, whether something else changed the wallpaper and tells you if so.
- If Controlled Folder Access (ransomware protection) is on, Windows may block the app from writing to `Pictures`. The app says so; allow `LosslessWallpaper.exe` in Windows Security > Virus & threat protection > Ransomware protection.
- If `Pictures` is synced by OneDrive, the installed copies are synced too. Not tested with OneDrive's "files on demand".

### Not tested

On-screen pixels (screen capture), colour management, HDR and ICC display calibration, multiple monitors, other laptop makers' burn-in tools, Windows 10, ARM64, group-policy-managed PCs, and display scales other than 150%.

## Files and settings

| Path | Contents |
| --- | --- |
| `%LOCALAPPDATA%\LosslessWallpaper\settings.ini` | Settings (below) |
| `%LOCALAPPDATA%\LosslessWallpaper\history.json` | History list |
| `%LOCALAPPDATA%\LosslessWallpaper\undo\` | Backups used by Undo (the last 20, plus `original`) |
| `%USERPROFILE%\Pictures\Lossless Wallpaper\` | Drop folder (default) |
| `%USERPROFILE%\Pictures\Lossless Wallpaper\Installed\` | The app's checked copies (installed or saved for later), named by hash |

`settings.ini` is plain `key=value` text. Lines starting with `#` are ignored; invalid values are ignored.

| Key | Values | Default |
| --- | --- | --- |
| `DropFolder` | folder path (environment variables are expanded) | `Pictures\Lossless Wallpaper` |
| `Mode` | `lossless` or `scaling` | `lossless` |
| `LastTarget` | `wallpaper`, `lockscreen` or `both` | `wallpaper` |
| `LastFit` | `crop` or `fit` | `crop` |
| `BarColor` | `#RRGGBB`, bars when fitting | `#000000` |
| `AlphaBackground` | `#RRGGBB`, colour transparency is flattened onto | `#000000` |

## Command line

With no arguments the exe runs the tray app (one instance at a time). With arguments it runs a short-lived worker, which the tray app also uses internally. Commands:

- `--quit` asks a running tray app to close.
- `--install <file> --target wallpaper|lockscreen|both|none --fit exact|crop|fit [--name <name>]` installs an image and checks it. `none` only prepares, checks and stores the image (Save for later).
- `--preview <file> [--fit exact|crop|fit] [--target wallpaper|lockscreen]` opens the full-screen preview. `--selftest <folder>` makes it capture what reached the screen (with and without Shift), save the expected images beside them and close (test helper).
- `--undo` restores the latest backup. `--undo --original` restores the setup from before the app was first used.
- `--check-lock <png> --since <ticks>` re-checks the lock screen copy.
- `--convert <file> --out <png> [--fit ...] [--width N --height N]` converts without installing (test helper).
- `--compare <a> --with <b>` compares pixels; exit code 0 means identical.
- `--warnings` lists current warnings. `--screen` prints the main screen's pixel size.
- `--result <file>` writes the worker's output to a file instead of the console.

## Uninstall

1. If you want the previous wallpaper back, use "Undo last change" first.
2. Untick "Start when I sign in".
3. Quit the app.
4. Delete `LosslessWallpaper.exe` and `%LOCALAPPDATA%\LosslessWallpaper`.
5. Optionally delete `Pictures\Lossless Wallpaper`. Do not delete it while an image from its `Installed` folder is still your wallpaper or lock screen.

## Tests

Both scripts need Python with Pillow and numpy, and a built `out\LosslessWallpaper.exe`.

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-core-tests.ps1
powershell -ExecutionPolicy Bypass -File tests\run-install-tests.ps1 [-LockScreen]
```

- `run-core-tests.ps1` changes nothing on the system. It converts generated test images and checks them with Pillow.
- `run-install-tests.ps1` changes the wallpaper briefly (and the lock screen with `-LockScreen`) and then undoes it with the app's own undo. Run the core tests first.
- Pass `-Python <path>` if `python` is not on your PATH.

## Privacy

No network access and no telemetry. Everything stays on your PC. The app only reads the drop folder, writes its own files (above), and changes the wallpaper, lock screen and Run-key settings you ask it to.

## Licence

MIT. See the `LICENSE` file.

## Credits

The interface's design process and several of its values come from "unslop" by Damir Sokolovsky (@heydamir), CC BY 4.0. Details are at the top of `src\Theme.cs`.
