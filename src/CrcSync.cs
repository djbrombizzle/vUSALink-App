using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VusaLinkApp
{
    // Watches commands typed into CRC's MCA so the app can hand the CPDLC ones to the EDST (see CrcForward).
    // Keystrokes are only looked at while a CRC window is in the foreground; nothing is stored or sent anywhere.
    class CrcSync : IDisposable
    {
        public event Action<string> CommandEntered;
        public bool Enabled = true;

        readonly StringBuilder buffer = new StringBuilder();
        readonly LowLevelKeyboardProc proc;
        IntPtr hook = IntPtr.Zero;
        IntPtr lastHwnd = IntPtr.Zero;
        bool lastWasCrc;

        public CrcSync()
        {
            proc = HookProc; // keep the delegate alive for the lifetime of the hook
        }

        public void Start()
        {
            if (hook != IntPtr.Zero) return;
            using (var p = Process.GetCurrentProcess())
            using (var m = p.MainModule)
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(m.ModuleName), 0);
            if (hook == IntPtr.Zero) Log.Info("CRC sync: keyboard hook failed " + Marshal.GetLastWin32Error());
        }

        public void Dispose()
        {
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
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
                    lastWasCrc = string.Equals(p.ProcessName, "CRC", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            if (!lastWasCrc) buffer.Clear();
            return lastWasCrc;
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && Enabled && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN) && CrcInForeground())
                    OnKey(Marshal.ReadInt32(lParam));
            }
            catch { }
            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        void OnKey(int vk)
        {
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
            if (ctrl) return;
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
            else if (vk == 0x1B) buffer.Clear();
            else if (vk == 0x0D)
            {
                var cmd = buffer.ToString().Trim();
                buffer.Clear();
                if (cmd != "" && CommandEntered != null) CommandEntered(cmd);
            }
            if (buffer.Length > 200) buffer.Clear();
        }

        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

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
