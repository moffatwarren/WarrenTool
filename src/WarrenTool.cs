// WarrenTool - one tray app that runs Screen Shot and Wallpaper Picker.
// Build with install.bat (uses the C# compiler that ships with Windows; no SDK needed).
//
// Runs in the background with a tray icon (left- or right-click for its menu) and two global hotkeys:
//   Ctrl+Alt+S   take a screenshot (ScreenShot.cs)
//   Ctrl+Alt+W   open or close the wallpaper picker (WallpaperPicker.cs)
//
// Command line:
//   WarrenTool.exe [--screenshot | --wallpaper] [--tray] [wallpapers folder]
//   --screenshot   start a capture (the "Screen Shot" Start Menu shortcut)
//   --wallpaper    open the wallpaper picker (the "Wallpaper Picker" Start Menu shortcut)
//   --tray         just start in the tray (used by "Start with Windows"; also what no flag does)
// When WarrenTool is already running, a second launch hands --screenshot / --wallpaper to it and exits.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenShot;
using WallpaperPicker;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;

// Without a target framework WPF behaves as .NET 4.0 and ignores the per-monitor DPI manifest
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]

namespace WarrenTool
{
    public static class Program
    {
        const string MutexName = "WarrenTool.SingleInstance";
        const string ScreenshotEventName = "WarrenTool.Screenshot";
        const string WallpaperEventName = "WarrenTool.Wallpaper";

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int dwProcessId);

        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += LoadEmbedded;
            Run(args);
        }

        // Libraries travel inside the exe (Install.ps1 embeds lib\zxing.dll), so the app stays a single
        // file. The runtime asks here when it can't find one on disk.
        static Assembly LoadEmbedded(object sender, ResolveEventArgs e)
        {
            using (Stream library = typeof(Program).Assembly.GetManifestResourceStream(new AssemblyName(e.Name).Name + ".dll"))
            {
                if (library == null) return null;
                var bytes = new MemoryStream();
                library.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }

        static bool HasFlag(string[] args, string flag)
        {
            return args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        }

        static void Run(string[] args)
        {
            bool screenshot = HasFlag(args, "--screenshot");
            bool wallpaper = HasFlag(args, "--wallpaper");
            // Default: <Pictures>\Wallpapers (GetFolderPath handles OneDrive redirection)
            string folder = args.FirstOrDefault(a => !a.StartsWith("--"))
                ?? IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Wallpapers");

            bool firstInstance;
            var mutex = new Mutex(true, MutexName, out firstInstance);
            var screenshotEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ScreenshotEventName);
            var wallpaperEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WallpaperEventName);

            if (!firstInstance)
            {
                // Already running in the tray: ask it to do what this launch was for
                if (screenshot || wallpaper) AllowSetForegroundWindow(-1); // ASFW_ANY
                if (screenshot) screenshotEvent.Set();
                if (wallpaper) wallpaperEvent.Set();
                return;
            }

            WinForms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var tray = new TrayApp(folder, screenshotEvent, wallpaperEvent);
            if (screenshot) tray.CaptureSoon();
            if (wallpaper) app.Dispatcher.BeginInvoke(new Action(tray.ShowPicker));
            app.Run();

            GC.KeepAlive(mutex);
        }
    }

    // ---------- Tray icon, global hotkeys, single-instance signalling ----------

    class TrayApp
    {
        const int ScreenshotHotkey = 1, WallpaperHotkey = 2;
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
        const uint VK_S = 0x53, VK_W = 0x57;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "WarrenTool";   // must match $runValue in Install.ps1

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        readonly HwndSource sink;
        readonly WinForms.NotifyIcon icon;
        readonly WinForms.ToolStripMenuItem startupItem;
        readonly Capturer capturer;
        readonly PickerWindow picker;

        public TrayApp(string folder, EventWaitHandle screenshotEvent, EventWaitHandle wallpaperEvent)
        {
            capturer = new Capturer(message => Warn("Screen Shot", message));
            picker = new PickerWindow(folder);
            // Created now rather than on first use, as when the picker was its own app
            new WindowInteropHelper(picker).EnsureHandle();

            // A message-only window receives the global hotkeys; editors and the picker come and go, this stays
            sink = new HwndSource(new HwndSourceParameters("WarrenTool.Hotkeys")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0,
            });
            sink.AddHook(WndProc);
            bool screenshotKeyOk = RegisterHotKey(sink.Handle, ScreenshotHotkey, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_S);
            bool wallpaperKeyOk = RegisterHotKey(sink.Handle, WallpaperHotkey, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_W);

            var menu = new WinForms.ContextMenuStrip
            {
                Renderer = new MenuRenderer(),
                ShowImageMargin = false,
                ShowCheckMargin = true,
                BackColor = Theme.Gdi(Theme.Base),
                ForeColor = Theme.Gdi(Theme.Text),
            };
            menu.HandleCreated += delegate
            {
                int corners = 3; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUNDSMALL
                Native.DwmSetWindowAttribute(menu.Handle, 33, ref corners, sizeof(int));
            };
            menu.Items.Add(new WinForms.ToolStripMenuItem("Take screenshot", null, delegate { CaptureSoon(); })
            {
                ShortcutKeyDisplayString = "Ctrl+Alt+S",
            });
            menu.Items.Add(new WinForms.ToolStripMenuItem("Open wallpaper picker", null, delegate { ShowPicker(); })
            {
                ShortcutKeyDisplayString = "Ctrl+Alt+W",
            });
            menu.Items.Add("Random wallpaper", null, delegate { RandomWallpaper(); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Open screenshots folder", null, delegate { Capturer.OpenFolder(); });
            menu.Items.Add("Open wallpapers folder", null, delegate { OpenWallpapersFolder(); });
            startupItem = new WinForms.ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); })
            {
                Checked = IsStartupEnabled(),
            };
            menu.Items.Add(startupItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Exit(); });
            foreach (WinForms.ToolStripItem item in menu.Items)
                if (item is WinForms.ToolStripMenuItem) item.Padding = new WinForms.Padding(0, 3, 0, 3);

            icon = new WinForms.NotifyIcon
            {
                Icon = MakeIcon(),
                Text = "WarrenTool\nScreenshot: Ctrl+Alt+S\nWallpaper: Ctrl+Alt+W",
                ContextMenuStrip = menu,
                Visible = true,
            };
            // There are two main actions, so a left-click opens the menu too
            icon.MouseClick += delegate(object s, WinForms.MouseEventArgs e)
            {
                if (e.Button == WinForms.MouseButtons.Left) ShowMenu();
            };

            if (!screenshotKeyOk && !wallpaperKeyOk)
                Warn("WarrenTool", "Ctrl+Alt+S and Ctrl+Alt+W are already used by another app. Use the tray menu instead.");
            else if (!screenshotKeyOk)
                Warn("Screen Shot", "Ctrl+Alt+S is already used by another app. Use the tray menu instead.");
            else if (!wallpaperKeyOk)
                Warn("Wallpaper Picker", "Ctrl+Alt+W is already used by another app. Use the tray menu instead.");

            // A second launch of the exe signals one of these events
            var waiter = new Thread(() =>
            {
                var events = new WaitHandle[] { screenshotEvent, wallpaperEvent };
                while (true)
                {
                    if (WaitHandle.WaitAny(events) == 0) dispatcher.BeginInvoke(new Action(CaptureSoon));
                    else dispatcher.BeginInvoke(new Action(ShowPicker));
                }
            }) { IsBackground = true };
            waiter.Start();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_HOTKEY) return IntPtr.Zero;
            switch (wParam.ToInt32())
            {
                case ScreenshotHotkey:
                    // Posted rather than called: the selection runs its own message loop
                    dispatcher.BeginInvoke(new Action(capturer.Capture));
                    handled = true;
                    break;
                case WallpaperHotkey:
                    TogglePicker();
                    handled = true;
                    break;
            }
            return IntPtr.Zero;
        }

        void Warn(string title, string message)
        {
            icon.ShowBalloonTip(5000, title, message, WinForms.ToolTipIcon.Warning);
        }

        // NotifyIcon only opens its menu on a right-click. Its own (private) method for that also
        // makes the menu close when the user clicks elsewhere.
        void ShowMenu()
        {
            var show = typeof(WinForms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            if (show != null) show.Invoke(icon, null);
        }

        // ---------- Screen Shot ----------

        public void CaptureSoon()
        {
            capturer.CaptureSoon();
        }

        // ---------- Wallpaper Picker ----------

        public void ShowPicker()
        {
            string problem = picker.ShowPicker();
            if (problem != null) Warn("Wallpaper Picker", problem);
        }

        void TogglePicker()
        {
            // A recent hide counts as "was open": whatever was clicked to get here may have
            // deactivated the picker (which hides it) before this runs
            if (picker.IsVisible || picker.JustHidden) picker.Dismiss();
            else ShowPicker();
        }

        void RandomWallpaper()
        {
            var files = PickerWindow.ScanFolder(picker.Folder);
            if (files.Count == 0) { ShowPicker(); return; }
            string file = files[new Random().Next(files.Count)];
            PickerWindow.ApplyWallpaper(file, true, null);
        }

        void OpenWallpapersFolder()
        {
            if (Directory.Exists(picker.Folder)) Process.Start("explorer.exe", "\"" + picker.Folder + "\"");
            else ShowPicker(); // shows the "folder not found" message
        }

        // ---------- App ----------

        static bool IsStartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunValue) != null;
        }

        void ToggleStartup()
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (IsStartupEnabled()) key.DeleteValue(RunValue, false);
                else key.SetValue(RunValue, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\" --tray");
            }
            startupItem.Checked = IsStartupEnabled();
        }

        void Exit()
        {
            UnregisterHotKey(sink.Handle, ScreenshotHotkey);
            UnregisterHotKey(sink.Handle, WallpaperHotkey);
            icon.Visible = false;
            icon.Dispose();
            Application.Current.Shutdown();
        }

        // Same .ico as the exe (embedded by Install.ps1), at the tray's size for the current DPI
        static Drawing.Icon MakeIcon()
        {
            using (var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("WarrenTool.ico"))
                return new Drawing.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
    }

    // ---------- Tray menu in Catppuccin Macchiato ----------

    class MenuColors : WinForms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground { get { return Theme.Gdi(Theme.Base); } }
        public override Drawing.Color ImageMarginGradientBegin { get { return Theme.Gdi(Theme.Base); } }
        public override Drawing.Color ImageMarginGradientMiddle { get { return Theme.Gdi(Theme.Base); } }
        public override Drawing.Color ImageMarginGradientEnd { get { return Theme.Gdi(Theme.Base); } }
        public override Drawing.Color MenuBorder { get { return Theme.Gdi(Theme.Surface1); } }
        public override Drawing.Color MenuItemBorder { get { return Theme.Gdi(Theme.Surface0); } }
        public override Drawing.Color MenuItemSelected { get { return Theme.Gdi(Theme.Surface0); } }
        public override Drawing.Color SeparatorDark { get { return Theme.Gdi(Theme.Surface0); } }
        public override Drawing.Color SeparatorLight { get { return Theme.Gdi(Theme.Base); } }
    }

    class MenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            // The shortcut ("Ctrl+Alt+S") is rendered through here too; keep it quieter than the label
            e.TextColor = Theme.Gdi(e.Text == e.Item.Text ? Theme.Text : Theme.Subtext0);
            base.OnRenderItemText(e);
        }

        // The stock check mark is a dark glyph on a light box
        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            e.Graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var pen = new Drawing.Pen(Theme.Gdi(Theme.Mauve), 2f))
            {
                pen.StartCap = pen.EndCap = Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = Drawing.Drawing2D.LineJoin.Round;
                e.Graphics.DrawLines(pen, new[]
                {
                    new Drawing.PointF(cx - 4.5f, cy), new Drawing.PointF(cx - 1.5f, cy + 3.5f), new Drawing.PointF(cx + 5f, cy - 3.5f),
                });
            }
        }
    }
}
