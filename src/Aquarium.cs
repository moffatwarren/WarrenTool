// Aquarium - a pixel-art fish tank screensaver, one tank across every monitor.
// A port of the aquarium lock screen in Linux-Setup (Hyprland_Setup/aquarium): Ocean.qml's simulation, with
// its two visitors, the shark (sometimes a great white) and the diver, and Aquarium.qml's drawing.
// Not part of WarrenTool.exe: Install.ps1 builds it into its own Aquarium.scr next to it, embeds the
// sprites (assets\aquarium\sprites) and makes it the screensaver. WarrenTool's Ctrl+Alt+L asks Windows to
// start it (WarrenTool.cs).
//
// Windows runs a screensaver as:
//   Aquarium.scr /s          full screen, on every monitor
//   Aquarium.scr /p <hwnd>   in the preview box in Screen Saver Settings
//   Aquarium.scr /c          its settings; it has none, and says so (no arguments does the same)
//
// Any key, mouse button or mouse movement ends it. When "On resume, display logon screen" is ticked it
// locks the PC itself as it goes, a crash included, so the lock never rests on Windows' grace period.
//
// The scene is laid out in "tank pixels" and scaled up by a whole number, so every tank pixel is the same
// size on screen and nothing is ever resampled. The tank spans the whole virtual screen: each frame is
// drawn once, at tank size, and each monitor's window shows its own part of it, so a fish that swims off
// one monitor carries on onto the next.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Aquarium
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        public const int SRCCOPY = 0x00CC0020, COLORONCOLOR = 3, HALFTONE = 4;
        public const int GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
        const int SPI_GETSCREENSAVESECURE = 0x0076;

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height,
            IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, int rop);

        [DllImport("gdi32.dll")]
        public static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        public static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref int value, int winIni);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        // When "On resume, display logon screen" is ticked in Screen Saver Settings
        public static void LockIfSecure()
        {
            int secure = 0;
            if (SystemParametersInfo(SPI_GETSCREENSAVESECURE, 0, ref secure, 0) && secure != 0) LockWorkStation();
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // A failure ends the aquarium (see FullScreen). WinForms' own "Continue or Quit" box would sit
            // over a screensaver with nobody there to answer it.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);

            // "/s", "/p 1234", "/p:1234", "/c:1234", in either case, and "-s" from some launchers
            string mode = args.Length > 0 ? args[0].TrimStart('/', '-').ToLowerInvariant() : "c";
            string value = args.Length > 1 ? args[1] : null;
            int colon = mode.IndexOf(':');
            if (colon > 0)
            {
                value = mode.Substring(colon + 1);
                mode = mode.Substring(0, colon);
            }

            if (mode == "s") FullScreen();
            else if (mode == "p" && value != null) Preview(new IntPtr(long.Parse(value)));
            else MessageBox.Show("The aquarium has no settings of its own.\n\n"
                + "How long Windows waits before showing it, and whether the PC locks when it ends "
                + "(\"On resume, display logon screen\"), are set in Screen Saver Settings. "
                + "Ctrl+Alt+L shows it straight away while WarrenTool is running.",
                "Aquarium", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static void FullScreen()
        {
            // However it ends, the PC is not left open behind it
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log(e.ExceptionObject as Exception);
                Native.LockIfSecure();
                Environment.Exit(1);
            };
            try
            {
                Cursor.Hide();
                Application.Run(new Screensaver(Screen.AllScreens));
            }
            finally
            {
                Native.LockIfSecure();
            }
        }

        static void Preview(IntPtr parent)
        {
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log(e.ExceptionObject as Exception);
                Environment.Exit(1);
            };
            Application.Run(new Screensaver(parent));
        }

        // Into WarrenTool's own error log (ErrorLog in WarrenTool.cs), where its other problems go
        static void Log(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WarrenTool", "error.log");
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  Aquarium: " + ex + "\r\n\r\n");
            }
            catch { }   // nowhere left to report it
        }
    }

    // ---------- The screensaver: one tank, its frame and the windows showing it ----------

    class Screensaver : ApplicationContext
    {
        // Mouse movement that doesn't count, in screen pixels: a desk knock, or a mouse that drifts
        const int MoveSlack = 4;
        // Input that doesn't count, in seconds after starting: the keys of Ctrl+Alt+L being let go
        const double Settle = 1;

        readonly Tank tank;
        readonly Frame frame;
        readonly List<TankWindow> windows = new List<TankWindow>();
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly IntPtr previewParent;
        double steppedTo;   // how far along the clock the tank has been stepped, in seconds
        Point cursor;
        bool ended;

        // Full screen: a window on each monitor, all looking into one tank
        public Screensaver(Screen[] screens)
        {
            Rectangle desktop = SystemInformation.VirtualScreen;
            // 270 tank pixels tall, near enough, on the shortest monitor: 1080p scales by 4, 1440p by 5, 4K by 8.
            // One scale for every monitor, so a fish keeps its size as it crosses from one to the next.
            int scale = Math.Max(1, screens.Min(s => s.Bounds.Height) / 270);

            var sources = new Rectangle[screens.Length];
            var targets = new Rectangle[screens.Length];
            for (int i = 0; i < screens.Length; i++)
            {
                Rectangle b = screens[i].Bounds;
                int left = b.X - desktop.X, top = b.Y - desktop.Y;
                int x = left / scale, y = top / scale;
                // A monitor that doesn't begin on a whole tank pixel shows its first one partly off its edge
                int dx = x * scale - left, dy = y * scale - top;
                sources[i] = new Rectangle(x, y, Ceil(b.Width - dx, scale), Ceil(b.Height - dy, scale));
                targets[i] = new Rectangle(dx, dy, sources[i].Width * scale, sources[i].Height * scale);
            }

            tank = new Tank(sources.Max(r => r.Right), sources.Max(r => r.Bottom));
            frame = new Frame(tank.Width, tank.Height);
            tank.Draw(frame.Bitmap);

            for (int i = 0; i < screens.Length; i++)
            {
                var window = new TankWindow(frame, sources[i], targets[i]) { Bounds = screens[i].Bounds, TopMost = true };
                window.Moved += Moved;
                window.Woken += Woken;
                window.FormClosed += delegate { ExitThread(); };
                windows.Add(window);
            }
            cursor = Cursor.Position;
            foreach (TankWindow window in windows) window.Show();
            // The keyboard goes to the primary monitor's window; any of them ends it
            windows[Math.Max(0, Array.FindIndex(screens, s => s.Primary))].Activate();
            Start();
        }

        // The preview box in Screen Saver Settings: a miniature of a 1080p tank, rather than a tank only a
        // preview's few pixels tall, in which a single weed would reach the surface
        public Screensaver(IntPtr parent)
        {
            previewParent = parent;
            Native.RECT client;
            Native.GetClientRect(parent, out client);
            var size = new Size(Math.Max(1, client.Right), Math.Max(1, client.Bottom));

            tank = new Tank(Math.Max(1, 270 * size.Width / size.Height), 270);
            frame = new Frame(tank.Width, tank.Height);
            tank.Draw(frame.Bitmap);

            var window = new TankWindow(frame, new Rectangle(0, 0, tank.Width, tank.Height), new Rectangle(Point.Empty, size));
            window.EmbedIn(parent, size);
            window.Show();
            windows.Add(window);
            Start();
        }

        static int Ceil(int length, int scale)
        {
            return (length + scale - 1) / scale;
        }

        void Start()
        {
            timer.Tick += delegate { Advance(); };
            timer.Start();
        }

        // Steps the tank at its own fixed rate, whatever the timer's (Windows rounds it to its 15.6 ms
        // tick), and shows the result
        void Advance()
        {
            if (previewParent != IntPtr.Zero && !Native.IsWindow(previewParent))
            {
                ExitThread();
                return;
            }

            double now = clock.Elapsed.TotalSeconds;
            // After a stall (the PC asleep, say) it carries on from now rather than racing to catch up
            if (now - steppedTo > 0.5) steppedTo = now - Tank.Step;
            if (now - steppedTo < Tank.Step) return;
            while (now - steppedTo >= Tank.Step)
            {
                tank.Tick();
                steppedTo += Tank.Step;
            }

            tank.Draw(frame.Bitmap);
            foreach (TankWindow window in windows) window.Present();
        }

        void Moved()
        {
            Point now = Cursor.Position;
            // A window appearing under the mouse counts as a move, and so can letting go of it
            if (clock.Elapsed.TotalSeconds < Settle)
            {
                cursor = now;
                return;
            }
            if (Math.Abs(now.X - cursor.X) > MoveSlack || Math.Abs(now.Y - cursor.Y) > MoveSlack) Woken();
        }

        void Woken()
        {
            if (clock.Elapsed.TotalSeconds >= Settle) ExitThread();
        }

        protected override void ExitThreadCore()
        {
            if (ended) return;   // closing the windows below comes back through here
            ended = true;
            timer.Stop();
            foreach (TankWindow window in windows) window.Close();
            base.ExitThreadCore();
        }
    }

    // One monitor's window onto the tank, or the preview box's
    class TankWindow : Form
    {
        const int WM_ACTIVATEAPP = 0x1C, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_MOUSEMOVE = 0x200;
        const int WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
        const int WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_MOUSEHWHEEL = 0x20E;

        readonly Frame frame;
        readonly Rectangle source;   // in tank pixels
        readonly Rectangle target;   // in this window's pixels

        // The mouse moved (by how much is the screensaver's to judge), or anything else that ends it happened
        public event Action Moved, Woken;

        public TankWindow(Frame frame, Rectangle source, Rectangle target)
        {
            this.frame = frame;
            this.source = source;
            this.target = target;
            Text = "Aquarium";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            // Every pixel is painted from the frame, so there is nothing to erase first and no flicker
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        // Makes this a child of the preview box in Screen Saver Settings, filling it
        public void EmbedIn(IntPtr parent, Size size)
        {
            Native.SetParent(Handle, parent);
            int style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
            Native.SetWindowLong(Handle, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);
            Bounds = new Rectangle(Point.Empty, size);
        }

        // Shows the frame that has just been drawn
        public void Present()
        {
            IntPtr hdc = Native.GetDC(Handle);
            try { Blit(hdc); }
            finally { Native.ReleaseDC(Handle, hdc); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr hdc = e.Graphics.GetHdc();
            try { Blit(hdc); }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }

        // Each tank pixel becomes a whole block of screen pixels; only the preview shrinks it, and smooths it
        void Blit(IntPtr hdc)
        {
            bool shrinking = target.Width < source.Width;
            Native.SetStretchBltMode(hdc, shrinking ? Native.HALFTONE : Native.COLORONCOLOR);
            if (shrinking) Native.SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
            Native.StretchBlt(hdc, target.X, target.Y, target.Width, target.Height,
                frame.Dc, source.X, source.Y, source.Width, source.Height, Native.SRCCOPY);
        }

        // Every key, Tab and the arrows included, before WinForms can take it for itself
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Raise(Woken);
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEMOVE:
                    Raise(Moved);
                    break;
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                case WM_LBUTTONDOWN:
                case WM_RBUTTONDOWN:
                case WM_MBUTTONDOWN:
                case WM_XBUTTONDOWN:
                case WM_MOUSEWHEEL:
                case WM_MOUSEHWHEEL:
                    Raise(Woken);
                    break;
                case WM_ACTIVATEAPP:
                    // Another app came to the front (the Start menu, Ctrl+Alt+Del, a window that pops up)
                    if (m.WParam == IntPtr.Zero) Raise(Woken);
                    break;
            }
            base.WndProc(ref m);
        }

        static void Raise(Action handler)
        {
            if (handler != null) handler();
        }
    }

    // The picture at tank size, in a DIB section: GDI+ draws into it, and StretchBlt copies from it
    class Frame
    {
        public readonly Bitmap Bitmap;
        public readonly IntPtr Dc;

        public Frame(int width, int height)
        {
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height,   // top-down, as GDI+ lays out its rows
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr bits;
            IntPtr dib = Native.CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Dc = Native.CreateCompatibleDC(IntPtr.Zero);
            Native.SelectObject(Dc, dib);
            // The same memory: what GDI+ draws is what StretchBlt copies. Both live as long as the process.
            Bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }
    }

    // ---------- Sprites ----------

    // One animated pixel-art sprite, facing right as make_sprites.py draws it, with a mirrored copy of
    // each frame for swimming left, made once rather than on every frame
    class Sprite
    {
        static readonly Dictionary<string, Sprite> loaded = new Dictionary<string, Sprite>();

        public readonly Bitmap[] Frames, Mirrored;
        public int Width { get { return Frames[0].Width; } }
        public int Height { get { return Frames[0].Height; } }

        // "fish_clown_orange" with 3 frames is fish_clown_orange_0.png to _2.png; with 1, "rock_0" is rock_0.png
        public static Sprite Get(string name, int frames)
        {
            Sprite sprite;
            if (!loaded.TryGetValue(name, out sprite)) loaded[name] = sprite = new Sprite(name, frames);
            return sprite;
        }

        Sprite(string name, int frames)
        {
            Frames = new Bitmap[frames];
            Mirrored = new Bitmap[frames];
            for (int i = 0; i < frames; i++)
            {
                Frames[i] = Load(frames > 1 ? name + "_" + i : name);
                Mirrored[i] = (Bitmap)Frames[i].Clone();
                Mirrored[i].RotateFlip(RotateFlipType.RotateNoneFlipX);
            }
        }

        // From the PNGs Install.ps1 embeds in Aquarium.scr, premultiplied, which GDI+ draws fastest
        public static Bitmap Load(string name)
        {
            using (Stream stream = typeof(Sprite).Assembly.GetManifestResourceStream(name + ".png"))
            {
                if (stream == null) throw new FileNotFoundException("Aquarium.scr is missing its sprite " + name + ".png");
                using (var png = new Bitmap(stream))
                {
                    var bitmap = new Bitmap(png.Width, png.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Tank.Canvas(bitmap))
                    {
                        g.CompositingMode = CompositingMode.SourceCopy;
                        Tank.Blit(g, png, 0, 0);
                    }
                    return bitmap;
                }
            }
        }
    }

    // ---------- The tank ----------

    // Ocean.qml's simulation step for step, with its numbers. Everything is in tank pixels and seconds.
    // The water is pure black, and nothing is drawn over it but the scenery itself.
    class Tank
    {
        public const double Step = 1.0 / 15;   // the simulation's rate, as on Linux

        // How long, in seconds, before the shark first shows up, and between its visits
        static readonly double[] SharkFirst = { 20, 60 }, SharkGap = { 60, 150 };
        // How often a visit is the great white rather than the usual shark. It is a shark's visit either
        // way, so the two never meet, and the diver takes turns with both.
        const double BigSharkChance = 0.25;
        // The same for the diver. The two visitors take turns: neither comes in while the other is in the
        // water, and each leaves the other a quiet spell after it goes.
        static readonly double[] DiverFirst = { 45, 120 }, DiverGap = { 150, 300 };

        static readonly int[] TailBeat = { 0, 1, 0, 2 };   // rest, up, rest, down
        static readonly string[] WeedNames = { "weed_green", "weed_teal", "weed_lime" };
        static readonly string[] Props = { "coral_pink", "coral_orange", "coral_violet", "coral_brain", "coral_tube", "rock_0", "rock_1" };

        class Species
        {
            public string Id;
            public string[] Colours;
            public int Count, Back;
            public double[] Speed, Band;
        }

        // Roster per 480 tank pixels of width, so a wider tank is not an emptier one
        static readonly Species[] Roster =
        {
            new Species { Id = "clown", Colours = new[] { "orange", "red" }, Count = 3, Speed = new[] { 12.0, 20 }, Band = new[] { 0.5, 1 }, Back = 1 },
            new Species { Id = "tang", Colours = new[] { "blue", "teal" }, Count = 3, Speed = new[] { 14.0, 24 }, Band = new[] { 0.15, 0.8 }, Back = 1 },
            new Species { Id = "angel", Colours = new[] { "yellow", "violet" }, Count = 2, Speed = new[] { 7.0, 12 }, Band = new[] { 0.2, 0.75 }, Back = 1 },
            new Species { Id = "gold", Colours = new[] { "gold", "pearl" }, Count = 3, Speed = new[] { 10.0, 18 }, Band = new[] { 0.1, 0.8 }, Back = 1 },
            new Species { Id = "puffer", Colours = new[] { "yellow", "green" }, Count = 2, Speed = new[] { 5.0, 9 }, Band = new[] { 0.55, 1 }, Back = 0 },
            new Species { Id = "betta", Colours = new[] { "magenta", "royal" }, Count = 2, Speed = new[] { 8.0, 14 }, Band = new[] { 0.1, 0.7 }, Back = 0 },
        };
        static readonly string[] SchoolColours = { "neon", "lime" };
        static readonly int[] SchoolSizes = { 9, 7 };

        class Fish
        {
            public Sprite Sprite;
            public bool Far;
            public Fish Leader;
            public double[] Band;
            public double Base, X, Y, V, Tv, Ty, Timer, Phase, Bob, OffX, OffY;
            public int Dir;
            public bool Turning;
            // Seconds left of darting away from the shark, and which way: left or right, up or down
            public double Panic;
            public int FleeDir;
            public bool FleeUp;
            public int Frame, DrawX, DrawY;
            public int W { get { return Sprite.Width; } }
            public int H { get { return Sprite.Height; } }
        }

        // The shark and the diver: in the water by turns, and away for a while in between
        class Visitor
        {
            public bool Active;
            public double Wait;   // seconds before it can come in
            public int Dir, Frame, DrawX, DrawY;
        }

        // The two sharks. Speeds are tank pixels a second. The fear zone (Scare) reaches `Ahead` in front of
        // the shark, `Behind` it and `Beside` above and below, and a fish in it darts `Escape` up or down;
        // PreyFor looks `Reach` ahead of the snout and `Spread` either side of its line. The great white is
        // twice the size, so all of that is wider, and it is slower in the water, with a slower beat of its tail.
        class SharkKind
        {
            public Sprite Sprite;
            public double Cruise, Lunge, Ahead, Behind, Beside, Reach, Spread, Beat, Bob;
            public double[] Escape;
        }

        class Shark : Visitor
        {
            public SharkKind Kind;   // this visit's
            public double X, Y, Ty, V, Burst, Rest, Timer, Phase;
            public int Turns;
            public bool Turning;
            public int W { get { return Kind.Sprite.Width; } }
            public int H { get { return Kind.Sprite.Height; } }
        }

        enum DiverMode { Down, Swim, Hover, Up }

        // Tracked by its centre, because its two poses are different shapes
        class Diver : Visitor
        {
            public Sprite Swim, Rise;
            public Sprite Pose;   // the one it is in: upright to sink and rise, flat to swim
            public DiverMode Mode;
            public double Cx, Cy, Tx, Ty, V, Timer, Breath, PuffGap, Phase;
            public int Legs, Puffs;
            public int W { get { return Pose.Width; } }
            public int H { get { return Pose.Height; } }
        }

        class Jelly
        {
            public Sprite Sprite;
            public double X, Y, Vx, Phase;
            public bool Rising;
            public int Frame;
        }

        class Crab
        {
            public double X, Timer, Phase;
            public int Dir, Frame;
            public bool Walking;
        }

        class Bubble
        {
            public Sprite Sprite;
            public bool Alive;
            public double X, Y, Vy, Seed, Timer;
            public int DrawX, DrawY;
        }

        class Weed
        {
            public Sprite Sprite;
            public double X, Rate;
            public int Sink, Offset;
        }

        public readonly int Width, Height;
        readonly int floorTop;
        readonly Random random = new Random();
        double clock;

        // Far fish first: fish[0..farCount) swim behind the back weeds, the rest in front of the jellies
        readonly List<Fish> fish = new List<Fish>();
        readonly int farCount;
        readonly SharkKind smallShark, greatWhite;
        readonly Shark shark = new Shark();
        readonly Diver diver;
        // The diver's exhaled bubbles: a fixed pool
        readonly List<Bubble> breath = new List<Bubble>();
        readonly List<Jelly> jellies = new List<Jelly>();
        readonly List<Crab> crabs = new List<Crab>();
        readonly List<Bubble> bubbles = new List<Bubble>();
        readonly List<Weed> backWeeds = new List<Weed>(), frontWeeds = new List<Weed>();
        readonly List<double> vents = new List<double>();
        readonly Sprite crab = Sprite.Get("crab", 2);

        // What never moves, drawn once: the far rocks on black, the sand with its corals, rocks and chest,
        // and the surface's three frames
        readonly Bitmap back, ground;
        readonly Bitmap[] surface = new Bitmap[3];

        public Tank(int width, int height)
        {
            Width = width;
            Height = height;
            floorTop = height - 30;
            double density = Math.Max(0.6, width / 480.0);

            var near = new List<Fish>();
            var far = new List<Fish>();
            foreach (Species s in Roster)
            {
                int total = Math.Max(1, Round(s.Count * density));
                for (int i = 0; i < total; i++)
                    near.Add(NewFish("fish_" + s.Id + "_" + s.Colours[i % s.Colours.Length], s.Speed, s.Band, false));
                for (int i = 0; i < Round(s.Back * density); i++)
                    far.Add(NewFish("fish_" + s.Id + "_" + Pick(s.Colours), s.Speed, s.Band, true));
            }
            for (int k = 0; k < SchoolColours.Length; k++)
            {
                double[] speed = { 16, 24 };
                Fish lead = NewFish("fish_tetra_" + SchoolColours[k], speed, new[] { 0.1, 0.7 }, false);
                near.Add(lead);
                for (int i = 1; i < SchoolSizes[k]; i++)
                {
                    Fish f = NewFish("fish_tetra_" + SchoolColours[k], speed, lead.Band, false);
                    f.Leader = lead;
                    f.OffX = Rand(-34, 8);
                    f.OffY = Rand(-13, 13);
                    near.Add(f);
                }
            }
            fish.AddRange(far);
            farCount = far.Count;
            fish.AddRange(near);

            foreach (Fish f in fish)
            {
                // A leader is always earlier in the list than its followers, so a school starts out together
                // instead of spending a minute assembling itself
                f.X = f.Leader != null ? f.Leader.X + f.OffX : Rand(0, Math.Max(1, Width - f.W));
                f.Y = f.Leader != null ? f.Leader.Y + f.OffY : Rand(BandTop(f), BandBottom(f));
                f.Ty = f.Y;
                f.Tv = f.V = f.Base;
            }

            smallShark = new SharkKind
            {
                Sprite = Sprite.Get("shark", 3), Cruise = 22, Lunge = 50, Ahead = 70, Behind = 24, Beside = 30,
                Escape = new[] { 24.0, 44 }, Reach = 110, Spread = 50, Beat = 1, Bob = 1.5,
            };
            greatWhite = new SharkKind
            {
                Sprite = Sprite.Get("shark_big", 3), Cruise = 18, Lunge = 40, Ahead = 100, Behind = 32, Beside = 40,
                Escape = new[] { 32.0, 56 }, Reach = 150, Spread = 70, Beat = 0.7, Bob = 2,
            };
            shark.Kind = smallShark;
            shark.Wait = Rand(SharkFirst[0], SharkFirst[1]);

            diver = new Diver { Swim = Sprite.Get("diver_swim", 3), Rise = Sprite.Get("diver_rise", 3) };
            diver.Pose = diver.Rise;
            diver.Wait = Rand(DiverFirst[0], DiverFirst[1]);
            // Enough for three breaths on their way up from the bottom at once
            int[] breathSizes = { 0, 1, 0, 1, 2 };
            for (int i = 0; i < 20; i++)
                breath.Add(new Bubble { Sprite = Sprite.Get("bubble_" + breathSizes[i % 5], 1), Seed = Rand(0, 6.28) });

            for (int i = 0; i < Math.Max(1, Round(2 * density)); i++)
                jellies.Add(new Jelly
                {
                    Sprite = Sprite.Get("jelly_" + (i % 2 == 1 ? "aqua" : "pink"), 3),
                    X = Rand(10, Width - 30), Y = Rand(20, floorTop - 60), Vx = Rand(-2, 2), Phase = Rand(0, 3),
                    Rising = random.NextDouble() < 0.5,
                });

            for (int i = 0; i < Math.Max(1, Round(density)); i++)
                crabs.Add(new Crab { X = Rand(20, Width - 40), Dir = 1, Timer = Rand(1, 5) });

            for (int i = 0; i < Round(16 * density); i++)
                bubbles.Add(new Bubble { Sprite = Sprite.Get("bubble_" + i % 3, 1), Seed = Rand(0, 6.28), Timer = Rand(0, 8) });

            back = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Canvas(back))
            {
                g.Clear(Color.Black);
                Bitmap range = Sprite.Load("far_0");
                Tile(g, range, 0, Height - 24 - range.Height);
                // Shifted so the two ranges' peaks do not line up
                range = Sprite.Load("far_1");
                Tile(g, range, -60, Height - 18 - range.Height);
            }

            ground = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Canvas(ground))
            {
                Bitmap sand = Sprite.Load("sand");
                Tile(g, sand, 0, Height - sand.Height);
                LayOutScenery(g, density);
            }

            Bitmap[] waves = Sprite.Get("surface", 3).Frames;
            for (int i = 0; i < surface.Length; i++)
            {
                surface[i] = new Bitmap(Width, waves[i].Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Canvas(surface[i])) Tile(g, waves[i], 0, 0);
            }

            // So the first frame shown already has everything in its place
            Tick();
        }

        // The weeds, and the corals, rocks and chest drawn onto the sand
        void LayOutScenery(Graphics ground, double density)
        {
            // The chest goes somewhere near the middle and vents bubbles. It is chosen first so that
            // nothing tall in front of it can hide it.
            double chestX = Rand(0.3, 0.7);
            Func<double, double> clearOfChest = x => Math.Abs(x - chestX) < 0.08 ? (x + 0.16) % 1 : x;

            foreach (double x in Scatter(Round(5 * density)))
                backWeeds.Add(new Weed { Sprite = Sprite.Get(Pick(WeedNames), 4), X = x, Sink = RandInt(3, 9), Offset = RandInt(0, 3), Rate = Rand(0.45, 0.8) });
            foreach (double x in Scatter(Round(2 * density)))
                frontWeeds.Add(new Weed { Sprite = Sprite.Get(Pick(WeedNames), 4), X = clearOfChest(x), Sink = RandInt(-4, 2), Offset = RandInt(0, 3), Rate = Rand(0.45, 0.8) });

            // Dealt from a shuffled deck rather than picked, so a tank cannot draw five of the same
            // thing and miss the rest
            var deck = new List<string>();
            foreach (double x in Scatter(Round(8 * density)))
            {
                if (deck.Count == 0)
                {
                    deck.AddRange(Props);
                    for (int i = deck.Count - 1; i > 0; i--)
                    {
                        int j = RandInt(0, i);
                        string swap = deck[i];
                        deck[i] = deck[j];
                        deck[j] = swap;
                    }
                }
                string name = deck[deck.Count - 1];
                deck.RemoveAt(deck.Count - 1);
                PlaceDecor(ground, name, clearOfChest(x), RandInt(2, 11));
            }
            PlaceDecor(ground, "chest", chestX, 1);

            vents.Add(chestX * Width + 11);
            for (int i = 0; i < Round(2 * density); i++) vents.Add(Rand(0.05, 0.95) * Width);
        }

        void PlaceDecor(Graphics ground, string name, double x, int sink)
        {
            Bitmap image = Sprite.Get(name, 1).Frames[0];
            Blit(ground, image, Round(x * Width - image.Width / 2.0), Height - image.Height - sink - 12);
        }

        // `count` places across the tank, from 0 to 1, one somewhere in each of `count` equal parts
        List<double> Scatter(int count)
        {
            var places = new List<double>();
            for (int i = 0; i < count; i++) places.Add((i + Rand(0.15, 0.85)) / count);
            return places;
        }

        // ---------- Movement ----------

        public void Tick()
        {
            clock += Step;
            StepShark(shark);
            StepDiver(diver);
            foreach (Bubble b in breath) StepBreath(b);
            foreach (Fish f in fish) StepFish(f);
            foreach (Jelly j in jellies) StepJelly(j);
            foreach (Crab c in crabs) StepCrab(c);
            foreach (Bubble b in bubbles) StepBubble(b);
        }

        double BandTop(Fish f) { return 10 + f.Band[0] * Math.Max(1, floorTop - f.H - 10); }
        double BandBottom(Fish f) { return 10 + f.Band[1] * Math.Max(1, floorTop - f.H - 10); }

        Fish NewFish(string name, double[] speed, double[] band, bool far)
        {
            return new Fish
            {
                Sprite = Sprite.Get(far ? name + "_far" : name, 3), Far = far,
                Base = Rand(speed[0], speed[1]) * (far ? 0.7 : 1), Band = band,
                Dir = random.NextDouble() < 0.5 ? 1 : -1, Timer = Rand(1, 6), Phase = Rand(0, 4), Bob = Rand(0, 6.28),
            };
        }

        void StepLeader(Fish f)
        {
            f.Timer -= Step;
            if (f.Timer <= 0)
            {
                f.Timer = Rand(2, 7);
                double mood = random.NextDouble();
                f.Tv = f.Base * (mood < 0.15 ? 2.4 : mood < 0.35 ? 0.35 : 1);
                f.Ty = Rand(BandTop(f), BandBottom(f));
                if (random.NextDouble() < 0.18) f.Turning = true;
            }

            // Turn at the glass, letting part of the fish go past it first
            if ((f.Dir > 0 && f.X > Width - f.W * 0.7) || (f.Dir < 0 && f.X < -f.W * 0.3)) f.Turning = true;

            // A turn is a slow-down, a flip, and then speeding up again
            if (f.Turning && f.V < 1.5)
            {
                f.Dir = -f.Dir;
                f.Turning = false;
            }

            f.V += ((f.Turning ? 0 : f.Tv) - f.V) * 0.1;
            f.X += f.Dir * f.V * Step;
            f.Y += (f.Ty - f.Y) * 0.012;
        }

        void StepFollower(Fish f)
        {
            Fish l = f.Leader;
            double tx = l.X - l.Dir * (f.OffX + 6) + (l.Dir < 0 ? l.W - f.W : 0);
            double ty = l.Y + f.OffY;
            double gap = tx - f.X;

            f.Dir = Math.Abs(gap) > 7 ? Math.Sign(gap) : l.Dir;
            double move = Math.Max(-40, Math.Min(40, gap * 0.9)) * Step;
            f.X += move;
            // Kept out of the sand: a leader that fled to the floor has its school below it
            f.Y += (Clamp(ty, 8, floorTop - f.H) - f.Y) * 0.05;
            f.V = Math.Abs(move) / Step;
        }

        // Whether the shark is close enough to frighten this fish, and if so, which way is away. The zone
        // reaches much further in front of the shark than behind it, so fish scatter ahead of it and close
        // up again in its wake.
        void Scare(Fish f)
        {
            Shark s = shark;
            if (!s.Active) return;
            SharkKind k = s.Kind;
            double dx = f.X + f.W / 2.0 - (s.X + s.W / 2.0);
            double dy = f.Y + f.H / 2.0 - (s.Y + s.H / 2.0);
            double rx = s.W / 2.0 + (Math.Sign(dx) == s.Dir ? k.Ahead : k.Behind);
            double ry = s.H / 2.0 + k.Beside;
            if (dx * dx / (rx * rx) + dy * dy / (ry * ry) > 1) return;

            int away = dx < 0 ? -1 : 1;
            bool up = dy < 0;
            // A new target only when the way out changes, or the fish would twitch on the spot
            if (f.Panic <= 0 || away != f.FleeDir || up != f.FleeUp)
            {
                f.FleeDir = away;
                f.FleeUp = up;
                f.Ty = Clamp(f.Y + (up ? -1 : 1) * Rand(k.Escape[0], k.Escape[1]), 8, Math.Max(8, floorTop - f.H));
            }
            f.Panic = Rand(1.2, 2.2);
        }

        // The diver frightens nobody, but the fish keep their distance: one that finds itself close turns
        // away at its own pace and moves up or down out of the diver's level. A school goes where its leader
        // goes, and the fish at the back of the tank are further away than they look, so neither is moved
        // directly.
        void Shy(Fish f)
        {
            Diver d = diver;
            if (!d.Active || f.Leader != null || f.Far) return;
            double dx = f.X + f.W / 2.0 - d.Cx;
            double dy = f.Y + f.H / 2.0 - d.Cy;
            double rx = d.W / 2.0 + 22, ry = d.H / 2.0 + 16;
            if (dx * dx / (rx * rx) + dy * dy / (ry * ry) > 1) return;
            f.Ty = Clamp(d.Cy + (dy < 0 ? -1 : 1) * (ry + 4) - f.H / 2.0, 8, Math.Max(8, floorTop - f.H));
            // On top of the leader's own slow drift, or a puffer the diver swims up to is still sitting on
            // its head when it arrives
            f.Y += (f.Ty - f.Y) * 0.04;
            if (f.Dir != (dx < 0 ? -1 : 1)) f.Turning = true;
        }

        // A frightened fish darts: it flips on the spot instead of slowing down to turn, swims faster than
        // it ever does otherwise, and leaves its band and its school. Once the panic wears off a school fish
        // finds its place again by itself.
        void Flee(Fish f)
        {
            f.Panic -= Step;
            f.Dir = f.FleeDir;
            f.Turning = false;
            f.V += (28 + f.Base * 1.3 - f.V) * 0.3;
            f.X += f.Dir * f.V * Step;
            // Cornered against the glass, it stays in the tank and gets out of the way by depth. The glass
            // also takes its speed, or it would glide off the screen when the panic ends.
            double held = Clamp(f.X, -f.W * 0.3, Width - f.W * 0.7);
            if (held != f.X)
            {
                f.X = held;
                f.V = Math.Min(f.V, 4);
            }
            f.Y += (f.Ty - f.Y) * 0.08;
            if (f.Panic <= 0)
            {
                f.Tv = f.Base;
                f.Timer = Rand(0.5, 2);
            }
        }

        void StepFish(Fish f)
        {
            Scare(f);
            if (f.Panic <= 0) Shy(f);
            if (f.Panic > 0) Flee(f);
            else if (f.Leader != null) StepFollower(f);
            else StepLeader(f);

            // Tail speed follows swimming speed
            f.Phase += Step * (1.6 + f.V * 0.42);
            f.Frame = TailBeat[(int)Math.Floor(f.Phase) % 4];
            f.DrawX = Round(f.X);
            f.DrawY = Round(f.Y + Math.Sin(clock * 1.3 + f.Bob) * 1.2);
        }

        // ---------- The shark ----------
        //
        // It is a visitor, not a resident: it waits off to one side, cruises in through the glass, crosses
        // the whole width of every monitor, and leaves by the far side. It never catches anything. It does
        // drift towards the nearest fish ahead and now and then lunges, which is what sends them scattering.
        // Now and then the visit is a great white instead, which does all the same things on a bigger scale.

        double SharkTop() { return 16; }
        double SharkBottom() { return Math.Max(16, floorTop - shark.H - 4); }

        void EnterShark(Shark s)
        {
            // Decided before anything is placed, since where it starts depends on its size
            s.Kind = random.NextDouble() < BigSharkChance ? greatWhite : smallShark;
            s.Active = true;
            s.Dir = random.NextDouble() < 0.5 ? 1 : -1;
            s.X = s.Dir > 0 ? -s.W - 4 : Width + 4;
            s.Y = s.Ty = Rand(SharkTop(), SharkBottom());
            s.V = s.Kind.Cruise;
            s.Burst = 0;
            s.Rest = Rand(2, 5);
            s.Timer = Rand(3, 7);
            s.Turns = random.NextDouble() < 0.35 ? 1 : 0;
            s.Turning = false;
            s.DrawX = Round(s.X);
            s.DrawY = Round(s.Y);
        }

        // The near fish closest in front of its snout, if any is within reach. Fish at the back of the tank
        // are further away than they look, so it does not go after them; they still flee from it.
        Fish PreyFor(Shark s)
        {
            double snout = s.X + (s.Dir > 0 ? s.W : 0);
            double mid = s.Y + s.H / 2.0;
            Fish best = null;
            double bestScore = double.PositiveInfinity;
            foreach (Fish f in fish)
            {
                if (f.Far) continue;
                double ahead = (f.X + f.W / 2.0 - snout) * s.Dir;
                double off = Math.Abs(f.Y + f.H / 2.0 - mid);
                if (ahead < 0 || ahead > s.Kind.Reach || off > s.Kind.Spread) continue;
                if (ahead + off * 2 < bestScore)
                {
                    bestScore = ahead + off * 2;
                    best = f;
                }
            }
            return best;
        }

        void StepShark(Shark s)
        {
            if (!s.Active)
            {
                s.Wait -= Step;
                if (s.Wait <= 0 && !diver.Active) EnterShark(s);
                return;
            }

            s.Timer -= Step;
            if (s.Timer <= 0)
            {
                s.Timer = Rand(3, 7);
                s.Ty = Rand(SharkTop(), SharkBottom());
                // At most one change of mind a visit, and only well inside the tank, so it never doubles back
                // out through the glass it came in by
                bool inside = s.X > Width * 0.25 && s.X + s.W < Width * 0.75;
                if (s.Turns > 0 && inside && random.NextDouble() < 0.35)
                {
                    s.Turning = true;
                    s.Turns--;
                }
            }

            Fish prey = s.Turning ? null : PreyFor(s);
            if (prey != null)
            {
                s.Ty = Clamp(prey.Y + prey.H / 2.0 - s.H / 2.0, SharkTop(), SharkBottom());
                if (s.Burst <= 0 && s.Rest <= 0 && random.NextDouble() < 0.04)
                {
                    s.Burst = Rand(0.7, 1.3);
                    s.Rest = Rand(5, 10);
                }
            }
            s.Burst -= Step;
            s.Rest -= Step;

            // A turn is a long glide down to a crawl and a flip; a fish's stops dead
            double target = s.Turning ? 0 : s.Burst > 0 ? s.Kind.Lunge : s.Kind.Cruise;
            s.V += (target - s.V) * (s.Burst > 0 ? 0.25 : 0.05);
            if (s.Turning && s.V < 6)
            {
                s.Dir = -s.Dir;
                s.Turning = false;
            }
            s.X += s.Dir * s.V * Step;
            s.Y += (s.Ty - s.Y) * (s.Burst > 0 ? 0.05 : 0.02);

            s.Phase += Step * (1 + s.V * 0.12) * s.Kind.Beat;
            s.Frame = TailBeat[(int)Math.Floor(s.Phase) % 4];
            s.DrawX = Round(s.X);
            s.DrawY = Round(s.Y + Math.Sin(clock * 0.7) * s.Kind.Bob);

            if ((s.Dir > 0 && s.X > Width + 8) || (s.Dir < 0 && s.X < -s.W - 8)) Leave(s, SharkGap, diver);
        }

        // A visitor going leaves the other one a quiet spell before it can come in. And if the other has
        // been kept waiting, it goes next, or a visitor with the shorter gap could come back every time
        // before the other ever got in.
        void Leave(Visitor visitor, double[] gap, Visitor other)
        {
            visitor.Active = false;
            visitor.Wait = Rand(gap[0], gap[1]);
            bool overdue = other.Wait <= 0;
            other.Wait = Math.Max(other.Wait, Rand(10, 25));
            if (overdue) visitor.Wait = Math.Max(visitor.Wait, other.Wait + Rand(20, 40));
        }

        // ---------- The diver ----------
        //
        // The other visitor. It sinks in feet first through the surface somewhere along the tank, swims two
        // or three short legs with a pause to look about after each, then rises straight back out. It
        // breathes out a string of bubbles every few seconds. Upright on the way down and up, flat while it
        // swims.

        // The range its centre keeps to while it swims: clear of the surface and of the sand
        double DiverTop() { return 14 + diver.Rise.Height / 2.0; }
        double DiverBottom() { return Math.Max(DiverTop(), floorTop - diver.Swim.Height / 2.0 - 6); }

        void EnterDiver(Diver d)
        {
            double margin = d.Swim.Width / 2.0 + 6;
            d.Active = true;
            d.Pose = d.Rise;
            d.Mode = DiverMode.Down;
            d.Cx = Rand(margin, Math.Max(margin, Width - margin));
            d.Cy = -d.Rise.Height / 2.0 - 2;
            d.Ty = Rand(DiverTop(), DiverTop() + (DiverBottom() - DiverTop()) * 0.6);
            d.Dir = random.NextDouble() < 0.5 ? 1 : -1;
            d.V = 0;
            d.Legs = RandInt(2, 3);
            d.Breath = Rand(1, 3);
            d.Puffs = 0;
            // Above the surface until its first step, rather than wherever its last visit left it
            PlaceDiver(d);
        }

        void NextLeg(Diver d)
        {
            double margin = d.Swim.Width / 2.0 + 6;
            int dir = random.NextDouble() < 0.5 ? 1 : -1;
            // Turned round rather than sent into the glass
            if (d.Cx + dir * 70 > Width - margin || d.Cx + dir * 70 < margin) dir = -dir;
            d.Tx = Clamp(d.Cx + dir * Rand(60, 180), margin, Math.Max(margin, Width - margin));
            d.Ty = Rand(DiverTop(), DiverBottom());
            d.Dir = d.Tx == d.Cx ? d.Dir : Math.Sign(d.Tx - d.Cx);
            d.Mode = DiverMode.Swim;
            d.V = 0;
        }

        void Exhale(Diver d)
        {
            Bubble b = breath.Find(one => !one.Alive);
            if (b == null) return;
            b.Alive = true;
            // From the regulator, which is at the front of the head in either pose
            b.X = d.Cx + d.Dir * (d.Pose == d.Swim ? d.W / 2.0 - 3 : 4);
            b.Y = d.Pose == d.Swim ? d.Cy - 2 : d.Cy - d.H / 2.0 + 6;
            b.Vy = Rand(16, 26);
        }

        void StepDiver(Diver d)
        {
            if (!d.Active)
            {
                d.Wait -= Step;
                if (d.Wait <= 0 && !shark.Active) EnterDiver(d);
                return;
            }

            if (d.Mode == DiverMode.Down)
            {
                // Sinks steadily, and settles gently at the depth it wants
                d.Cy += Clamp((d.Ty - d.Cy) * 0.5, 2, 11) * Step;
                if (d.Cy >= d.Ty - 0.5)
                {
                    d.Pose = d.Swim;
                    d.Mode = DiverMode.Hover;
                    d.Timer = Rand(1, 2.5);
                }
            }
            else if (d.Mode == DiverMode.Swim)
            {
                double gap = d.Tx - d.Cx;
                d.V += (Math.Min(10, Math.Abs(gap) * 0.8 + 1) - d.V) * 0.06;
                d.Cx += Math.Sign(gap) * Math.Min(Math.Abs(gap), d.V * Step);
                d.Cy += (d.Ty - d.Cy) * 0.02;
                if (Math.Abs(gap) < 1)
                {
                    d.Mode = DiverMode.Hover;
                    d.Timer = Rand(2, 5);
                }
            }
            else if (d.Mode == DiverMode.Hover)
            {
                d.V *= 0.9;
                d.Timer -= Step;
                if (d.Timer <= 0)
                {
                    if (d.Legs > 0)
                    {
                        d.Legs--;
                        NextLeg(d);
                    }
                    else
                    {
                        d.Pose = d.Rise;
                        d.Mode = DiverMode.Up;
                        d.V = 0;
                    }
                }
            }
            else
            {
                d.V += (9 - d.V) * 0.04;
                d.Cy -= d.V * Step;
                if (d.Cy < -d.H / 2.0 - 4) Leave(d, DiverGap, shark);
            }

            // A breath every few seconds, let out as a short string of bubbles
            d.Breath -= Step;
            if (d.Breath <= 0 && d.Cy > 12)
            {
                d.Puffs = RandInt(3, 5);
                d.PuffGap = 0;
                d.Breath = Rand(3, 5);
            }
            if (d.Puffs > 0)
            {
                d.PuffGap -= Step;
                if (d.PuffGap <= 0)
                {
                    Exhale(d);
                    d.Puffs--;
                    d.PuffGap = Rand(0.1, 0.25);
                }
            }

            // The kick is slow drifting down, slower still hovering, and quick swimming or rising
            d.Phase += Step * (d.Mode == DiverMode.Down ? 0.7 : d.Mode == DiverMode.Hover ? 1.1 : 2.4);
            d.Frame = TailBeat[(int)Math.Floor(d.Phase) % 4];
            PlaceDiver(d);
        }

        // Its pose's top left corner, from its centre, bobbing gently while it swims or hovers
        void PlaceDiver(Diver d)
        {
            bool bobbing = d.Mode == DiverMode.Hover || d.Mode == DiverMode.Swim;
            d.DrawX = Round(d.Cx - d.W / 2.0);
            d.DrawY = Round(d.Cy - d.H / 2.0 + (bobbing ? Math.Sin(clock * 1.1) * 1.2 : 0));
        }

        void StepBreath(Bubble b)
        {
            if (!b.Alive) return;
            b.Y -= b.Vy * Step;
            if (b.Y < 6) b.Alive = false;
            b.DrawX = Round(b.X + Math.Sin(clock * 2.4 + b.Seed) * 1.5);
            b.DrawY = Round(b.Y);
        }

        void StepJelly(Jelly j)
        {
            // Contract and thrust upward, relax and sink. A beat lifts more than the rest of the cycle sinks
            // while rising, and less while falling; with one fixed strength a jellyfish only ever goes one
            // way and ends up pinned to an edge.
            if (j.Y < 40) j.Rising = false;
            if (j.Y > floorTop - j.Sprite.Height - 30) j.Rising = true;

            j.Phase += Step * 0.9;
            double beat = j.Phase % 3;
            j.Frame = beat < 1.5 ? 0 : beat < 2 ? 1 : beat < 2.6 ? 2 : 1;
            if (beat >= 2 && beat < 2.6) j.Y -= (j.Rising ? 24 : 6) * Step;
            else j.Y += 3 * Step;

            j.X += j.Vx * Step;
            if (j.X < -4) j.Vx = Math.Abs(j.Vx);
            if (j.X > Width - j.Sprite.Width + 4) j.Vx = -Math.Abs(j.Vx);
        }

        void StepCrab(Crab c)
        {
            c.Timer -= Step;
            if (c.Timer <= 0)
            {
                c.Walking = !c.Walking;
                c.Timer = c.Walking ? Rand(1.5, 5) : Rand(2, 9);
                if (random.NextDouble() < 0.5) c.Dir = -c.Dir;
            }
            if (c.Walking)
            {
                c.X += c.Dir * 9 * Step;
                c.Phase += Step * 7;
                if (c.X < 2)
                {
                    c.X = 2;
                    c.Dir = 1;
                }
                if (c.X > Width - crab.Width - 2)
                {
                    c.X = Width - crab.Width - 2;
                    c.Dir = -1;
                }
            }
            c.Frame = (int)Math.Floor(c.Phase) % 2;
        }

        void StepBubble(Bubble b)
        {
            if (!b.Alive)
            {
                b.Timer -= Step;
                if (b.Timer > 0) return;
                b.Alive = true;
                b.X = Pick(vents) + Rand(-3, 3);
                b.Y = floorTop + Rand(-4, 6);
                b.Vy = Rand(14, 30);
            }
            b.Y -= b.Vy * Step;
            b.DrawX = Round(b.X + Math.Sin(clock * 2.2 + b.Seed) * 2.4);
            b.DrawY = Round(b.Y);
            if (b.Y < 5)
            {
                b.Alive = false;
                // Mostly short gaps, sometimes a long one, so they come in strings
                b.Timer = random.NextDouble() < 0.7 ? Rand(0.2, 1.2) : Rand(3, 11);
            }
        }

        // ---------- Drawing ----------

        // Back to front, as Aquarium.qml stacks it. The visitors are in front of every fish, behind the near
        // weeds and the bubbles.
        public void Draw(Bitmap target)
        {
            using (Graphics g = Canvas(target))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                Blit(g, back, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;

                for (int i = 0; i < farCount; i++) DrawFish(g, fish[i]);
                foreach (Weed w in backWeeds) DrawWeed(g, w, 22);
                Blit(g, ground, 0, 0);
                foreach (Crab c in crabs) Blit(g, crab.Frames[c.Frame], Round(c.X), Height - 17 - crab.Height);
                foreach (Jelly j in jellies) Blit(g, j.Sprite.Frames[j.Frame], Round(j.X), Round(j.Y));
                for (int i = farCount; i < fish.Count; i++) DrawFish(g, fish[i]);
                if (diver.Active) DrawVisitor(g, diver.Pose, diver);
                if (shark.Active) DrawVisitor(g, shark.Kind.Sprite, shark);
                foreach (Weed w in frontWeeds) DrawWeed(g, w, 4);
                foreach (Bubble b in breath) if (b.Alive) Blit(g, b.Sprite.Frames[0], b.DrawX, b.DrawY);
                foreach (Bubble b in bubbles) if (b.Alive) Blit(g, b.Sprite.Frames[0], b.DrawX, b.DrawY);
                Blit(g, surface[(int)Math.Floor(clock / 0.4) % 3], 0, 0);
            }
        }

        static void DrawFish(Graphics g, Fish f)
        {
            Blit(g, (f.Dir < 0 ? f.Sprite.Mirrored : f.Sprite.Frames)[f.Frame], f.DrawX, f.DrawY);
        }

        static void DrawVisitor(Graphics g, Sprite sprite, Visitor v)
        {
            Blit(g, (v.Dir < 0 ? sprite.Mirrored : sprite.Frames)[v.Frame], v.DrawX, v.DrawY);
        }

        // Rooted in the sand, `lift` tank pixels above the bottom
        void DrawWeed(Graphics g, Weed w, int lift)
        {
            int frame = (int)Math.Floor(clock * w.Rate * 4 + w.Offset) % 4;
            Blit(g, w.Sprite.Frames[frame], Round(w.X * Width) - 13, Height - w.Sprite.Height - w.Sink - lift);
        }

        void Tile(Graphics g, Bitmap tile, int x, int y)
        {
            for (; x < Width; x += tile.Width) Blit(g, tile, x, y);
        }

        // Pixel for pixel: no smoothing, and no half-pixel offsets to smooth across
        public static Graphics Canvas(Bitmap bitmap)
        {
            Graphics g = Graphics.FromImage(bitmap);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            return g;
        }

        // At its size in pixels, whatever resolution the PNG claims
        public static void Blit(Graphics g, Image image, int x, int y)
        {
            g.DrawImage(image, new Rectangle(x, y, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
        }

        // JavaScript's Math.round, which the QML used: halves go up, where Math.Round would go to even
        static int Round(double value)
        {
            return (int)Math.Floor(value + 0.5);
        }

        static double Clamp(double value, double low, double high)
        {
            return Math.Max(low, Math.Min(high, value));
        }

        double Rand(double a, double b)
        {
            return a + random.NextDouble() * (b - a);
        }

        int RandInt(int a, int b)
        {
            return (int)Math.Floor(Rand(a, b + 1));
        }

        T Pick<T>(IList<T> list)
        {
            return list[RandInt(0, list.Count - 1)];
        }
    }
}
