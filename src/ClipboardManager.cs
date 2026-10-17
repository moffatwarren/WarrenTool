// Clipboard Manager - clipboard history in a window in the middle of the screen. The Windows
// counterpart of the SUPER+V Quickshell menu (ClipboardMenu.qml) in Linux-Setup, and drawn the same:
// a Catppuccin Mocha card with a filter box, one row per entry and a hint line along the bottom.
// Part of WarrenTool, which owns the tray icon and the hotkey (WarrenTool.cs).
//
// Everything copied, text and images, is kept newest first in %LOCALAPPDATA%\WarrenTool\Clipboard,
// so the history survives a restart, as cliphist's does.
//
// Ctrl+Alt+V (or "Open clipboard history" in the tray menu) toggles the window.
//
//   Just type                  filter
//   Up / Down                  move (wraps around)
//   PageUp / PageDown          eight at a time
//   Home / End                 first / last
//   Enter or click             put it on the clipboard, then close
//   Delete or right-click      remove it (the window stays open)
//   Esc / click outside        close

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;
using Mocha = AudioSwitcher.Mocha;
using Native = ScreenShot.Native;
using Placement = AudioSwitcher.Placement;
using WinForms = System.Windows.Forms;

namespace ClipboardManager
{
    // ---------- The history ----------

    // One entry: a piece of text, or an image kept as a PNG
    public class Clip
    {
        public long Id;                 // newer is higher
        public string File;
        public bool IsImage;
        public string Text;             // text entries: all of it
        public string Hash;             // image entries: of the PNG, so copying the same image again is one entry
        public int Width, Height;
        public string Label;            // what the row shows, and what the filter matches
        public string Detail;           // an image's size in pixels, right of its label
        public BitmapSource Thumb;
        public bool ThumbRequested;

        public string Kind { get { return IsImage ? "image" : "text"; } }
    }

    // Newest first, with one file per entry so it survives a restart:
    //   <id>.txt                  text, UTF-8
    //   <id>_<w>x<h>_<hash>.png   an image
    public class History
    {
        const int MaxEntries = 750;             // cliphist's default
        const int MaxText = 4 * 1024 * 1024;    // characters; anything bigger is not kept
        const int LabelLength = 300;            // the row elides long before this

        public static readonly string Folder = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarrenTool", "Clipboard");

        readonly List<Clip> clips = new List<Clip>();
        long lastId;

        public event Action Changed;

        public ReadOnlyCollection<Clip> Clips { get { return clips.AsReadOnly(); } }

        public void Load()
        {
            clips.Clear();
            try
            {
                if (Directory.Exists(Folder))
                    foreach (string file in Directory.GetFiles(Folder))
                    {
                        var c = Parse(file);
                        if (c != null) clips.Add(c);
                    }
            }
            catch { }
            clips.Sort((a, b) => b.Id.CompareTo(a.Id));
            if (clips.Count > 0) lastId = clips[0].Id;
            while (clips.Count > MaxEntries) Delete(clips[clips.Count - 1]);
        }

        static Clip Parse(string file)
        {
            string name = IOPath.GetFileNameWithoutExtension(file);
            string ext = IOPath.GetExtension(file).ToLowerInvariant();
            long id;
            try
            {
                if (ext == ".txt")
                {
                    if (!long.TryParse(name, out id)) return null;
                    return TextClip(id, file, System.IO.File.ReadAllText(file, Encoding.UTF8));
                }
                if (ext == ".png")
                {
                    string[] parts = name.Split('_');
                    if (parts.Length != 3) return null;
                    string[] size = parts[1].Split('x');
                    int w, h;
                    if (!long.TryParse(parts[0], out id) || size.Length != 2
                        || !int.TryParse(size[0], out w) || !int.TryParse(size[1], out h)) return null;
                    return ImageClip(id, file, parts[2], w, h, new FileInfo(file).Length);
                }
            }
            catch { }
            return null;
        }

        static Clip TextClip(long id, string file, string text)
        {
            return new Clip { Id = id, File = file, Text = text, Label = OneLine(text), Detail = "" };
        }

        // Labelled as cliphist labels them: "png  547 KiB", with the size in pixels beside it
        static Clip ImageClip(long id, string file, string hash, int w, int h, long bytes)
        {
            return new Clip
            {
                Id = id, File = file, IsImage = true, Hash = hash, Width = w, Height = h,
                Label = "png  " + SizeText(bytes), Detail = w + "x" + h,
            };
        }

        // cliphist's preview: the text on one line, every run of whitespace a single space
        static string OneLine(string text)
        {
            var line = new StringBuilder();
            bool gap = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || char.IsControl(c)) { gap = line.Length > 0; continue; }
                if (gap) line.Append(' ');
                gap = false;
                line.Append(c);
                if (line.Length >= LabelLength) break;
            }
            return line.ToString();
        }

        static string SizeText(long bytes)
        {
            string[] units = { "B", "KiB", "MiB" };
            double size = bytes;
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return Math.Round(size) + " " + units[i];
        }

        // Ids are taken when something is copied, so an image that takes a while to encode still lands
        // below whatever was copied after it
        public long NextId()
        {
            lastId = Math.Max(lastId + 1, DateTime.UtcNow.Ticks);
            return lastId;
        }

        public void AddText(long id, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText) return;
            var old = clips.FirstOrDefault(c => !c.IsImage && c.Text == text);
            // Copying again what is already newest (as putting an entry back does) changes nothing
            if (old != null && clips[0] == old) return;
            string file = IOPath.Combine(Folder, id + ".txt");
            try
            {
                Directory.CreateDirectory(Folder);
                System.IO.File.WriteAllText(file, text, new UTF8Encoding(false));
            }
            catch { return; }
            if (old != null) Delete(old);
            Insert(TextClip(id, file, text));
        }

        public void AddImage(long id, byte[] png, int w, int h)
        {
            string hash;
            using (var sha = SHA1.Create())
                hash = BitConverter.ToString(sha.ComputeHash(png)).Replace("-", "").Substring(0, 16);
            var old = clips.FirstOrDefault(c => c.IsImage && c.Hash == hash);
            if (old != null && clips[0] == old) return;
            string file = IOPath.Combine(Folder, id + "_" + w + "x" + h + "_" + hash + ".png");
            try
            {
                Directory.CreateDirectory(Folder);
                System.IO.File.WriteAllBytes(file, png);
            }
            catch { return; }
            var clip = ImageClip(id, file, hash, w, h, png.LongLength);
            if (old != null)
            {
                clip.Thumb = old.Thumb;
                clip.ThumbRequested = old.ThumbRequested && old.Thumb != null;
                Delete(old);
            }
            Insert(clip);
        }

        void Insert(Clip clip)
        {
            int at = 0;
            while (at < clips.Count && clips[at].Id > clip.Id) at++;
            clips.Insert(at, clip);
            while (clips.Count > MaxEntries) Delete(clips[clips.Count - 1]);
            Raise();
        }

        public void Remove(Clip clip)
        {
            if (!clips.Contains(clip)) return;
            Delete(clip);
            Raise();
        }

        public void Clear()
        {
            foreach (var c in clips.ToList()) Delete(c);
            Raise();
        }

        void Delete(Clip clip)
        {
            clips.Remove(clip);
            try { System.IO.File.Delete(clip.File); } catch { }
        }

        void Raise()
        {
            if (Changed != null) Changed();
        }
    }

    // ---------- Watching the clipboard ----------

    // Adds whatever is copied to the history
    public class Recorder : IDisposable
    {
        const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        readonly History history;
        readonly HwndSource sink;
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;

        public Recorder(History history)
        {
            this.history = history;
            sink = new HwndSource(new HwndSourceParameters("WarrenTool.Clipboard")
            {
                ParentWindow = new IntPtr(-3), // HWND_MESSAGE
                WindowStyle = 0,
            });
            sink.AddHook(WndProc);
            AddClipboardFormatListener(sink.Handle);
        }

        public void Dispose()
        {
            RemoveClipboardFormatListener(sink.Handle);
            sink.Dispose();
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_CLIPBOARDUPDATE) return IntPtr.Zero;
            // Read after this notification returns: the copying app may still have the clipboard open
            dispatcher.BeginInvoke(new Action(Read));
            handled = true;
            return IntPtr.Zero;
        }

        void Read()
        {
            try
            {
                // Retries for a second if another app is holding the clipboard
                WinForms.IDataObject data = WinForms.Clipboard.GetDataObject();
                if (data == null || Private(data)) return;

                // Text wins: Office and browsers put a picture of a selection beside its text
                if (data.GetDataPresent(WinForms.DataFormats.UnicodeText))
                {
                    history.AddText(history.NextId(), data.GetData(WinForms.DataFormats.UnicodeText) as string);
                    return;
                }
                // The PNG keeps transparency; apps that offer only a bitmap get that converted
                var png = data.GetDataPresent("PNG") ? data.GetData("PNG") as MemoryStream : null;
                if (png != null) { SaveImage(history.NextId(), png.ToArray(), null); return; }
                if (data.GetDataPresent(WinForms.DataFormats.Bitmap))
                {
                    var bitmap = data.GetData(WinForms.DataFormats.Bitmap) as Drawing.Bitmap;
                    if (bitmap != null) SaveImage(history.NextId(), null, bitmap);
                }
            }
            catch { }   // the clipboard stayed busy, or the app that copied went away
        }

        // Password managers mark what they copy as not for history; Windows' own Win+V history skips it too
        static bool Private(WinForms.IDataObject data)
        {
            if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return true;
            if (data.GetDataPresent("Clipboard Viewer Ignore")) return true;
            var allowed = data.GetDataPresent("CanIncludeInClipboardHistory")
                ? data.GetData("CanIncludeInClipboardHistory") as MemoryStream : null;
            return allowed != null && allowed.Length >= 4 && BitConverter.ToInt32(allowed.ToArray(), 0) == 0;
        }

        // Encoding a screen-sized image takes a moment, so it happens off the UI thread
        void SaveImage(long id, byte[] png, Drawing.Bitmap bitmap)
        {
            Task.Run(() =>
            {
                int w, h;
                try
                {
                    if (png == null)
                    {
                        using (bitmap)
                        using (var stream = new MemoryStream())
                        {
                            bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png);
                            png = stream.ToArray();
                        }
                    }
                    var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.DelayCreation,
                        BitmapCacheOption.None).Frames[0];
                    w = frame.PixelWidth;
                    h = frame.PixelHeight;
                }
                catch { return; }   // not a readable image
                dispatcher.BeginInvoke(new Action(() => history.AddImage(id, png, w, h)));
            });
        }

        // Puts an entry back on the clipboard (where the Recorder sees it and moves it to the top).
        // False when another app is holding the clipboard.
        public static bool Copy(Clip clip)
        {
            try
            {
                if (!clip.IsImage)
                {
                    Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, clip.Text), true);
                    return true;
                }
                byte[] png = System.IO.File.ReadAllBytes(clip.File);
                var frame = BitmapFrame.Create(new MemoryStream(png), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                // As Screen Shot copies: a bitmap without alpha, since apps that mishandle it paste black,
                // and the PNG for the apps that prefer it
                var opaque = new FormatConvertedBitmap(frame, PixelFormats.Bgr24, null, 0);
                opaque.Freeze();
                var data = new DataObject();
                data.SetImage(opaque);
                data.SetData("PNG", new MemoryStream(png), false);
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch { return false; }
        }
    }

    // ---------- The window ----------

    public class ClipboardWindow : Window
    {
        // The Quickshell menu's geometry (OverlayPanel.qml and ClipboardMenu.qml)
        const double PanelWidth = 720;
        const double Pad = 18;
        const double HeaderHeight = 58;
        const double RowHeight = 48;
        const int RowsShown = 8;
        const double BodyHeight = RowHeight * RowsShown;
        const double BodyWidth = PanelWidth - 2 * Pad - 2;     // inside the panel's 1px border
        const string Hints = "↑ ↓  move    ↵  copy    Del  remove    Esc  cancel";

        static readonly Brush Dim = Frozen(new SolidColorBrush(Color.FromArgb(0xCC, 0x11, 0x11, 0x1B)));
        static readonly FontFamily Icons = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        const string SearchGlyph = "", TextGlyph = "", ImageGlyph = "";
        static readonly Duration FadeIn = TimeSpan.FromMilliseconds(120);
        static readonly Duration FadeOut = TimeSpan.FromMilliseconds(90);

        class Row
        {
            public Border Root, Preview;
            public Image Thumb;
            public TextBlock Glyph, Label, Detail;
        }

        readonly History history;
        readonly Action<string> warn;
        readonly Canvas body = new Canvas
        {
            ClipToBounds = true,
            Background = Brushes.Transparent,   // hit-testable everywhere, rows or not
            Width = BodyWidth,
            Height = BodyHeight,
            Cursor = Cursors.Hand,
        };
        // Enough rows to fill the body while one is half scrolled off the top
        readonly Row[] rows = new Row[RowsShown + 1];
        readonly TextBox filterBox = new TextBox();
        readonly Run subtitle = new Run();
        readonly TextBlock counter = new TextBlock();
        readonly TextBlock footer = new TextBlock();

        Border panel;
        List<Clip> shown = new List<Clip>();
        int current = -1;           // index into shown, -1 for none
        double scroll;              // pixels
        bool mouseSeen;
        Point lastMouse;
        int pressedIndex = -1;
        bool closing;
        int fadeId;
        DateTime hiddenAt = DateTime.MinValue;

        public ClipboardWindow(History history, Action<string> warn)
        {
            this.history = history;
            this.warn = warn;
            Title = "Clipboard";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = Mocha.UiFont;
            FontSize = Mocha.FontSize;

            for (int i = 0; i < rows.Length; i++)
            {
                rows[i] = BuildRow();
                body.Children.Add(rows[i].Root);
            }
            Content = BuildChrome();

            // Preview, so the list's keys are seen before the filter box gets them
            PreviewKeyDown += OnKeyDown;
            Deactivated += delegate { Dismiss(); };
            IsVisibleChanged += delegate { if (!IsVisible) hiddenAt = DateTime.UtcNow; };
            history.Changed += delegate { if (IsVisible && !closing) Refilter(true, 0); };
        }

        public bool JustHidden { get { return (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300; } }

        Clip Current { get { return current >= 0 && current < shown.Count ? shown[current] : null; } }

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        // Called once at startup: the first screenful of thumbnails is ready before the first opening
        public void Preload()
        {
            foreach (var c in history.Clips.Take(rows.Length)) RequestThumb(c);
        }

        // Closing (Alt+F4) just hides; the tray app owns the lifetime
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Dismiss();
        }

        public void Dismiss()
        {
            if (!IsVisible || closing) return;
            closing = true;
            int id = ++fadeId;
            var fade = new DoubleAnimation(0, FadeOut);
            fade.Completed += delegate
            {
                if (id != fadeId) return;   // reopened in the meantime
                closing = false;
                Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        }

        // Covers the monitor under the mouse, dimmed, with the panel in the middle
        public void ShowWindow()
        {
            var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            double scale = Native.ScaleOf(screen);
            Width = screen.Bounds.Width / scale;
            Height = screen.Bounds.Height / scale;

            // Every opening starts unfiltered, at the newest entry
            filterBox.Clear();
            Refilter(false, 0);
            mouseSeen = false;
            pressedIndex = -1;

            bool opening = !IsVisible || closing;
            closing = false;
            fadeId++;
            if (opening)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            }
            Show();
            Placement.Move(this, screen, (w, h) => new Drawing.Point(screen.Bounds.Left, screen.Bounds.Top), true);
            Activate();
            filterBox.Focus();
            if (opening) BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
        }

        // ---------- UI construction ----------

        UIElement BuildChrome()
        {
            // The title and the subtitle (an image's size) are one line of text, so they share a baseline
            var heading = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom };
            heading.Inlines.Add(new Run("Clipboard") { Foreground = Mocha.Lavender, FontSize = Mocha.FontSize + 4, FontWeight = FontWeights.Bold });
            heading.Inlines.Add(new Run("  "));
            subtitle.Foreground = Mocha.Subtext0;
            heading.Inlines.Add(subtitle);
            counter.Foreground = Mocha.Overlay0;
            counter.Margin = new Thickness(14, 0, 0, 1);
            counter.VerticalAlignment = VerticalAlignment.Bottom;
            var top = new DockPanel { VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(counter, Dock.Right);
            top.Children.Add(counter);
            top.Children.Add(heading);

            var header = new Grid { Height = HeaderHeight };
            header.Children.Add(top);
            header.Children.Add(BuildFilterField());

            body.Margin = new Thickness(0, 14, 0, 0);
            body.MouseMove += delegate(object s, MouseEventArgs e) { Hover(e.GetPosition(body)); };
            body.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { pressedIndex = IndexAt(e.GetPosition(body)); e.Handled = true; };
            body.MouseRightButtonDown += delegate(object s, MouseButtonEventArgs e) { pressedIndex = IndexAt(e.GetPosition(body)); e.Handled = true; };
            body.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { Clicked(e, false); };
            body.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { Clicked(e, true); };
            body.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                ScrollTo(scroll - e.Delta / 120.0 * RowHeight * 2);
                e.Handled = true;
            };

            footer.FontSize = Mocha.FontSize - 1;
            footer.Height = 16;
            footer.Margin = new Thickness(0, 12, 0, 0);

            var layout = new StackPanel();
            layout.Children.Add(header);
            layout.Children.Add(body);
            layout.Children.Add(footer);

            panel = new Border
            {
                Width = PanelWidth,
                Padding = new Thickness(Pad),
                CornerRadius = new CornerRadius(16),
                Background = Mocha.Base,
                BorderBrush = Mocha.Surface1,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = layout,
            };

            // Clicking the dimmed screen around the panel is the mouse's Escape
            var root = new Grid { Background = Dim };
            root.Children.Add(panel);
            root.MouseDown += delegate { if (!panel.IsMouseOver) Dismiss(); };
            return root;
        }

        // The filter box holds the keyboard focus the whole time the window is open,
        // so typing lands in it without clicking it first
        UIElement BuildFilterField()
        {
            var glyph = new TextBlock
            {
                Text = SearchGlyph,
                FontFamily = Icons,
                Foreground = Mocha.Overlay0,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var placeholder = new TextBlock
            {
                Text = "type to filter",
                Foreground = Mocha.Overlay0,
                Margin = new Thickness(32, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            filterBox.Background = Brushes.Transparent;
            filterBox.BorderThickness = new Thickness(0);
            filterBox.Foreground = Mocha.Text;
            filterBox.CaretBrush = Mocha.Text;
            filterBox.SelectionBrush = Mocha.Surface2;
            filterBox.Padding = new Thickness(28, 0, 10, 0);
            filterBox.VerticalContentAlignment = VerticalAlignment.Center;
            ContextMenuService.SetIsEnabled(filterBox, false);
            filterBox.TextChanged += delegate
            {
                placeholder.Visibility = filterBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                Refilter(false, 0);
            };

            var inner = new Grid();
            inner.Children.Add(glyph);
            inner.Children.Add(placeholder);
            inner.Children.Add(filterBox);
            return new Border
            {
                Height = 30,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(8),
                Background = Mocha.Surface0,
                Child = inner,
            };
        }

        // A thumbnail where an image entry has one, a glyph otherwise, so both line up in one column
        Row BuildRow()
        {
            var thumb = new Image
            {
                Stretch = Stretch.UniformToFill,
                // Centered, so UniformToFill crops around the middle rather than the top-left
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(thumb, BitmapScalingMode.HighQuality);
            var glyph = new TextBlock
            {
                FontFamily = Icons,
                FontSize = Mocha.FontSize + 4,
                Foreground = Mocha.Overlay0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var face = new Grid { Clip = Frozen(new RectangleGeometry(new Rect(0, 0, 60, 34), 6, 6)) };
            face.Children.Add(thumb);
            face.Children.Add(glyph);
            var preview = new Border
            {
                Width = 60,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = face,
            };

            var label = new TextBlock
            {
                Margin = new Thickness(12, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var detail = new TextBlock
            {
                FontSize = Mocha.FontSize - 1,
                Foreground = Mocha.Overlay0,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(detail, 2);
            grid.Children.Add(preview);
            grid.Children.Add(label);
            grid.Children.Add(detail);

            var root = new Border
            {
                Width = BodyWidth,
                Height = RowHeight - 4,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,   // the body handles the mouse for every row
                Child = grid,
            };
            return new Row { Root = root, Preview = preview, Thumb = thumb, Glyph = glyph, Label = label, Detail = detail };
        }

        // ---------- The list ----------

        // Matches the label, or the kind ("image", "text"), as the Quickshell menu does. keepPlace stays on the
        // same entry if it is still there; otherwise the cursor goes to "index".
        void Refilter(bool keepPlace, int index)
        {
            Clip was = keepPlace ? Current : null;
            string q = filterBox.Text.Trim();
            shown = history.Clips
                .Where(c => q.Length == 0
                    || c.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                    || c.Kind.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            int at = was != null ? shown.IndexOf(was) : -1;
            if (at < 0) at = keepPlace ? Math.Min(current, shown.Count - 1) : index;
            JumpTo(Math.Max(0, at));
        }

        // Selects an entry and scrolls just far enough to show it
        void JumpTo(int index)
        {
            current = Math.Max(-1, Math.Min(shown.Count - 1, index));
            if (current >= 0)
            {
                double top = current * RowHeight;
                if (top < scroll) scroll = top;
                else if (top + RowHeight > scroll + BodyHeight) scroll = top + RowHeight - BodyHeight;
            }
            ScrollTo(scroll);
        }

        void ScrollTo(double value)
        {
            scroll = Math.Max(0, Math.Min(value, Math.Max(0, shown.Count * RowHeight - BodyHeight)));
            Render();
        }

        void Render()
        {
            int first = (int)Math.Floor(scroll / RowHeight);
            for (int k = 0; k < rows.Length; k++)
            {
                var row = rows[k];
                int i = first + k;
                if (i >= shown.Count)
                {
                    row.Root.Visibility = Visibility.Collapsed;
                    continue;
                }
                var c = shown[i];
                bool selected = i == current;
                row.Root.Visibility = Visibility.Visible;
                Canvas.SetTop(row.Root, i * RowHeight - scroll + 2);
                row.Root.Background = selected ? Mocha.Surface0 : Brushes.Transparent;
                row.Root.BorderBrush = selected ? Mocha.Lavender : Brushes.Transparent;
                row.Label.Text = c.Label;
                row.Label.Foreground = selected ? Mocha.Lavender : Mocha.Text;
                row.Detail.Text = c.Detail;
                row.Preview.Background = c.IsImage ? Mocha.Mantle : Brushes.Transparent;
                if (c.IsImage) RequestThumb(c);
                row.Thumb.Source = c.IsImage ? c.Thumb : null;
                row.Thumb.Visibility = row.Thumb.Source != null ? Visibility.Visible : Visibility.Collapsed;
                row.Glyph.Visibility = row.Thumb.Source != null ? Visibility.Collapsed : Visibility.Visible;
                row.Glyph.Text = c.IsImage ? ImageGlyph : TextGlyph;
            }

            var cur = Current;
            subtitle.Text = cur != null && cur.IsImage ? cur.Detail : "";
            counter.Text = shown.Count + " / " + history.Clips.Count;
            footer.Text = history.Clips.Count == 0 ? "Clipboard history is empty"
                : shown.Count == 0 ? "Nothing matches"
                : Hints;
            footer.Foreground = shown.Count == 0 ? Mocha.Red : Mocha.Overlay0;
        }

        // Decoded at twice the preview's size, so it stays sharp at up to 200% scaling
        void RequestThumb(Clip clip)
        {
            if (!clip.IsImage || clip.ThumbRequested) return;
            clip.ThumbRequested = true;
            Task.Run(() =>
            {
                BitmapSource bmp = null;
                try
                {
                    var image = new BitmapImage();
                    using (var fs = new FileStream(clip.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        image.BeginInit();
                        image.CacheOption = BitmapCacheOption.OnLoad;
                        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        // Decode the side that ends up cropped to fill the 60x34 box
                        if (clip.Width * 34 > clip.Height * 60) image.DecodePixelHeight = 68;
                        else image.DecodePixelWidth = 120;
                        image.StreamSource = fs;
                        image.EndInit();
                    }
                    image.Freeze();
                    bmp = image;
                }
                catch { }   // the glyph stays in its place
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    clip.Thumb = bmp;
                    if (IsVisible) Render();
                }));
            });
        }

        // ---------- Input ----------

        int IndexAt(Point p)
        {
            if (p.Y < 0 || p.Y >= BodyHeight || p.X < 0 || p.X >= BodyWidth) return -1;
            int i = (int)Math.Floor((p.Y + scroll) / RowHeight);
            return i < shown.Count ? i : -1;
        }

        // The mouse moves the cursor only when it really moves. The window appearing under a still mouse
        // sends a move too, and taking that one would steal the selection from the keyboard.
        void Hover(Point p)
        {
            bool first = !mouseSeen;
            bool moved = Math.Abs(p.X - lastMouse.X) > 1 || Math.Abs(p.Y - lastMouse.Y) > 1;
            if (!first && !moved) return;
            mouseSeen = true;
            lastMouse = p;
            if (first) return;
            int i = IndexAt(p);
            if (i >= 0 && i != current)
            {
                current = i;
                Render();
            }
        }

        // Left-click copies, right-click removes (like Delete), on the row pressed and released on
        void Clicked(MouseButtonEventArgs e, bool remove)
        {
            e.Handled = true;
            int i = IndexAt(e.GetPosition(body));
            int pressed = pressedIndex;
            pressedIndex = -1;
            if (i < 0 || i != pressed) return;
            if (remove) Remove(i);
            else CopyAndClose(shown[i]);
        }

        // These keys drive the list; every other key is left for the filter box to type
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Wherever the focus has wandered, the character this key produces still lands in the filter
            if (!filterBox.IsKeyboardFocused) filterBox.Focus();

            int n = shown.Count;
            switch (e.Key)
            {
                case Key.Up: JumpTo(current <= 0 ? n - 1 : current - 1); break;
                case Key.Down: JumpTo(current >= n - 1 ? 0 : current + 1); break;
                case Key.PageUp: JumpTo(Math.Max(0, current - RowsShown)); break;
                case Key.PageDown: JumpTo(current + RowsShown); break;
                case Key.Home: JumpTo(0); break;
                case Key.End: JumpTo(n - 1); break;
                // Delete leaves the window open, so a run of junk can be cleared without reopening it
                case Key.Delete: if (Current != null) Remove(current); break;
                case Key.Enter: if (Current != null) CopyAndClose(Current); break;
                case Key.Escape: Dismiss(); break;
                case Key.Tab: break;    // would move the focus out of the filter box
                default: return;
            }
            e.Handled = true;
        }

        // The row goes at once, and the cursor keeps its place in the shortened list (history.Changed refilters)
        void Remove(int index)
        {
            current = index;
            history.Remove(shown[index]);
        }

        void CopyAndClose(Clip clip)
        {
            // Hidden first, so the app underneath has the focus back for the paste
            Dismiss();
            if (!Recorder.Copy(clip)) warn("Couldn't copy: another app is using the clipboard. Try again.");
        }
    }
}
