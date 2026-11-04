// The integrity level this process runs at. A program started from inside a folder that carries a Low mandatory label (a folder that has been marked untrusted) runs at
// Low even when the user's session is Medium: it cannot write the user's ~/.claude or %APPDATA%, and Windows (UIPI) may keep clicks and
// messages from Medium windows away from it. The pet says so in plain words instead of failing quietly.
using System;
using System.Runtime.InteropServices;

namespace ClaudeCompanionPet
{
    static class Integrity
    {
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        // SECURITY_MANDATORY_*_RID: 0x1000 low, 0x2000 medium, 0x3000 high, 0x4000 system; -1 = could not be read
        public static int Rid()
        {
            if (Env.Get("PET_INTEGRITY") == "low") return 0x1000; // tests only: show the "move me to a normal folder" dialog without needing a low-labelled folder
            IntPtr token = IntPtr.Zero, buf = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), 8 /* TOKEN_QUERY */, out token)) return -1;
                int n; GetTokenInformation(token, 25 /* TokenIntegrityLevel */, IntPtr.Zero, 0, out n);
                if (n <= 0) return -1;
                buf = Marshal.AllocHGlobal(n);
                if (!GetTokenInformation(token, 25, buf, n, out n)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            catch { return -1; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); if (token != IntPtr.Zero) CloseHandle(token); }
        }
        public static string Name()
        {
            int r = Rid();
            return r < 0 ? "unknown" : r < 0x2000 ? "Low (0x" + r.ToString("x") + ")" : r < 0x3000 ? "Medium (0x" + r.ToString("x") + ")" : r < 0x4000 ? "High (0x" + r.ToString("x") + ")" : "System (0x" + r.ToString("x") + ")";
        }
        public static bool IsLow { get { int r = Rid(); return r >= 0 && r < 0x2000; } }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint GetNamedSecurityInfo(string name, int objType, uint secInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
        // The mandatory label on a file: its RID (0x1000 low, 0x2000 medium ...), 0x2000 when the file has no label (Windows treats that as medium), -1 when it could not be read.
        public static int FileLabel(string path)
        {
            IntPtr owner, group, dacl, sacl, sd = IntPtr.Zero;
            try
            {
                if (GetNamedSecurityInfo(path, 1 /* SE_FILE_OBJECT */, 0x10 /* LABEL_SECURITY_INFORMATION */, out owner, out group, out dacl, out sacl, out sd) != 0) return -1;
                if (sacl == IntPtr.Zero) return 0x2000;
                if (Marshal.ReadInt16(sacl, 4) < 1) return 0x2000;                   // AceCount
                IntPtr sid = new IntPtr(sacl.ToInt64() + 8 + 8);                    // ACL header (8) + ACE header (4) + mask (4)
                int count = Marshal.ReadByte(sid, 1);
                return count < 1 ? -1 : Marshal.ReadInt32(sid, 8 + 4 * (count - 1));
            }
            catch { return -1; }
            finally { if (sd != IntPtr.Zero) LocalFree(sd); }
        }
        static string LevelName(int r) { return r < 0 ? "unreadable" : r < 0x2000 ? "Low" : r < 0x3000 ? "Medium" : r < 0x4000 ? "High" : "System"; }

        // Facts only, for the log: this process's token level, the label on the program file, and whether the Claude settings folder was actually writable.
        public static string Describe()
        {
            string exe = ""; try { exe = Env.ExePath(); } catch { }
            string w = Diag.CanWriteDir(Env.ClaudeDir());
            return "process token " + Name() + "; program file label " + LevelName(FileLabel(exe)) + "; write test in " + Env.ClaudeDir() + ": " + (w == null ? "allowed" : "DENIED (" + w + ")");
        }
        // Shown to the user only when this process runs at Low AND the write into the Claude settings folder was really refused.
        // The cause is not guessed from the folder name: Windows gives a new process the lower of its launcher's level and its file's label, so the two are read and reported.
        public static string Problem()
        {
            if (!IsLow) return null;
            string w = Diag.CanWriteDir(Env.ClaudeDir()); if (w == null) return null;
            int label = FileLabel(Env.ExePath());
            string cause = label >= 0 && label < 0x2000
                ? Lang.T("程序文件本身带低完整性标签。把整个文件夹复制到普通位置（桌面等）再从那里启动，复制出来的文件通常不带这个标签。", "The program file itself carries a Low integrity label. Copy the whole folder to an ordinary place (e.g. the desktop) and start it from there; the copies normally do not carry the label.")
                : Lang.T("程序文件的标签是 " + LevelName(label) + "，所以是启动它的程序本身运行在低完整性。请从普通程序启动，例如在资源管理器里双击 pet.exe。", "The program file's label is " + LevelName(label) + ", so the program that started it runs at Low integrity. Start it from an ordinary program, e.g. double-click pet.exe in Explorer.");
            return Lang.T("小人现在以低完整性运行（" + Name() + "），写入 Claude Code 的设置目录被拒绝了：\r\n" + Env.ClaudeDir() + "\r\n（" + w + "）\r\n所以连接 Claude Code 会失败，也可能收不到点击。\r\n\r\n", "The pet runs at a low integrity level (" + Name() + ") and writing to Claude Code's settings folder was refused:\r\n" + Env.ClaudeDir() + "\r\n(" + w + ")\r\nSo connecting to Claude Code will fail, and clicks may not arrive.\r\n\r\n") + cause;
        }
    }
}
