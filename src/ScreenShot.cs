// Screen Shot - captures a region of the screen and opens it for markup.
// Part of WarrenTool, which owns the tray icon and the hotkey (WarrenTool.cs).
//
// Ctrl+Alt+S (or "Take screenshot" in the tray menu) starts a capture:
// drag a rectangle, and the region opens in an editor in the middle of the screen.
//
//   Selecting:  drag                     select the region
//               Esc / right-click        cancel
//
//   Editor:     B A R O T D              brush, arrow, rectangle, ellipse, text, pixelate
//               X                        copy text: drag a box around text in the shot to copy it as text,
//                                        or around a QR code to copy what it says (its URL)
//               + / -                    stroke size
//               Ctrl+Z, Ctrl+Y           undo, redo (also Ctrl+Shift+Z)
//               Ctrl+C                   copy the marked-up shot to the clipboard
//               Ctrl+S                   save it to Pictures\Screenshots
//               Esc                      close

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;
using WinForms = System.Windows.Forms;
// WinRT, by alias only: Windows.Foundation has its own Point, Rect and Size
using WinFoundation = global::Windows.Foundation;
using WinImaging = global::Windows.Graphics.Imaging;
using WinOcr = global::Windows.Media.Ocr;
using WinStreams = global::Windows.Storage.Streams;

namespace ScreenShot
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // Display scaling of a monitor (1.0 = 100%)
        public static double ScaleOf(WinForms.Screen screen)
        {
            try
            {
                var b = screen.Bounds;
                var center = new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 };
                uint dpiX, dpiY;
                // MONITOR_DEFAULTTONEAREST, MDT_EFFECTIVE_DPI
                if (GetDpiForMonitor(MonitorFromPoint(center, 2), 0, out dpiX, out dpiY) == 0) return dpiX / 96.0;
            }
            catch { }
            return 1;
        }
    }

    // ---------- Catppuccin Macchiato ----------

    static class Theme
    {
        public const int Base = 0x24273A, Mantle = 0x1E2030, Crust = 0x181926;
        public const int Surface0 = 0x363A4F, Surface1 = 0x494D64, Surface2 = 0x5B6078;
        public const int Text = 0xCAD3F5, Subtext0 = 0xA5ADCB;
        public const int Mauve = 0xC6A0F6, Lavender = 0xB7BDF8, Blue = 0x8AADF4;
        public const int Red = 0xED8796, Peach = 0xF5A97F, Yellow = 0xEED49F, Green = 0xA6DA95;

        // Pen colors offered in the editor. Red is the default: the desktop is mostly mauve and
        // lavender already, so red is what stands out on top of a screenshot of it.
        public static readonly int[] Swatches = { Red, Peach, Yellow, Green, Blue, Mauve, Text, Crust };
        public static readonly string[] SwatchNames = { "Red", "Peach", "Yellow", "Green", "Blue", "Mauve", "White", "Black" };

        public static Color Media(int rgb)
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        public static Brush Solid(int rgb, byte alpha = 255)
        {
            Color c = Media(rgb);
            var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            brush.Freeze();
            return brush;
        }

        public static Drawing.Color Gdi(int rgb)
        {
            return Drawing.Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        // Win32 COLORREF is 0x00BBGGRR
        public static int ColorRef(int rgb)
        {
            return ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
        }

        public static ToolTip Tip(string text)
        {
            return new ToolTip
            {
                Content = text,
                Background = Solid(Crust),
                Foreground = Solid(Text),
                BorderBrush = Solid(Surface1),
                Padding = new Thickness(8, 4, 8, 4),
            };
        }
    }

    // ---------- Capturing ----------

    // Freezes the screen, lets the user drag out a region, and opens it in an editor.
    // WarrenTool's tray icon and hotkey call in here.
    class Capturer
    {
        readonly Action<string> warn;
        bool selecting;

        // `warn` shows a problem to the user (a tray balloon)
        public Capturer(Action<string> warn)
        {
            this.warn = warn;
        }

        // Gives whatever was just clicked (tray menu, Start Menu) a moment to get off the screen
        public void CaptureSoon()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate { timer.Stop(); Capture(); };
            timer.Start();
        }

        // Post this rather than call it from a window procedure: the selection runs its own message loop
        public void Capture()
        {
            if (selecting) return;
            selecting = true;
            try
            {
                Drawing.Rectangle desktop = WinForms.SystemInformation.VirtualScreen;
                using (var frozen = new Drawing.Bitmap(desktop.Width, desktop.Height, Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    using (var g = Drawing.Graphics.FromImage(frozen))
                        g.CopyFromScreen(desktop.Location, Drawing.Point.Empty, desktop.Size, Drawing.CopyPixelOperation.SourceCopy);

                    Drawing.Rectangle region;
                    using (var overlay = new SelectionOverlay(frozen, desktop))
                    {
                        if (overlay.ShowDialog() != WinForms.DialogResult.OK) return;
                        region = overlay.Selection;
                    }

                    var center = new Drawing.Point(desktop.X + region.X + region.Width / 2, desktop.Y + region.Y + region.Height / 2);
                    var editor = new EditorWindow(Crop(frozen, region), WinForms.Screen.FromPoint(center));
                    editor.Show();
                    editor.Activate();
                }
            }
            catch (Exception ex)
            {
                // e.g. the secure desktop (UAC prompt, lock screen) can't be captured
                warn("Couldn't take the screenshot: " + ex.Message);
            }
            finally
            {
                selecting = false;
            }
        }

        static BitmapSource Crop(Drawing.Bitmap source, Drawing.Rectangle region)
        {
            using (var part = source.Clone(region, Drawing.Imaging.PixelFormat.Format32bppRgb))
            {
                var bits = part.LockBits(new Drawing.Rectangle(0, 0, part.Width, part.Height),
                    Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    // 96 DPI so one image pixel is one WPF unit, whatever the monitor's scaling
                    var shot = BitmapSource.Create(part.Width, part.Height, 96, 96, PixelFormats.Bgr32, null,
                        bits.Scan0, bits.Stride * part.Height, bits.Stride);
                    shot.Freeze();
                    return shot;
                }
                finally { part.UnlockBits(bits); }
            }
        }

        public static void OpenFolder()
        {
            Directory.CreateDirectory(EditorWindow.SaveFolder);
            Process.Start("explorer.exe", "\"" + EditorWindow.SaveFolder + "\"");
        }
    }

    // ---------- Region selection ----------

    // Covers every monitor with the frozen screenshot, dimmed, and lets the user drag out a rectangle.
    // A WinForms form because its coordinates are real pixels, the same ones the bitmap is made of.
    class SelectionOverlay : WinForms.Form
    {
        const int MinSize = 3;

        readonly Drawing.Bitmap frozen;
        readonly Drawing.Bitmap dimmed;
        readonly Drawing.Font labelFont = new Drawing.Font("Segoe UI", 9f);
        Drawing.Point anchor;
        Drawing.Rectangle selection;
        bool dragging;

        // In the bitmap's coordinates
        public Drawing.Rectangle Selection { get { return selection; } }

        public SelectionOverlay(Drawing.Bitmap frozen, Drawing.Rectangle desktop)
        {
            this.frozen = frozen;
            var all = new Drawing.Rectangle(0, 0, frozen.Width, frozen.Height);
            dimmed = new Drawing.Bitmap(frozen.Width, frozen.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
            using (var g = Drawing.Graphics.FromImage(dimmed))
            using (var shade = new Drawing.SolidBrush(Drawing.Color.FromArgb(120, Theme.Gdi(Theme.Crust))))
            {
                g.DrawImage(frozen, all, all, Drawing.GraphicsUnit.Pixel);
                g.FillRectangle(shade, all);
            }

            Text = "Screen Shot";
            AutoScaleMode = WinForms.AutoScaleMode.None;
            FormBorderStyle = WinForms.FormBorderStyle.None;
            StartPosition = WinForms.FormStartPosition.Manual;
            Bounds = desktop;
            ShowInTaskbar = false;
            TopMost = true;
            Cursor = WinForms.Cursors.Cross;
            SetStyle(WinForms.ControlStyles.AllPaintingInWmPaint | WinForms.ControlStyles.UserPaint |
                WinForms.ControlStyles.OptimizedDoubleBuffer | WinForms.ControlStyles.Opaque, true);
            // Keeps one back buffer for the whole drag instead of allocating a screen-sized one per repaint
            Drawing.BufferedGraphicsManager.Current.MaximumBuffer = new Drawing.Size(desktop.Width + 1, desktop.Height + 1);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle); // so Esc reaches us
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dimmed.Dispose();
                labelFont.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref WinForms.Message msg, WinForms.Keys keyData)
        {
            if (keyData == WinForms.Keys.Escape)
            {
                DialogResult = WinForms.DialogResult.Cancel;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(WinForms.MouseEventArgs e)
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                DialogResult = WinForms.DialogResult.Cancel;
            }
            else if (e.Button == WinForms.MouseButtons.Left)
            {
                anchor = Clamp(e.Location);
                dragging = true;
                SetSelection(Drawing.Rectangle.Empty);
            }
        }

        protected override void OnMouseMove(WinForms.MouseEventArgs e)
        {
            if (!dragging) return;
            Drawing.Point p = Clamp(e.Location);
            SetSelection(Drawing.Rectangle.FromLTRB(Math.Min(anchor.X, p.X), Math.Min(anchor.Y, p.Y),
                Math.Max(anchor.X, p.X), Math.Max(anchor.Y, p.Y)));
        }

        protected override void OnMouseUp(WinForms.MouseEventArgs e)
        {
            if (e.Button != WinForms.MouseButtons.Left || !dragging) return;
            dragging = false;
            // A click without a drag cancels, as it does in slurp
            bool usable = selection.Width >= MinSize && selection.Height >= MinSize;
            DialogResult = usable ? WinForms.DialogResult.OK : WinForms.DialogResult.Cancel;
        }

        Drawing.Point Clamp(Drawing.Point p)
        {
            return new Drawing.Point(Math.Max(0, Math.Min(frozen.Width, p.X)), Math.Max(0, Math.Min(frozen.Height, p.Y)));
        }

        // Repaints only what the old and new selection touch, so dragging stays smooth on large desktops
        void SetSelection(Drawing.Rectangle next)
        {
            Drawing.Rectangle before = Footprint();
            selection = next;
            Drawing.Rectangle after = Footprint();
            if (!before.IsEmpty) Invalidate(before);
            if (!after.IsEmpty) Invalidate(after);
            Update();
        }

        Drawing.Rectangle Footprint()
        {
            if (selection.IsEmpty) return Drawing.Rectangle.Empty;
            return Drawing.Rectangle.Union(Drawing.Rectangle.Inflate(selection, 2, 2), LabelBounds());
        }

        string LabelText { get { return selection.Width + " \u00D7 " + selection.Height; } }

        // Just under the selection's bottom-left corner, or inside it when there's no room below
        Drawing.Rectangle LabelBounds()
        {
            Drawing.Size size = WinForms.TextRenderer.MeasureText(LabelText, labelFont);
            size = new Drawing.Size(size.Width + 10, size.Height + 6);
            int x = Math.Max(0, Math.Min(selection.Left, ClientSize.Width - size.Width));
            int y = selection.Bottom + 6;
            if (y + size.Height > ClientSize.Height) y = selection.Bottom - size.Height - 6;
            return new Drawing.Rectangle(new Drawing.Point(x, y), size);
        }

        protected override void OnPaint(WinForms.PaintEventArgs e)
        {
            var g = e.Graphics;
            g.CompositingMode = Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(dimmed, e.ClipRectangle, e.ClipRectangle, Drawing.GraphicsUnit.Pixel);
            if (selection.IsEmpty) return;

            g.DrawImage(frozen, selection, selection, Drawing.GraphicsUnit.Pixel);
            g.CompositingMode = Drawing.Drawing2D.CompositingMode.SourceOver;
            g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Default;
            // The border sits just outside, so every selected pixel is visible
            using (var pen = new Drawing.Pen(Theme.Gdi(Theme.Mauve)))
                g.DrawRectangle(pen, selection.X - 1, selection.Y - 1, selection.Width + 1, selection.Height + 1);

            Drawing.Rectangle label = LabelBounds();
            using (var back = new Drawing.SolidBrush(Theme.Gdi(Theme.Crust)))
                g.FillRectangle(back, label);
            WinForms.TextRenderer.DrawText(g, LabelText, labelFont, label, Theme.Gdi(Theme.Text), Theme.Gdi(Theme.Crust),
                WinForms.TextFormatFlags.HorizontalCenter | WinForms.TextFormatFlags.VerticalCenter);
        }
    }

    // ---------- The editor ----------

    enum Tool { Brush, Arrow, Rectangle, Ellipse, Text, Pixelate, CopyText }

    // One undoable step: either one element added to the drawing, or everything cleared from it
    class Edit
    {
        public UIElement[] Items;
        public bool IsClear;
    }

    // A flat toolbar button. Not focusable, so the keyboard stays with the drawing (or the text being typed).
    class Chip : Border
    {
        static readonly Brush HoverBrush = Theme.Solid(Theme.Surface0);
        static readonly Brush ActiveBrush = Theme.Solid(Theme.Surface1);
        static readonly Brush PressedBrush = Theme.Solid(Theme.Surface2);
        static readonly Brush AccentBrush = Theme.Solid(Theme.Mauve);
        static readonly Brush AccentHoverBrush = Theme.Solid(Theme.Lavender);
        static readonly Brush AccentPressedBrush = Theme.Solid(Theme.Blue);

        readonly bool accent;
        bool pressed;
        bool active;

        public event Action Click;

        public Chip(UIElement content, string tip, bool accent = false)
        {
            this.accent = accent;
            Child = content;
            CornerRadius = new CornerRadius(6);
            BorderThickness = new Thickness(1);
            Padding = new Thickness(7);
            Margin = new Thickness(1, 0, 1, 0);
            VerticalAlignment = VerticalAlignment.Center;
            if (tip != null) ToolTip = Theme.Tip(tip);

            MouseEnter += delegate { Refresh(); };
            MouseLeave += delegate { Refresh(); };
            MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                pressed = true;
                CaptureMouse();
                Refresh();
                e.Handled = true;
            };
            MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!pressed) return;
                bool inside = IsMouseOver;
                pressed = false;
                ReleaseMouseCapture();
                Refresh();
                e.Handled = true;
                if (inside && Click != null) Click();
            };
            LostMouseCapture += delegate { pressed = false; Refresh(); };
            IsEnabledChanged += delegate { Opacity = IsEnabled ? 1 : 0.35; };
            Refresh();
        }

        // The selected tool
        public bool Active
        {
            get { return active; }
            set { active = value; Refresh(); }
        }

        void Refresh()
        {
            if (accent) Background = pressed ? AccentPressedBrush : IsMouseOver ? AccentHoverBrush : AccentBrush;
            else Background = pressed ? PressedBrush : active ? ActiveBrush : IsMouseOver ? HoverBrush : Brushes.Transparent;
            BorderBrush = active ? AccentBrush : Brushes.Transparent;
        }
    }

    public class EditorWindow : Window
    {
        const int DefaultLineSize = 5;      // swappy's line_size
        const int MaxLineSize = 30;
        const string CopyLabel = "Copy to clipboard";
        const string SaveLabel = "Save";

        static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable Text, Segoe UI");
        static readonly FontFamily InkFont = new FontFamily("Segoe UI");
        static readonly Brush TextBrush = Theme.Solid(Theme.Text);
        static readonly Brush CrustBrush = Theme.Solid(Theme.Crust);

        // A text box with no chrome of its own, so what is typed sits exactly where it will be drawn
        static readonly ControlTemplate BareTextBox = (ControlTemplate)XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'" +
            " xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>" +
            "<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'" +
            " BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='3'>" +
            "<ScrollViewer x:Name='PART_ContentHost' Padding='{TemplateBinding Padding}' Focusable='False'" +
            " HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>" +
            "</Border></ControlTemplate>");

        public static string SaveFolder
        {
            get { return IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"); }
        }

        readonly BitmapSource shot;
        readonly int pixelWidth, pixelHeight;
        WinForms.Screen screen;

        // The shot and everything drawn on it, in image pixels. Scaled to fit the screen by `view`.
        readonly Grid surface;
        readonly Image image = new Image { Stretch = Stretch.Fill };
        readonly Canvas ink = new Canvas();         // finished markup; this is what gets flattened
        readonly Canvas scratch = new Canvas();     // the text box being typed in, the pixelate marquee
        readonly Viewbox view = new Viewbox { Stretch = Stretch.Fill };

        readonly Dictionary<Tool, Chip> toolChips = new Dictionary<Tool, Chip>();
        readonly List<KeyValuePair<Color, Ellipse>> swatchRings = new List<KeyValuePair<Color, Ellipse>>();
        readonly TextBlock sizeText = new TextBlock();
        readonly TextBlock copyText = new TextBlock { Text = CopyLabel };
        readonly TextBlock saveText = new TextBlock { Text = SaveLabel };
        Chip undoChip, redoChip, clearChip;

        readonly Stack<Edit> undo = new Stack<Edit>();
        readonly Stack<Edit> redo = new Stack<Edit>();

        Tool tool = Tool.Brush;
        Color color = Theme.Media(Theme.Red);
        int lineSize = DefaultLineSize;
        double scale = 1;       // display scaling of the monitor the window is on
        Point start;
        UIElement drawing;      // the shape being dragged out
        Tool drawingWith;       // ... and the tool it was started with
        TextBox textBox;        // the text being typed

        // A short message over the bottom of the shot, for things with no button to flash
        readonly TextBlock toastText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        readonly Border toast = new Border();
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        public EditorWindow(BitmapSource shot, WinForms.Screen screen)
        {
            this.shot = shot;
            this.screen = screen;
            pixelWidth = shot.PixelWidth;
            pixelHeight = shot.PixelHeight;

            Title = "Screenshot";
            Background = Theme.Solid(Theme.Base);
            Foreground = TextBrush;
            FontFamily = UiFont;
            FontSize = 12.5;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            UseLayoutRounding = true;
            Focusable = true;
            try
            {
                using (var stream = typeof(EditorWindow).Assembly.GetManifestResourceStream("ScreenShot.ico"))
                    Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { }

            // Layout rounding would nudge the surface off whole image pixels on scaled monitors
            image.Source = shot;
            surface = new Grid
            {
                Width = pixelWidth,
                Height = pixelHeight,
                ClipToBounds = true,
                UseLayoutRounding = false,
                Background = Brushes.Transparent,
            };
            surface.Children.Add(image);
            surface.Children.Add(ink);
            surface.Children.Add(scratch);
            surface.MouseLeftButtonDown += OnSurfaceDown;
            surface.MouseMove += OnSurfaceMove;
            surface.MouseLeftButtonUp += delegate { FinishDrawing(); };
            surface.LostMouseCapture += delegate { FinishDrawing(); };
            view.Child = surface;

            var frame = new Border
            {
                BorderBrush = Theme.Solid(Theme.Surface0),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = view,
            };
            toastText.Foreground = TextBrush;
            toast.Child = toastText;
            toast.Background = Theme.Solid(Theme.Crust, 0xF2);
            toast.BorderBrush = Theme.Solid(Theme.Surface1);
            toast.BorderThickness = new Thickness(1);
            toast.CornerRadius = new CornerRadius(8);
            toast.Padding = new Thickness(12, 6, 12, 7);
            toast.Margin = new Thickness(0, 0, 0, 10);
            toast.MaxWidth = 560;
            toast.HorizontalAlignment = HorizontalAlignment.Center;
            toast.VerticalAlignment = VerticalAlignment.Bottom;
            toast.IsHitTestVisible = false;
            toast.Visibility = Visibility.Collapsed;
            toastTimer.Tick += delegate { toastTimer.Stop(); toast.Visibility = Visibility.Collapsed; };

            var layers = new Grid();
            layers.Children.Add(frame);
            layers.Children.Add(toast);
            var stage = new Border { Padding = new Thickness(16), Background = Brushes.Transparent, Child = layers };
            stage.MouseLeftButtonDown += delegate { CommitText(); };

            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            var root = new DockPanel();
            root.Children.Add(toolbar);
            root.Children.Add(stage);
            Content = root;

            SetTool(Tool.Brush);
            SetColor(color);
            SetLineSize(lineSize);
            UpdateHistoryChips();
            ApplyScale(Native.ScaleOf(screen));

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += delegate
            {
                // Keep the buttons from changing width when their label flashes "Copied" / "Saved"
                copyText.MinWidth = copyText.ActualWidth;
                saveText.MinWidth = saveText.ActualWidth;
                Center();
                Keyboard.Focus(this);
            };
        }

        double StrokeWidth { get { return lineSize * scale; } }

        // 20 at the default stroke size, as in swappy's text_size
        double TextSize { get { return (10 + 2 * lineSize) * scale; } }

        // ---------- Window placement and theme ----------

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            // Title bar in the toolbar's color, so the two read as one header
            int dark = 1, caption = Theme.ColorRef(Theme.Mantle), text = Theme.ColorRef(Theme.Text), border = Theme.ColorRef(Theme.Surface0);
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));      // DWMWA_USE_IMMERSIVE_DARK_MODE
            Native.DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));   // DWMWA_CAPTION_COLOR
            Native.DwmSetWindowAttribute(hwnd, 36, ref text, sizeof(int));      // DWMWA_TEXT_COLOR
            Native.DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));    // DWMWA_BORDER_COLOR

            // Onto the target monitor first, so the layout below uses that monitor's scaling
            var area = screen.WorkingArea;
            Native.SetWindowPos(hwnd, IntPtr.Zero, area.Left + area.Width / 3, area.Top + area.Height / 3, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            ApplyScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
            Center();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            screen = WinForms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
            ApplyScale(newDpi.DpiScaleX);
        }

        // Shows the shot pixel for pixel, shrinking it only when it wouldn't fit on the monitor
        void ApplyScale(double monitorScale)
        {
            scale = monitorScale;
            var area = screen.WorkingArea;
            double naturalW = pixelWidth / scale, naturalH = pixelHeight / scale;
            double maxW = area.Width / scale * 0.92 - 60;       // room for the stage padding and window frame
            double maxH = area.Height / scale * 0.92 - 150;     // ... plus the title bar and toolbar
            double fit = Math.Min(1, Math.Min(maxW / naturalW, maxH / naturalH));
            view.Width = naturalW * fit;
            view.Height = naturalH * fit;
            RenderOptions.SetBitmapScalingMode(image, fit < 1 ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);
        }

        // In real pixels: WPF's own Left/Top are unreliable across monitors with different scaling
        void Center()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Native.RECT r;
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out r)) return;
            var area = screen.WorkingArea;
            int x = area.Left + (area.Width - (r.Right - r.Left)) / 2;
            int y = area.Top + Math.Max(0, (area.Height - (r.Bottom - r.Top)) / 2);
            Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // ---------- Toolbar ----------

        // Icons are drawn on a 16x16 grid
        static UIElement Glyph(string data, Brush brush, bool filled = false)
        {
            return new ShapePath
            {
                Data = Geometry.Parse(data),
                Width = 16,
                Height = 16,
                Stroke = filled ? null : brush,
                Fill = filled ? brush : null,
                StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            };
        }

        static UIElement Divider()
        {
            return new Border { Width = 1, Height = 20, Margin = new Thickness(8, 0, 8, 0), Background = Theme.Solid(Theme.Surface0) };
        }

        Chip AddChip(Panel panel, UIElement content, string tip, Action click)
        {
            var chip = new Chip(content, tip);
            chip.Click += click;
            panel.Children.Add(chip);
            return chip;
        }

        void AddTool(Panel panel, Tool t, string glyph, string tip, bool filled = false)
        {
            toolChips[t] = AddChip(panel, Glyph(glyph, TextBrush, filled), tip, delegate { SetTool(t); });
        }

        static UIElement Labelled(string glyph, TextBlock label, Brush brush)
        {
            label.Foreground = brush;
            label.FontWeight = FontWeights.SemiBold;
            label.TextAlignment = TextAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(7, 0, 2, 0);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Glyph(glyph, brush));
            row.Children.Add(label);
            return row;
        }

        UIElement BuildToolbar()
        {
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            AddTool(left, Tool.Brush, "M2.5,13.5 L3.5,10 L10.5,3 Q11.5,2 12.5,3 L13,3.5 Q14,4.5 13,5.5 L6,12.5 Z M9.5,4 L12,6.5", "Brush (B)");
            AddTool(left, Tool.Arrow, "M3.5,12.5 L12.5,3.5 M6.5,3.5 H12.5 V9.5", "Arrow (A)");
            AddTool(left, Tool.Rectangle, "M2.5,4 H13.5 V12 H2.5 Z", "Rectangle (R)");
            AddTool(left, Tool.Ellipse, "M2.5,8 A5.5,4.5 0 1 0 13.5,8 A5.5,4.5 0 1 0 2.5,8 Z", "Ellipse (O)");
            AddTool(left, Tool.Text, "M3.5,5 V3.5 H12.5 V5 M8,3.5 V12.5 M6,12.5 H10", "Text (T)");
            AddTool(left, Tool.Pixelate, "M2,2 H6 V6 H2 Z M10,2 H14 V6 H10 Z M6,6 H10 V10 H6 Z M2,10 H6 V14 H2 Z M10,10 H14 V14 H10 Z",
                "Pixelate (D)", true);
            AddTool(left, Tool.CopyText, "M2.5,5 V2.5 H5 M11,2.5 H13.5 V5 M13.5,11 V13.5 H11 M5,13.5 H2.5 V11 M5.5,6.5 H10.5 M5.5,9.5 H9",
                "Copy text: drag a box around text or a QR code to copy it (X)");
            left.Children.Add(Divider());

            for (int i = 0; i < Theme.Swatches.Length; i++)
            {
                Color c = Theme.Media(Theme.Swatches[i]);
                var dot = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = new SolidColorBrush(c),
                    Stroke = Theme.Solid(Theme.Surface2),
                    StrokeThickness = 1,
                };
                var ring = new Ellipse { Width = 20, Height = 20, Stroke = TextBrush, StrokeThickness = 2 };
                var swatch = new Grid { Width = 20, Height = 20 };
                swatch.Children.Add(dot);
                swatch.Children.Add(ring);
                AddChip(left, swatch, Theme.SwatchNames[i], delegate { SetColor(c); }).Padding = new Thickness(5);
                swatchRings.Add(new KeyValuePair<Color, Ellipse>(c, ring));
            }
            left.Children.Add(Divider());

            AddChip(left, Glyph("M4,8 H12", TextBrush), "Thinner (-)", delegate { SetLineSize(lineSize - 1); });
            sizeText.Width = 22;
            sizeText.TextAlignment = TextAlignment.Center;
            sizeText.VerticalAlignment = VerticalAlignment.Center;
            sizeText.ToolTip = Theme.Tip("Stroke size");
            left.Children.Add(sizeText);
            AddChip(left, Glyph("M4,8 H12 M8,4 V12", TextBrush), "Thicker (+)", delegate { SetLineSize(lineSize + 1); });
            left.Children.Add(Divider());

            undoChip = AddChip(left, Glyph("M6,3.5 L3,6.5 L6,9.5 M3,6.5 H10 A3,3 0 0 1 10,12.5 H7", TextBrush), "Undo (Ctrl+Z)", Undo);
            redoChip = AddChip(left, Glyph("M10,3.5 L13,6.5 L10,9.5 M13,6.5 H6 A3,3 0 0 0 6,12.5 H9", TextBrush), "Redo (Ctrl+Y)", Redo);
            clearChip = AddChip(left, Glyph("M3,4.5 H13 M6.5,4.5 V3 H9.5 V4.5 M4.5,4.5 L5,13 H11 L11.5,4.5 M7,7 V10.5 M9,7 V10.5", TextBrush),
                "Clear all markup", Clear);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 0) };
            var save = new Chip(Labelled("M8,2.5 V10 M5,7 L8,10 L11,7 M3,13 H13", saveText, TextBrush), "Save to Pictures\\Screenshots (Ctrl+S)")
            {
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 6, 0),
            };
            save.Click += Save;
            var copy = new Chip(Labelled("M6,6 H13 V13 H6 Z M3.5,10 V3.5 H10", copyText, CrustBrush), "Copy to clipboard (Ctrl+C)", true)
            {
                Padding = new Thickness(12, 7, 12, 7),
            };
            copy.Click += Copy;
            right.Children.Add(save);
            right.Children.Add(copy);

            var bar = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(left);
            bar.Children.Add(right);
            return new Border { Background = Theme.Solid(Theme.Mantle), Padding = new Thickness(10, 6, 10, 8), Child = bar };
        }

        void SetTool(Tool next)
        {
            CommitText();
            tool = next;
            foreach (var pair in toolChips) pair.Value.Active = pair.Key == tool;
            surface.Cursor = tool == Tool.Brush ? Cursors.Pen : tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        }

        void SetColor(Color next)
        {
            color = next;
            foreach (var pair in swatchRings) pair.Value.Visibility = pair.Key == color ? Visibility.Visible : Visibility.Hidden;
            if (textBox != null) textBox.Foreground = textBox.CaretBrush = new SolidColorBrush(color);
        }

        void SetLineSize(int next)
        {
            lineSize = Math.Max(1, Math.Min(MaxLineSize, next));
            sizeText.Text = lineSize.ToString();
            if (textBox != null) textBox.FontSize = TextSize;
        }

        // ---------- Drawing ----------

        Point Clamp(Point p)
        {
            return new Point(Math.Max(0, Math.Min(pixelWidth, p.X)), Math.Max(0, Math.Min(pixelHeight, p.Y)));
        }

        void OnSurfaceDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            CommitText();   // clicking away from the text being typed finishes it
            Point p = Clamp(e.GetPosition(surface));
            start = p;
            drawingWith = tool;
            Brush stroke = new SolidColorBrush(color);

            switch (tool)
            {
                case Tool.Text:
                    BeginText(p);
                    return;

                case Tool.Brush:
                    var line = new Polyline
                    {
                        Stroke = stroke,
                        StrokeThickness = StrokeWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                    };
                    // Two points, so a click without a drag still leaves a dot
                    line.Points.Add(p);
                    line.Points.Add(new Point(p.X + 0.01, p.Y));
                    drawing = line;
                    break;

                case Tool.Arrow:
                    drawing = new ShapePath
                    {
                        Stroke = stroke,
                        Fill = stroke,
                        StrokeThickness = StrokeWidth,
                        StrokeStartLineCap = PenLineCap.Round,
                    };
                    break;

                case Tool.Rectangle:
                    drawing = new Rectangle { Stroke = stroke, StrokeThickness = StrokeWidth, StrokeLineJoin = PenLineJoin.Round };
                    break;

                case Tool.Ellipse:
                    drawing = new Ellipse { Stroke = stroke, StrokeThickness = StrokeWidth };
                    break;

                case Tool.Pixelate:
                    drawing = new Rectangle
                    {
                        Stroke = TextBrush,
                        StrokeThickness = Math.Max(1, scale),
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        Fill = Theme.Solid(Theme.Crust, 0x66),
                    };
                    break;

                case Tool.CopyText:
                    drawing = new Rectangle
                    {
                        Stroke = Theme.Solid(Theme.Mauve),
                        StrokeThickness = Math.Max(1, scale),
                        StrokeDashArray = new DoubleCollection { 4, 3 },
                        Fill = Theme.Solid(Theme.Mauve, 0x33),
                    };
                    break;
            }

            drawing.IsHitTestVisible = false;
            // A marquee is only a guide, so it stays out of the drawing
            (tool == Tool.Pixelate || tool == Tool.CopyText ? scratch : ink).Children.Add(drawing);
            DragTo(p);
            surface.CaptureMouse();
        }

        void OnSurfaceMove(object sender, MouseEventArgs e)
        {
            if (drawing != null) DragTo(Clamp(e.GetPosition(surface)));
        }

        // Extends the shape being drawn to the mouse position
        void DragTo(Point p)
        {
            var line = drawing as Polyline;
            var arrow = drawing as ShapePath;
            if (line != null)
            {
                line.Points.Add(p);
            }
            else if (arrow != null)
            {
                arrow.Data = ArrowGeometry(start, p, arrow.StrokeThickness);
            }
            else
            {
                var box = (FrameworkElement)drawing;
                Canvas.SetLeft(box, Math.Min(start.X, p.X));
                Canvas.SetTop(box, Math.Min(start.Y, p.Y));
                box.Width = Math.Abs(p.X - start.X);
                box.Height = Math.Abs(p.Y - start.Y);
            }
        }

        // A shaft with a solid triangular head whose tip is exactly at `to`
        static Geometry ArrowGeometry(Point from, Point to, double thickness)
        {
            Vector along = to - from;
            double length = along.Length;
            if (length < 0.5) return Geometry.Empty;
            along.Normalize();
            var across = new Vector(-along.Y, along.X);
            double head = Math.Min(length, thickness * 4.5);
            Point neck = to - along * head;

            var shape = new StreamGeometry();
            using (var ctx = shape.Open())
            {
                ctx.BeginFigure(from, false, false);
                ctx.LineTo(to - along * (head * 0.9), true, false);
                // Left open and unstroked: the fill closes it, and a stroke would blunt the tip
                ctx.BeginFigure(to, true, false);
                ctx.LineTo(neck + across * (head * 0.42), false, false);
                ctx.LineTo(neck - across * (head * 0.42), false, false);
            }
            shape.Freeze();
            return shape;
        }

        void FinishDrawing()
        {
            if (drawing == null) return;
            UIElement done = drawing;
            drawing = null;
            surface.ReleaseMouseCapture();

            var box = done as FrameworkElement;
            if (scratch.Children.Contains(done))
            {
                // A marquee: what matters is the region it outlined
                scratch.Children.Remove(done);
                var region = new Int32Rect((int)Math.Round(Canvas.GetLeft(box)), (int)Math.Round(Canvas.GetTop(box)),
                    (int)Math.Round(box.Width), (int)Math.Round(box.Height));
                region.Width = Math.Min(region.Width, pixelWidth - region.X);
                region.Height = Math.Min(region.Height, pixelHeight - region.Y);
                if (region.Width < 4 || region.Height < 4) return;
                if (drawingWith == Tool.CopyText)
                {
                    CopyText(region);
                    return;
                }
                done = Pixelated(region);
                ink.Children.Add(done);
            }
            else if (!(done is Polyline) && IsTiny(box))
            {
                // A stray click with a shape tool: nothing worth keeping
                ink.Children.Remove(done);
                return;
            }
            Record(new Edit { Items = new[] { done } });
        }

        static bool IsTiny(FrameworkElement shape)
        {
            var arrow = shape as ShapePath;
            if (arrow != null) return arrow.Data == null || arrow.Data.Bounds.IsEmpty || (arrow.Data.Bounds.Width < 3 && arrow.Data.Bounds.Height < 3);
            return shape.Width < 3 && shape.Height < 3;
        }

        // The region of the shot as coarse blocks of its average colors, for hiding things
        Image Pixelated(Int32Rect region)
        {
            int block = Math.Max(6, Math.Min(24, Math.Min(region.Width, region.Height) / 6));
            int stride = region.Width * 4;
            var pixels = new byte[stride * region.Height];
            shot.CopyPixels(region, pixels, stride, 0);     // BGRX

            for (int by = 0; by < region.Height; by += block)
            {
                for (int bx = 0; bx < region.Width; bx += block)
                {
                    int w = Math.Min(block, region.Width - bx), h = Math.Min(block, region.Height - by);
                    int b = 0, g = 0, r = 0;
                    for (int y = by; y < by + h; y++)
                    {
                        for (int x = bx; x < bx + w; x++)
                        {
                            int i = y * stride + x * 4;
                            b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
                        }
                    }
                    int count = w * h;
                    for (int y = by; y < by + h; y++)
                    {
                        for (int x = bx; x < bx + w; x++)
                        {
                            int i = y * stride + x * 4;
                            pixels[i] = (byte)(b / count); pixels[i + 1] = (byte)(g / count); pixels[i + 2] = (byte)(r / count);
                        }
                    }
                }
            }

            var mosaic = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            mosaic.Freeze();
            var patch = new Image { Source = mosaic, Width = region.Width, Height = region.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(patch, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(patch, region.X);
            Canvas.SetTop(patch, region.Y);
            return patch;
        }

        // ---------- Copying text out of the shot ----------

        // Reads a region of the shot (not the markup on it) and puts what it says on the clipboard:
        // the contents of a QR code or barcode if there is one in it, otherwise its text
        void CopyText(Int32Rect region)
        {
            int width = region.Width, height = region.Height;
            var pixels = new byte[width * 4 * height];
            shot.CopyPixels(region, pixels, width * 4, 0);  // BGRX
            double monitorScale = scale;

            // Off the UI thread: a large box takes a moment to search
            Task.Run((Action)delegate
            {
                try
                {
                    bool qr;
                    string[] codes = Codes.Read(pixels, width, height, out qr);

                    int largeWidth, largeHeight;
                    byte[] large = Ocr.Enlarge(pixels, width, height, monitorScale, out largeWidth, out largeHeight);
                    // A code drawn very small only reads once enlarged
                    if (codes == null && large != pixels && largeWidth * largeHeight <= Codes.EnlargedSearchLimit)
                        codes = Codes.Read(large, largeWidth, largeHeight, out qr);

                    if (codes != null) Copied(codes, qr ? "QR code" : "barcode", qr ? "QR codes" : "barcodes", null);
                    else Ocr.Read(large, largeWidth, largeHeight, delegate(string[] lines, string problem) { Copied(lines, "text", "lines", problem); });
                }
                catch (Exception ex)
                {
                    Copied(null, null, null, ex.Message);
                }
            });
        }

        // Puts what was read on the clipboard, one item per line, and says so. Called from worker threads.
        void Copied(string[] items, string one, string many, string problem)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (problem != null) { ShowToast("Couldn't read that: " + problem); return; }
                if (items.Length == 0) { ShowToast("No text or QR code found there"); return; }
                try
                {
                    Clipboard.SetText(string.Join(Environment.NewLine, items));
                    ShowToast((items.Length == 1 ? "Copied " + one : "Copied " + items.Length + " " + many) + ":  " + items[0]);
                }
                catch (Exception)
                {
                    ShowToast("Couldn't copy it: another app is using the clipboard");
                }
            }));
        }

        void ShowToast(string message)
        {
            toastText.Text = message;
            toast.Visibility = Visibility.Visible;
            toastTimer.Stop();
            toastTimer.Start();
        }

        // ---------- Text ----------

        void BeginText(Point p)
        {
            double size = TextSize, pad = size * 0.2;
            Brush brush = new SolidColorBrush(color);
            textBox = new TextBox
            {
                Template = BareTextBox,
                Background = Brushes.Transparent,
                BorderBrush = Theme.Solid(Theme.Text, 0x80),
                BorderThickness = new Thickness(Math.Max(1, scale)),
                Padding = new Thickness(pad, 0, pad, 0),
                Foreground = brush,
                CaretBrush = brush,
                SelectionBrush = Theme.Solid(Theme.Mauve),
                FontFamily = InkFont,
                FontSize = size,
                FontWeight = FontWeights.SemiBold,
                AcceptsReturn = true,   // Shift+Enter; plain Enter is taken by OnPreviewKeyDown to finish
                MinWidth = size * 2,
            };
            // The click lands on the middle of the first line
            Canvas.SetLeft(textBox, p.X - pad);
            Canvas.SetTop(textBox, p.Y - size * 0.7);
            scratch.Children.Add(textBox);
            TextBox box = textBox;
            box.Focus();
            // Again once it has been laid out, in case it couldn't take the focus yet
            Dispatcher.BeginInvoke(new Action(() => { if (textBox == box) box.Focus(); }), DispatcherPriority.Input);
        }

        // Turns the text being typed into part of the drawing
        void CommitText()
        {
            if (textBox == null) return;
            TextBox box = textBox;
            textBox = null;

            // Where the first character actually sits inside the box, so the text doesn't shift
            Rect first = box.GetRectFromCharacterIndex(0);
            double dx = first.IsEmpty ? 0 : first.X, dy = first.IsEmpty ? 0 : first.Top;
            scratch.Children.Remove(box);
            Keyboard.Focus(this);

            string text = box.Text.TrimEnd();
            if (text.Length == 0) return;
            var block = new TextBlock
            {
                Text = text,
                Foreground = box.Foreground,
                FontFamily = box.FontFamily,
                FontSize = box.FontSize,
                FontWeight = box.FontWeight,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(block, Canvas.GetLeft(box) + dx);
            Canvas.SetTop(block, Canvas.GetTop(box) + dy);
            ink.Children.Add(block);
            Record(new Edit { Items = new UIElement[] { block } });
        }

        // ---------- Undo / redo ----------

        void Record(Edit edit)
        {
            undo.Push(edit);
            redo.Clear();
            UpdateHistoryChips();
        }

        void Undo()
        {
            CommitText();
            if (undo.Count == 0) return;
            Edit edit = undo.Pop();
            if (edit.IsClear) foreach (var item in edit.Items) ink.Children.Add(item);
            else ink.Children.Remove(edit.Items[0]);
            redo.Push(edit);
            UpdateHistoryChips();
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            Edit edit = redo.Pop();
            if (edit.IsClear) ink.Children.Clear();
            else ink.Children.Add(edit.Items[0]);
            undo.Push(edit);
            UpdateHistoryChips();
        }

        void Clear()
        {
            CommitText();
            if (ink.Children.Count == 0) return;
            var items = ink.Children.Cast<UIElement>().ToArray();
            ink.Children.Clear();
            Record(new Edit { Items = items, IsClear = true });
        }

        void UpdateHistoryChips()
        {
            undoChip.IsEnabled = undo.Count > 0;
            redoChip.IsEnabled = redo.Count > 0;
            clearChip.IsEnabled = ink.Children.Count > 0;
        }

        // ---------- Keyboard ----------

        void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Before anything else gets the key, so Esc closes from anywhere in the window
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
                return;
            }

            ModifierKeys mods = Keyboard.Modifiers;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool typing = textBox != null;
            if ((mods & ModifierKeys.Alt) != 0) return;

            if ((mods & ModifierKeys.Control) != 0)
            {
                switch (e.Key)
                {
                    case Key.C:
                        if (typing && textBox.SelectionLength > 0) return;  // copying text out of the text box
                        Copy();
                        break;
                    case Key.S: Save(); break;
                    case Key.Z:
                        if (typing) return;     // the text box's own undo
                        if (shift) Redo(); else Undo();
                        break;
                    case Key.Y:
                        if (typing) return;
                        Redo();
                        break;
                    default: return;
                }
                e.Handled = true;
                return;
            }

            if (typing)
            {
                if (e.Key == Key.Enter && !shift)
                {
                    CommitText();
                    e.Handled = true;
                }
                return;     // every other key is text
            }

            switch (e.Key)
            {
                case Key.B: SetTool(Tool.Brush); break;
                case Key.A: SetTool(Tool.Arrow); break;
                case Key.R: SetTool(Tool.Rectangle); break;
                case Key.O: SetTool(Tool.Ellipse); break;
                case Key.T: SetTool(Tool.Text); break;
                case Key.D: SetTool(Tool.Pixelate); break;
                case Key.X: SetTool(Tool.CopyText); break;
                case Key.OemPlus: case Key.Add: SetLineSize(lineSize + 1); break;
                case Key.OemMinus: case Key.Subtract: SetLineSize(lineSize - 1); break;
                default: return;
            }
            e.Handled = true;
        }

        // ---------- Output ----------

        // The shot with the markup drawn on it, at the shot's exact pixel size
        BitmapSource Flatten()
        {
            CommitText();
            var all = new Rect(0, 0, pixelWidth, pixelHeight);
            var markup = new VisualBrush(ink)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = all,
                Stretch = Stretch.None,
            };
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(shot, all);
                dc.DrawRectangle(markup, null, all);
            }
            var flat = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
            flat.Render(visual);
            flat.Freeze();
            return flat;
        }

        static MemoryStream Png(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            return stream;
        }

        void Copy()
        {
            try
            {
                BitmapSource flat = Flatten();
                // No alpha channel in the bitmap: apps that mishandle it paste a black image.
                // The PNG is for apps that prefer it (browsers, chat apps).
                var opaque = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
                opaque.Freeze();
                var data = new DataObject();
                data.SetImage(opaque);
                data.SetData("PNG", Png(flat), false);
                Clipboard.SetDataObject(data, true);
                Flash(copyText, "Copied", CopyLabel);
            }
            catch (Exception)
            {
                // Another app is holding the clipboard open
                Flash(copyText, "Copy failed", CopyLabel);
            }
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveFolder);
                string file = IOPath.Combine(SaveFolder, "screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                using (var png = Png(Flatten()))
                using (var output = File.Create(file))
                    png.CopyTo(output);
                Flash(saveText, "Saved", SaveLabel);
            }
            catch (Exception)
            {
                Flash(saveText, "Failed", SaveLabel);
            }
        }

        void Flash(TextBlock label, string message, string normal)
        {
            label.Text = message;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += delegate { timer.Stop(); label.Text = normal; };
            timer.Start();
        }
    }

    // ---------- Text recognition ----------

    // The OCR engine built into Windows (Windows.Media.Ocr). Its WinRT calls are made directly:
    // the usual helpers (await, AsBuffer) live in System.Runtime.WindowsRuntime.dll, which only
    // compiles against the Windows SDK's metadata, and the SDK isn't on a stock Windows.
    static class Ocr
    {
        // Text on screen is small for the recognizer; it reads noticeably better enlarged
        const double Magnify = 3;

        static WinOcr.OcrEngine engine;

        // The image (BGRX pixels) at the size the recognizer reads best, or the same array if that is its size already
        public static byte[] Enlarge(byte[] pixels, int width, int height, double monitorScale, out int toWidth, out int toHeight)
        {
            // On a scaled monitor the text is already that much larger
            double limit = (double)WinOcr.OcrEngine.MaxImageDimension / Math.Max(width, height);
            double zoom = Math.Max(1, Math.Min(Magnify / monitorScale, limit));
            toWidth = (int)(width * zoom);
            toHeight = (int)(height * zoom);
            return zoom > 1 ? Resize(pixels, width, height, toWidth, toHeight) : pixels;
        }

        // Reads the text in an image (BGRX pixels). Calls `done` with its lines (none if there is
        // no text) or with a problem, possibly on another thread.
        public static void Read(byte[] pixels, int width, int height, Action<string[], string> done)
        {
            try
            {
                if (engine == null) engine = WinOcr.OcrEngine.TryCreateFromUserProfileLanguages();
                if (engine == null)
                {
                    done(null, "Windows has no OCR language installed (Settings > Time & language > Language & region)");
                    return;
                }

                WinImaging.SoftwareBitmap bitmap;
                using (var writer = new WinStreams.DataWriter())
                {
                    writer.WriteBytes(pixels);
                    bitmap = WinImaging.SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),
                        WinImaging.BitmapPixelFormat.Bgra8, width, height, WinImaging.BitmapAlphaMode.Ignore);
                }

                engine.RecognizeAsync(bitmap).Completed = delegate(WinFoundation.IAsyncOperation<WinOcr.OcrResult> operation, WinFoundation.AsyncStatus status)
                {
                    try
                    {
                        done(operation.GetResults().Lines.Select(line => line.Text).Where(text => text.Trim().Length > 0).ToArray(), null);
                    }
                    catch (Exception ex)
                    {
                        done(null, ex.Message);
                    }
                    finally
                    {
                        bitmap.Dispose();
                    }
                };
            }
            catch (Exception ex)
            {
                done(null, ex.Message);
            }
        }

        static byte[] Resize(byte[] pixels, int width, int height, int toWidth, int toHeight)
        {
            var format = Drawing.Imaging.PixelFormat.Format32bppRgb;
            using (var small = new Drawing.Bitmap(width, height, format))
            using (var large = new Drawing.Bitmap(toWidth, toHeight, format))
            {
                var bits = small.LockBits(new Drawing.Rectangle(0, 0, width, height), Drawing.Imaging.ImageLockMode.WriteOnly, format);
                Marshal.Copy(pixels, 0, bits.Scan0, pixels.Length);
                small.UnlockBits(bits);

                using (var g = Drawing.Graphics.FromImage(large))
                using (var edges = new Drawing.Imaging.ImageAttributes())
                {
                    // Mirrors the image past its edges, so text touching them isn't blended into a dark border
                    edges.SetWrapMode(Drawing.Drawing2D.WrapMode.TileFlipXY);
                    g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(small, new Drawing.Rectangle(0, 0, toWidth, toHeight), 0, 0, width, height, Drawing.GraphicsUnit.Pixel, edges);
                }

                bits = large.LockBits(new Drawing.Rectangle(0, 0, toWidth, toHeight), Drawing.Imaging.ImageLockMode.ReadOnly, format);
                var enlarged = new byte[bits.Stride * toHeight];
                Marshal.Copy(bits.Scan0, enlarged, 0, enlarged.Length);
                large.UnlockBits(bits);
                return enlarged;
            }
        }
    }

    // ---------- QR codes and barcodes ----------

    // Decoded by ZXing.Net (lib\zxing.dll, Apache 2.0), which is embedded in the exe: see WarrenTool.Program.LoadEmbedded
    static class Codes
    {
        // An enlarged copy of the image is only searched up to this many pixels. It finds codes
        // drawn too small to read at 1:1, but the search time grows with the size.
        public const int EnlargedSearchLimit = 6000000;

        // What the QR codes or barcodes in an image (BGRX pixels) say, or null if it has none.
        // `qr` tells which of the two they are.
        public static string[] Read(byte[] pixels, int width, int height, out bool qr)
        {
            qr = false;
            var reader = new ZXing.BarcodeReaderGeneric { AutoRotate = false };
            // The 2D codes, and the 1D ones with a checksum strong enough not to be "found" in ordinary text
            reader.Options.PossibleFormats = new List<ZXing.BarcodeFormat>
            {
                ZXing.BarcodeFormat.QR_CODE, ZXing.BarcodeFormat.DATA_MATRIX, ZXing.BarcodeFormat.AZTEC, ZXing.BarcodeFormat.PDF_417,
                ZXing.BarcodeFormat.EAN_13, ZXing.BarcodeFormat.UPC_A, ZXing.BarcodeFormat.CODE_128,
            };
            reader.Options.TryHarder = true;
            reader.Options.TryInverted = true;      // light codes on a dark background

            var format = ZXing.RGBLuminanceSource.BitmapFormat.BGR32;
            ZXing.Result[] found = reader.DecodeMultiple(pixels, width, height, format);
            if (found == null || found.Length == 0)
            {
                // The search for several codes and the search for one use different detectors
                ZXing.Result single = reader.Decode(pixels, width, height, format);
                if (single == null) return null;
                found = new[] { single };
            }

            string[] texts = found.Select(code => code.Text).Where(text => !string.IsNullOrEmpty(text)).Distinct().ToArray();
            if (texts.Length == 0) return null;
            qr = found.All(code => code.BarcodeFormat == ZXing.BarcodeFormat.QR_CODE);
            return texts;
        }
    }
}
