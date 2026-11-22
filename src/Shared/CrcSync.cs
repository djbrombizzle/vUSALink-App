using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    //
    // Windows silently removes a low-level hook whose callback is ever too slow (about 1 s), after which the app
    // would see nothing typed in CRC until restarted. So the hooks run on their own thread (a busy app window can't
    // delay them), the callbacks never touch the disk or look up processes, and a watchdog puts the hooks back
    // if Windows drops them anyway.
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
        readonly Dispatcher dispatcher;          // the app's UI thread: events and logging go there
        Dispatcher hookDispatcher;               // the hooks' own thread
        IntPtr module;
        Timer watchdog;
        volatile HashSet<uint> crcPids = new HashSet<uint>();   // refreshed by the watchdog, read by the hooks
        int reinstalls;
        IntPtr hook = IntPtr.Zero;
        IntPtr lastHwnd = IntPtr.Zero;
        bool lastWasCrc;
        bool dirty;   // the buffer may not match CRC's MCA (cursor moved, text recalled, ...)
        string DirtyReason;          // which key made the line "edited"
        public string LastSkipReason;   // why the last skipped line was skipped (read in CommandSkipped)

        static string NavKeyName(int vk)
        {
            switch (vk)
            {
                case 0x21: return "PgUp"; case 0x22: return "PgDn"; case 0x23: return "End"; case 0x24: return "Home";
                case 0x25: return "Left arrow"; case 0x26: return "Up arrow"; case 0x27: return "Right arrow"; case 0x28: return "Down arrow";
                case 0x2D: return "Insert"; case 0x2E: return "Delete"; case 0x09: return "Tab";
            }
            return "VK 0x" + vk.ToString("X2");
        }
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
            if (hookDispatcher != null) return;
            LoadCrcKeys();
            RefreshPids();
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                module = GetModuleHandle(m.ModuleName);
            var started = new ManualResetEvent(false);
            var th = new Thread(() =>
            {
                hookDispatcher = Dispatcher.CurrentDispatcher;
                InstallHooks();
                started.Set();
                Dispatcher.Run();   // the message loop the low-level hooks are called from
            }) { IsBackground = true, Name = "CrcHooks" };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
            started.WaitOne(5000);
            watchdog = new Timer(_ => Watch(), null, 2000, 2000);
        }

        // On the hooks' thread.
        void InstallHooks()
        {
            Unhook();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, module, 0);
            int e1 = Marshal.GetLastWin32Error();
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            int e2 = Marshal.GetLastWin32Error();
            if (hook == IntPtr.Zero) LogLater("CRC sync: keyboard hook failed " + e1);
            if (mouseHook == IntPtr.Zero) LogLater("CRC sync: mouse hook failed " + e2);
        }

        void Unhook()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            hook = mouseHook = IntPtr.Zero;
        }

        // Every 2 s: refresh which processes are CRC, and check the hooks are still called. Every move of the mouse
        // pointer passes through the mouse hook, so a pointer that moved over CRC with no mouse-hook call since the
        // last check, twice in a row, means Windows has dropped the hooks: put them back. (Not "last input time":
        // joysticks and USB push-to-talk buttons count as input but never pass through these hooks.)
        void Watch()
        {
            try
            {
                RefreshPids();
                if (hookDispatcher == null) return;
                POINT pt;
                if (!GetCursorPos(out pt)) return;
                int calls = mouseCalls;
                bool moved = pt.X != lastPt.X || pt.Y != lastPt.Y, silent = calls == lastCalls;
                lastPt = pt; lastCalls = calls;
                // Only judged while CRC is in front: input to an elevated window never reaches a non-admin hook,
                // and an elevated CRC can't be helped by reinstalling (see CrcElevated).
                uint pid;
                GetWindowThreadProcessId(GetForegroundWindow(), out pid);
                if (!crcPids.Contains(pid) || CrcElevated || !moved || !silent) { suspect = 0; return; }
                if (++suspect < 2) return;
                suspect = 0;
                reinstalls++;
                if (reinstalls <= 5 || reinstalls % 50 == 0)
                    LogLater("CRC sync: Windows stopped calling the keyboard/mouse hooks; reinstalling (" + reinstalls + ")");
                hookDispatcher.BeginInvoke(new Action(InstallHooks));
            }
            catch (Exception ex) { LogLater("CRC sync: watchdog: " + ex.Message); }
        }

        POINT lastPt;
        int lastCalls, suspect;
        volatile int mouseCalls;   // mouse hook calls (pointer moves, clicks)

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT p);

        void RefreshPids()
        {
            var ids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName(ProcessName)) { ids.Add((uint)p.Id); p.Dispose(); }
            if (ids.SetEquals(crcPids)) return;
            crcPids = ids;
            lastHwnd = IntPtr.Zero;
            bool elevated = ids.Any(HigherThanUs);
            if (elevated != CrcElevated)
                LogLater(elevated
                    ? "CRC sync: CRC is running as administrator and this app isn't, so Windows hides what's typed in CRC from it. Start CRC normally, or run this app as administrator too"
                    : "CRC sync: CRC runs at the same level as this app");
            CrcElevated = elevated;
        }

        // CRC runs as administrator and this app doesn't (Windows' UIPI): no key or click in CRC reaches the hooks.
        public volatile bool CrcElevated;

        // True when this process can't read the other one's token: it runs at a higher integrity level (elevated).
        static bool HigherThanUs(uint pid)
        {
            var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return false;
            try
            {
                IntPtr tok;
                if (OpenProcessToken(h, 0x0008, out tok)) { CloseHandle(tok); return false; }   // TOKEN_QUERY
                return Marshal.GetLastWin32Error() == 5;   // ERROR_ACCESS_DENIED
            }
            finally { CloseHandle(h); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        void LogLater(string s) { dispatcher.BeginInvoke(new Action(() => Log.Info(s))); }

        public void Dispose()
        {
            if (watchdog != null) watchdog.Dispose();
            var d = hookDispatcher;
            if (d != null)
            {
                try { d.Invoke(new Action(Unhook)); } catch { }
                d.InvokeShutdown();
            }
        }

        // Only middle-button presses in CRC matter (that's QF on a data block); nothing else is looked at.
        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            mouseCalls++;
            try
            {
                if (nCode >= 0 && Enabled && wParam == (IntPtr)WM_MBUTTONDOWN && ReadoutLikely != null && CrcInForeground())
                    dispatcher.BeginInvoke(new Action(() => ReadoutLikely()));
                // A left-click in CRC while a command is being typed can put the clicked aircraft into the MCA
                // (CRC's "finish the command by clicking the target"): the app can't see which, so the line is
                // no longer known exactly. Clicks with an empty line (e.g. Ctrl+Shift+click to start a track) are fine.
                if (nCode >= 0 && Enabled && wParam == (IntPtr)WM_LBUTTONDOWN && buffer.Length > 0 && CrcInForeground())
                    MarkEdited("mouse click while typing");
            }
            catch { }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        void Reset()
        {
            buffer.Clear();
            dirty = false;
            DirtyReason = null;
        }

        bool CrcInForeground()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == lastHwnd) return lastWasCrc;
            lastHwnd = hwnd;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            lastWasCrc = crcPids.Contains(pid);
            // What's on CRC's MCA line stays there while you work in another program (the EDST, say), so the buffer
            // is kept; it starts over only in a different CRC window, which has its own MCA.
            if (lastWasCrc && hwnd != LastCrcWindow)
            {
                Reset();
                LastCrcWindow = hwnd;
                LogLater("CRC sync: CRC window focused (hwnd " + hwnd + ")");
            }
            return lastWasCrc;
        }

        // The CRC window used most recently (where its MCA input goes).
        public volatile IntPtr LastCrcWindow;

        // What the app knows about CRC's MCA line (for CRC echo, which types into it):
        //   Typed:  characters the app saw you type there and you haven't entered yet. Leave the line alone.
        //   Unsure: a key or click (arrows, Ctrl+key, aircraft select, a click while typing) may have changed the line,
        //           which happens in everyday use. Clear it with Esc before typing.
        public bool CrcLineTyped { get { return buffer.Length > 0 && !dirty; } }
        public bool CrcLineUnsure { get { return dirty; } }
        public string CrcLineInfo
        {
            get
            {
                return dirty ? "unsure (" + (DirtyReason ?? "?") + ")" + (buffer.Length > 0 ? ", " + buffer.Length + " chars seen" : "")
                    : buffer.Length == 0 ? "empty" : buffer.Length + " chars typed";
            }
        }

        // The app typed a whole line + Enter into CRC (its keys are ignored by the hook): CRC's line is empty again.
        public void LineSentByApp()
        {
            var d = hookDispatcher;
            if (d != null) d.BeginInvoke(new Action(Reset));
        }

        // CRC's own key bindings (from %LOCALAPPDATA%\CRC\GeneralSettings.json): push-to-talk keys type nothing in
        // the MCA, and the aircraft-select key inserts an aircraft the app can't see. CRC stores WPF Key values.
        readonly HashSet<int> pttVks = new HashSet<int>();
        int selectVk = -1;

        void LoadCrcKeys()
        {
            try
            {
                var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CRC", "GeneralSettings.json");
                if (!System.IO.File.Exists(path)) return;
                var root = Json.Parse(System.IO.File.ReadAllText(path));
                foreach (var name in new[] { "Ptt0Configuration", "Ptt1Configuration" })
                {
                    var c = Json.Obj(root, name);
                    if (c == null || Json.Str(c, "DeviceType") != "Keyboard") continue;
                    int key = (int)Json.Num(c, "ButtonOrKey", 0);
                    if (key <= 0) continue;
                    int vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey((System.Windows.Input.Key)key);
                    if (vk > 0) pttVks.Add(vk);
                    // Left/right modifier keys arrive in the low-level hook as their sided codes.
                    if (vk == 0x11) { pttVks.Add(0xA2); pttVks.Add(0xA3); }
                    if (vk == 0x10) { pttVks.Add(0xA0); pttVks.Add(0xA1); }
                    if (vk == 0x12) { pttVks.Add(0xA4); pttVks.Add(0xA5); }
                }
                System.Windows.Input.Key sel;
                var selName = Json.Str(root, "AircraftSelectKey");
                if (!string.IsNullOrEmpty(selName) && Enum.TryParse(selName, out sel))
                    selectVk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(sel);
                Log.Info("CRC sync: CRC keys: push-to-talk VK " + string.Join(",", pttVks.Select(v => "0x" + v.ToString("X2"))) +
                    ", aircraft select VK " + (selectVk > 0 ? "0x" + selectVk.ToString("X2") : "none"));
            }
            catch (Exception ex) { Log.Info("CRC sync: couldn't read CRC key settings: " + ex.Message); }
        }

        static bool IsModifier(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C || vk == 0x14 || vk == 0x90 || vk == 0x91;
        }

        void MarkEdited(string why)
        {
            if (!dirty) DirtyReason = why;
            dirty = true;
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
            if (pttVks.Contains(vk)) return false;                           // push-to-talk: types nothing
            if (IsModifier(vk)) return false;                                // Ctrl/Shift/Alt/Win/locks on their own
            if (vk == selectVk)
            {
                // CRC's aircraft-select key puts the aircraft under the cursor into the MCA: the app can't see which.
                if (buffer.Length > 0) MarkEdited("aircraft-select key");
                return false;
            }
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            // Ctrl + another key (e.g. Ctrl+R recall) can change the line, even when Ctrl is also push-to-talk:
            // safer to ask for a retype than to guess.
            bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
            if (ctrl) { MarkEdited("Ctrl+key (VK 0x" + vk.ToString("X2") + ")"); return false; }
            // CRC's ERAM function keys replace the MCA line with a command prefix (F5 -> "QZ ", F6 -> "QU ", ...).
            if (vk >= 0x70 && vk <= 0x78)
            {
                var pre = shift ? ShiftFKeys[vk - 0x70] : FKeys[vk - 0x70];
                if (pre != null) { buffer.Clear(); buffer.Append(pre); dirty = false; DirtyReason = null; }
                else MarkEdited((shift ? "Shift+" : "") + "F" + (vk - 0x6F));   // unknown key: CRC may have changed the line
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
            else if ((vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E || vk == 0x09)
                MarkEdited(NavKeyName(vk));   // PgUp/PgDn/End/Home/arrows, Ins, Del, Tab (also numpad keys with NumLock off)
            else if (vk == 0x0D)
            {
                var raw = buffer.ToString();
                bool exact = !dirty;
                LastSkipReason = dirty ? DirtyReason : null;
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
                if (Diagnose != null) { var d = Diagnose; dispatcher.BeginInvoke(new Action(() => d(raw, erase))); }
                if (CommandEntered != null) CommandEntered(cmd);
                if (erase > 0)
                {
                    // Let this hook call return first, then rewrite CRC's line.
                    dispatcher.BeginInvoke(new Action(() => SendKeys(0x08, erase, true)));
                    LogLater("CRC line trimmed by " + erase + " chars before Enter: " + cmd);
                    return true;
                }
                if (erase < 0)
                {
                    // A command only the EDST knows (UC): clear CRC's line instead of letting CRC reject it.
                    int all = raw.Length;
                    dispatcher.BeginInvoke(new Action(() => SendKeys(0x08, all, false)));
                    LogLater("CRC line cleared (EDST-only command): " + cmd);
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

        const int WH_MOUSE_LL = 14, WM_MBUTTONDOWN = 0x207, WM_LBUTTONDOWN = 0x201;
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
