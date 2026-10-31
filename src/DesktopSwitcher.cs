// Desktop Switcher - steps through Windows' virtual desktops with a hotkey.
// Part of WarrenTool, which owns the tray icon and the hotkey (WarrenTool.cs).
//
// Ctrl+Alt+R switches to the next desktop in Task View's order, from the last one back round to the
// first, with the same slide as Ctrl+Win+Right. With only one desktop it does nothing.
//
// It works with a RustDesk session in front too, and switches this PC's desktop, not the remote one's.
// RustDesk hooks the keyboard while its window is focused and sends every key to the remote PC, and a
// hook sees keys before Windows looks for hotkeys, so a registered Ctrl+Alt+R never fires there.
// Ctrl+Alt+R therefore has a keyboard hook of its own (KeyHook), which Windows asks first because it
// was installed last: it is put back at the front of the line every second, so it stays ahead of a
// RustDesk window opened later. It takes the R and leaves Ctrl and Alt alone, as everything else
// that uses them needs them. The registered hotkey stays as well, for the windows a hook isn't told
// about (an app running as administrator).
//
// Switching uses Explorer's own virtual desktop interface, the one Task View uses. Microsoft doesn't
// document it and changes it between Windows versions; these declarations are Windows 11 24H2's and
// 25H2's. If an update changes it again, Ctrl+Alt+R shows a tray notice instead of switching.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace DesktopSwitcher
{
    // ---------- Explorer's virtual desktops (COM) ----------

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object QueryService(ref Guid service, ref Guid iid);
    }

    [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IObjectArray
    {
        int GetCount();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetAt(int index, ref Guid iid);
    }

    [ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktop
    {
        bool IsViewVisible(IntPtr view);
        Guid GetId();
    }

    // Only the methods we call need real signatures, but every method up to the last of them must be
    // declared, in order, to keep it in its slot
    [ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IntPtr desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IVirtualDesktop GetCurrentDesktop();
        IObjectArray GetDesktops();
        void GetAdjacentDesktop(IntPtr from, int direction, IntPtr desktop);
        void SwitchDesktop(IVirtualDesktop desktop);
        void SwitchDesktopAndMoveForegroundView(IntPtr desktop);
        void CreateDesktop(IntPtr desktop);
        void MoveDesktop(IntPtr desktop, int index);
        void RemoveDesktop(IntPtr desktop, IntPtr fallback);
        void FindDesktop(IntPtr id, IntPtr desktop);
        void GetDesktopSwitchIncludeExcludeViews(IntPtr desktop, IntPtr include, IntPtr exclude);
        void SetDesktopName(IntPtr desktop, IntPtr name);
        void SetDesktopWallpaper(IntPtr desktop, IntPtr path);
        void UpdateWallpaperPathForAllDesktops(IntPtr path);
        void CopyDesktopState(IntPtr view0, IntPtr view1);
        void CreateRemoteDesktop(IntPtr path, IntPtr desktop);
        void SwitchRemoteDesktop(IntPtr desktop, IntPtr switchType);
        void SwitchDesktopWithAnimation(IVirtualDesktop desktop);
    }

    // ---------- Ctrl+Alt+R ----------

    public class Desktops
    {
        static readonly Guid ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid DesktopManagerService = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

        [DllImport("user32.dll")]
        static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId);

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint thread, uint to, bool attach);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        readonly Action<string> warn;

        public Desktops(Action<string> warn)
        {
            this.warn = warn;
            var dispatcher = Dispatcher.CurrentDispatcher;
            // Its thread keeps it running
            new KeyHook(delegate { dispatcher.BeginInvoke(new Action(Next)); });
        }

        public void Next()
        {
            try
            {
                var shell = (IShellServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(ImmersiveShell));
                Guid service = DesktopManagerService, iid = typeof(IVirtualDesktopManagerInternal).GUID;
                var manager = (IVirtualDesktopManagerInternal)shell.QueryService(ref service, ref iid);

                var desktops = manager.GetDesktops();
                int count = desktops.GetCount();
                if (count < 2) return;
                Guid current = manager.GetCurrentDesktop().GetId(), desktopIid = typeof(IVirtualDesktop).GUID;
                int index = 0;
                for (int i = 0; i < count; i++)
                    if (((IVirtualDesktop)desktops.GetAt(i, ref desktopIid)).GetId() == current) index = i;
                var next = (IVirtualDesktop)desktops.GetAt((index + 1) % count, ref desktopIid);

                FocusShell();
                manager.SwitchDesktopWithAnimation(next);
            }
            // A missing interface comes back as either, depending on where it's found missing
            catch (COMException) { warn("Windows would not switch the desktop. A Windows update may have changed how it's done."); }
            catch (InvalidCastException) { warn("Windows would not switch the desktop. A Windows update may have changed how it's done."); }
        }

        // Switching desktops leaves the keyboard focus where it was: on a window that is now out of sight,
        // and with RustDesk, a session that goes on taking every key. Explorer moves the focus to the new
        // desktop's last window itself when its own desktop window has it, so it's given that first.
        // Windows only lets the app in front move the focus; sharing input with it and with Explorer for
        // a moment makes this thread one of them, even when the keypress came in through the hook.
        static void FocusShell()
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return;
            uint me = GetCurrentThreadId();
            uint front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint explorer = GetWindowThreadProcessId(shell, IntPtr.Zero);
            bool withFront = front != 0 && front != me && AttachThreadInput(me, front, true);
            bool withExplorer = explorer != me && explorer != front && AttachThreadInput(me, explorer, true);
            SetForegroundWindow(shell);
            if (withExplorer) AttachThreadInput(me, explorer, false);
            if (withFront) AttachThreadInput(me, front, false);
        }
    }

    // ---------- The keyboard hook ----------

    // On a thread of its own: Windows waits on the hook for every key pressed anywhere and silently drops
    // one that keeps it waiting, so it can't share the UI thread with thumbnails and OCR.
    class KeyHook
    {
        const int WH_KEYBOARD_LL = 13, HC_ACTION = 0, LLKHF_UP = 0x80;
        const int WM_TIMER = 0x113;
        const uint EVENT_SYSTEM_DESKTOPSWITCH = 0x20;
        const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_R = 0x52, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
        const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
        static readonly int[] Modifiers = { VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL, VK_LMENU, VK_RMENU, VK_LWIN, VK_RWIN };

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        delegate void WinEventProc(IntPtr hook, uint e, IntPtr hwnd, int obj, int child, uint thread, uint time);

        [StructLayout(LayoutKind.Sequential)]
        struct KeyEvent { public int Vk, ScanCode, Flags, Time; public IntPtr ExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct Msg { public IntPtr Hwnd; public int Message; public IntPtr WParam, LParam; public int Time, X, Y, Private; }

        [DllImport("user32.dll")]
        static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        [DllImport("user32.dll")]
        static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);

        [DllImport("user32.dll")]
        static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string name);

        readonly Action pressed;
        // Windows keeps only pointers to these, so they're held here, away from the garbage collector
        readonly HookProc onKey;
        readonly WinEventProc onDesktopSwitch;
        // Which modifiers are down, kept from the keys seen here: RustDesk keeps them from Windows,
        // so Windows' own idea of them is no use while its window is in front
        readonly bool[] held = new bool[256];
        // Whether Windows has seen the key go down too: if it then sees it up while this hook didn't,
        // the key-up went past the hook unseen (in a window running as administrator), and it's let go of
        readonly bool[] systemSaw = new bool[256];
        IntPtr hook;
        bool takingR;

        public KeyHook(Action pressed)
        {
            this.pressed = pressed;
            onKey = OnKey;
            onDesktopSwitch = delegate
            {
                Array.Clear(held, 0, held.Length);
                takingR = false;
            };
            new Thread(Run) { IsBackground = true, Name = "Ctrl+Alt+R hook" }.Start();
        }

        void Run()
        {
            Rehook();
            // Ctrl+Alt+Del, UAC and the lock screen take the keyboard to another desktop, and the key-ups
            // with it (0: WINEVENT_OUTOFCONTEXT)
            SetWinEventHook(EVENT_SYSTEM_DESKTOPSWITCH, EVENT_SYSTEM_DESKTOPSWITCH, IntPtr.Zero, onDesktopSwitch, 0, 0, 0);
            SetTimer(IntPtr.Zero, IntPtr.Zero, 1000, IntPtr.Zero);
            // The hook and the event are called from inside GetMessage
            Msg msg;
            while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.Message == WM_TIMER) Rehook();
        }

        // Back to the front of the line: the newest hook is asked first. The new one goes in before the
        // old one comes out, so no key gets through in between.
        void Rehook()
        {
            IntPtr fresh = SetWindowsHookEx(WH_KEYBOARD_LL, onKey, GetModuleHandle(null), 0);
            if (fresh == IntPtr.Zero) return;
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = fresh;
        }

        IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code == HC_ACTION)
            {
                var key = (KeyEvent)Marshal.PtrToStructure(lParam, typeof(KeyEvent));
                if (Take(key.Vk & 0xFF, (key.Flags & LLKHF_UP) != 0)) return new IntPtr(1);
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        // True keeps the key from going any further
        bool Take(int vk, bool up)
        {
            // A keyboard always says which side; keys typed by software (remote control, macros) may not
            if (vk == VK_SHIFT) vk = VK_LSHIFT;
            else if (vk == VK_CONTROL) vk = VK_LCONTROL;
            else if (vk == VK_MENU) vk = VK_LMENU;

            foreach (int m in Modifiers)
            {
                if (!held[m]) continue;
                if (GetAsyncKeyState(m) < 0) systemSaw[m] = true;
                else if (systemSaw[m]) held[m] = false;
            }

            if (Array.IndexOf(Modifiers, vk) >= 0)
            {
                if (!up && !held[vk]) systemSaw[vk] = false;
                held[vk] = !up;
                return false;
            }
            if (vk != VK_R) return false;

            // The R's key-up goes with the press that switched
            if (up)
            {
                bool taken = takingR;
                takingR = false;
                return taken;
            }
            bool ctrl = held[VK_LCONTROL] || held[VK_RCONTROL], alt = held[VK_LMENU] || held[VK_RMENU];
            bool others = held[VK_LSHIFT] || held[VK_RSHIFT] || held[VK_LWIN] || held[VK_RWIN];
            bool chord = ctrl && alt && !others;
            // Held down, R repeats: taken too, without switching again
            if (takingR && chord) return true;
            takingR = chord;
            if (chord) pressed();
            return chord;
        }
    }
}
