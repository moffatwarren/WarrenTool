# WarrenTool

Windows tools in one lightweight tray app, installed once:

- **Screen Shot** (**`Ctrl+Alt+S`**): grab a region of the screen, mark it up, and copy it to the clipboard. The Windows counterpart of `grim -g "$(slurp)" - | swappy -f -`. **`Ctrl+Shift+Alt+S`** copies the text or QR code in a region straight to the clipboard, without the editor.
- **Wallpaper Picker** (**`Ctrl+Alt+W`**): browse your wallpapers as a scrollable row of slanted cards and set the **Desktop Wallpaper** and **Lock Screen** with one keypress.
- **Audio Switcher** (**`Ctrl+Alt+O`**): step to the next audio output, from the outputs you choose. The Windows counterpart of the Quickshell audio menu and `audio-output-toggle.sh` in [Linux-Setup](https://github.com/moffatwarren/Linux-Setup).
- **Clipboard Manager** (**`Ctrl+Alt+V`**): a searchable history of the text and images you've copied, in a window in the middle of the screen. The Windows counterpart of the Quickshell clipboard menu (`ClipboardMenu.qml` and `clipboard-history.sh`) in Linux-Setup, and it looks the same.
- **Desktop Switcher** (**`Ctrl+Alt+R`**): step to the next virtual desktop, round from the last to the first. Works from inside a RustDesk session too, and switches this PC's desktop, not the remote one's.
- **Aquarium** (**`Ctrl+Alt+L`**): a Catppuccin pixel-art fish tank screensaver, visited by a shark, a great white and a scuba diver, stretched across all your monitors as one tank. It starts after 5 minutes idle, and the PC locks when you come back. The Windows counterpart of the aquarium lock screen in Linux-Setup.
- **Pip** (in the Herdr app): a pixel critter under Herdr's agents list that knocks when an agent needs you, dances when one is done, and naps while you're away. Linux-Setup's Herdr mod, put into Herdr by `install.bat` when Herdr is installed.

All five tools run inside one background process with one tray icon, so every hotkey is always ready. The Aquarium is a screensaver, which Windows starts by itself, so it is its own small program; the tray app gives it `Ctrl+Alt+L`.

## Features
- One tray icon, showing the icon of the current audio output (Catppuccin maroon, darker on a light taskbar). **Left-click** it for the audio menu. **Right-click** it for the menu: **Take screenshot**, **Copy text from screen**, **Open wallpaper picker**, **Random wallpaper**, **Open clipboard history**, **Clear clipboard history**, **Lock with aquarium**, **Open screenshots folder**, **Open wallpapers folder**, **Start with Windows** and **Exit**.
- Catppuccin throughout.
- If something goes wrong, WarrenTool keeps running: a tray notice says so, and the details go in `%LOCALAPPDATA%\WarrenTool\error.log`.
- Native Windows: nothing to install first, no SDKs or Python. The one third-party library (ZXing.Net, for QR codes) is in the repo and gets built into the exe.

**Screen Shot**
- Freezes the screen while you drag a rectangle, so open menus and tooltips can be captured.
- Opens the region in an editor in the middle of the screen: brush, highlighter, arrow, rectangle, ellipse, text and pixelate.
- The highlighter is Linux-Setup's: it tints a light page like a real highlighter, keeping black text black, and still shows on a dark one.
- **Copy to clipboard** copies the shot with its markup, at exactly the size you selected.
- **Copy text** reads the text inside a box you drag on the shot and copies it as text (Windows' built-in OCR). Drag the box around a **QR code** instead and it copies the code's URL.
- **`Ctrl+Shift+Alt+S`** does the same in one step: drag a rectangle on the screen and what it says is copied, with no editor. A note at the bottom of the screen shows what was copied, and the window you were in (a RustDesk session, say) keeps the focus, so you can paste straight away.

**Wallpaper Picker**
- Opens on your current wallpaper, on the monitor your mouse is on.
- Just start typing to filter the wallpapers by file name.
- Sets the Desktop wallpaper and Lock Screen together, or the Desktop only.
- Picks up new, changed and removed images in the folder automatically.
- Loads its thumbnails in the background when WarrenTool starts, and keeps a card-sized copy of each in `%LOCALAPPDATA%\WarrenTool\Thumbnails`, so the picker opens already filled in. Only the cards around the one on show are held in memory; the rest reload from those copies as you scroll to them.
- Supports `.jpg`, `.jpeg`, `.png`, `.bmp`, and `.webp` images.

**Audio Switcher**
- Switching makes the output the default for everything: apps, and calls (Windows' "default communication device").
- Choose which outputs `Ctrl+Alt+O` cycles through, and the icon each one gets.
- Each output remembers the mic picked while it was in use, and brings it back when you switch to it.

**Clipboard Manager**
- Keeps everything you copy, text and images, newest first, with a thumbnail for each image.
- Right-click an image to mark it up in Screen Shot's editor; copying it from there adds the marked-up version to the history.
- The history is saved in `%LOCALAPPDATA%\WarrenTool\Clipboard`, so it survives a restart. It keeps the 750 newest entries.
- Copying something that is already in the history moves it to the top instead of adding it twice.
- Skips what password managers mark as private, as Windows' own `Win+V` history does.
- Works with remote-desktop clipboard sync (RustDesk, Remote Desktop). A screenshot you copy, or an entry you pick from the history, reaches the other PC's clipboard. If WarrenTool runs there too, it goes to the top of that PC's history as well.

**Desktop Switcher**
- Cycles through your desktops in Task View's order, with the same slide as `Ctrl+Win+→`, and the window you last used on the new desktop gets the focus.
- Works while a RustDesk session is focused, windowed or fullscreen: `Ctrl+Alt+R` stays on this PC instead of going to the remote one.

**Aquarium**
- The same tank as Linux-Setup's: clownfish, tangs, angelfish, goldfish, pufferfish, bettas, schools of tetras, jellyfish, a crab, swaying weed, corals and a chest venting bubbles, all from the same sprites.
- One tank across every monitor, not one per monitor: fish swim off one screen and onto the next.
- Visitors drop by, one at a time, as on Linux. A shark cruises in through the glass, across every monitor and out the far side, drifting towards the fish and now and then lunging, which sends them scattering. One visit in four is a great white instead: twice the size, slower, and given a wider berth.
- Between the sharks, a scuba diver sinks in through the surface, swims a few short legs breathing out strings of bubbles, and rises back out. The fish keep their distance.
- Crisp pixels at any resolution: the tank is scaled up by a whole number (5× on 1440p, 4× on 1080p).

---

## Install
1. Clone or download this repo.
2. Double-click **`install.bat`**. It:
   - removes the separate Screen Shot and Wallpaper Picker apps if they are installed (if either started with Windows, WarrenTool will too)
   - builds the app into `%LOCALAPPDATA%\WarrenTool` (using the C# compiler built into Windows)
   - adds **Screen Shot** and **Wallpaper Picker** shortcuts to the Start Menu
   - creates **`Pictures\Wallpapers`** if it doesn't exist, adding `Lowpoly_Street.png` as a starter image when it has no images (OneDrive-redirected Pictures folders are detected automatically)
   - turns on **Start with Windows** (first install only; after that the tray menu setting is kept)
   - makes the **Aquarium** your screensaver, after 5 minutes and locking the PC when it ends (first install only, and only if you don't already have a screensaver; after that your Screen Saver Settings are kept)
   - if the **Herdr** app is installed, puts **Pip** in it, and moves an open Herdr from its stock layout to `main-pip`, the one with a spot for Pip (see [Pip](#pip-in-herdr))
   - starts the app in the tray
3. Put your images in **`Pictures\Wallpapers`**.

Once installed, the repo folder can be moved or deleted. To update, pull the latest changes and run `install.bat` again; your settings are kept.

> Windows 11 hides new tray icons in the `^` overflow. Drag the icon onto the taskbar to keep it visible.

To remove it, double-click **`uninstall.bat`**. This removes the app, the Start Menu shortcuts, the startup entry, the audio settings, Pip from Herdr and, if the Aquarium is still your screensaver, the screensaver. Saved screenshots and your wallpapers are not touched.

---

## Screen Shot
Press **`Ctrl+Alt+S`** (or choose **Take screenshot** from the tray menu, or the **Screen Shot** Start Menu shortcut), then drag a rectangle over what you want. `Esc`, a right-click, or a click without dragging cancels.

The region opens in the editor:

| Key / Mouse | Action |
|---|---|
| `B` | Brush (the default) |
| `H` | Highlighter: a wide marker stroke, for going over a line of text |
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

### Copy text from screen
Press **`Ctrl+Shift+Alt+S`** (or choose **Copy text from screen** from the tray menu) and drag a rectangle, as for a screenshot. Instead of opening the editor, the region is read like the editor's **Copy text** tool does it: a QR code's contents, or otherwise its text, go on the clipboard, and a note at the bottom of the screen shows what was copied. The focus goes back to the window you were in, so you can paste right away. That includes a RustDesk session: the text reaches the remote PC's clipboard the same way a screenshot copied from the editor does.

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

## Audio Switcher
Press **`Ctrl+Alt+O`** to switch to the next output in the rotation. A popup at the bottom of the screen shows which output you're on and its volume, and the tray icon changes to that output's icon.

**Left-click** the tray icon for the audio menu:

| Click | Action |
|---|---|
| An output | Make it the default output now |
| An output's switch | Include it in `Ctrl+Alt+O` (several can be on) |
| An output's icon | Choose its icon (volume, speakers, headphones, earbuds, Bluetooth, display, TV). The tray icon uses it too |
| An input | Make it the default mic. The output in use at the time remembers it |
| A slider | Volume of the default output / input (dragging or scrolling) |
| The percentage | Mute / unmute |
| An input's mic icon | Mute / unmute that mic |
| **Sound settings…** | Open Windows' Sound settings |
| `Esc` / click elsewhere | Close |

- Outputs are listed and cycled in name order. Only plugged-in devices are shown; unplugged ones keep their settings for when they come back.
- New outputs join the rotation automatically. If every switch is off, `Ctrl+Alt+O` cycles through all of them.
- Windows keeps a volume level per device, so each output comes back at the level you left it at.
- The **volume** icon (the default) follows the volume level, like Windows' own. Headsets, HDMI/DisplayPort and Bluetooth devices start with a matching icon; anything else starts as **volume** until you pick one.
- Icons come from JetBrainsMono Nerd Font, as in the Linux bar, when it is installed; otherwise from Windows' own Segoe Fluent Icons.
- Apps you've set to a specific device in Windows' **Volume mixer** keep using that device.
- Settings are stored in the registry under `HKCU\Software\WarrenTool\Audio` and removed by `uninstall.bat`.

---

## Clipboard Manager
Press **`Ctrl+Alt+V`** (or choose **Open clipboard history** from the tray menu) to open the history on the monitor your mouse is on.

| Key / Mouse | Action |
|---|---|
| Type anything | Filter (matches the text, or `image` / `text` for every entry of that kind) |
| `↑` `↓` | Move (wraps around) |
| `PgUp` `PgDn` | Jump eight at a time |
| `Home` `End` | Jump to the first / last |
| `Enter` or click | Put it back on the clipboard and close, ready to paste with `Ctrl+V` |
| `Del` | Remove it from the history (the window stays open) |
| Right-click | An image: open it in Screen Shot's editor to mark it up. Text: remove it, as `Del` does |
| `Esc` / `Ctrl+Alt+V` / click outside | Close |

**Clear clipboard history** in the tray menu deletes every entry, after asking.

Note: Office uses `Ctrl+Alt+V` for Paste Special. While WarrenTool is running, the shortcut opens the clipboard history instead.

---

## Desktop Switcher
Press **`Ctrl+Alt+R`** to switch to the next virtual desktop. From the last desktop it goes back to the first. With only one desktop it does nothing (add more with `Ctrl+Win+D` or Task View).

`Ctrl+Alt+R` works even when a RustDesk window has the focus. RustDesk normally captures every key and sends it to the remote PC, so other shortcuts like `Ctrl+Win+→` switch the remote PC's desktops. `Ctrl+Alt+R` is caught before RustDesk sees it: your own PC switches desktops, and the remote PC gets nothing. The keyboard focus moves with you, so after switching away from a RustDesk session your typing goes to the new desktop, not the remote PC. Only this shortcut works this way; WarrenTool's other shortcuts go to the remote PC as usual.

Switching uses Windows' own virtual desktop interface, the one Task View uses. Microsoft doesn't document it and has changed it between Windows versions. WarrenTool supports Windows 11 24H2 and 25H2. If a Windows update changes it again, `Ctrl+Alt+R` shows a tray notice instead of switching.

---

## Aquarium
The aquarium starts by itself after 5 minutes without input. Press **`Ctrl+Alt+L`** (or choose **Lock with aquarium** from the tray menu) to start it straight away, as `SUPER+L` does on Linux.

Any key, mouse button or mouse movement ends it, and the Windows lock screen comes up: sign in with your PIN, Windows Hello or password as usual. The first second after it starts doesn't count, so letting go of `Ctrl+Alt+L` doesn't end it.

**Why isn't it the lock screen itself?** On Linux the aquarium is the lock screen, with the password box over the fish. Windows doesn't let apps replace its lock screen, so here the aquarium is a screensaver with **On resume, display logon screen** ticked: it plays while you're away, and Windows' own lock screen checks your password. The PC is just as locked; only the sign-in happens on Windows' screen rather than over the tank.

To change how long Windows waits, or to stop it locking, open **Screen Saver Settings** (search for "screen saver" in Start, or run `control desk.cpl,,@screensaver`). The aquarium is listed there as **Aquarium**, with a preview. Choosing **(None)** turns it off; `Ctrl+Alt+L` then shows a notice instead.

- It covers every monitor with one tank, scaled to fit the shortest one. Monitors of different heights see the parts of the tank they cover.
- Windows doesn't start a screensaver while a video is playing or a game is running full screen.
- Your power plan's **Turn off my screen after** still applies, so the screens go dark while the aquarium is up if that comes first. Make it longer than the screensaver wait to see the fish for a while.

---

## Pip (in Herdr)
Pip lives in its own block between the Herdr app's Agents list and the sidebar footer. Herdr's own agent status drives it, so nothing has to tell it anything:

| Herdr says | Pip |
|---|---|
| an agent is `blocked` | comes to the middle and knocks on the glass, under a "Claude needs you!" sign naming the space |
| an agent is `done` | dances under confetti, under a "Claude's done!" sign, until you look at that agent |
| any agent is `working` | gets to work: types at a laptop, reads a book, stacks blocks, hunts a bug, or thinks until a lightbulb goes on, changing every minute or so, faster with more agents at it |
| nothing going on | wanders, and every half-minute or so kicks a ball about, chases a butterfly, smells a flower, digs something up, or yawns and stretches |
| you're away | naps, and says hello when you're back |

Click it to go to the agent it's pointing at; otherwise clicking pets it. It's drawn in the theme's colours, so it follows whatever theme Herdr uses.

- **Setup.** `install.bat` copies Pip (`herdr-app\mods\pip`) and `herdr-app\layouts\main-pip.json` into Herdr's folder, `%USERPROFILE%\.config\herdr-app`, if the Herdr app is installed; otherwise it skips them, and running it again once Herdr is installed adds them. Only those three files are copied: the rest of that folder is Herdr's.
- **The layout.** Pip needs a block of its own, and Herdr replaces its own layouts on every start, so Pip lives in `main-pip`, a copy of Herdr's `main` with that block added. Herdr remembers which layout it shows:
  - Herdr open on its stock `main`: the install switches it to `main-pip`. Any other layout you've picked is left alone; `Ctrl+Shift+L` switches layouts.
  - Herdr never run: it starts on `main-pip`.
  - Herdr closed: it stays on what it was showing. Open it and press `Ctrl+Shift+L` until Pip shows up, or run `install.bat` again with Herdr open.
- **Away** is the PC locked, the screensaver running (`Ctrl+Alt+L` included), or no input for 5 minutes. WarrenTool tells Pip by keeping `%LOCALAPPDATA%\WarrenTool\pip.away` while you're away, so Pip only naps while WarrenTool is running. Herdr keeps `blocked` and `done` until you act, so whatever an agent did while you were gone is still on show when you're back.
- **An agent that exits leaves no `done`.** Herdr clears a terminal's status once its shell is back in front, so a run that ends with the program quitting goes from `working` straight to nothing, and Pip doesn't dance.
- **Previews:** `herdr-app do pip.demo-knock` (also `demo-party`, `demo-work`, `demo-busy`, `demo-sleep`, `demo-idle`) shows a mood without waiting for an agent; `demo-off` ends it.
- `uninstall.bat` moves an open Herdr from `main-pip` back to `main`, then removes Pip and `main-pip`. A closed Herdr left on `main-pip` goes back to `main` by itself when it next starts.

Pip's three files are the same as Linux-Setup's (`Hyprland_Setup/herdr-app`), and are kept that way: a change to `main.js` in one repo goes to the other too. The one `main.js` runs on both, learning you're away from WarrenTool on Windows and from hypridle and `lock.sh` on Linux. `main-pip` is a copy of Herdr's `main` layout, and copies don't get Herdr's updates. To pick up a newer `main`, clone it again (`herdr-app do layout.clone --name main --as main-pip-new`), add Pip's block (`{"id": "pip", "type": "block", "size": {"px": 112}, "content": {"mod": "pip"}}`) before `sidebar-footer`, and put the result in `herdr-app\layouts\main-pip.json`. The 112 is mirrored as `BLOCK_H` in `main.js`.

---

## Files Included
- `install.bat` / `uninstall.bat` - Double-click to install or remove.
- `Install.ps1` - The installer: builds the exe (including its icons), copies files, creates the shortcuts and wallpapers folder, and puts Pip in Herdr. Run with `-Uninstall` to remove. `-HerdrHome` is Herdr's folder (the batch files pass `%USERPROFILE%\.config\herdr-app`).
- `src\WarrenTool.cs` - The tray app: startup, the tray icon and menu, the hotkeys, and the away marker Pip naps by.
- `herdr-app\mods\pip\main.js`, `mod.json` - Pip, a Herdr mod. Copied into Herdr's `mods\pip` by the installer.
- `herdr-app\layouts\main-pip.json` - Herdr's `main` layout with a block for Pip. Copied into Herdr's `layouts` by the installer.
- `src\ScreenShot.cs` - Screen Shot: the selection overlay, the editor, text and QR code reading.
- `src\WallpaperPicker.cs` - Wallpaper Picker: the picker window and setting the wallpaper.
- `src\AudioSwitcher.cs` - Audio Switcher: Windows audio devices, the audio menu, the switch popup and the tray icon's artwork.
- `src\ClipboardManager.cs` - Clipboard Manager: watching the clipboard, the saved history and the history window.
- `src\DesktopSwitcher.cs` - Desktop Switcher: switching virtual desktops, and the keyboard hook that catches `Ctrl+Alt+R` ahead of RustDesk.
- `src\Aquarium.cs` - Aquarium: the tank, the fish and the visitors (the sharks and the diver), and the screensaver's windows. Built into its own `Aquarium.scr`, not into the exe.
- `assets\aquarium\sprites` - The Aquarium's pixel art, embedded in `Aquarium.scr`. Made by `assets\aquarium\make_sprites.py` (Python, only needed to change the art). The generator and the sprites are the same as Linux-Setup's (`Hyprland_Setup/aquarium`), and are kept that way.
- `src\Set-LockScreen.ps1` - Sets the Lock Screen image (WinRT API). Installed next to the exe and called by the picker.
- `src\app.manifest` - Makes the app DPI aware, so captures are pixel-exact on scaled and mixed-DPI monitors.
- `assets\Lowpoly_Street.png` - Starter wallpaper, copied in when the wallpapers folder has no images.
- `lib\zxing.dll` - [ZXing.Net](https://github.com/micjahn/ZXing.Net) 0.16.11, the QR code and barcode reader. The installer embeds it in the exe, so nothing is installed alongside. Licensed under Apache 2.0 (`lib\zxing-LICENSE.txt`).
