using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace VusaLinkApp
{
    // Watches commands typed into CRC's MCA so the app can hand the CPDLC ones to the EDST (see CrcForward).
    // Keystrokes are only looked at while a CRC window is in the foreground; nothing is stored or sent anywhere.
    //
    // CRC doesn't know the EDST's uplink modifiers ("QZ 240 DAL123 /U" -> MESSAGE TOO LONG). When Enter is
    // pressed on such a line, the app passes the full command to the EDST, holds back that Enter, erases the
    // modifiers from CRC's MCA with Backspaces and presses Enter itself, so CRC gets "QZ 240 DAL123" and
    // amends as usual. This only happens when the app knows exactly what's in CRC's MCA (no cursor keys,
    // Delete or Ctrl shortcuts were used on the line).
    class CrcSync : IDisposable
    {
        public event Action<string> CommandEntered;
        public event Action<string> CommandSkipped;   // line was edited with cursor keys etc.: not passed on
        public event Action ReadoutLikely;            // QF typed, or middle-click in CRC (QF on a data block)
        public bool Enabled = true;
        public string ProcessName = "CRC";      // VUSALINK_CRC_PROCESS can point this at a stand-in for testing
        public Func<string, int> TrimForCrc;    // characters to erase before CRC gets the line (0 = none)
        public Action<string, int> Diagnose;    // every Enter in CRC (raw line, chars to erase): for the log

        readonly StringBuilder buffer = new StringBuilder();
        readonly LowLevelKeyboardProc proc, mouseProc;
        IntPtr mouseHook = IntPtr.Zero;
        readonly Dispatcher dispatcher;
        IntPtr hook = IntPtr.Zero;
        IntPtr lastHwnd = IntPtr.Zero;
        bool lastWasCrc;
        bool dirty;   // the buffer may not match CRC's MCA (cursor moved, text recalled, ...)
        bool holdEnterUp; // the Enter press was held back, so hold back its release as well

        static readonly IntPtr Marker = (IntPtr)0x56555341; // "VUSA": tags keystrokes this app injects

        // CRC ERAM function-key command prefixes, F1..F9 (same map CRC Companion uses).
        static readonly string[] FKeys = { "QF ", "QP ", "QT ", "QX ", "QZ ", "QU ", "QL ", "QQ ", "QB " };
        static readonly string[] ShiftFKeys = { "DM ", "QD ", null, null, null, null, "WR ", "QR ", null };

        public CrcSync()
        {
            proc = HookProc; // keep the delegates alive for the lifetime of the hooks
            mouseProc = MouseProc;
            dispatcher = Dispatcher.CurrentDispatcher;
        }

        public void Start()
        {
            if (hook != IntPtr.Zero) return;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
            {
                var mod = GetModuleHandle(m.ModuleName);
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, mod, 0);
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, mod, 0);
            }
            if (hook == IntPtr.Zero) Log.Info("CRC sync: keyboard hook failed " + Marshal.GetLastWin32Error());
            if (mouseHook == IntPtr.Zero) Log.Info("CRC sync: mouse hook failed " + Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            hook = mouseHook = IntPtr.Zero;
        }

        // Only middle-button presses in CRC matter (that's QF on a data block); nothing else is looked at.
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && Enabled && wParam == (IntPtr)WM_MBUTTONDOWN && ReadoutLikely != null && CrcInForeground())
                    dispatcher.BeginInvoke(new Action(() => ReadoutLikely()));
            }
            catch { }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        void Reset()
        {
            buffer.Clear();
            dirty = false;
        }

        bool CrcInForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == lastHwnd) return lastWasCrc;
            lastHwnd = hwnd;
            lastWasCrc = false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                    lastWasCrc = string.Equals(p.ProcessName, ProcessName, StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            Reset();
            if (lastWasCrc) Log.Info("CRC sync: CRC window focused (hwnd " + hwnd + ")");
            return lastWasCrc;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && Enabled && Marshal.ReadIntPtr(lParam, 16) != Marker)
                {
                    int vk = Marshal.ReadInt32(lParam);
                    bool down = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
                    bool up = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;
                    if (up && vk == 0x0D && holdEnterUp) { holdEnterUp = false; return (IntPtr)1; }  // its release too
                    if (down && CrcInForeground() && OnKey(vk)) { holdEnterUp = true; return (IntPtr)1; } // Enter held back; re-sent below
                }
            }
            catch { }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        // Returns true to keep the key from reaching CRC.
        bool OnKey(int vk)
        {
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
            if (ctrl) { dirty = true; return false; }                         // e.g. Ctrl+R recalls a command in CRC
            // CRC's ERAM function keys replace the MCA line with a command prefix (F5 -> "QZ ", F6 -> "QU ", ...).
            if (vk >= 0x70 && vk <= 0x78)
            {
                var pre = shift ? ShiftFKeys[vk - 0x70] : FKeys[vk - 0x70];
                if (pre != null) { buffer.Clear(); buffer.Append(pre); dirty = false; }
                else dirty = true;   // unknown key: CRC may have changed the line
                return false;
            }
            if (vk >= 0x41 && vk <= 0x5A) buffer.Append((char)vk);
            else if (vk >= 0x30 && vk <= 0x39 && !shift) buffer.Append((char)vk);
            else if (vk >= 0x60 && vk <= 0x69) buffer.Append((char)('0' + vk - 0x60));
            else if (vk == 0x20) buffer.Append(' ');
            else if (vk == 0xBF || vk == 0x6F) buffer.Append('/');
            else if (vk == 0xBE || vk == 0x6E) buffer.Append('.');
            else if (vk == 0x6A || (vk == 0x38 && shift)) buffer.Append('*');
            else if (vk == 0x6B || (vk == 0xBB && shift)) buffer.Append('+');
            else if (vk == 0x6D || (vk == 0xBD && !shift)) buffer.Append('-');
            else if (vk == 0xDB && !shift) buffer.Append('[');
            else if (vk == 0xDD && !shift) buffer.Append(']');
            else if (vk == 0xC0) buffer.Append(shift ? '~' : '`');
            else if (vk == 0x08) { if (buffer.Length > 0) buffer.Length--; }
            else if (vk == 0x1B) Reset();
            else if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x09) dirty = true; // PgUp..Down, Ins, Del, Tab
            else if (vk == 0x0D)
            {
                var raw = buffer.ToString();
                bool exact = !dirty;
                Reset();
                var cmd = raw.Trim();
                if (cmd == "") return false;
                if (cmd.StartsWith("QF") && ReadoutLikely != null)
                    dispatcher.BeginInvoke(new Action(() => ReadoutLikely()));
                if (!exact)
                {
                    // What's in CRC's MCA may differ from what the app saw, so never send a guess to the EDST.
                    if (CommandSkipped != null) CommandSkipped(cmd);
                    return false;
                }
                int erase = TrimForCrc != null ? TrimForCrc(raw) : 0;
                if (Diagnose != null) Diagnose(raw, erase);
                if (CommandEntered != null) CommandEntered(cmd);
                if (erase > 0)
                {
                    // Let this hook call return first, then rewrite CRC's line.
                    dispatcher.BeginInvoke(new Action(() => SendKeys(0x08, erase, true)));
                    Log.Info("CRC line trimmed by " + erase + " chars before Enter: " + cmd);
                    return true;
                }
                if (erase < 0)
                {
                    // A command only the EDST knows (UC): clear CRC's line instead of letting CRC reject it.
                    int all = raw.Length;
                    dispatcher.BeginInvoke(new Action(() => SendKeys(0x08, all, false)));
                    Log.Info("CRC line cleared (EDST-only command): " + cmd);
                    return true;
                }
                return false;
            }
            if (buffer.Length > 200) Reset();
            return false;
        }

        static void SendKeys(ushort vk, int count, bool thenEnter)
        {
            int n = count * 2 + (thenEnter ? 2 : 0);
            var inputs = new INPUT[n];
            int i = 0;
            for (int k = 0; k < count; k++) { inputs[i++] = Key(vk, false); inputs[i++] = Key(vk, true); }
            if (thenEnter) { inputs[i++] = Key(0x0D, false); inputs[i++] = Key(0x0D, true); }
            if (SendInput((uint)n, inputs, Marshal.SizeOf(typeof(INPUT))) != n)
                Log.Info("CRC sync: SendInput failed " + Marshal.GetLastWin32Error());
        }

        static INPUT Key(ushort vk, bool up)
        {
            return new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0, dwExtraInfo = Marker } }
            };
        }

        const int WH_MOUSE_LL = 14, WM_MBUTTONDOWN = 0x207;
        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern short GetKeyState(int vk);
    }
}
