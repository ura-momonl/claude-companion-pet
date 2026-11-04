// "Back to work": find the terminal window a session runs in and bring it to the front.
// Finding it (two ways, in this order):
//   1. the console window of the claude process (AttachConsole + GetConsoleWindow): a classic console window is the window itself; a Windows
//      Terminal tab has a hidden pseudo-console window whose OWNER is that Windows Terminal window, so this finds the right one of several
//      Windows Terminal windows. A pseudo-console window without a visible owner (VS Code terminal) is not usable.
//   2. the process chain above claude (shell -> terminal / editor): the nearest process that has a visible top-level window. When it has
//      several (VS Code windows share one process), the one whose title contains the project folder name, else the top-most.
// Limit: Windows Terminal has no way to name a TAB from outside, so only the window comes to the front; the tab that was last active stays.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCompanionPet
{
    static class TermWindow
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Native.RECT r);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool OpenIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
        [DllImport("kernel32.dll")] static extern bool AttachConsole(uint pid);
        [DllImport("kernel32.dll")] static extern bool FreeConsole();
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        public class Win { public IntPtr H; public uint Pid; public string Class = "", Title = ""; }

        static readonly string[] NEVER = { "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "csrss.exe", "smss.exe", "sihost.exe", "system", "" };
        static readonly string[] SHELL_CLASSES = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "IME", "MSCTFIME UI", "Default IME" };

        static string TextOf(IntPtr h) { StringBuilder sb = new StringBuilder(300); GetWindowText(h, sb, 300); return sb.ToString(); }
        static string ClassOf(IntPtr h) { StringBuilder sb = new StringBuilder(100); GetClassName(h, sb, 100); return sb.ToString(); }

        // visible, un-owned, real-sized top-level windows of other programs, top-most first (Z order)
        public static List<Win> TopWindows()
        {
            List<Win> res = new List<Win>();
            uint me = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                try
                {
                    if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                    uint pid; GetWindowThreadProcessId(h, out pid); if (pid == me) return true;
                    int cloaked; if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out cloaked, 4) == 0 && cloaked != 0 && cloaked != 2) return true; // 2 = on another virtual desktop (still reachable)
                    Native.RECT r; if (!GetWindowRect(h, out r) || r.R - r.L < 80 || r.B - r.T < 40) return true;
                    string cls = ClassOf(h); if (Array.IndexOf(SHELL_CLASSES, cls) >= 0) return true;
                    if ((GetWindowLong(h, -20) & 0x80) != 0 && (GetWindowLong(h, -20) & 0x40000) == 0) return true; // tool window that is not an app window
                    res.Add(new Win { H = h, Pid = pid, Class = cls, Title = TextOf(h) });
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return res;
        }

        // console window of a process, as the top-level window that owns it (null when there is none we can use); consoleWhy says what happened
        static string consoleWhy = "";
        static Win ConsoleWindow(int pid, List<Win> all)
        {
            consoleWhy = "";
            try
            {
                if (!AttachConsole((uint)pid)) { consoleWhy = "attach error " + Marshal.GetLastWin32Error(); return null; }
                IntPtr c;
                try { c = GetConsoleWindow(); } finally { FreeConsole(); }
                if (c == IntPtr.Zero) { consoleWhy = "no console window"; return null; }
                IntPtr root = GetAncestor(c, 3 /* GA_ROOTOWNER */);
                consoleWhy = "console class " + ClassOf(c) + (IsWindowVisible(c) ? " visible" : " hidden") + (root != c ? ", owned by class " + ClassOf(root) : ", no owner");
                if (root == IntPtr.Zero) root = c;
                foreach (Win w in all) if (w.H == root) return w;
            }
            catch (Exception e) { consoleWhy = "exception " + e.GetType().Name; }
            return null;
        }

        // the processes from the session's claude process upwards, stopping at the desktop shell (never a target)
        public static List<int> Chain(int pid)
        {
            List<int> chain = new List<int>(); int cur = pid;
            for (int i = 0; i < 12 && cur > 4; i++)
            {
                chain.Add(cur);
                int parent = ProcTree.ParentOf(cur);
                if (parent <= 4) break;
                if (Array.IndexOf(NEVER, ProcTree.NameOf(parent)) >= 0) break;
                cur = parent;
            }
            return chain;
        }

        public static Win Find(int pid, string project, out string how)
        {
            how = "";
            if (pid <= 0) { how = "no process known"; return null; }
            List<Win> all = TopWindows();
            Win w = ConsoleWindow(pid, all);
            if (w != null) { how = "console window"; return w; }
            foreach (int p in Chain(pid))
            {
                List<Win> mine = all.FindAll(delegate (Win x) { return x.Pid == (uint)p; });
                if (mine.Count == 0) continue;
                Win pick = null;
                if (mine.Count > 1 && !string.IsNullOrEmpty(project)) pick = mine.Find(delegate (Win x) { return x.Title.IndexOf(project, StringComparison.OrdinalIgnoreCase) >= 0; });
                how = "process " + ProcTree.NameOf(p) + " (console: " + consoleWhy + ")" + (pick != null ? " (title match of " + mine.Count + ")" : mine.Count > 1 ? " (top-most of " + mine.Count + ")" : "");
                return pick ?? mine[0];
            }
            how = "no window above process " + pid + " (console: " + consoleWhy + ")";
            return null;
        }

        // Bring the window to the front. Returns a short account for the log; ok = it really is the foreground window afterwards.
        public static string Raise(IntPtr h, out bool ok)
        {
            ok = false;
            if (!IsWindow(h)) return "window is gone";
            bool was = IsIconic(h);
            string rest = "";
            if (was)
            {
                string by = "ShowWindow";
                ShowWindow(h, 9 /* SW_RESTORE */);
                for (int i = 0; i < 10 && IsIconic(h); i++) System.Threading.Thread.Sleep(30);
                if (IsIconic(h)) { by = "OpenIcon"; OpenIcon(h); for (int i = 0; i < 10 && IsIconic(h); i++) System.Threading.Thread.Sleep(30); }
                if (IsIconic(h)) { by = "SC_RESTORE"; PostMessage(h, 0x112 /* WM_SYSCOMMAND */, (IntPtr)0xF120 /* SC_RESTORE */, IntPtr.Zero); for (int i = 0; i < 15 && IsIconic(h); i++) System.Threading.Thread.Sleep(30); }
                rest = IsIconic(h) ? "restore did not take effect (still minimised)" : "restored (" + by + ")";
            }
            string lead = was ? rest + " and raised" : "raised", tried = "";
            if (IsFront(h)) { ok = !IsIconic(h); return lead; }
            // Windows lets only the process that last received input take the foreground (a real click on the panel is exactly that; a click
            // that was only posted to it, as in the tests, is not). Ways around it that change nothing the user can see:
            // 1. share the input queue of the current foreground window for a moment
            IntPtr fg = GetForegroundWindow(); uint fgPid;
            uint fgTid = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out fgPid), me = GetCurrentThreadId();
            bool att = fgTid != 0 && fgTid != me && AttachThreadInput(me, fgTid, true);
            try { SetForegroundWindow(h); BringWindowToTop(h); }
            finally { if (att) AttachThreadInput(me, fgTid, false); }
            if (IsFront(h)) { ok = !IsIconic(h); return lead + " (shared input queue)"; }
            tried += "shared input queue " + (att ? "" : "not possible ") + "no; ";
            // 2. SwitchToThisWindow (the Alt+Tab call)
            SwitchToThisWindow(h, true);
            if (IsFront(h)) { ok = !IsIconic(h); return lead + " (SwitchToThisWindow)"; }
            tried += "SwitchToThisWindow no";
            FLASHWINFO f = new FLASHWINFO(); f.cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)); f.hwnd = h; f.dwFlags = 3 | 12; f.uCount = 3; // caption + tray, until it is in front
            FlashWindowEx(ref f);
            return "Windows refused to bring it to the front (" + tried + "); its taskbar button was flashed instead";
        }
        static bool IsFront(IntPtr h)
        {
            if (GetForegroundWindow() != h) { SetForegroundWindow(h); }
            for (int i = 0; i < 6; i++) { if (GetForegroundWindow() == h) return true; System.Threading.Thread.Sleep(25); }
            return GetForegroundWindow() == h;
        }
        public static string Describe(Win w) { return w == null ? "none" : "class " + w.Class + " process " + ProcTree.NameOf((int)w.Pid); }
    }
}
