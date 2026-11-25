using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace VusaLinkApp
{
    // CRC echo: types an ERAM command line into CRC's MCA (e.g. "QU LUNNI DAL123" + Enter) so CRC matches an uplink
    // sent from the EDST. CRC has no command interface, so typing is the only way in, and CRC only takes keys while
    // its window has focus (keys posted to it without focus are ignored, tested Nov 2026). So the line is typed by
    // briefly bringing CRC's ERAM window to the front and then giving focus back.
    //
    // Safety: it types only into a CRC window whose title shows an ERAM display ("CRC : 1 : ERAM : ZDC"), never the
    // Messages or Controllers windows (typing there could send a text message) or a window showing a browser tab,
    // and only once that window is confirmed in front. Otherwise nothing is typed. The keys carry the app's marker,
    // so CrcSync ignores them (no mirroring loop).
    static class CrcType
    {
        static readonly IntPtr Marker = (IntPtr)0x56555341; // "VUSA", same as CrcSync

        public static bool IsEramWindow(IntPtr h)
        {
            return h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h) && Title(h).Contains(": ERAM");
        }

        // The ERAM window to type into. In order:
        //   1. the CRC window used last, if it shows an ERAM display;
        //   2. the top-level window that owns it (the window used last can be a CRC popup, e.g. a QF readout or menu);
        //   3. the CRC ERAM window highest in the z-order, i.e. the one in front most recently (not the largest: a
        //      second ERAM window behind the main one could be bigger). Minimized windows only as a last resort.
        // Zero if there is none. 'how' says which rule picked it (for the log).
        public static IntPtr FindTarget(IntPtr lastUsed, string processName, out string how)
        {
            how = "none";
            var pids = Process.GetProcessesByName(processName).Select(p => (uint)p.Id).ToList();
            if (pids.Count == 0) return IntPtr.Zero;
            uint lp;
            bool lastIsCrc = lastUsed != IntPtr.Zero && IsWindow(lastUsed) && GetWindowThreadProcessId(lastUsed, out lp) != 0 && pids.Contains(lp);
            if (lastIsCrc && IsEramWindow(lastUsed)) { how = "last used"; return lastUsed; }
            if (lastIsCrc)
            {
                foreach (var a in new[] { GetAncestor(lastUsed, 2), GetAncestor(lastUsed, 3), GetWindow(lastUsed, 4) })   // GA_ROOT, GA_ROOTOWNER, GW_OWNER
                    if (IsEramWindow(a)) { how = "owner of last used '" + Title(lastUsed) + "'"; return a; }
            }
            IntPtr front = IntPtr.Zero, minimized = IntPtr.Zero;
            EnumWindows((h, l) =>   // top of the z-order first
            {
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains(pid) || !IsEramWindow(h)) return true;
                if (IsIconic(h)) { if (minimized == IntPtr.Zero) minimized = h; return true; }
                front = h; return false;
            }, IntPtr.Zero);
            string lastNote = lastUsed == IntPtr.Zero ? "" : " (last used was '" + Title(lastUsed) + "')";
            if (front != IntPtr.Zero) { how = "frontmost ERAM window" + lastNote; return front; }
            if (minimized != IntPtr.Zero) { how = "minimized ERAM window" + lastNote; return minimized; }
            return IntPtr.Zero;
        }

        public static string Title(IntPtr h)
        {
            var t = new StringBuilder(256); GetWindowText(h, t, 256); return t.ToString();
        }

        public static bool CanType(string line)
        {
            ushort vk; bool shift;
            return !string.IsNullOrEmpty(line) && line.ToUpperInvariant().All(c => VkFor(c, out vk, out shift));
        }

        // Types 'line' + Enter into CRC window 'crc', then gives focus back to 'back'. Must be called on the UI
        // thread while this app is in front (Windows only lets the foreground app hand focus on). Returns null when
        // it was typed, or why it wasn't.
        // clearFirst: press Esc first, which clears CRC's MCA line (used when the app isn't sure the line is empty).
        public static async Task<string> TypeLine(IntPtr crc, string line, IntPtr back, bool clearFirst)
        {
            if (!CanType(line)) return "CAN'T TYPE THAT LINE";
            if (!IsEramWindow(crc)) return "NO ERAM WINDOW";
            if (GetForegroundWindow() != crc) BringToFront(crc);
            // Wait (up to 0.5 s) until CRC really is in front: Windows sometimes delays or refuses the switch, and
            // keys sent before then would go to another window.
            for (int i = 0; i < 25 && GetForegroundWindow() != crc; i++) await Task.Delay(20);
            if (GetForegroundWindow() != crc || !IsEramWindow(crc))
            {
                if (back != IntPtr.Zero) BringToFront(back);
                return "COULDN'T FOCUS CRC";
            }
            await Task.Delay(60);   // let CRC give its MCA keyboard focus
            if (GetForegroundWindow() != crc) return "CRC LOST FOCUS";
            var inputs = new List<INPUT>();
            if (clearFirst) { inputs.Add(Key(0x1B, false)); inputs.Add(Key(0x1B, true)); }
            foreach (var ch in line.ToUpperInvariant())
            {
                ushort vk; bool shift;
                VkFor(ch, out vk, out shift);
                if (shift) inputs.Add(Key(0x10, false));
                inputs.Add(Key(vk, false)); inputs.Add(Key(vk, true));
                if (shift) inputs.Add(Key(0x10, true));
            }
            inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true));
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            await Task.Delay(60);
            if (back != IntPtr.Zero) BringToFront(back);
            return sent == inputs.Count ? null : "ONLY " + sent + "/" + inputs.Count + " KEYS SENT";
        }

        static bool VkFor(char ch, out ushort vk, out bool shift)
        {
            shift = false; vk = 0;
            if (ch >= 'A' && ch <= 'Z') { vk = ch; return true; }
            if (ch >= '0' && ch <= '9') { vk = ch; return true; }
            switch (ch)
            {
                case ' ': vk = 0x20; return true;
                case '/': vk = 0xBF; return true;
                case '.': vk = 0xBE; return true;
                case '-': vk = 0xBD; return true;                 // VK_OEM_MINUS
                case '+': vk = 0xBB; shift = true; return true;   // Shift + VK_OEM_PLUS ("=" key): AM ... RMK ++text++
            }
            return false;
        }

        // SetForegroundWindow is only allowed for the foreground process; attaching to the foreground thread's
        // input lets it hand focus on.
        public static bool BringToFront(IntPtr h)
        {
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            bool ok = SetForegroundWindow(h);
            if (attached) AttachThreadInput(me, fgThread, false);
            return ok;
        }

        public static IntPtr Foreground() { return GetForegroundWindow(); }

        static INPUT Key(ushort vk, bool up)
        {
            return new INPUT
            {
                type = 1,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u, dwExtraInfo = Marker } }
            };
        }

        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    }
}
