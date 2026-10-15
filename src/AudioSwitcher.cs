// Audio Switcher - steps through audio outputs with a hotkey, with a menu for choosing which ones.
// Part of WarrenTool, which owns the tray icon and the hotkey (WarrenTool.cs).
// The Windows counterpart of Linux-Setup's Quickshell AudioMenu.qml and audio-output-toggle.sh.
//
// Ctrl+Alt+O makes the next output in the rotation the default device for everything: the
// Console, Multimedia and Communications roles alike. The tray icon shows the default output's icon.
//
// Left-click the tray icon for the menu:
//   click an output              make it the default output now
//   its switch                   include it in the Ctrl+Alt+O rotation (several can be on)
//   its icon                     choose the icon it gets, here and on the tray icon
//   click an input               make it the default input. The output that is default at the time
//                                remembers it, and brings it back whenever that output is switched to.
//   the sliders                  the default output's and input's volume. Windows keeps a level per
//                                device, so each output comes back at the level it was left at.
//   the percentages              mute / unmute
//   Esc / click elsewhere        close
//
// Kept per output in HKCU\Software\WarrenTool\Audio\Outputs\<endpoint id>:
//   Enabled   DWORD, 0 = left out of Ctrl+Alt+O. No value counts as on, so a device plugged in for
//             the first time joins the rotation rather than being silently skipped.
//   Icon      the icon key picked in the menu. No value means one is guessed (see Guess).
//   Mic       endpoint id of the input chosen while this output was the default.
// A record outlives its device, so a headset comes back as it was left. Only devices that are
// plugged in are listed: a row for an absent one would be a switch with nothing to switch.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using ScreenShot;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AudioSwitcher
{
    // ---------- Windows Core Audio (COM) ----------

    enum Flow { Render = 0, Capture = 1 }
    enum Role { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid Format;
        public int Id;
        public PropertyKey(string format, int id) { Format = new Guid(format); Id = id; }
    }

    // Only the type and the first word of the value are read; 24 bytes covers it on x86 and x64
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Value;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(Flow flow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(Flow flow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(Flow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(int channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(int channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(int channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    // Undocumented, but it is what the Sound control panel itself calls to set a default device, and
    // the only way there is to do it; its layout has been the same since Windows 7. The methods before
    // SetDefaultEndpoint are only placeholders that keep it in its slot.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int defaultFormat, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int defaultPeriod, IntPtr period, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }

    // ---------- Devices ----------

    class Endpoint
    {
        public string Id;
        public string Name;         // "Speakers (Realtek(R) Audio)"
        public int FormFactor;      // what the driver says it is: 3 headphones, 5 headset, 9 HDMI / DisplayPort, ...
        public string Bus;          // "BTHENUM", "USB", "HDAUDIO", ...
    }

    // The devices and their levels, and notice of changes to either. Both events are raised on the
    // UI thread, and coalesced: setting a default fires one change per role, and a volume drag fires
    // one per step.
    class AudioSystem : IMMNotificationClient
    {
        const int DEVICE_STATE_ACTIVE = 1, STGM_READ = 0, CLSCTX_ALL = 0x17, VT_LPWSTR = 31, VT_UI4 = 19;
        static readonly PropertyKey FriendlyName = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 14);
        static readonly PropertyKey EnumeratorName = new PropertyKey("A45C254E-DF1C-4EFD-8020-67D146A850E0", 24);
        static readonly PropertyKey FormFactor = new PropertyKey("1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E", 0);
        static Guid context = Guid.NewGuid();

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PropVariant value);

        readonly Dispatcher dispatcher;
        readonly IMMDeviceEnumerator devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        readonly IPolicyConfig policy = (IPolicyConfig)new PolicyConfigClient();
        readonly LevelWatch outputWatch, inputWatch;
        int changePending, levelsPending;

        public event Action Changed;        // a device came or went, or a default changed
        public event Action LevelsChanged;  // the default output's or input's volume or mute changed

        public AudioSystem(Dispatcher dispatcher)
        {
            this.dispatcher = dispatcher;
            outputWatch = new LevelWatch(PostLevels);
            inputWatch = new LevelWatch(PostLevels);
            Watch();
            devices.RegisterEndpointNotificationCallback(this);
        }

        public void Dispose()
        {
            devices.UnregisterEndpointNotificationCallback(this);
            outputWatch.Watch(null);
            inputWatch.Watch(null);
        }

        // Plugged-in devices in name order, which is also the order Ctrl+Alt+O steps through them
        public List<Endpoint> List(bool output)
        {
            var list = new List<Endpoint>();
            IMMDeviceCollection all;
            if (devices.EnumAudioEndpoints(output ? Flow.Render : Flow.Capture, DEVICE_STATE_ACTIVE, out all) != 0) return list;
            int count;
            all.GetCount(out count);
            for (int i = 0; i < count; i++)
            {
                IMMDevice device;
                if (all.Item(i, out device) != 0) continue;
                var e = Describe(device);
                if (e != null) list.Add(e);
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(all);
            return list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(e => e.Id).ToList();
        }

        static Endpoint Describe(IMMDevice device)
        {
            string id;
            if (device.GetId(out id) != 0) return null;
            var e = new Endpoint { Id = id, Name = id };
            IPropertyStore store;
            if (device.OpenPropertyStore(STGM_READ, out store) != 0) return e;
            e.Name = Read(store, FriendlyName) as string ?? id;
            e.Bus = Read(store, EnumeratorName) as string ?? "";
            object form = Read(store, FormFactor);
            e.FormFactor = form is int ? (int)form : -1;
            Marshal.ReleaseComObject(store);
            return e;
        }

        static object Read(IPropertyStore store, PropertyKey key)
        {
            PropVariant v;
            if (store.GetValue(ref key, out v) != 0) return null;
            try
            {
                if (v.Type == VT_LPWSTR) return Marshal.PtrToStringUni(v.Value);
                if (v.Type == VT_UI4) return (int)(v.Value.ToInt64() & 0xFFFFFFFF);
                return null;
            }
            finally { PropVariantClear(ref v); }
        }

        // The default device for the Console role, which is the one Windows calls "the default"; null for none
        public string DefaultId(bool output)
        {
            IMMDevice device;
            if (devices.GetDefaultAudioEndpoint(output ? Flow.Render : Flow.Capture, Role.Console, out device) != 0) return null;
            string id;
            int hr = device.GetId(out id);
            Marshal.ReleaseComObject(device);
            return hr == 0 ? id : null;
        }

        // Default for everything: what apps play to (Console, Multimedia) and what calls use (Communications)
        public bool SetDefault(string id)
        {
            bool ok = true;
            foreach (Role role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                ok &= policy.SetDefaultEndpoint(id, role) == 0;
            return ok;
        }

        IAudioEndpointVolume OpenVolume(string id)
        {
            IMMDevice device;
            if (id == null || devices.GetDevice(id, out device) != 0) return null;
            try
            {
                object volume;
                Guid iid = typeof(IAudioEndpointVolume).GUID;
                return device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out volume) == 0 ? (IAudioEndpointVolume)volume : null;
            }
            finally { Marshal.ReleaseComObject(device); }
        }

        public bool Level(string id, out float volume, out bool muted)
        {
            volume = 0;
            muted = false;
            var control = OpenVolume(id);
            if (control == null) return false;
            bool ok = control.GetMasterVolumeLevelScalar(out volume) == 0 & control.GetMute(out muted) == 0;
            Marshal.ReleaseComObject(control);
            return ok;
        }

        // Unmutes too: a slider that moves and makes no sound reads as a broken slider
        public void SetLevel(string id, float volume)
        {
            var control = OpenVolume(id);
            if (control == null) return;
            control.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, volume)), ref context);
            control.SetMute(false, ref context);
            Marshal.ReleaseComObject(control);
        }

        public void SetMuted(string id, bool muted)
        {
            var control = OpenVolume(id);
            if (control == null) return;
            control.SetMute(muted, ref context);
            Marshal.ReleaseComObject(control);
        }

        // Follow whichever devices are the defaults now
        void Watch()
        {
            outputWatch.Watch(OpenVolume(DefaultId(true)));
            inputWatch.Watch(OpenVolume(DefaultId(false)));
        }

        // Notifications arrive on a Core Audio thread: just hand them to the UI thread
        void PostChange()
        {
            if (Interlocked.Exchange(ref changePending, 1) != 0) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                changePending = 0;
                Watch();
                if (Changed != null) Changed();
                if (LevelsChanged != null) LevelsChanged();
            }));
        }

        void PostLevels()
        {
            if (Interlocked.Exchange(ref levelsPending, 1) != 0) return;
            dispatcher.BeginInvoke(new Action(() =>
            {
                levelsPending = 0;
                if (LevelsChanged != null) LevelsChanged();
            }));
        }

        public int OnDeviceStateChanged(string id, int state) { PostChange(); return 0; }
        public int OnDeviceAdded(string id) { PostChange(); return 0; }
        public int OnDeviceRemoved(string id) { PostChange(); return 0; }
        public int OnDefaultDeviceChanged(Flow flow, Role role, string id) { PostChange(); return 0; }
        // Fires constantly (levels, formats, jack sensing) and says nothing the menu shows
        public int OnPropertyValueChanged(string id, PropertyKey key) { return 0; }
    }

    // Volume and mute notifications from one device
    class LevelWatch : IAudioEndpointVolumeCallback
    {
        readonly Action notify;
        IAudioEndpointVolume control;

        public LevelWatch(Action notify) { this.notify = notify; }

        public void Watch(IAudioEndpointVolume next)
        {
            if (control != null)
            {
                control.UnregisterControlChangeNotify(this);
                Marshal.ReleaseComObject(control);
            }
            control = next;
            if (control != null) control.RegisterControlChangeNotify(this);
        }

        public int OnNotify(IntPtr data) { notify(); return 0; }
    }

    // ---------- What is remembered per output ----------

    static class Settings
    {
        const string Root = @"Software\WarrenTool\Audio\Outputs\";

        static object Get(string id, string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(Root + id))
                return key == null ? null : key.GetValue(name);
        }

        static void Set(string id, string name, object value, RegistryValueKind kind)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Root + id))
                key.SetValue(name, value, kind);
        }

        public static bool IsEnabled(string id)
        {
            object on = Get(id, "Enabled");
            return !(on is int) || (int)on != 0;
        }

        public static void SetEnabled(string id, bool on) { Set(id, "Enabled", on ? 1 : 0, RegistryValueKind.DWord); }

        public static string Icon(string id) { return Get(id, "Icon") as string; }

        public static void SetIcon(string id, string key) { Set(id, "Icon", key, RegistryValueKind.String); }

        public static string Mic(string id) { return Get(id, "Mic") as string; }

        public static void SetMic(string id, string inputId) { Set(id, "Mic", inputId, RegistryValueKind.String); }
    }

    // ---------- Icons ----------

    class IconChoice
    {
        public readonly string Key, Label;
        readonly int nerd, fluent;

        public IconChoice(string key, string label, int nerd, int fluent)
        {
            Key = key;
            Label = label;
            this.nerd = nerd;
            this.fluent = fluent;
        }

        public string Glyph { get { return Glyphs.Of(nerd, fluent); } }
    }

    // The Linux menu's Material Design Icons, out of JetBrainsMono Nerd Font. Without that font
    // installed, the nearest Segoe Fluent Icons (Windows' own icon font) stand in.
    static class Glyphs
    {
        static readonly bool Nerd = HasNerdFont();
        public static readonly FontFamily Font = Nerd
            ? new FontFamily("JetBrainsMono NF")
            : new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        // In the order the menu's palette draws them. "volume" is the default, and the only one that
        // is not a fixed glyph: on the tray icon it follows the level.
        public static readonly IconChoice[] Choices =
        {
            new IconChoice("volume", "Volume", 0xF057E, 0xE767),
            new IconChoice("speaker", "Speakers", 0xF04C3, 0xE7F5),
            new IconChoice("headphones", "Headphones", 0xF02CB, 0xE7F6),
            new IconChoice("earbuds", "Earbuds", 0xF184F, 0xE95B),
            new IconChoice("bluetooth", "Bluetooth", 0xF00B1, 0xE702),
            new IconChoice("display", "Display", 0xF0F5F, 0xE7F3),
            new IconChoice("tv", "TV", 0xF0502, 0xE7F4),
        };

        static bool HasNerdFont()
        {
            GlyphTypeface face;
            return new Typeface("JetBrainsMono NF").TryGetGlyphTypeface(out face) && face.CharacterToGlyphMap.ContainsKey(0xF057E);
        }

        public static string Of(int nerd, int fluent) { return char.ConvertFromUtf32(Nerd ? nerd : fluent); }

        public static IconChoice Choice(string key)
        {
            return Choices.FirstOrDefault(c => c.Key == key) ?? Choices[0];
        }

        public static string Mic(bool muted) { return muted ? Of(0xF036D, 0xF781) : Of(0xF036C, 0xE720); }

        // What the tray icon and the switch popup draw for an output: a muted output is muted whatever
        // its icon, and "volume" follows the level
        public static string ForLevel(string key, float volume, bool muted)
        {
            if (muted) return Of(0xF075F, 0xE74F);
            if (key != "volume") return Choice(key).Glyph;
            if (volume <= 0.01) return Of(0xF0581, 0xE992);
            if (volume < 0.34) return Of(0xF057F, 0xE993);
            if (volume < 0.67) return Of(0xF0580, 0xE994);
            return Of(0xF057E, 0xE995);
        }

        // A glyph alone on a transparent square, scaled to fill it, as a tray icon
        public static Drawing.Icon TrayIcon(string glyph, Color color, int size)
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 100, Brushes.Black, 1.0);
            Geometry shape = text.BuildGeometry(new Point(0, 0));
            Rect ink = shape.Bounds;
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                if (!ink.IsEmpty && ink.Width > 0 && ink.Height > 0)
                {
                    double fit = (size - 1) / Math.Max(ink.Width, ink.Height);
                    dc.PushTransform(new TranslateTransform(size / 2.0, size / 2.0));
                    dc.PushTransform(new ScaleTransform(fit, fit));
                    dc.PushTransform(new TranslateTransform(-(ink.X + ink.Width / 2), -(ink.Y + ink.Height / 2)));
                    dc.DrawGeometry(new SolidColorBrush(color), null, shape);
                }
            }
            var render = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            render.Render(visual);
            var pixels = new byte[size * size * 4];
            new FormatConvertedBitmap(render, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);

            // A one-image .ico holding a 32-bit DIB (bottom-up rows, then an unused AND mask), as Install.ps1 writes
            int maskStride = (size + 31) / 32 * 4;
            var ico = new MemoryStream();
            var w = new BinaryWriter(ico);
            w.Write((short)0); w.Write((short)1); w.Write((short)1);
            w.Write((byte)size); w.Write((byte)size); w.Write((short)0); w.Write((short)1); w.Write((short)32);
            w.Write(40 + pixels.Length + maskStride * size); w.Write(22);
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32); w.Write(new byte[24]);
            for (int y = size - 1; y >= 0; y--) w.Write(pixels, y * size * 4, size * 4);
            w.Write(new byte[maskStride * size]);
            w.Flush();
            ico.Position = 0;
            return new Drawing.Icon(ico);
        }
    }

    // ---------- Catppuccin Mocha, as in the Quickshell menu ----------

    static class Mocha
    {
        public static readonly Brush Base = Solid(0x1E1E2E), Mantle = Solid(0x181825), Crust = Solid(0x11111B);
        public static readonly Brush Surface0 = Solid(0x313244), Surface1 = Solid(0x45475A), Surface2 = Solid(0x585B70);
        public static readonly Brush Overlay0 = Solid(0x6C7086), Subtext0 = Solid(0xA6ADC8), Text = Solid(0xCDD6F4);
        public static readonly Brush Lavender = Solid(0xB4BEFE), Blue = Solid(0x89B4FA), Green = Solid(0xA6E3A1), Red = Solid(0xF38BA8);
        public static readonly Color Maroon = Color.FromRgb(0xEB, 0xA0, 0xAC);   // the Linux bar's audio pill
        public static readonly Color LatteMaroon = Color.FromRgb(0xE6, 0x45, 0x53); // the same, on a light taskbar
        public static readonly FontFamily UiFont = new FontFamily("JetBrainsMono NF, JetBrains Mono, Cascadia Mono, Consolas");
        public const double FontSize = 12;

        static Brush Solid(int rgb)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Label(string text, Brush color, double size)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = color,
                FontSize = size,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
        }

        public static TextBlock Glyph(string glyph, Brush color)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = Glyphs.Font,
                Foreground = color,
                FontSize = FontSize,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        // Background follows the mouse: hover when over it, rest otherwise
        public static void Hover(Border b, Func<Brush> rest, Brush hover)
        {
            b.Background = rest();
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = rest(); };
        }

        public static ToolTip Tip(string text)
        {
            return new ToolTip
            {
                Content = text,
                Background = Crust,
                Foreground = Text,
                BorderBrush = Surface1,
                FontFamily = UiFont,
                FontSize = FontSize - 1,
                Padding = new Thickness(8, 4, 8, 4),
            };
        }
    }

    // Moves a window that is already shown (and sized to its content) to a spot worked out in physical
    // pixels, from its size and the monitor's work area
    static class Placement
    {
        const uint Flags = Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;

        public static void Move(Window window, WinForms.Screen screen, Func<int, int, Drawing.Point> where, bool firstOntoScreen)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (firstOntoScreen)
            {
                // Onto the monitor first, so WPF has rescaled the window for that monitor's DPI before it is measured
                Native.SetWindowPos(hwnd, IntPtr.Zero, screen.WorkingArea.Left, screen.WorkingArea.Top, 0, 0, Flags);
                window.UpdateLayout();
            }
            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return;
            Drawing.Point p = where(r.Right - r.Left, r.Bottom - r.Top);
            Native.SetWindowPos(hwnd, IntPtr.Zero, p.X, p.Y, 0, 0, Flags);
        }

        public static int Clamp(int value, int low, int high) { return Math.Max(low, Math.Min(value, high)); }
    }

    // ---------- The switcher: tray icon, hotkey and menu share this ----------

    public class Switcher
    {
        readonly AudioSystem audio;
        readonly Action<string> warn;
        AudioMenu menu;
        SwitchPopup popup;
        List<Endpoint> outputs;
        string trayArt;     // what the tray icon was last drawn as: glyph and color
        readonly UserPreferenceChangedEventHandler themeChanged;

        public Drawing.Icon Icon { get; private set; }
        public string Status { get; private set; }
        public event Action IconChanged;

        public Switcher(Action<string> warn)
        {
            this.warn = warn;
            audio = new AudioSystem(Dispatcher.CurrentDispatcher);
            outputs = audio.List(true);
            audio.Changed += delegate
            {
                outputs = audio.List(true);
                if (menu != null && menu.IsVisible) menu.Rebuild();
            };
            audio.LevelsChanged += delegate
            {
                UpdateTray();
                if (menu != null && menu.IsVisible) menu.RefreshLevels();
            };
            // Windows' light and dark taskbars need different colors; follow a switch between them
            var dispatcher = Dispatcher.CurrentDispatcher;
            themeChanged = delegate { dispatcher.BeginInvoke(new Action(UpdateTray)); };
            SystemEvents.UserPreferenceChanged += themeChanged;
            UpdateTray();
        }

        internal AudioSystem Audio { get { return audio; } }

        public void Dispose()
        {
            SystemEvents.UserPreferenceChanged -= themeChanged;
            audio.Dispose();
        }

        // ---------- Ctrl+Alt+O ----------

        // The next output in the rotation after the default one, in menu order. Not finding the default
        // in it (switched off, or gone) lands on the first. With every output switched off, all of them
        // cycle: a dead hotkey is a worse answer than ignoring the switches for one press.
        public void Next()
        {
            var all = audio.List(true);
            if (all.Count == 0) { warn("No audio outputs are plugged in."); return; }
            var candidates = all.Where(e => Settings.IsEnabled(e.Id)).ToList();
            if (candidates.Count == 0) candidates = all;
            string current = audio.DefaultId(true);
            var next = candidates[(candidates.FindIndex(e => e.Id == current) + 1) % candidates.Count];
            if (!MakeDefaultOutput(next.Id)) return;

            float volume;
            bool muted;
            audio.Level(next.Id, out volume, out muted);
            if (popup == null) popup = new SwitchPopup();
            popup.Flash(Glyphs.ForLevel(IconKey(next), volume, muted), next.Name, muted ? "muted" : Percent(volume));
        }

        // ---------- Used by the menu ----------

        // Makes an output the default for everything, and brings back the input picked alongside it
        internal bool MakeDefaultOutput(string id)
        {
            if (!audio.SetDefault(id))
            {
                warn("Windows would not switch the audio output.");
                return false;
            }
            string mic = Settings.Mic(id);
            if (mic != null && mic != audio.DefaultId(false) && audio.List(false).Any(e => e.Id == mic))
                audio.SetDefault(mic);
            return true;
        }

        // Makes an input the default, and remembers it for the output that is default right now
        internal void MakeDefaultInput(string id)
        {
            if (!audio.SetDefault(id)) { warn("Windows would not switch the audio input."); return; }
            string output = audio.DefaultId(true);
            if (output != null) Settings.SetMic(output, id);
        }

        internal void SetIcon(string outputId, string key)
        {
            Settings.SetIcon(outputId, key);
            UpdateTray();
        }

        internal List<Endpoint> Outputs { get { return outputs; } }

        public void ToggleMenu()
        {
            if (menu == null) menu = new AudioMenu(this);
            // A recent hide counts as "was open": the click on the tray icon that got here also
            // deactivated the menu, which hides it, before this runs
            if (menu.IsVisible || menu.JustHidden) menu.Dismiss();
            else menu.Open(WinForms.Cursor.Position);
        }

        // An icon picked in the menu, or failing that what the device says it is. Headphones-versus-
        // speakers is not something to go on (USB DACs and front jacks all call themselves speakers),
        // so that, the distinction the menu exists to let you make, is left as "volume".
        internal static string IconKey(Endpoint e)
        {
            return Settings.Icon(e.Id) ?? Guess(e);
        }

        static string Guess(Endpoint e)
        {
            if (e.Bus.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) return "bluetooth";
            if (e.FormFactor == 3 || e.FormFactor == 5) return "headphones";
            if (e.FormFactor == 9) return "display";
            return "volume";
        }

        internal static string Percent(float volume) { return (int)Math.Round(volume * 100) + "%"; }

        // ---------- Tray icon ----------

        void UpdateTray()
        {
            string id = audio.DefaultId(true);
            Endpoint e = outputs.FirstOrDefault(o => o.Id == id);
            float volume = 0;
            bool muted = false;
            string glyph, status;
            if (e == null)
            {
                glyph = Glyphs.ForLevel("volume", 0, false);
                status = "No audio output";
            }
            else
            {
                audio.Level(id, out volume, out muted);
                glyph = Glyphs.ForLevel(IconKey(e), volume, muted);
                status = Trim(e.Name, 50) + "\n" + (muted ? "Muted" : "Volume " + Percent(volume));
            }
            Color color = LightTaskbar() ? Mocha.LatteMaroon : Mocha.Maroon;
            string art = glyph + color;
            bool changed = art != trayArt || status != Status;
            Status = status;
            if (art != trayArt)
            {
                trayArt = art;
                Drawing.Icon old = Icon;
                Icon = Glyphs.TrayIcon(glyph, color, WinForms.SystemInformation.SmallIconSize.Width);
                if (IconChanged != null) IconChanged();
                if (old != null) old.Dispose();
            }
            else if (changed && IconChanged != null) IconChanged();
        }

        // Windows 11's default theme has a light taskbar, where the Mocha maroon is too pale to see
        static bool LightTaskbar()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object light = key == null ? null : key.GetValue("SystemUsesLightTheme");
                return light is int && (int)light != 0;
            }
        }

        static string Trim(string s, int max) { return s.Length <= max ? s : s.Substring(0, max - 1) + "…"; }
    }

    // ---------- The menu ----------

    // Left-click on the tray icon. Laid out as the Quickshell AudioMenu: outputs carry a switch, not a
    // radio button, because more than one can be in the Ctrl+Alt+O rotation; clicking the row itself
    // makes that output the default. Inputs carry a radio button, as there is only one default input.
    class AudioMenu : Window
    {
        const double Pad = 10;

        readonly Switcher owner;
        readonly AudioSystem audio;
        readonly StackPanel outputRows = new StackPanel();
        readonly StackPanel inputRows = new StackPanel();
        readonly TextBlock countText;
        readonly LevelSlider outputSlider = new LevelSlider(), inputSlider = new LevelSlider();
        readonly TextBlock outputPercent, inputPercent;
        readonly Border frame;
        readonly TranslateTransform rise = new TranslateTransform();
        readonly List<Action> micRefreshers = new List<Action>();
        string iconPickerFor = "";      // the output whose icon palette is open, by id
        Drawing.Point anchor;
        WinForms.Screen screen;
        DateTime hiddenAt = DateTime.MinValue;

        public AudioMenu(Switcher owner)
        {
            this.owner = owner;
            audio = owner.Audio;
            Title = "Audio";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Mocha.UiFont;
            FontSize = Mocha.FontSize;
            Foreground = Mocha.Text;

            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = "Audio", Foreground = Mocha.Lavender, FontWeight = FontWeights.Bold });
            body.Children.Add(Rule(5));

            countText = Mocha.Label("", Mocha.Overlay0, Mocha.FontSize - 3);
            outputPercent = PercentText(true);
            body.Children.Add(Header("Output", countText, outputSlider, outputPercent, 5));
            body.Children.Add(outputRows);

            inputPercent = PercentText(false);
            body.Children.Add(Header("Input", null, inputSlider, inputPercent, 8));
            body.Children.Add(inputRows);

            body.Children.Add(Rule(5));
            var settings = Mocha.Label("Sound settings…", Mocha.Subtext0, Mocha.FontSize - 2);
            settings.Margin = new Thickness(0, 5, 0, 0);
            settings.HorizontalAlignment = HorizontalAlignment.Left;
            settings.Cursor = Cursors.Hand;
            settings.MouseEnter += delegate { settings.Foreground = Mocha.Lavender; };
            settings.MouseLeave += delegate { settings.Foreground = Mocha.Subtext0; };
            settings.MouseLeftButtonUp += delegate
            {
                Dismiss();
                try { Process.Start("ms-settings:sound"); } catch { Process.Start("control.exe", "mmsys.cpl"); }
            };
            body.Children.Add(settings);

            outputSlider.Moved += v => SetLevel(true, v);
            inputSlider.Moved += v => SetLevel(false, v);

            // Wider than most menus: device names run long, and a row has to fit a switch beside one
            frame = new Border
            {
                Width = 360,
                Background = Mocha.Base,
                BorderBrush = Mocha.Surface1,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(Pad),
                Child = body,
                RenderTransform = rise,
            };
            Content = frame;

            Deactivated += delegate { Dismiss(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Dismiss(); };
            // The icon palette opening or closing changes the height: keep the menu where it was anchored
            SizeChanged += delegate { if (IsVisible) Place(false); };
        }

        public bool JustHidden { get { return (DateTime.UtcNow - hiddenAt).TotalMilliseconds < 300; } }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true;
            Dismiss();
        }

        public void Dismiss()
        {
            if (!IsVisible) return;
            iconPickerFor = "";
            Hide();
            hiddenAt = DateTime.UtcNow;
        }

        // Opens beside the tray icon that was clicked (where the mouse is), inside the work area
        public void Open(Drawing.Point at)
        {
            anchor = at;
            screen = WinForms.Screen.FromPoint(at);
            Rebuild();
            Opacity = 0;
            Show();
            Place(true);
            Activate();
            Focus();
            // Rise out of the taskbar, as the Linux menu drops out of the bar
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            rise.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(85)));
        }

        void Place(bool firstOntoScreen)
        {
            if (screen == null) return;
            Placement.Move(this, screen, (w, h) =>
            {
                var area = screen.WorkingArea;
                int gap = (int)(10 * Native.ScaleOf(screen));
                return new Drawing.Point(
                    Placement.Clamp(anchor.X - w / 2, area.Left + gap, area.Right - w - gap),
                    Placement.Clamp(anchor.Y - h - gap, area.Top + gap, area.Bottom - h - gap));
            }, firstOntoScreen);
        }

        // ---------- Contents ----------

        public void Rebuild()
        {
            string defaultOut = audio.DefaultId(true), defaultIn = audio.DefaultId(false);

            outputRows.Children.Clear();
            var outputs = owner.Outputs;
            foreach (var e in outputs) outputRows.Children.Add(OutputItem(e, e.Id == defaultOut));
            if (outputs.Count == 0) outputRows.Children.Add(Empty("No outputs"));
            UpdateCount();

            inputRows.Children.Clear();
            micRefreshers.Clear();
            var inputs = audio.List(false);
            foreach (var e in inputs) inputRows.Children.Add(InputRow(e, e.Id == defaultIn));
            if (inputs.Count == 0) inputRows.Children.Add(Empty("No inputs"));

            RefreshLevels();
        }

        void UpdateCount()
        {
            countText.Text = owner.Outputs.Count(e => Settings.IsEnabled(e.Id)) + " in Ctrl+Alt+O";
        }

        // The sliders and percentages, and the inputs' mute glyphs
        public void RefreshLevels()
        {
            ShowLevel(outputSlider, outputPercent, audio.DefaultId(true));
            ShowLevel(inputSlider, inputPercent, audio.DefaultId(false));
            foreach (var refresh in micRefreshers) refresh();
        }

        void ShowLevel(LevelSlider slider, TextBlock percent, string id)
        {
            float volume;
            bool muted;
            bool known = audio.Level(id, out volume, out muted);
            if (!slider.Dragging) slider.Show(volume, muted, known);
            percent.Text = !known ? "—" : muted ? "muted" : Switcher.Percent(volume);
            percent.Foreground = !known ? Mocha.Overlay0 : muted ? Mocha.Red : Mocha.Green;
        }

        void SetLevel(bool output, double volume)
        {
            string id = audio.DefaultId(output);
            if (id == null) return;
            audio.SetLevel(id, (float)volume);
            (output ? outputSlider : inputSlider).Show(volume, false, true);
            RefreshLevels();
        }

        static Border Rule(double above)
        {
            return new Border { Height = 1, Background = Mocha.Surface1, Margin = new Thickness(0, above, 0, 0) };
        }

        static TextBlock Empty(string text)
        {
            var t = Mocha.Label(text, Mocha.Subtext0, Mocha.FontSize);
            t.Margin = new Thickness(0, 5, 0, 0);
            return t;
        }

        // The level of the default device, and the mute toggle for it
        TextBlock PercentText(bool output)
        {
            var t = Mocha.Label("—", Mocha.Overlay0, Mocha.FontSize - 3);
            t.Width = 42;
            t.TextAlignment = TextAlignment.Right;
            t.Cursor = Cursors.Hand;
            t.MouseLeftButtonUp += delegate
            {
                string id = audio.DefaultId(output);
                float volume;
                bool muted;
                if (id == null || !audio.Level(id, out volume, out muted)) return;
                audio.SetMuted(id, !muted);
                RefreshLevels();
            };
            return t;
        }

        // "Output  2 in Ctrl+Alt+O ......... [slider] 28%"
        static UIElement Header(string title, TextBlock note, LevelSlider slider, TextBlock percent, double above)
        {
            var row = new DockPanel { Margin = new Thickness(0, above, 0, 0), LastChildFill = false };
            row.Children.Add(Mocha.Label(title, Mocha.Subtext0, Mocha.FontSize - 3));
            if (note != null)
            {
                note.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(note);
            }
            DockPanel.SetDock(percent, Dock.Right);
            DockPanel.SetDock(slider, Dock.Right);
            row.Children.Add(percent);
            row.Children.Add(slider);
            return row;
        }

        // An output's row, with its icon palette underneath when that is open
        UIElement OutputItem(Endpoint e, bool isDefault)
        {
            var item = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            string key = Switcher.IconKey(e);
            bool picking = iconPickerFor == e.Id;

            var row = new Border { Height = 26, CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand };
            Mocha.Hover(row, () => Brushes.Transparent, Mocha.Surface0);
            row.MouseLeftButtonUp += delegate { owner.MakeDefaultOutput(e.Id); };

            // The glyph doubles as the button for this output's icon palette: the thing you click is
            // the thing you are changing
            var iconButton = new Border
            {
                Width = 22,
                Height = 20,
                CornerRadius = new CornerRadius(5),
                Child = Mocha.Glyph(Glyphs.Choice(key).Glyph, isDefault ? Mocha.Green : Mocha.Subtext0),
                ToolTip = Mocha.Tip("Choose icon"),
            };
            Mocha.Hover(iconButton, () => picking ? Mocha.Surface1 : Brushes.Transparent, Mocha.Surface2);
            iconButton.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs a)
            {
                a.Handled = true;
                iconPickerFor = picking ? "" : e.Id;
                Rebuild();
            };

            var name = Mocha.Label(e.Name, isDefault ? Mocha.Green : Mocha.Text, Mocha.FontSize);
            name.Margin = new Thickness(8, 0, 8, 0);
            name.ToolTip = Mocha.Tip(e.Name);
            ToolTipService.SetInitialShowDelay(name, 800);

            var toggle = Switch(Settings.IsEnabled(e.Id), on =>
            {
                Settings.SetEnabled(e.Id, on);
                UpdateCount();
            });
            toggle.ToolTip = Mocha.Tip("Include in Ctrl+Alt+O");

            var line = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
            DockPanel.SetDock(iconButton, Dock.Left);
            DockPanel.SetDock(toggle, Dock.Right);
            line.Children.Add(iconButton);
            line.Children.Add(toggle);
            line.Children.Add(name);
            row.Child = line;
            item.Children.Add(row);

            if (picking) item.Children.Add(IconPalette(e, key));
            return item;
        }

        // Which icon an output gets can't be worked out reliably from the device, so it is asked once
        // and remembered
        UIElement IconPalette(Endpoint e, string current)
        {
            var choices = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (var choice in Glyphs.Choices)
            {
                var c = choice;
                bool picked = c.Key == current;
                var cell = new Border
                {
                    Width = 30,
                    Height = 22,
                    Margin = new Thickness(2, 0, 2, 0),
                    CornerRadius = new CornerRadius(5),
                    Cursor = Cursors.Hand,
                    Child = Mocha.Glyph(c.Glyph, picked ? Mocha.Blue : Mocha.Subtext0),
                    ToolTip = Mocha.Tip(c.Label),
                };
                Mocha.Hover(cell, () => picked ? Mocha.Surface1 : Brushes.Transparent, picked ? Mocha.Surface1 : Mocha.Surface0);
                cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs a)
                {
                    a.Handled = true;
                    owner.SetIcon(e.Id, c.Key);
                    iconPickerFor = "";
                    Rebuild();
                };
                choices.Children.Add(cell);
            }
            return new Border
            {
                Height = 30,
                Margin = new Thickness(0, 2, 0, 2),
                CornerRadius = new CornerRadius(6),
                Background = Mocha.Mantle,
                Child = choices,
            };
        }

        UIElement InputRow(Endpoint e, bool isDefault)
        {
            var row = new Border { Height = 26, Margin = new Thickness(0, 5, 0, 0), CornerRadius = new CornerRadius(6), Cursor = Cursors.Hand };
            Mocha.Hover(row, () => Brushes.Transparent, Mocha.Surface0);
            row.MouseLeftButtonUp += delegate
            {
                owner.MakeDefaultInput(e.Id);
            };

            // Only one input can be the default, so a radio button rather than the outputs' switch
            var radio = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = isDefault ? Mocha.Blue : Mocha.Surface1,
                BorderBrush = isDefault ? Mocha.Blue : Mocha.Overlay0,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = isDefault ? new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Mocha.Crust } : null,
            };

            var name = Mocha.Label(e.Name, isDefault ? Mocha.Green : Mocha.Text, Mocha.FontSize);
            name.Margin = new Thickness(8, 0, 8, 0);
            name.ToolTip = Mocha.Tip(e.Name);
            ToolTipService.SetInitialShowDelay(name, 800);

            // This input's mute
            var glyph = Mocha.Glyph("", Mocha.Subtext0);
            var mute = new Border { Width = 22, Height = 20, CornerRadius = new CornerRadius(5), Child = glyph };
            Mocha.Hover(mute, () => Brushes.Transparent, Mocha.Surface1);
            Action refresh = delegate
            {
                float volume;
                bool muted;
                audio.Level(e.Id, out volume, out muted);
                glyph.Text = Glyphs.Mic(muted);
                glyph.Foreground = muted ? Mocha.Red : isDefault ? Mocha.Green : Mocha.Subtext0;
                mute.ToolTip = Mocha.Tip(muted ? "Unmute" : "Mute");
            };
            refresh();
            micRefreshers.Add(refresh);
            mute.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs a)
            {
                a.Handled = true;
                float volume;
                bool muted;
                if (audio.Level(e.Id, out volume, out muted)) audio.SetMuted(e.Id, !muted);
                RefreshLevels();
            };

            var line = new DockPanel { Margin = new Thickness(6, 0, 6, 0) };
            DockPanel.SetDock(radio, Dock.Left);
            DockPanel.SetDock(mute, Dock.Right);
            line.Children.Add(radio);
            line.Children.Add(mute);
            line.Children.Add(name);
            row.Child = line;
            return row;
        }

        // Whether an output is in the Ctrl+Alt+O rotation
        static FrameworkElement Switch(bool on, Action<bool> changed)
        {
            var slide = new TranslateTransform(on ? 14 : 0, 0);
            var knob = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(2, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = slide,
            };
            var track = new Border { Width = 30, Height = 16, CornerRadius = new CornerRadius(8), Child = knob };
            Action paint = delegate
            {
                track.Background = on ? Mocha.Blue : Mocha.Surface1;
                knob.Background = on ? Mocha.Crust : Mocha.Text;
            };
            paint();

            // A few pixels of slack around it, so it is easy to hit without changing the default output
            var hit = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(4),
                Margin = new Thickness(-4),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Child = track,
            };
            hit.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs a)
            {
                a.Handled = true;
                on = !on;
                paint();
                var move = new DoubleAnimation(on ? 14 : 0, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                slide.BeginAnimation(TranslateTransform.XProperty, move);
                changed(on);
            };
            return hit;
        }
    }

    // The volume track in the menu's Output and Input headers: drag, click or scroll (1% a notch)
    class LevelSlider : Grid
    {
        const double W = 110, Handle = 12, Span = W - Handle;

        readonly Border fill, knob;
        readonly ScaleTransform grow = new ScaleTransform(1, 1);
        double value;
        bool known;

        public event Action<double> Moved;

        public LevelSlider()
        {
            Width = W;
            Height = 18;
            Background = Brushes.Transparent;   // the whole strip takes the mouse, not just the track
            Cursor = Cursors.Hand;

            var track = new Grid { Height = 4, Margin = new Thickness(Handle / 2, 0, Handle / 2, 0), VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = Mocha.Surface1 });
            fill = new Border { CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            track.Children.Add(fill);
            knob = new Border
            {
                Width = Handle,
                Height = Handle,
                CornerRadius = new CornerRadius(Handle / 2),
                BorderBrush = Mocha.Crust,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = grow,
            };
            Children.Add(track);
            Children.Add(knob);
            Show(0, false, false);

            MouseEnter += delegate { Grow(); };
            MouseLeave += delegate { Grow(); };
        }

        public bool Dragging { get { return IsMouseCaptured; } }

        public void Show(double level, bool muted, bool available)
        {
            value = Math.Max(0, Math.Min(1, level));
            known = available;
            Brush accent = muted ? Mocha.Overlay0 : Mocha.Blue;
            fill.Background = accent;
            knob.Background = accent;
            fill.Width = Span * value;
            knob.Margin = new Thickness(Span * value, 0, 0, 0);
            // Drawn empty rather than hidden while there is no device, so the header keeps its shape
            Opacity = known ? 1 : 0.5;
        }

        void Grow()
        {
            double to = IsMouseCaptured ? 1.25 : IsMouseOver ? 1.15 : 1;
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(90)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            grow.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            grow.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void MoveTo(double level)
        {
            if (known && Moved != null) Moved(Math.Max(0, Math.Min(1, level)));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (!known) return;
            e.Handled = true;
            CaptureMouse();
            Grow();
            MoveTo((e.GetPosition(this).X - Handle / 2) / Span);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured) MoveTo((e.GetPosition(this).X - Handle / 2) / Span);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!IsMouseCaptured) return;
            e.Handled = true;
            ReleaseMouseCapture();
            Grow();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            MoveTo(value + 0.01 * e.Delta / 120);
        }
    }

    // ---------- The popup after Ctrl+Alt+O ----------

    // Which output Ctrl+Alt+O landed on, at the bottom of the screen for a moment, the way the Linux
    // script's notification does. It never takes the focus or the mouse.
    class SwitchPopup : Window
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;

        [DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        readonly TextBlock glyph, name, level;
        readonly DispatcherTimer linger = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };

        public SwitchPopup()
        {
            Title = "Audio output";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Mocha.UiFont;
            FontSize = Mocha.FontSize + 1;

            glyph = Mocha.Glyph("", Mocha.Green);
            glyph.FontSize = 18;
            glyph.Margin = new Thickness(0, 0, 12, 0);
            name = Mocha.Label("", Mocha.Text, FontSize);
            name.MaxWidth = 420;
            level = Mocha.Label("", Mocha.Subtext0, FontSize);
            level.Margin = new Thickness(12, 0, 0, 0);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(glyph);
            line.Children.Add(name);
            line.Children.Add(level);
            Content = new Border
            {
                Background = Mocha.Base,
                BorderBrush = Mocha.Surface1,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 10, 18, 10),
                Child = line,
            };

            SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            };
            linger.Tick += delegate
            {
                linger.Stop();
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                fade.Completed += delegate { if (!linger.IsEnabled) Hide(); };
                BeginAnimation(OpacityProperty, fade);
            };
        }

        public void Flash(string icon, string device, string volume)
        {
            glyph.Text = icon;
            name.Text = device;
            level.Text = volume;
            bool showing = IsVisible && Opacity > 0.5;
            if (!IsVisible) { Opacity = 0; Show(); }

            // Bottom middle of the monitor the mouse is on, just above the taskbar
            var screen = WinForms.Screen.FromPoint(WinForms.Cursor.Position);
            Placement.Move(this, screen, (w, h) =>
            {
                var area = screen.WorkingArea;
                return new Drawing.Point(area.Left + (area.Width - w) / 2, area.Bottom - h - (int)(48 * Native.ScaleOf(screen)));
            }, true);

            BeginAnimation(OpacityProperty, showing ? null : new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            if (showing) Opacity = 1;
            linger.Stop();
            linger.Start();
        }
    }
}
