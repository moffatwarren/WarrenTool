# WarrenTool

Windows tools in one lightweight tray app, installed once:

- **Screen Shot** (**`Ctrl+Alt+S`**): grab a region of the screen, mark it up, and copy it to the clipboard. The Windows counterpart of `grim -g "$(slurp)" - | swappy -f -`.
- **Wallpaper Picker** (**`Ctrl+Alt+W`**): browse your wallpapers as a scrollable row of slanted cards and set the **Desktop Wallpaper** and **Lock Screen** with one keypress.

Both run inside one background process with one tray icon, so both hotkeys are always ready.

## Features
- One tray icon. Left- or right-click it for the menu: **Take screenshot**, **Open wallpaper picker**, **Random wallpaper**, **Open screenshots folder**, **Open wallpapers folder**, **Start with Windows** and **Exit**.
- Catppuccin throughout.
- Native Windows: nothing to install first, no SDKs or Python. The one third-party library (ZXing.Net, for QR codes) is in the repo and gets built into the exe.

**Screen Shot**
- Freezes the screen while you drag a rectangle, so open menus and tooltips can be captured.
- Opens the region in an editor in the middle of the screen: brush, arrow, rectangle, ellipse, text and pixelate.
- **Copy to clipboard** copies the shot with its markup, at exactly the size you selected.
- **Copy text** reads the text inside a box you drag on the shot and copies it as text (Windows' built-in OCR). Drag the box around a **QR code** instead and it copies the code's URL.

**Wallpaper Picker**
- Opens on your current wallpaper, on the monitor your mouse is on.
- Just start typing to filter the wallpapers by file name.
- Sets the Desktop wallpaper and Lock Screen together, or the Desktop only.
- Picks up new images in the folder automatically.
- Supports `.jpg`, `.jpeg`, `.png`, `.bmp`, and `.webp` images.

---

## Install
1. Clone or download this repo.
2. Double-click **`install.bat`**. It:
   - removes the separate Screen Shot and Wallpaper Picker apps if they are installed (if either started with Windows, WarrenTool will too)
   - builds the app into `%LOCALAPPDATA%\WarrenTool` (using the C# compiler built into Windows)
   - adds **Screen Shot** and **Wallpaper Picker** shortcuts to the Start Menu
   - creates **`Pictures\Wallpapers`** if it doesn't exist, adding `Lowpoly_Street.png` as a starter image when it has no images (OneDrive-redirected Pictures folders are detected automatically)
   - turns on **Start with Windows** (first install only; after that the tray menu setting is kept)
   - starts the app in the tray
3. Put your images in **`Pictures\Wallpapers`**.

Once installed, the repo folder can be moved or deleted. To update, pull the latest changes and run `install.bat` again; your settings are kept.

> Windows 11 hides new tray icons in the `^` overflow. Drag the icon onto the taskbar to keep it visible.

To remove it, double-click **`uninstall.bat`**. This removes the app, the Start Menu shortcuts and the startup entry. Saved screenshots and your wallpapers are not touched.

---

## Screen Shot
Press **`Ctrl+Alt+S`** (or choose **Take screenshot** from the tray menu, or the **Screen Shot** Start Menu shortcut), then drag a rectangle over what you want. `Esc`, a right-click, or a click without dragging cancels.

The region opens in the editor:

| Key / Mouse | Action |
|---|---|
| `B` | Brush (the default) |
| `A` | Arrow |
| `R` | Rectangle |
| `O` | Ellipse |
| `T` | Text: click to place, type, `Enter` to finish (`Shift+Enter` for a new line) |
| `D` | Pixelate: drag over what you want hidden |
| `X` | Copy text: drag a box around text in the shot to copy it to the clipboard as text, or around a QR code to copy its URL |
| `+` `-` | Stroke size (text size follows it) |
| `Ctrl+Z` / `Ctrl+Y` or `Ctrl+Shift+Z` | Undo / redo |
| `Ctrl+C` or **Copy to clipboard** | Copy the shot with its markup. The editor stays open |
| `Ctrl+S` or **Save** | Save a PNG to `Pictures\Screenshots` |
| `Esc` | Close the editor |

Each capture opens its own editor, so you can keep one open while taking another.

**Copy text** uses the OCR engine that ships with Windows, in your Windows display language. It is reliable on ordinary interface and document text. Small monospace text (terminals, code) comes out less cleanly: expect the odd wrong character, such as `_` read as a dash or a slashed `0` read as `@`.

If the box contains a QR code, its contents are copied and the text around it is ignored; several codes in one box are copied one per line. Data Matrix, Aztec, PDF417, EAN-13, UPC-A and Code 128 codes are read the same way. The box needs to contain the whole code.

---

## Wallpaper Picker
Press **`Ctrl+Alt+W`** (or choose **Open wallpaper picker** from the tray menu, or the **Wallpaper Picker** Start Menu shortcut) to open the picker.

| Key / Mouse | Action |
|---|---|
| Type anything | Filter by file name (every word typed must appear in the name) |
| `←` `→` / mouse wheel | Browse |
| `↑` `↓` / `PgUp` `PgDn` | Jump five at a time |
| `Home` `End` | Jump to the first / last |
| `Ctrl+R` | Random |
| `Enter` or double-click | Set Desktop wallpaper + Lock Screen, then close |
| `Shift+Enter` | Set Desktop wallpaper only, then close |
| `Esc` | Clear the filter, or hide if there is none |
| `Ctrl+Alt+W` / click elsewhere | Hide |

**Random wallpaper** in the tray menu sets a random image from the folder (Desktop + Lock Screen) without opening the picker.

---

## Files Included
- `install.bat` / `uninstall.bat` - Double-click to install or remove.
- `Install.ps1` - The installer: builds the exe (including its icons), copies files, creates the shortcuts and wallpapers folder. Run with `-Uninstall` to remove.
- `src\WarrenTool.cs` - The tray app: startup, the tray icon and menu, and the two hotkeys.
- `src\ScreenShot.cs` - Screen Shot: the selection overlay, the editor, text and QR code reading.
- `src\WallpaperPicker.cs` - Wallpaper Picker: the picker window and setting the wallpaper.
- `src\Set-LockScreen.ps1` - Sets the Lock Screen image (WinRT API). Installed next to the exe and called by the picker.
- `src\app.manifest` - Makes the app DPI aware, so captures are pixel-exact on scaled and mixed-DPI monitors.
- `assets\Lowpoly_Street.png` - Starter wallpaper, copied in when the wallpapers folder has no images.
- `lib\zxing.dll` - [ZXing.Net](https://github.com/micjahn/ZXing.Net) 0.16.11, the QR code and barcode reader. The installer embeds it in the exe, so nothing is installed alongside. Licensed under Apache 2.0 (`lib\zxing-LICENSE.txt`).
