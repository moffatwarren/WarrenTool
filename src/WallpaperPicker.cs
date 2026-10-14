// Wallpaper Picker - a horizontal, slanted-card carousel for choosing a wallpaper.
// Part of WarrenTool, which owns the tray icon and the hotkey (WarrenTool.cs).
//
// Ctrl+Alt+W (or "Open wallpaper picker" in the tray menu) toggles the picker.
//
//   Just type                            filter by file name
//   <- / ->, mouse wheel                 browse
//   Up / Down, PageUp / PageDown         jump five at a time
//   Home / End                           jump to the first / last
//   Ctrl+R                               random
//   Enter or double-click                set Desktop wallpaper + Lock Screen, then close
//   Shift+Enter                          set Desktop wallpaper only, then close
//   Esc                                  clear the filter, or hide if there is none
//   Click elsewhere                      hide

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;
using WinForms = System.Windows.Forms;

namespace WallpaperPicker
{
    // ---------- The picker window ----------

    class Card
    {
        public string File;
        public string Name;                 // file name without its extension: what is shown and filtered on
        public int Index;                   // position in Batch.Cards
        public Grid Root;
        public Image Image;
        public ShapePath Outline;
        public TranslateTransform Slide;    // entrance offset of the whole card
        public TranslateTransform Drift;    // parallax offset of the image inside the card
        public UIElement Shade;
        public Grid Face;
        public double Width, Height;
    }

    // One set of cards plus its loading state; replaced whenever the folder contents change
    class Batch
    {
        public List<Card> Cards;            // every image in the folder
        public volatile Card[] Shown;       // the ones matching the filter, in the same order
        public bool[] Requested;
        public readonly object Lock = new object();
    }

    public class PickerWindow : Window
    {
        // Card geometry (device-independent pixels)
        const double WindowH = 790;
        const double Edge = 22;             // padding between the panel's sides and what is in it
        const double CardH = 520;
        const double SelectedH = 620;       // the selected card stands taller than the rest
        const double SlantRatio = 0.25;     // sideways lean per unit of height
        const double Strip = 200;           // a card's width measured straight across
        const double Gap = 14;
        const double Step = Strip + Gap;
        const double SelectedExtra = 260;   // how much wider the selected card gets
        const double Parallax = 34;         // how far an image drifts inside its card

        // Motion
        const double Spring = 18;           // scroll stiffness (critically damped)
        const double IntroRise = 70;        // cards slide up their own slant when the picker opens...
        const double IntroCard = 0.38;      // ...each taking this long (seconds)...
        const double IntroStagger = 0.035;  // ...starting this much later per card away from the center
        const int IntroReach = 8;
        const double IntroLength = IntroReach * IntroStagger + IntroCard;
        static readonly Duration FadeIn = TimeSpan.FromMilliseconds(170);
        static readonly Duration FadeOut = TimeSpan.FromMilliseconds(110);

        static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp" };
        // Catppuccin Mocha
        static readonly Brush Base = Solid(0x1E1E2E);       // the panel
        static readonly Brush Mantle = Solid(0x181825);     // a card whose image has not loaded
        static readonly Brush Crust = Solid(0x11111B);      // the shade over distant cards
        static readonly Brush Surface0 = Solid(0x313244);   // the filter box and the panel's edge
        static readonly Brush Overlay1 = Solid(0x7F849C);   // dim text
        static readonly Brush Subtext0 = Solid(0xA6ADC8);
        static readonly Brush BodyText = Solid(0xCDD6F4);
        static readonly Brush Lavender = Solid(0xB4BEFE);   // the title and the hovered card
        static readonly Brush Peach = Solid(0xFAB387);      // the accent: the selected card, the caret
        static readonly FontFamily UiFont = new FontFamily("JetBrainsMono NF, JetBrains Mono, Cascadia Mono, Consolas");

        public readonly string Folder;

        Batch batch = new Batch { Cards = new List<Card>(), Shown = new Card[0], Requested = new bool[0] };
        // The cards in the carousel. "selected" indexes into these, not into every card in the folder.
        Card[] cards { get { return batch.Shown; } }

        readonly Canvas canvas = new Canvas { ClipToBounds = true };
        readonly TextBox filterBox = new TextBox();
        readonly TextBlock counter = new TextBlock();
        readonly Run nameText = new Run();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly double dpiScale;
        readonly int decodeHeight;

        Border shell;
        int selected;
        Card hovered;
        double offset;          // animated scroll position, in card units
        double velocity;        // of the scroll position, in cards per second
        double lastFrame;
        bool animating;
        double shownAt;         // when the opening animation started
        bool introPending;      // the opening animation starts with the first rendered frame
        bool closing;           // fading out
        int fadeId;
        int wheelAccum;
        volatile int focusIndex;
        DateTime hiddenAt = DateTime.MinValue;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SystemParametersInfo(int uAction, int uParam, string lpvParam, int fuWinIni);

        public PickerWindow(string folder)
        {
            Folder = folder;
            Title = "Wallpaper Picker";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Height = WindowH;
            FontFamily = UiFont;

            using (var g = Drawing.Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiY / 96.0;
            decodeHeight = (int)(SelectedH * dpiScale);

            Content = BuildChrome();

            // Preview, so the carousel's keys are seen before the filter box gets them
            PreviewKeyDown += OnKeyDown;
            PreviewMouseWheel += OnWheel;
            Deactivated += delegate { Dismiss(); };
            IsVisibleChanged += delegate { if (!IsVisible) hiddenAt = DateTime.UtcNow; };
            canvas.SizeChanged += delegate { Layout(); };
        }

        public bool JustHidden { get { return (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300; } }

        // Closing (Alt+F4) just hides; the tray app owns the lifetime
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Dismiss();
        }

        // Fades the picker out, then hides it
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

        static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        static Brush Solid(int rgb)
        {
            return Frozen(new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)));
        }

        public static List<string> ScanFolder(string folder)
        {
            if (!Directory.Exists(folder)) return new List<string>();
            return Directory.GetFiles(folder)
                .Where(f => Extensions.Contains(IOPath.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => IOPath.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Shows the picker on the monitor under the mouse. Returns an error message, or null.
        public string ShowPicker()
        {
            if (!Directory.Exists(Folder)) return "Wallpaper folder not found:\n" + Folder;

            var files = ScanFolder(Folder);
            if (files.Count == 0) return "No images (.jpg, .png, .bmp, .webp) found in:\n" + Folder;
            if (!files.SequenceEqual(batch.Cards.Select(c => c.File))) Rebuild(files);

            // Every opening starts unfiltered (clearing the box puts all the cards back on show)
            filterBox.Clear();
            selected = FindCurrentWallpaper(files);
            offset = selected;
            velocity = 0;
            focusIndex = selected;
            hovered = null;
            UpdateSelectionVisuals();

            var area = WinForms.Screen.FromPoint(WinForms.Cursor.Position).WorkingArea;
            // Shrink the whole picker on screens too short for its full height
            double fit = Math.Min(1, area.Height / dpiScale * 0.92 / WindowH);
            shell.LayoutTransform = new ScaleTransform(fit, fit);
            Height = WindowH * fit;
            Width = area.Width / dpiScale * 0.94;   // nearly the full width of any monitor
            Left = area.Left / dpiScale + (area.Width / dpiScale - Width) / 2;
            Top = area.Top / dpiScale + (area.Height / dpiScale - Height) / 2;

            // Already open and staying open: nothing to animate in
            bool opening = !IsVisible || closing;
            closing = false;
            fadeId++;
            if (opening)
            {
                introPending = true;
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
            }

            Show();
            Activate();
            filterBox.Focus();
            Layout();
            if (opening)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, FadeIn));
                StartAnimation();
            }
            return null;
        }

        // ---------- UI construction ----------

        UIElement BuildChrome()
        {
            // The title and the wallpaper's name are one line of text, so they share a baseline
            var heading = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            heading.Inlines.Add(new Run("Wallpaper  ") { Foreground = Lavender, FontSize = 15, FontWeight = FontWeights.Bold });
            nameText.Foreground = Subtext0;
            nameText.FontSize = 12;
            heading.Inlines.Add(nameText);
            counter.Foreground = Overlay1;
            counter.FontSize = 12;
            counter.Margin = new Thickness(16, 0, 0, 0);
            counter.VerticalAlignment = VerticalAlignment.Bottom;
            var top = new DockPanel();
            DockPanel.SetDock(counter, Dock.Right);
            top.Children.Add(counter);
            top.Children.Add(heading);

            var header = new StackPanel { Margin = new Thickness(Edge, 16, Edge, 10) };
            header.Children.Add(top);
            header.Children.Add(BuildFilterField());

            canvas.Margin = new Thickness(Edge, 0, Edge, 0);

            var footer = new TextBlock { FontSize = 11, Margin = new Thickness(Edge, 8, Edge, 16) };
            string[] hints =
            {
                "← → wheel", "move", "↑ ↓", "page", "Ctrl+R", "random",
                "↵", "apply", "Shift+↵", "desktop only", "Esc", "cancel",
            };
            for (int i = 0; i < hints.Length; i += 2)
            {
                footer.Inlines.Add(new Run(hints[i] + "  ") { Foreground = Subtext0 });
                footer.Inlines.Add(new Run(hints[i + 1] + "     ") { Foreground = Overlay1 });
            }

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(header, 0);
            Grid.SetRow(canvas, 1);
            Grid.SetRow(footer, 2);
            layout.Children.Add(header);
            layout.Children.Add(canvas);
            layout.Children.Add(footer);

            shell = new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = Base,
                BorderBrush = Surface0,
                BorderThickness = new Thickness(1),
                Child = layout,
            };
            // Drag the window by its empty background
            shell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (!e.Handled) DragMove(); };
            return shell;
        }

        // The filter box holds the keyboard focus the whole time the picker is open,
        // so typing lands in it without clicking it first
        UIElement BuildFilterField()
        {
            var glyph = new TextBlock
            {
                Text = "",    // magnifier
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Overlay1,
                FontSize = 12,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            var placeholder = new TextBlock
            {
                Text = "type to filter",
                Foreground = Overlay1,
                FontSize = 12,
                Margin = new Thickness(33, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            filterBox.Background = Brushes.Transparent;
            filterBox.BorderThickness = new Thickness(0);
            filterBox.Foreground = BodyText;
            filterBox.CaretBrush = Peach;
            filterBox.SelectionBrush = Peach;
            filterBox.FontSize = 12;
            filterBox.Padding = new Thickness(29, 0, 10, 0);
            filterBox.VerticalContentAlignment = VerticalAlignment.Center;
            ContextMenuService.SetIsEnabled(filterBox, false);

            var inner = new Grid();
            inner.Children.Add(glyph);
            inner.Children.Add(placeholder);
            inner.Children.Add(filterBox);
            var field = new Border
            {
                Height = 30,
                Margin = new Thickness(0, 10, 0, 0),
                CornerRadius = new CornerRadius(7),
                Background = Surface0,
                BorderBrush = Surface0,
                BorderThickness = new Thickness(1),
                Child = inner,
            };

            filterBox.TextChanged += delegate
            {
                bool any = filterBox.Text.Length > 0;
                placeholder.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
                field.BorderBrush = any ? Peach : Surface0;
                ApplyFilter();
            };
            return field;
        }

        // Puts on show the cards whose name contains every word in the filter box
        void ApplyFilter()
        {
            string[] words = filterBox.Text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            Card current = selected < cards.Length ? cards[selected] : null;

            // Layout brings back the ones that are on show and in view
            foreach (var c in batch.Cards) c.Root.Visibility = Visibility.Collapsed;
            batch.Shown = batch.Cards
                .Where(c => words.All(w => c.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0))
                .ToArray();

            // Stay on the same wallpaper if it still matches, otherwise go to the first match.
            // The cards have all changed places, so jump there rather than scroll.
            selected = Math.Max(0, Array.IndexOf(cards, current));
            offset = selected;
            velocity = 0;
            focusIndex = selected;
            UpdateSelectionVisuals();
            Layout();
        }

        void Rebuild(List<string> files)
        {
            canvas.Children.Clear();
            var next = new Batch { Cards = new List<Card>(), Requested = new bool[files.Count] };
            for (int i = 0; i < files.Count; i++)
            {
                var c = BuildCard(files[i], i);
                next.Cards.Add(c);
                canvas.Children.Add(c.Root);
            }
            next.Shown = next.Cards.ToArray();
            batch = next;

            for (int t = 0; t < 2; t++)
            {
                var b = next;
                new Thread(() => LoadLoop(b)) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            }
        }

        static Geometry CardShape(double width, double height)
        {
            double slant = height * SlantRatio;
            var shape = new StreamGeometry();
            using (var ctx = shape.Open())
            {
                ctx.BeginFigure(new Point(slant, 0), true, true);
                ctx.PolyLineTo(new[] { new Point(width, 0), new Point(width - slant, height), new Point(0, height) }, true, true);
            }
            shape.Freeze();
            return shape;
        }

        Card BuildCard(string file, int index)
        {
            double width = Strip + CardH * SlantRatio;
            var shape = CardShape(width, CardH);
            string name = IOPath.GetFileNameWithoutExtension(file);

            var placeholder = new TextBlock
            {
                Text = name,
                Foreground = Overlay1,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Width = Strip - 20,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Centered alignment makes UniformToFill crop around the middle instead of the top-left.
            // The negative margin makes the image overhang the card, leaving room for it to drift.
            var drift = new TranslateTransform();
            var image = new Image
            {
                Stretch = Stretch.UniformToFill,
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(-Parallax, 0, -Parallax, 0),
                RenderTransform = drift,
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var face = new Grid
            {
                Clip = shape,
                Background = Mantle,
            };
            face.Children.Add(placeholder);
            face.Children.Add(image);
            // Dims distant cards. (The card's own opacity belongs to the opening animation.)
            var shade = new System.Windows.Shapes.Rectangle { Fill = Crust, Opacity = 0, IsHitTestVisible = false };
            face.Children.Add(shade);

            // Only the selected and the hovered card are outlined
            var outline = new ShapePath
            {
                Data = shape,
                StrokeLineJoin = PenLineJoin.Round,
            };

            var slide = new TranslateTransform();
            var root = new Grid
            {
                Width = width,
                Height = CardH,
                RenderTransform = slide,
                Cursor = Cursors.Hand,
            };
            root.Children.Add(face);
            root.Children.Add(outline);

            var card = new Card
            {
                File = file, Name = name, Index = index,
                Root = root, Image = image, Outline = outline, Slide = slide, Drift = drift,
                Shade = shade, Face = face, Width = width, Height = CardH,
            };

            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                int i = Array.IndexOf(cards, card);     // where the filter has put it
                if (e.ClickCount >= 2) Apply(i, (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
                else Select(i);
            };
            root.MouseEnter += delegate { hovered = card; UpdateSelectionVisuals(); };
            root.MouseLeave += delegate { if (hovered == card) hovered = null; UpdateSelectionVisuals(); };

            return card;
        }

        // ---------- Selection & animation ----------

        void Select(int index)
        {
            index = Math.Max(0, Math.Min(cards.Length - 1, index));
            if (index == selected) return;
            selected = index;
            focusIndex = index;
            UpdateSelectionVisuals();
            StartAnimation();
        }

        void UpdateSelectionVisuals()
        {
            if (batch.Cards.Count == 0) return;
            if (cards.Length == 0)
            {
                counter.Text = "0 matching";
                nameText.Text = "No wallpapers match the filter";
                return;
            }
            for (int i = 0; i < cards.Length; i++)
            {
                var c = cards[i];
                if (i == selected)
                {
                    c.Outline.Stroke = Peach;
                    c.Outline.StrokeThickness = 3;
                    Panel.SetZIndex(c.Root, 2);
                }
                else
                {
                    c.Outline.Stroke = c == hovered ? Lavender : null;
                    c.Outline.StrokeThickness = 2;
                    Panel.SetZIndex(c.Root, c == hovered ? 1 : 0);
                }
            }
            bool filtered = cards.Length < batch.Cards.Count;
            counter.Text = (selected + 1) + " / " + cards.Length + (filtered ? " matching" : "");
            nameText.Text = cards[selected].Name;
        }

        void StartAnimation()
        {
            if (animating) return;
            animating = true;
            lastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Min(0.05, now - lastFrame);
            lastFrame = now;

            if (introPending)
            {
                introPending = false;
                shownAt = now;
            }

            // Critically damped spring toward the selected card: it picks up speed and settles
            // without a jolt at either end, and never overshoots. (Exact step, stable for any dt.)
            double x = offset - selected;
            double pull = (velocity + Spring * x) * dt;
            double decay = Math.Exp(-Spring * dt);
            offset = selected + (x + pull) * decay;
            velocity = (velocity - Spring * pull) * decay;

            bool settled = Math.Abs(selected - offset) < 0.001 && Math.Abs(velocity) < 0.01;
            if (settled)
            {
                offset = selected;
                velocity = 0;
                if (now - shownAt >= IntroLength)
                {
                    animating = false;
                    CompositionTarget.Rendering -= OnFrame;
                }
            }
            Layout();
        }

        void Layout()
        {
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w <= 0) return;
            double center = w / 2, middle = h / 2;
            double intro = introPending ? 0 : clock.Elapsed.TotalSeconds - shownAt;

            // The two cards around the scroll position share the selected size (their weights sum to 1),
            // so the growth slides smoothly from card to card and the centered one stays centered.
            double pushed = -SelectedExtra / 2;
            for (int i = 0; i < cards.Length; i++)
            {
                var c = cards[i];
                double rel = i - offset, d = Math.Abs(rel);
                double near = Math.Max(0, 1 - d);
                double weight = near * near * (3 - 2 * near);   // smoothstep: eases, and still sums to 1
                double ch = CardH + (SelectedH - CardH) * weight;
                double slant = ch * SlantRatio;
                double cw = Strip + SelectedExtra * weight + slant;
                // Cards are placed by their mid-height edge, which stays put as a card (and its lean) grows
                double x = center + rel * Step - Strip / 2 + pushed - slant / 2;
                pushed += SelectedExtra * weight;
                if (x > w + 40 || x + cw < -40)
                {
                    c.Root.Visibility = Visibility.Collapsed;
                    continue;
                }
                c.Root.Visibility = Visibility.Visible;
                Canvas.SetLeft(c.Root, x);
                Canvas.SetTop(c.Root, middle - ch / 2);

                if (Math.Abs(cw - c.Width) > 0.01 || Math.Abs(ch - c.Height) > 0.01)
                {
                    c.Width = cw;
                    c.Height = ch;
                    c.Root.Width = cw;
                    c.Root.Height = ch;
                    var shape = CardShape(cw, ch);
                    c.Face.Clip = shape;
                    c.Outline.Data = shape;
                }

                // Opening: cards fade in while sliding up along their slant, the center one first
                double arrived = 1;
                if (intro < IntroLength)
                {
                    double p = (intro - Math.Min(d, IntroReach) * IntroStagger) / IntroCard;
                    p = 1 - Math.Max(0, Math.Min(1, p));
                    arrived = 1 - p * p * p;
                }
                c.Root.Opacity = arrived;
                c.Slide.Y = (1 - arrived) * IntroRise;
                c.Slide.X = -(1 - arrived) * IntroRise * SlantRatio;

                // Images lag slightly behind their cards, like a scene seen through a window
                c.Drift.X = -Parallax * Math.Tanh(rel / 2.5);
                c.Shade.Opacity = Math.Min(0.45, Math.Max(0, d - 0.6) * 0.09);
            }
        }

        // ---------- Input ----------

        // These keys drive the carousel; every other key is left for the filter box to type
        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // Wherever the focus has wandered, the character this key produces still lands in the filter
            if (!filterBox.IsKeyboardFocused) filterBox.Focus();

            switch (e.Key)
            {
                case Key.Left: Select(selected - 1); break;
                case Key.Right: Select(selected + 1); break;
                case Key.Up: case Key.PageUp: Select(selected - 5); break;
                case Key.Down: case Key.PageDown: Select(selected + 5); break;
                case Key.Home: Select(0); break;
                case Key.End: Select(cards.Length - 1); break;
                case Key.R:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
                    if (cards.Length > 1)
                    {
                        var rnd = new Random();
                        int r;
                        do { r = rnd.Next(cards.Length); } while (r == selected);
                        Select(r);
                    }
                    break;
                case Key.Enter: Apply(selected, (Keyboard.Modifiers & ModifierKeys.Shift) == 0); break;
                case Key.Escape:
                    if (filterBox.Text.Length > 0) filterBox.Clear();
                    else Dismiss();
                    break;
                case Key.Tab: break;    // would move the focus out of the filter box
                default: return;
            }
            e.Handled = true;
        }

        void OnWheel(object sender, MouseWheelEventArgs e)
        {
            // Accumulate so precision touchpads (small deltas) scroll at a sane rate
            wheelAccum += e.Delta;
            while (wheelAccum >= 120) { Select(selected - 1); wheelAccum -= 120; }
            while (wheelAccum <= -120) { Select(selected + 1); wheelAccum += 120; }
            e.Handled = true;
        }

        // ---------- Thumbnails ----------

        // Index in the batch of the not-yet-requested card nearest position "from" in the list, or -1
        static int NextToLoad(Batch b, IList<Card> list, int from)
        {
            int f = Math.Max(0, Math.Min(from, list.Count - 1));
            for (int r = 0; r < list.Count; r++)
            {
                if (f + r < list.Count && !b.Requested[list[f + r].Index]) return list[f + r].Index;
                if (f - r >= 0 && !b.Requested[list[f - r].Index]) return list[f - r].Index;
            }
            return -1;
        }

        // Always decode the not-yet-loaded image nearest the current selection, leaving the
        // cards the filter has hidden until last.
        // Stops when everything is loaded or the batch has been replaced by a rescan.
        void LoadLoop(Batch b)
        {
            while (b == batch)
            {
                int idx;
                lock (b.Lock)
                {
                    var shown = b.Shown;
                    int f = focusIndex;
                    idx = NextToLoad(b, shown, f);
                    if (idx < 0) idx = NextToLoad(b, b.Cards, f < shown.Length ? shown[f].Index : 0);
                    if (idx < 0) return;
                    b.Requested[idx] = true;
                }

                BitmapImage bmp = null;
                try
                {
                    bmp = new BitmapImage();
                    using (var fs = new FileStream(b.Cards[idx].File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        bmp.DecodePixelHeight = decodeHeight;
                        bmp.StreamSource = fs;
                        bmp.EndInit();
                    }
                    bmp.Freeze();
                }
                catch
                {
                    bmp = null; // unsupported/corrupt file: placeholder with the file name stays visible
                }

                if (bmp != null)
                {
                    var card = b.Cards[idx];
                    var img = bmp;
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        card.Image.Source = img;
                        card.Image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                    }));
                }
            }
        }

        // ---------- Applying ----------

        void Apply(int index, bool includeLockScreen)
        {
            if (index < 0 || index >= cards.Length) return;     // nothing matches the filter
            Select(index);
            Dismiss();
            // The picker is gone by the time this finishes, so only failures are worth reporting
            ApplyWallpaper(cards[index].File, includeLockScreen, msg =>
            {
                if (msg.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                    Dispatcher.BeginInvoke(new Action(() =>
                        MessageBox.Show(msg, "Wallpaper Picker", MessageBoxButton.OK, MessageBoxImage.Warning)));
            });
        }

        // Sets the wallpaper on a background thread, then reports a status message
        public static void ApplyWallpaper(string file, bool includeLockScreen, Action<string> done)
        {
            Task.Run(() =>
            {
                // SPI_SETDESKWALLPAPER, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
                bool ok = SystemParametersInfo(0x0014, 0, file, 0x01 | 0x02);
                string msg = ok ? "Desktop wallpaper set" : "Failed to set wallpaper";

                if (ok && includeLockScreen)
                    msg = SetLockScreen(file) ? "Desktop + Lock Screen set" : "Desktop set (Lock Screen failed)";

                if (done != null) done(msg);
            });
        }

        // The Lock Screen API is WinRT, which is easiest to reach from Windows PowerShell.
        static bool SetLockScreen(string file)
        {
            string script = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Set-LockScreen.ps1");
            if (!System.IO.File.Exists(script)) return false;

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script +
                "\" -ImagePath \"" + file + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    if (!p.WaitForExit(30000)) return false;
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        // Start on whatever is currently the desktop wallpaper, if it lives in this folder
        static int FindCurrentWallpaper(List<string> files)
        {
            foreach (var current in CurrentWallpaperCandidates())
            {
                if (string.IsNullOrEmpty(current)) continue;
                int i = files.FindIndex(f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
                if (i < 0)
                    i = files.FindIndex(f => string.Equals(IOPath.GetFileName(f), IOPath.GetFileName(current),
                        StringComparison.OrdinalIgnoreCase));
                if (i >= 0) return i;
            }
            return 0;
        }

        // Most reliable source first. The "WallPaper" registry value goes stale when the
        // wallpaper is set through Settings, so it's only the last resort.
        static IEnumerable<string> CurrentWallpaperCandidates()
        {
            yield return WallpaperOfMonitorUnderCursor();

            byte[] transcoded = null;
            string legacy = null;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    if (key != null)
                    {
                        transcoded = key.GetValue("TranscodedImageCache") as byte[];
                        legacy = key.GetValue("WallPaper") as string;
                    }
                }
            }
            catch { }

            // TranscodedImageCache: 24-byte header followed by the source path (UTF-16, null-terminated)
            if (transcoded != null && transcoded.Length > 24)
                yield return System.Text.Encoding.Unicode.GetString(transcoded, 24, transcoded.Length - 24).Split('\0')[0];

            yield return legacy;
        }

        static string WallpaperOfMonitorUnderCursor()
        {
            try
            {
                var dw = (IDesktopWallpaper)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")));
                try
                {
                    var pt = WinForms.Cursor.Position;
                    uint count = dw.GetMonitorDevicePathCount();
                    string first = null;
                    for (uint i = 0; i < count; i++)
                    {
                        string id = dw.GetMonitorDevicePathAt(i);
                        string path = dw.GetWallpaper(id);
                        if (first == null) first = path;
                        var r = dw.GetMonitorRECT(id);
                        if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom) return path;
                    }
                    return first;
                }
                finally { Marshal.ReleaseComObject(dw); }
            }
            catch { return null; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }

    // Windows 8+ desktop wallpaper API (only the methods we call need real signatures, but
    // every method must be declared in vtable order)
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
        uint GetMonitorDevicePathCount();
        NativeRect GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID);
    }
}
