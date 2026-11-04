// The desktop pet window: borderless, per-pixel-transparent, always on top. It draws the current skin picture for the
// current state and, when a session needs you, a speech bubble stuck to the pet (project name + reason). No pop-ups.
// Drag it to move it (the spot is remembered; while it is held it plays the "picked up" animation), click it for a little happy action,
// right-click it or the tray icon for the menu. It gets drowsy and falls asleep when nothing happens (a click wakes it), and hides itself
// while a full-screen app (slides, video, game) is in front.
// Env (tests): PET_LOG (event log), PET_DATA (settings folder), PET_INSTANCE (mutex suffix), PET_STARTUP_DIR, CLAUDE_COMPANION_DIR, CLAUDE_CONFIG_DIR, PET_SKIN,
//              PET_SLEEP_MS (quiet time before it gets drowsy; default 90 s without a session, 5 min with an idle one).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace ClaudeCompanionPet
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; public SIZE(int a, int b) { cx = a; cy = b; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Gradient, Anim; }
        [StructLayout(LayoutKind.Sequential)] struct CompAttr { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref CompAttr d);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        // blur what is behind the window (state 3 = blur, 4 = acrylic), clipped to a rounded rectangle at the window's top-left; false when the system refuses
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        // 0 = refused, 1 = blur clipped to a rounded region (corners are a pixel staircase), 2 = blur with corners rounded by the system itself (Windows 11, smooth)
        public static int Glass(IntPtr h, int state, int tintAbgr, int w, int ht, int radius)
        {
            try
            {
                AccentPolicy ap = new AccentPolicy { State = state, Flags = state == 0 ? 0 : 2, Gradient = tintAbgr, Anim = 0 };
                IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(AccentPolicy))); Marshal.StructureToPtr(ap, p, false);
                CompAttr ca = new CompAttr { Attr = 19, Data = p, Size = Marshal.SizeOf(typeof(AccentPolicy)) };
                int r = SetWindowCompositionAttribute(h, ref ca); Marshal.FreeHGlobal(p);
                if (r == 0) return 0;
                int pref = state == 0 ? 1 /* DWMWCP_DONOTROUND */ : 2 /* DWMWCP_ROUND */;
                bool sysRound = DwmSetWindowAttribute(h, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref pref, 4) == 0;
                if (state == 0 || sysRound) { SetWindowRgn(h, IntPtr.Zero, true); return state == 0 ? 1 : 2; }
                SetWindowRgn(h, CreateRoundRectRgn(0, 0, w, ht, (radius + 2) * 2, (radius + 2) * 2), true);   // a little tighter than the painted corner
                return 1;
            }
            catch { return 0; }
        }
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize, dwFlags; public IntPtr hwndTrack; public int dwHoverTime; }
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        public static IntPtr WindowAt(Point c) { return WindowFromPoint(new POINT(c.X, c.Y)); }
        // a per-pixel-alpha picture as the whole content of a layered window, top-left at (x,y) on the screen
        public static void PresentBitmap(IntPtr hwnd, Bitmap bmp, int x, int y, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBmp);
                POINT dst = new POINT(x, y), src = new POINT(0, 0);
                SIZE size = new SIZE(bmp.Width, bmp.Height);
                BLENDFUNCTION bf = new BLENDFUNCTION(); bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = alpha; bf.AlphaFormat = 1;
                UpdateLayeredWindow(hwnd, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, 2);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screenDc);
                if (hBmp != IntPtr.Zero) { SelectObject(memDc, old); DeleteObject(hBmp); }
                DeleteDC(memDc);
            }
        }
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    }

    class PetForm : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 8;
        const int WM_MOUSEACTIVATE = 0x21, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEMOVE = 0x200, WM_RBUTTONUP = 0x205;

        readonly string logFile, settingsFile, fontName;
        public readonly float k;                  // screen scale (dpi / 96)
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        View view = new View(); Board board = new Board();
        List<Skin> skins = new List<Skin>();
        Skin skin;
        string skinId = "";
        int winX, winY, winW, winH, tick;
        bool shown, userHidden, hideIdle, dragging, sound, firstView = true;
        // behaviour: what the pet does on top of the state picture (picked up, happy click, sleepy...)
        class Act { public string Name; public Skin.Clip Clip; public long Start; }
        Act act;
        bool pressed, lifted, fsHidden, drawn;
        long lastTouch = Env.Now(), lastActEnd, sleepAfterOverride;
        int sizeIdx = 1; static readonly float[] SIZES = { 0.75f, 1f, 1.5f }; static readonly string[] SIZE_KEYS = { "small", "medium", "large" };
        string screensSig = "", fsWhy = "";
        public bool SetupDone;
        int grabX, grabY;
        int ax = int.MinValue, ay = int.MinValue; // anchor: bottom-centre of the window
        string lastSig = "", lastTraySig = "", lastKey = "", flashText = "";
        long moodSince = Env.Now(), flashUntil;
        string moodKey = "";
        WizardForm wizard;
        // panel (session list), badge, long-task reminder
        PanelForm panel;
        bool pinned, hoverPet, panelWanted, panelSuppressed, wasDown, wasRight, doneChime, menuOpen;
        long menuClosedAt;
        long hoverSince, leaveAt; int longTaskSec = 120;
        bool floatOn = true; int floatX = int.MinValue, floatY; long panelHoverSince; bool prevPanelHover; int lastFx = int.MinValue, lastFy;   // the floating window is its own thing: own place, own on/off
        Rectangle figRect, bubbleRect;
        readonly Dictionary<string, long> doneSeen = new Dictionary<string, long>();
        static readonly int[] LONG_CHOICES = { 60, 120, 300, 600 };

        public PetForm(string demoState)
        {
            logFile = Env.Get("PET_LOG");
            settingsFile = Path.Combine(Env.DataDir(), "settings.txt");
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) k = g.DpiX / 96f;
            fontName = Array.Exists(FontFamily.Families, delegate (FontFamily f) { return f.Name == "Microsoft YaHei UI"; }) ? "Microsoft YaHei UI" : "Segoe UI";
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            Text = "claude-companion-pet"; Size = new Size(10, 10);
            LoadSettings();
            if (Env.Plugin) { SetupDone = true; hideIdle = true; } // the plugin has installed the hooks already; the pet hides while no session runs
            long so; if (long.TryParse(Env.Get("PET_SLEEP_MS") ?? "", out so) && so > 0) sleepAfterOverride = so;
            skins = Skin.Discover();
            SelectSkin(Env.Get("PET_SKIN") ?? skinId);
            IntPtr h = Handle; // create the window now; it is shown only when there is something to show
            panel = new PanelForm(this, fontName, k);
            Model.LongTaskMs = longTaskSec * 1000L;

            tray.Text = "claude-companion";
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { userHidden = !userHidden; Log(userHidden ? "user hid the window (tray)" : "user showed the window (tray)"); Sync(); } };
            menu.Opening += delegate { BuildMenu(); };
            menu.Opened += delegate { menuOpen = true; Log("menu shown at " + menu.Left + "," + menu.Top + " " + menu.Width + "x" + menu.Height); };
            menu.Closed += delegate (object o, ToolStripDropDownClosedEventArgs e) { menuOpen = false; menuClosedAt = Env.Now(); Log("menu closed (" + e.CloseReason + ")"); };
            tray.Icon = MakeIcon(null, "#8b8aa8");
            tray.Visible = true;

            Poll();
            timer.Interval = 40; timer.Tick += delegate { OnTick(); }; timer.Start();
            Log("ready zh=" + Lang.Zh + " scale=" + k.ToString(CultureInfo.InvariantCulture) + " skin=" + (skin == null ? "none" : skin.Id) + " skins=" + skins.Count);
            RefreshLinked();
            if (!Env.Plugin && linked && Connector.HookExeMissing())
            {   // the hooks name a copy of this program that was deleted or moved (a new version unpacked elsewhere): point them at this one, the same settings change the Connect command makes (a backup is kept)
                string fixErr = Connector.Connect(); Log("the connection named a program that is gone: repaired " + (fixErr == null ? "ok" : "FAILED: " + fixErr.Replace("\r\n", " | "))); RefreshLinked();
            }
            if (skin == null) Log("NO SKIN FOUND: looked in " + Path.Combine(Env.ExeDir(), "skins") + " and " + Path.Combine(Env.DataDir(), "skins"));
            string integrityNote = Env.Plugin ? null : Integrity.Problem();
            if (integrityNote != null) BeginInvoke((MethodInvoker)delegate { Log("integrity is Low and the settings folder is not writable: telling the user"); MessageBox.Show(integrityNote, "claude-companion", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
            // no setup window on the first start: the pet just appears (default skin: the small robot); "Connect Claude Code…" in the right-click menu opens the guide when wanted
            else if (!Env.Plugin) HelloBubble();
            if (skin == null) BeginInvoke((MethodInvoker)delegate { MessageBox.Show(Lang.T("找不到小人的皮肤（skins 文件夹）。\r\n请把 pet.exe 和 skins 文件夹放在一起，或重新解压整个压缩包。\r\n\r\n日志：", "The pet's skins (the skins folder) were not found.\r\nKeep pet.exe and the skins folder together, or unzip the whole package again.\r\n\r\nLog: ") + Diag.Path_(), "claude-companion", MessageBoxButtons.OK, MessageBoxIcon.Warning); });
        }

        // No session is not "broken": say what the state is in words, once at start (and again on a click while it is idle)
        public void HelloBubble()
        {
            if (view.Mode != "idle") return;
            Flash(linked ? Lang.T("已连接，待命中\n开个会话我就动起来", "Connected, ready\nStart a Claude Code session")
                         : Lang.T("还没连接 Claude Code\n右键我 → 连接", "Not connected yet\nRight-click me → Connect"), 9000);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        // ---------- log / settings ----------
        public void Log(string msg)
        {
            if (!msg.StartsWith("view ")) Diag.Write(msg); // the always-on log gets everything except the lines that carry project names
            if (string.IsNullOrEmpty(logFile)) return;
            try { File.AppendAllText(logFile, DateTime.UtcNow.ToString("o") + " " + msg + "\r\n"); } catch { }
        }
        bool linked;                                  // is Claude Code connected (our hooks in settings.json)? read at start, after connect/disconnect and when the menu opens
        public void RefreshLinked() { linked = Connector.IsConnected(); lastTraySig = ""; }
        public Rectangle PetArea() { return ax == int.MinValue ? Screen.PrimaryScreen.WorkingArea : Screen.FromPoint(new Point(ax, ay - 1)).WorkingArea; }
        void LoadSettings()
        {
            try
            {
                foreach (string line in File.ReadAllLines(settingsFile))
                {
                    int i = line.IndexOf('='); if (i < 0) continue;
                    string key = line.Substring(0, i), val = line.Substring(i + 1); int n;
                    if (key == "ax" && int.TryParse(val, out n)) ax = n;
                    else if (key == "ay" && int.TryParse(val, out n)) ay = n;
                    else if (key == "hideIdle") hideIdle = val == "1";
                    else if (key == "sound") sound = val == "1";
                    else if (key == "size") { int z = Array.IndexOf(SIZE_KEYS, val); if (z >= 0) sizeIdx = z; }
                    else if (key == "skin") skinId = val;
                    else if (key == "setupDone") SetupDone = val == "1";
                    else if (key == "longTaskSec" && int.TryParse(val, out n) && n >= 0) longTaskSec = n;
                    else if (key == "doneChime") doneChime = val == "1";
                    else if (key == "floatX" && int.TryParse(val, out n)) floatX = n;
                    else if (key == "floatY" && int.TryParse(val, out n)) floatY = n;
                    else if (key == "floatOn") floatOn = val != "0";
                    else if (key == "glass") PanelForm.GlassOn = val != "0";
                    else if (key == "strip") PanelForm.SimpleStrip = val != "full";
                    else if (key == "claudeApi") ClaudeApi.Enabled = val == "1";
                    else if (key == "lang") Lang.Set(val);
                    else if (key == "hideAfterMin" && int.TryParse(val, out n) && n >= 0) Backend.HideAfterMin = n;
                    else if (key == "look") Theme.Mode = val == "light" || val == "dark" ? val : null;
                }
            }
            catch { }
        }
        public void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                File.WriteAllText(settingsFile, "ax=" + ax + "\r\nay=" + ay + "\r\nhideIdle=" + (hideIdle ? "1" : "0") + "\r\nsound=" + (sound ? "1" : "0") + "\r\nsize=" + SIZE_KEYS[sizeIdx] + "\r\nskin=" + skinId + "\r\nsetupDone=" + (SetupDone ? "1" : "0") + "\r\nlongTaskSec=" + longTaskSec + "\r\ndoneChime=" + (doneChime ? "1" : "0") + "\r\n" + "\r\nfloatOn=" + (floatOn ? "1" : "0") + (floatX != int.MinValue ? "\r\nfloatX=" + floatX + "\r\nfloatY=" + floatY : "") + (Theme.Mode != null ? "\r\nlook=" + Theme.Mode : "") + "\r\nglass=" + (PanelForm.GlassOn ? "1" : "0") + "\r\nclaudeApi=" + (ClaudeApi.Enabled ? "1" : "0") + "\r\nstrip=" + (PanelForm.SimpleStrip ? "simple" : "full") + "\r\nhideAfterMin=" + Backend.HideAfterMin + "\r\nlang=" + (Lang.Zh ? "zh" : "en"));
            }
            catch (Exception e) { Log("settings not saved: " + e.Message); }
        }

        // ---------- skins ----------
        public List<Skin> Skins { get { return skins; } }
        public string SkinId { get { return skin == null ? "" : skin.Id; } }
        public void SelectSkin(string id)
        {
            Skin pick = skins.FirstOrDefault(s => s.Id == id) ?? skins.FirstOrDefault(s => s.Id == "robot") ?? skins.FirstOrDefault();
            skin = pick; skinId = pick == null ? "" : pick.Id; lastKey = "";
            Log("skin " + skinId);
            if (shown || pick != null) { try { SaveSettings(); } catch { } }
        }

        // ---------- state -> view ----------
        void Poll()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Dictionary<string, object> snap = Backend.ReadSnapshot();
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            Board bd = Display.Build(snap, Env.Now()); // the only source: the backend snapshot
            if (perfOn) PerfNote(t0, t1, System.Diagnostics.Stopwatch.GetTimestamp(), bd);
            LogSnapshot(snap);
            View v = bd.View; board = bd;
            CheckDone(v); CheckCtx(bd);
            string key = v.SkinState;
            if (key != moodKey) { moodKey = key; moodSince = Env.Now(); }
            if (v.Sig != lastSig)
            {
                Log("view " + v.Sig + (v.HasBubble ? " bubble=\"" + v.BProject + ": " + v.BReason + "\"" : ""));
                bool newNeed = v.HasBubble && (v.BKind == "approval" || v.BKind == "answer" || v.BKind == "error");
                if (newNeed && !firstView && sound) { try { SystemSounds.Exclamation.Play(); } catch { } }
                lastSig = v.Sig; lastTouch = Env.Now();
                if (Sleeping()) { Log("act " + act.Name + " cancelled (session change)"); act = null; lastKey = ""; }
            }
            firstView = false;
            view = v;
            bool need = v.Mood == "approval" || v.Mood == "answer" || v.Mood == "error";
            if (act != null && act.Name != "lift" && act.Name != "held" && act.Name != "drop" && (need || (Sleeping() && v.Mood != "idle")))
            { Log("act " + act.Name + " cancelled (" + v.Mood + ")"); act = null; lastKey = ""; }
            fsWhy = "";
            bool fs = FullScreenActive();
            if (fs != fsHidden) { fsHidden = fs; Log(fs ? "full-screen app in front: pet hidden (" + fsWhy + ")" : "full-screen app gone: pet back"); }
            string ss = string.Join(";", Screen.AllScreens.Select(sc => sc.WorkingArea.ToString()).ToArray());
            if (ss != screensSig) { if (screensSig != "") Log("screens changed: " + ss); screensSig = ss; lastKey = ""; }
        }
        void OnTick()
        {
            long t = perfOn ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { Tick(); } catch (Exception e) { Program.Problem("error in the pet's timer", e); }
            if (perfOn) { double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; pfTicks++; pfTickSum += ms; if (ms > pfTickMax) pfTickMax = ms; }
        }
        // PET_PERF (tests): one line every 30 s with how long the UI thread spent per timer tick, per snapshot read and per view build
        readonly bool perfOn = Env.Get("PET_PERF") != null;
        long pfNext; int pfTicks, pfPolls; double pfTickSum, pfTickMax, pfReadSum, pfReadMax, pfBuildSum, pfBuildMax; long pfAgeMax;
        void PerfNote(long t0, long t1, long t2, Board bd)
        {
            double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency, rd = (t1 - t0) * f, bl = (t2 - t1) * f; pfPolls++; pfReadSum += rd; pfBuildSum += bl;
            if (rd > pfReadMax) pfReadMax = rd; if (bl > pfBuildMax) pfBuildMax = bl;
            long age = bd.GeneratedAt > 0 ? Env.Now() - bd.GeneratedAt : -1; if (age > pfAgeMax) pfAgeMax = age;
            if (Env.Now() < pfNext) return;
            pfNext = Env.Now() + 30000;
            System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
            Diag.Write("perf: ticks " + pfTicks + " avg " + (pfTicks > 0 ? pfTickSum / pfTicks : 0).ToString("0.00") + " ms max " + pfTickMax.ToString("0.0") + " ms; snapshot reads " + pfPolls + " avg " + (pfPolls > 0 ? pfReadSum / pfPolls : 0).ToString("0.000") + " ms max " + pfReadMax.ToString("0.000") + " ms; view builds avg " + (pfPolls > 0 ? pfBuildSum / pfPolls : 0).ToString("0.000") + " ms max " + pfBuildMax.ToString("0.000") + " ms; max snapshot age " + pfAgeMax + " ms; working set " + me.WorkingSet64 / 1048576 + " MB private " + me.PrivateMemorySize64 / 1048576 + " MB threads " + me.Threads.Count + " handles " + me.HandleCount + " gc " + GC.GetTotalMemory(false) / 1048576 + " MB");
            pfTicks = 0; pfPolls = 0; pfTickSum = pfTickMax = pfReadSum = pfReadMax = pfBuildSum = pfBuildMax = 0; pfAgeMax = 0;
        }
        // one line whenever what the pet is told changes (counts and states, no names or paths)
        string lastSnapLine = ""; long lastSnapAt;
        void LogSnapshot(Dictionary<string, object> snap)
        {
            int vis = 0, all = 0; object so; List<object> ss = null;
            if (snap.TryGetValue("sessions", out so)) ss = so as List<object>;
            if (ss != null) foreach (object o in ss) { all++; Dictionary<string, object> d = J.Obj(o); if (d != null && d.ContainsKey("visible") && d["visible"] is bool && (bool)d["visible"]) vis++; }
            Dictionary<string, object> u = snap.ContainsKey("usage") ? J.Obj(snap["usage"]) : null, a = snap.ContainsKey("accounts") ? J.Obj(snap["accounts"]) : null, src = snap.ContainsKey("sources") ? J.Obj(snap["sources"]) : null;
            string line = "status " + J.Str(snap, "status") + " sessions " + vis + "/" + all + " usage " + (u == null ? "none" : J.Str(u, "state")) + " quota " + (a == null ? "none" : J.Str(a, "status")) + " sources " + (src == null ? "-" : J.Str(src, "claude_sessions") + "," + J.Str(src, "other_sessions") + "," + J.Str(src, "usage") + "," + J.Str(src, "accounts"));
            if (u != null && u.ContainsKey("windows"))
            {   // the figures the usage page shows, numbers only: today / 7 d / 30 d tokens, price status and scan completeness
                Dictionary<string, object> ws = J.Obj(u["windows"]); Dictionary<string, object> sc = u.ContainsKey("scan") ? J.Obj(u["scan"]) : null;
                foreach (string pk in new string[] { "today", "7d", "30d" })
                {
                    Dictionary<string, object> w = ws != null && ws.ContainsKey(pk) ? J.Obj(ws[pk]) : null; if (w == null) continue;
                    Dictionary<string, object> tk = w.ContainsKey("tokens") ? J.Obj(w["tokens"]) : null, cs = w.ContainsKey("cost") ? J.Obj(w["cost"]) : null;
                    line += " | " + pk + " tokens " + J.Long(tk, "total") + " cost " + J.Str(cs, "status") + " usd " + J.Num(cs, "usd", 0).ToString("0.####", CultureInfo.InvariantCulture) + " unpriced " + J.Long(cs, "unpriced_tokens") + " complete " + (w.ContainsKey("complete") ? w["complete"] : "?");
                }
                if (sc != null) line += " | scan complete " + (sc.ContainsKey("complete") ? sc["complete"] : "?") + " files " + J.Long(sc, "files_done") + "/" + J.Long(sc, "files_total");
            }
            long nowMs = Env.Now(); // figures move while the first scan runs: at most one line per 5 s
            if (line != lastSnapLine && nowMs - lastSnapAt >= 5000) { lastSnapLine = line; lastSnapAt = nowMs; Log("snapshot " + line); }
        }
        void Tick()
        {
            tick++;
            if (tick == 30 && Env.Get("PET_FAULT") == "tick") throw new InvalidOperationException("PET_FAULT test: an error inside the timer"); // tests only
            if (tick % 12 == 0) Poll();
            long now = Env.Now();
            if (Env.Plugin)
            {   // no session for a while: quit (the SessionStart hook of the next session starts it again)
                if (view.Mode != "idle") idleSince = 0; else if (idleSince == 0) idleSince = now;
                if (idleSince != 0 && now - idleSince > QuitAfter()) { Log("no session for " + (now - idleSince) / 1000 + " s: quit"); Quit(); return; }
            }
            Advance(now);
            TickPanel(now);
            if (act == null && !lifted && !pressed && view.Mood == "idle" && skin != null && skin.Has("sleepy")
                && now - Math.Max(lastTouch, Math.Max(moodSince, lastActEnd)) > SleepAfter())
            { Log("quiet for " + (now - Math.Max(lastTouch, Math.Max(moodSince, lastActEnd))) / 1000 + " s: drowsy"); StartAct("sleepy", "once"); }
            Sync();
            PlacePanel(now);
        }
        long idleSince;
        static long QuitAfter() { long v; return long.TryParse(Env.Get("PET_QUIT_MS") ?? "", out v) && v > 0 ? v : 600000; }
        long SleepAfter() { return sleepAfterOverride > 0 ? sleepAfterOverride : view.Mode == "idle" ? 90000 : 300000; }

        // ---------- behaviour: picked up, click, sleep ----------
        bool StartAct(string state, string dfltMode)
        {
            Skin.Clip c = skin == null ? null : skin.GetClip(state, dfltMode);
            if (c == null) { act = null; lastKey = ""; return false; }
            act = new Act { Name = state, Clip = c, Start = Env.Now() }; lastKey = "";
            Log("act " + state + " variant=" + c.Variant + " frames=" + c.Fr.Length);
            return true;
        }
        bool Sleeping() { return act != null && (act.Name == "sleepy" || act.Name == "asleep"); }
        void Advance(long now)
        {
            if (act == null || !act.Clip.Done(now - act.Start)) return;
            string n = act.Name; lastActEnd = now;
            if (n == "lift") { if (!StartAct("held", "pingpong")) act = null; }
            else if (n == "sleepy") { if (!StartAct("asleep", "pingpong")) act = null; }
            else { act = null; lastKey = ""; Log("act " + n + " finished"); }
        }
        void BeginLift()
        {
            lifted = true; lastTouch = Env.Now();
            Log("picked up");
            StartAct("lift", "once");
        }
        void EndLift()
        {
            lifted = false; lastTouch = Env.Now();
            Log("put down");
            if (!StartAct("drop", "once")) { act = null; lastKey = ""; }
        }
        // a click on the floating window (not on a tab, row or chip) pins it open or lets it go again
        public void PanelClick() { TogglePin(); }
        public bool Pinned { get { return pinned; } }
        int dragBaseX, dragBaseY;
        public void PanelDragStart() { dragBaseX = panel.X; dragBaseY = panel.Y; }
        public void PanelDrag(int dx, int dy) { panel.MoveTo(dragBaseX + dx, dragBaseY + dy); }
        public void PanelDragEnd()
        {   // the dropped place becomes the strip's place (an open panel anchors at the corner that has room, so invert that)
            panelSuppressed = !pinned && !panel.Expanded;
            Rectangle area = PetArea(); int m = (int)Math.Round(6 * k);
            Size cs; { bool e = panel.Expanded; panel.Expanded = false; cs = panel.Measure(board, Env.Now(), area.Width - 2 * m); panel.Expanded = e; }
            floatX = panel.X; floatY = panel.Y;
            if (panel.Expanded)
            {
                if (panel.X + panel.W / 2 > area.Left + area.Width / 2) floatX = panel.X + panel.W - cs.Width;
                if (panel.Y + panel.H / 2 > area.Top + area.Height / 2) floatY = panel.Y + panel.H - cs.Height;
            }
            lastFx = floatX; lastFy = floatY;
            SaveSettings(); Log("floating window moved to " + floatX + "," + floatY);
        }
        void TogglePin()
        {
            pinned = !pinned; panelSuppressed = false; lastTouch = Env.Now();
            Log(pinned ? "panel pinned (click)" : "panel unpinned (click)");
        }
        void PetClick()
        {
            long now = Env.Now(); lastTouch = now;
            if (skin == null) return;
            if (Sleeping()) { Log("click: wake up"); if (!StartAct("wake", "once")) { act = null; lastKey = ""; } return; }
            if (act != null) { Log("click ignored (" + act.Name + " is playing)"); return; }
            if (view.Mood == "approval" || view.Mood == "answer" || view.Mood == "error") { Log("click ignored (it is calling you)"); return; }
            if (now - lastActEnd < 1500) { Log("click ignored (cooldown)"); return; }
            if (view.Mode == "idle" && !Env.Plugin && !pinned) Flash(linked ? Lang.T("没有在运行的会话\n待命中", "No session running\nI'm on standby") : Lang.T("还没连接 Claude Code\n右键我 → 连接", "Not connected yet\nRight-click me → Connect"), 3500);
            if (!StartAct("happy", "once"))
            {   // a skin without its own happy actions: the "done" pictures once
                Skin.Clip c = skin.GetClip("done", "once");
                if (c == null) return;
                act = new Act { Name = "happy", Clip = c, Start = now }; lastKey = ""; Log("act happy variant=done frames=" + c.Fr.Length);
            }
            if (sound) { try { SystemSounds.Asterisk.Play(); } catch { } }
        }

        // a full-screen app (slide show, video, game, borderless full-screen window) is in front
        bool FullScreenActive()
        {
            try
            {
                // 3 = a Direct3D full-screen app (game), 4 = presentation mode. (2 = "busy" is not used: inside this process it read 2 while the same call from another process said 5.)
                int st; if (Native.SHQueryUserNotificationState(out st) == 0 && (st == 3 || st == 4)) { fsWhy = "shell state " + st; return true; }
                IntPtr fg = Native.GetForegroundWindow(); if (fg == IntPtr.Zero || fg == Handle || Native.IsIconic(fg)) return false;
                uint pid; Native.GetWindowThreadProcessId(fg, out pid); if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;
                StringBuilder cn = new StringBuilder(64); Native.GetClassName(fg, cn, 64);
                string c = cn.ToString(); if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return false;
                if ((Native.GetWindowLong(fg, -16) & 0xC00000) == 0xC00000) return false; // has a title bar: an ordinary (even maximised) window
                Native.RECT r; if (!Native.GetWindowRect(fg, out r)) return false;
                Rectangle b = Screen.FromHandle(fg).Bounds;
                bool cover = r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom;
                if (cover) fsWhy = "window " + c + " " + r.L + "," + r.T + "," + r.R + "," + r.B + " style 0x" + Native.GetWindowLong(fg, -16).ToString("x");
                return cover;
            }
            catch { return false; }
        }

        public void Flash(string text, int ms) { flashText = text; flashUntil = Env.Now() + ms; Sync(); }

        // ---------- drawing ----------
        void Sync()
        {
            bool want = !userHidden && !fsHidden && !(view.Mode == "idle" && hideIdle && flashText == "") && skin != null;
            if (want)
            {
                Draw();
                if (!shown && drawn) { shown = true; Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE); Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); Log("window shown (" + view.Mode + "/" + view.Mood + ") at " + winX + "," + winY + " " + winW + "x" + winH); }
            }
            else if (shown) { shown = false; Native.ShowWindow(Handle, Native.SW_HIDE); Log("window hidden (" + (userHidden ? "user" : fsHidden ? "full-screen app" : "no session") + ")"); }
            UpdateTray();
        }

        static SolidBrush Br(Color c) { return new SolidBrush(c); }

        void Draw()
        {
            if (flashText != "" && Env.Now() > flashUntil) flashText = "";
            bool active = view.Mode == "active";
            Act a = act;
            Bitmap pic = a != null ? a.Clip.At(Env.Now() - a.Start) : (view.Tired ? TiredPic() : null) ?? skin.Pick(view.SkinState, Env.Now() - moodSince);
            if (pic == null) return;
            bool flash = flashText != "";
            bool bubble = view.HasBubble || flash;
            string kind = flash ? "info" : view.BKind, l1 = flash ? flashText : PanelForm.BaseName(view.BProject), l2 = flash ? "" : view.BReason, l3 = flash ? "" : view.BMore;
            if (flash) { int nl = flashText.IndexOf('\n'); if (nl >= 0) { l1 = flashText.Substring(0, nl); l2 = flashText.Substring(nl + 1); } }
            long secs = Env.Now() / 1000;
            string key = Theme.Current().Dark + "|" + skin.Id + "|" + pic.GetHashCode() + "|" + view.Sig + "|" + (bubble ? kind + l1 + l2 + l3 : "") + "|" + (bubble ? (Env.Now() / 500 % 2) : 0) + "|" + view.Mode + "|" + userHidden + "|" + sizeIdx + "|" + view.Badge + view.BadgeKind + "|" + panelWanted;
            if (key == lastKey) return;
            lastKey = key;

            float sc = skin.Scale * k * SIZES[sizeIdx];   // the same size with or without a live session (it used to shrink to 70% when none ran)
            int rw = Math.Max(1, (int)Math.Round(pic.Width * sc)), rh = Math.Max(1, (int)Math.Round(pic.Height * sc));
            int pad = (int)Math.Round(3 * k);
            int bw = (int)Math.Round(292 * k), tail = (int)Math.Round(10 * k), gap = (int)Math.Round(4 * k), bob = (int)Math.Round(2 * k), bsh = (int)Math.Round(3 * k);
            float f1 = 16 * k, f2 = 14 * k, f3 = 12 * k;
            int lh1 = (int)Math.Round(f1 * 1.35), lh2 = (int)Math.Round(f2 * 1.35), lh3 = (int)Math.Round(f3 * 1.35);
            int bpad = (int)Math.Round(10 * k);
            int bh = bubble ? bpad * 2 + lh1 + (l2.Length > 0 ? lh2 : 0) + (l3.Length > 0 ? lh3 : 0) : 0;
            winW = Math.Max(rw, bubble ? bw + 2 * bsh : 0) + 2 * pad;
            winH = pad + rh + pad + (bubble ? bh + tail + gap + bob + bsh : 0);

            // keep the anchor (bottom-centre) fixed so the pet does not move when the bubble appears
            if (ax == int.MinValue) { Rectangle wa = Screen.PrimaryScreen.WorkingArea; ax = wa.Right - (int)(130 * k); ay = wa.Bottom - (int)(10 * k); }
            Rectangle area = Screen.FromPoint(new Point(ax, ay - 1)).WorkingArea; // the nearest screen, so a pet left on a screen that is gone comes back onto one that is there
            if (!dragging)
            {
                winX = Math.Min(Math.Max(ax - winW / 2, area.Left), Math.Max(area.Left, area.Right - winW));
                winY = Math.Min(Math.Max(ay - winH, area.Top), Math.Max(area.Top, area.Bottom - winH));
            }

            using (Bitmap bmp = new Bitmap(winW, winH, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                // pixel art: enlarging keeps hard pixels (nearest neighbour, also for 1.5x); shrinking first blows the picture up 4x with hard pixels and then averages it down,
                // so the result is a clean area average and not the smear of a bicubic filter working on every second pixel
                Bitmap shrinkSrc = null; Bitmap drawSrc = pic;
                if (sc < 0.999f) { shrinkSrc = new Bitmap(pic.Width * 4, pic.Height * 4, PixelFormat.Format32bppPArgb); using (Graphics ug = Graphics.FromImage(shrinkSrc)) { ug.InterpolationMode = InterpolationMode.NearestNeighbor; ug.PixelOffsetMode = PixelOffsetMode.Half; ug.CompositingMode = CompositingMode.SourceCopy; ug.DrawImage(pic, new Rectangle(0, 0, shrinkSrc.Width, shrinkSrc.Height), 0, 0, pic.Width, pic.Height, GraphicsUnit.Pixel); } drawSrc = shrinkSrc; }
                g.InterpolationMode = sc >= 0.999f ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half; g.SmoothingMode = SmoothingMode.None;
                int ox = (winW - rw) / 2, oy = winH - pad - rh;
                using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(hit, ox, oy, rw, rh); // alpha 1: invisible, but a fully transparent pixel lets the mouse through to the window behind
                g.DrawImage(drawSrc, new Rectangle(ox, oy, rw, rh), 0, 0, drawSrc.Width, drawSrc.Height, GraphicsUnit.Pixel);
                if (shrinkSrc != null) shrinkSrc.Dispose();
                figRect = new Rectangle(ox, oy, rw, rh);
                bubbleRect = bubble ? new Rectangle((winW - bw) / 2, 0, bw + bsh, bh + tail + bsh) : Rectangle.Empty;
                if (view.Badge > 0) DrawBadge(g, ox + (int)Math.Round(rw * 0.70), oy + (int)Math.Round(rh * 0.04), view.Badge, view.BadgeKind == "need");
                if (bubble)
                {
                    int off = (kind != "error" && kind != "info" && kind != "quota" && (Env.Now() / 500 % 2 == 0)) ? bob : 0; // bobs while it waits for you
                    DrawBubble(g, (winW - bw) / 2, off, bw, bh, tail, k, f1, f2, f3, lh1, lh2, lh3, bpad, kind, l1, l2, l3);
                }
                Present(bmp, (byte)255); drawn = true;
            }
        }

        void DrawBubble(Graphics g, int bx, int by, int bw, int bh, int tail, float kk, float f1, float f2, float f3, int lh1, int lh2, int lh3, int bpad, string kind, string l1, string l2, string l3)
        {
            Theme t = Theme.Current();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int r = (int)Math.Round(14 * kk), bd = Math.Max(1, (int)Math.Round(1 * kk)), sh = (int)Math.Round(3 * kk); float hb = bd / 2f;
            int cx = bx + bw / 2;
            // hard offset shadow, then the card, then the tail (a triangle that points at the pet) in the same outline
            Theme.SoftShadow(g, t, bx + hb, by + hb, bw - bd, bh - bd, r, Math.Max(2, sh), 0, 1);
            using (GraphicsPath p = Theme.Round(bx + hb, by + hb, bw - bd, bh - bd, r))
            {
                using (SolidBrush fill = Br(t.Paper)) g.FillPath(fill, p);
                using (Pen pen = new Pen(t.Edge, bd)) g.DrawPath(pen, p);
            }
            Point[] tri = new Point[] { new Point(cx - tail, by + bh - bd), new Point(cx + tail, by + bh - bd), new Point(cx, by + bh + tail) };
            using (SolidBrush fill = Br(t.Paper)) g.FillPolygon(fill, tri);
            using (Pen pen = new Pen(t.Edge, bd) { LineJoin = LineJoin.Miter }) { g.DrawLine(pen, tri[0], tri[2]); g.DrawLine(pen, tri[1], tri[2]); }
            using (SolidBrush fill = Br(t.Paper)) g.FillRectangle(fill, cx - tail + bd / 2 + 1, by + bh - bd - 1, 2 * tail - bd - 1, bd + 2); // opens the card's bottom edge where the tail starts
            // kind icon + text
            string ik = kind == "approval" ? "wait" : kind == "answer" ? "answer" : kind == "error" ? "error" : kind == "quota" ? "quota" : "";
            float tx = bx + bpad + 2 * kk, tw = bw - 2 * bpad - 4 * kk, ty = by + bpad;
            if (ik != "") { int d = (int)Math.Round(24 * kk); Theme.Icon(g, t, ik, bx + bpad, by + bpad + (lh1 - d) / 2f, d, kk); tx = bx + bpad + d + 8 * kk; tw = bw - bpad - (tx - bx) - bpad; }
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            StringFormat sf = new StringFormat(StringFormatFlags.NoWrap); sf.Trimming = StringTrimming.EllipsisCharacter;
            using (Font fb = new Font(fontName, f1, FontStyle.Bold, GraphicsUnit.Pixel))
            using (Font fr = new Font(fontName, f2, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font fm = new Font(fontName, f3, FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush ink = Br(t.Ink))
            using (SolidBrush mute = Br(t.Ink2))
            {
                g.DrawString(l1, fb, ink, new RectangleF(tx, ty, tw, lh1), sf); ty += lh1;
                if (l2.Length > 0) { g.DrawString(l2, fr, ink, new RectangleF(tx, ty, tw, lh2), sf); ty += lh2; }
                if (l3.Length > 0) g.DrawString(l3, fm, mute, new RectangleF(tx, ty, tw, lh3), sf);
            }
        }

        void Present(Bitmap bmp, byte alpha) { Native.PresentBitmap(Handle, bmp, winX, winY, alpha); }

        // number badge on the pet's corner: sessions that need you (orange), else how many sessions there are (calm blue)
        void DrawBadge(Graphics g, int x, int y, int n, bool need)
        {
            int d = (int)Math.Round(22 * k);
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (SolidBrush b = Br(ColorTranslator.FromHtml(need ? "#f0a020" : "#5b7fa8"))) g.FillEllipse(b, x, y, d, d);
            using (Pen p = new Pen(ColorTranslator.FromHtml("#2b2d33"), Math.Max(2f, 2 * k))) g.DrawEllipse(p, x, y, d, d);
            using (Font f = new Font(fontName, (n > 9 ? 11f : 13f) * k, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(n > 99 ? "99+" : n.ToString(), f, Brushes.White, new RectangleF(x, y + 1, d, d), sf);
        }

        // "tired" look when the context window is nearly full: the last (drowsiest) picture of the sleepy animation, if the skin has one
        Bitmap TiredPic()
        {
            if (skin == null || !skin.Has("sleepy")) return null;
            Bitmap[] fr = skin.Frames("sleepy"); return fr.Length == 0 ? null : fr[fr.Length - 1];
        }

        // ---------- panel: hover shows it, a click on the pet pins it, a click on a row goes back to that session ----------
        void TickPanel(long now)
        {
            if (Env.Get("PET_TEST") == null && Env.Get("PET_NO_HOVER") == null)
            {
                Point cur = Cursor.Position; IntPtr top = Native.WindowAt(cur);
                bool onPet = shown && !pressed && top == Handle && new Rectangle(winX + figRect.X, winY + figRect.Y, figRect.Width, figRect.Height).Contains(cur);
                if (onPet && !hoverPet) { hoverPet = true; hoverSince = now; Log("pointer on the pet"); }
                else if (!onPet && hoverPet) { hoverPet = false; leaveAt = now; Log("pointer off the pet"); }
                bool onPanel = panel.Up && top == panel.Handle && new Rectangle(panel.X, panel.Y, panel.W - (int)Math.Round(14 * k), panel.H - (int)Math.Round(14 * k)).Contains(cur);
                if (panel.Hover && !onPanel) { panel.Hover = false; leaveAt = now; }
                else if (!panel.Hover && onPanel) panel.Hover = true;
            }
            if (panel.Hover && !prevPanelHover) panelHoverSince = now;
            prevPanelHover = panel.Hover;
            // the pointer rests on the small strip for a moment: it opens into the full panel; it folds back shortly after the pointer leaves
            bool hover = (panel.Hover && now - panelHoverSince >= 180) || (panel.Expanded && (panel.Hover || now - leaveAt < 450));
            if (panelSuppressed && !panel.Hover) panelSuppressed = false;
            // pinned panel: a click anywhere else folds it away (the panel never takes focus, so "lost focus" is "clicked somewhere else")
            bool down = (Native.GetAsyncKeyState(1) & 0x8000) != 0;
            if (menuOpen && ((down && !wasDown) || (Native.GetAsyncKeyState(2) & 0x8000) != 0 && !wasRight))
            {   // a click anywhere outside the open menu closes it (a click on a window of another program that does not take the focus would otherwise leave it open)
                Point cur = Cursor.Position; bool inside = menu.Bounds.Contains(cur);
                foreach (ToolStripItem ti in menu.Items) { ToolStripMenuItem mi = ti as ToolStripMenuItem; if (mi != null && mi.DropDown.Visible && mi.DropDown.Bounds.Contains(cur)) inside = true; }
                if (!inside) { Log("click outside the menu: closing it"); menu.Close(); }
            }
            wasRight = (Native.GetAsyncKeyState(2) & 0x8000) != 0;
            if (pinned && down && !wasDown && !menuOpen && now - menuClosedAt > 400)
            {
                Point c = Cursor.Position;
                if (!new Rectangle(panel.X, panel.Y, panel.W, panel.H).Contains(c)) { pinned = false; Log("panel unpinned (click elsewhere)"); }
            }
            wasDown = down;
            // (while the window is carried it keeps the shape it had: an open panel does not fold under the hand, the strip does not open)
            bool want = floatOn && !panelSuppressed && (pinned || (panel.Dragging ? panel.Expanded : hover));
            if (want != panelWanted)
            {
                panelWanted = want; lastKey = ""; lastTouch = now;
                if (want) Log("panel wanted (" + (pinned ? "pinned" : "hover") + ") rows=" + view.Items.Count);
            }
        }
        void PlacePanel(long now)
        {
            if (!floatOn) { if (panel.Up) { panel.HideIt(); Log("floating window hidden"); } return; }
            if (panel.Dragging) { panel.Render(board, now); return; }   // carried by the hand: nothing else may move it (two writers of the position made it flicker)
            Rectangle area = PetArea(); int m = (int)Math.Round(6 * k);
            bool exp = panelWanted;
            if (exp != panel.Expanded) { panel.SetExpanded(exp); Log(exp ? "panel expanded (" + (pinned ? "pinned" : "hover") + ") rows=" + view.Items.Count : "panel folded to the small strip"); }
            panel.Expanded = exp;
            Size cs; { bool e = panel.Expanded; panel.Expanded = false; cs = panel.Measure(board, now, area.Width - 2 * m); panel.Expanded = e; }
            if (floatX == int.MinValue)
            {   // first start: beside the pet; afterwards the window stays wherever the user dropped it
                floatX = winX + figRect.X - cs.Width - m * 2;   // to the left of the pet, at the height of its feet, so the two never overlap
                floatY = winY + figRect.Bottom - cs.Height;
            }
            int fx = Math.Min(Math.Max(floatX, area.Left + m), Math.Max(area.Left + m, area.Right - cs.Width - m));
            int fy = Math.Min(Math.Max(floatY, area.Top + m), Math.Max(area.Top + m, area.Bottom - cs.Height - m));
            int px = fx, py = fy;
            Size sz = cs;
            if (exp)
            {   // the full panel grows from the strip's corner that has room (right edge fixed in the right half of the screen, bottom edge fixed in the lower half)
                sz = panel.Measure(board, now, area.Width - 2 * m);
                if (fx + cs.Width / 2 > area.Left + area.Width / 2) px = fx + cs.Width - sz.Width;
                if (fy + cs.Height / 2 > area.Top + area.Height / 2) py = fy + cs.Height - sz.Height;
                px = Math.Min(Math.Max(px, area.Left + m), Math.Max(area.Left + m, area.Right - sz.Width - m));
                py = Math.Min(Math.Max(py, area.Top + m), Math.Max(area.Top + m, area.Bottom - sz.Height - m));
                // while open, only the page changed (the window did not move): the top edge never drops, so the tab strip stays under the pointer
                if (panel.Up && fx == lastFx && fy == lastFy && wasExpanded && py > panel.Y) py = panel.Y;
            }
            lastFx = fx; lastFy = fy; wasExpanded = exp;
            bool was = panel.Up;
            int ox = panel.X, oy = panel.Y;
            panel.X = px; panel.Y = py;
            panel.Render(board, now);       // draws only when the content changed (or it was just hidden)
            string rows = panel.RowsScreen() + " tab " + panel.Tab + " tabs " + panel.TabsScreen() + " chips " + panel.ChipsScreen(); if (rows != lastRows) { lastRows = rows; Log("panel rows (screen): " + rows); }
            if (!was) { panel.ShowIt(); Log("floating window shown at " + panel.X + "," + panel.Y + " " + panel.W + "x" + panel.H); }
            else if (ox != px || oy != py) panel.MoveTo(px, py);
        }
        bool wasExpanded;
        string lastRows = "";
        public void PanelLeft() { leaveAt = Env.Now(); }

        // a click on a session row or on the bubble: the backend says whether there is a window and does the jump (Backend.TryJump re-checks it at the click)
        public void RowClick(Item it, string why)
        {
            if (it.NoWin)
            {
                string note = PanelForm.NoWindowNote(it);
                Log("go back (" + why + "): " + it.Provider + " session not jumpable: " + it.WindowReason);
                Flash(it.Name + "\n" + note, 4500);
                return;
            }
            GoTo(it, why);
        }

        public void GoTo(Item it, string why)
        {
            pinned = false; panelSuppressed = true; lastKey = "";
            string reason; bool ok = Backend.TryJump(it.Provider, it.Id, out reason);
            Log("go back (" + why + "): " + it.Provider + " session -> " + (ok ? "window raised" : "not raised") + " [" + reason + "]");
            if (ok) return;
            if (Display.IsPreRaiseFailure(reason)) Flash(it.Name + "\n" + Display.WindowNote(reason == "session_not_found" ? "no_usable_window" : reason, it.Provider), 5000);
            else Flash(Lang.T("Windows 没让我把窗口提到前面\n任务栏上的按钮在闪，点它就行", "Windows would not bring the window to the front\nits taskbar button is flashing - click it"), 6000);
        }

        // the context figure of a session, logged when it moves to another whole percent (numbers only: no names, no conversation)
        readonly Dictionary<string, long> ctxLogged = new Dictionary<string, long>();
        void CheckCtx(Board bd)
        {
            foreach (Item it in bd.View.Items)
            {
                if (it.Context == null) continue;
                CtxView c = Display.Context(bd, it);
                long key = (c.Pct < 0 ? -1 : (long)Math.Round(c.Pct) * 10) + (c.Window >= 1000000 ? 1 : 0), old; string k2 = it.Provider + ":" + it.Id;
                if (ctxLogged.TryGetValue(k2, out old) && old == key) continue;
                ctxLogged[k2] = key;
                Log("context " + it.Provider + " " + (c.Pct < 0 ? "no percentage" : "~" + Math.Round(c.Pct) + "%") + " (" + (c.HasUsed ? Display.Tok(c.Used) : "unknown") + " of " + (c.HasWindow ? Display.Tok(c.Window) : "unknown window") + ", " + J.Str(it.Context, "quality") + ")");
            }
        }

        // a finished session: a long task (worked longer than the threshold) gets the celebration, a short one only changes quietly
        void CheckDone(View v)
        {
            long now = Env.Now();
            foreach (Item it in v.Items)
            {
                if (it.State != "done") continue;
                long seen; if (doneSeen.TryGetValue(it.Id, out seen) && seen >= it.Since) continue;
                doneSeen[it.Id] = it.Since;
                if (firstView || now - it.Since > 20000) continue; // finished before the pet looked: nothing to celebrate now
                bool isLong = Model.LongTaskMs > 0 && it.BusyMs >= Model.LongTaskMs;
                Log("task finished: worked " + it.BusyMs / 1000 + " s, threshold " + Model.LongTaskMs / 1000 + " s: " + (isLong ? "long, celebrate" : "short, quiet") + (isLong && doneChime ? " + chime" : ""));
                Log("view task finished: " + it.Name);
                if (!isLong) continue;
                if (!lifted) StartAct("done", "once");
                Flash(Lang.T("做完了！\n" + it.Name + " · 忙了 " + PanelForm.Dur(it.BusyMs), "All done!\n" + it.Name + " · worked " + PanelForm.Dur(it.BusyMs)), 7000);
                if (doneChime) { if (Env.Get("PET_NO_AUDIO") != null) Log("chime (muted by PET_NO_AUDIO)"); else { try { SystemSounds.Asterisk.Play(); } catch { } } }
            }
        }

        // ---------- tray ----------
        static string LampOf(string mood) { switch (mood) { case "busy": return "#ffb347"; case "approval": return "#ffe27a"; case "answer": return "#8fe3ff"; case "done": return "#6fd98a"; case "error": return "#ff5f5f"; case "quotaOut": return "#4a4960"; } return "#8b8aa8"; }
        Icon MakeIcon(Bitmap pic, string lamp)
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                using (SolidBrush b = Br(ColorTranslator.FromHtml("#2b2d33"))) g.FillRectangle(b, 1, 1, 30, 30);
                if (pic != null)
                {   // the upper part of the picture (the face) fills the icon
                    int side = Math.Min(pic.Width, (int)(pic.Height * 0.62)); int sx = (pic.Width - side) / 2;
                    g.DrawImage(pic, new Rectangle(2, 2, 28, 28), sx, 0, side, side, GraphicsUnit.Pixel);
                }
                using (SolidBrush b = Br(ColorTranslator.FromHtml(lamp))) g.FillEllipse(b, 21, 21, 10, 10);
                IntPtr hi = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(hi).Clone();
                Native.DestroyIcon(hi);
                return ic;
            }
        }
        // the notification-area icon is a status badge: a ring that is as full as what is left of the tightest quota window (green / amber / red), the lamp of the busiest session in the middle
        Icon MakeStatusIcon(string lamp, double left)
        {
            using (Bitmap bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath tile = Theme.Round(0.5f, 0.5f, 31f, 31f, 8f)) using (SolidBrush b = Br(ColorTranslator.FromHtml("#24262c"))) g.FillPath(b, tile);
                Rectangle ring = new Rectangle(4, 4, 24, 24);
                using (Pen track = new Pen(Color.FromArgb(70, 255, 255, 255), 4f)) g.DrawEllipse(track, ring);
                if (left >= 0)
                {
                    Color rc = ColorTranslator.FromHtml(left < 15 ? "#ff6b61" : left < 30 ? "#f0b34a" : "#3cc7ad");
                    using (Pen arc = new Pen(rc, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) if (left > 1) g.DrawArc(arc, ring, -90, (float)(360.0 * Math.Min(100, left) / 100.0 * 0.999));
                }
                using (SolidBrush b = Br(ColorTranslator.FromHtml(lamp))) g.FillEllipse(b, 10, 10, 12, 12);
                IntPtr hi = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(hi).Clone();
                Native.DestroyIcon(hi);
                return ic;
            }
        }
        void UpdateTray()
        {
            double lc = board.Left("claude"), lx = board.Left("codex"), low = lc < 0 ? lx : lx < 0 ? lc : Math.Min(lc, lx);
            string sig = view.Mood + "|" + view.Tooltip + "|" + Math.Round(lc) + "|" + Math.Round(lx) + "|" + linked;
            if (sig == lastTraySig) return;
            lastTraySig = sig;
            Icon old = tray.Icon;
            tray.Icon = MakeStatusIcon(LampOf(view.Mood), low);
            if (old != null) old.Dispose();
            List<string> q = new List<string>();
            if (lc >= 0) q.Add("Claude " + Lang.T("剩 ", "") + Math.Round(lc) + "%");
            if (lx >= 0) q.Add("Codex " + Lang.T("剩 ", "") + Math.Round(lx) + "%");
            string state = view.Mode == "idle" && !linked && !Env.Plugin ? Lang.T("未连接 Claude Code（右键→连接）", "not connected (right-click → Connect)") : view.Tooltip;
            string tip = q.Count > 0 ? string.Join(" · ", q.ToArray()) + "\n" + state : state;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 62) + "…" : tip;
        }

        // ---------- menu ----------
        void BuildMenu()
        {
            menu.Items.Clear(); linked = Connector.IsConnected();
            Theme th = Theme.Current(); MenuStyle rd = new MenuStyle(th); Font mf = new Font("Segoe UI Semilight", 9f);
            menu.Renderer = rd; menu.Font = mf; menu.ShowImageMargin = false; menu.ShowCheckMargin = true;
            // 1. look and size
            ToolStripMenuItem sk = new ToolStripMenuItem(Lang.T("皮肤", "Skin"));
            foreach (Skin s in skins)
            {
                Skin sc = s; ToolStripMenuItem mi = new ToolStripMenuItem(s.Title); mi.Checked = skin != null && s.Id == skin.Id;
                mi.Click += delegate { SelectSkin(sc.Id); Sync(); };
                sk.DropDownItems.Add(mi);
            }
            sk.DropDownItems.Add(new ToolStripSeparator());
            ToolStripItem open = sk.DropDownItems.Add(Lang.T("打开皮肤文件夹", "Open the skins folder"));
            open.Click += delegate
            {
                string d = Path.Combine(Env.DataDir(), "skins");
                try { Directory.CreateDirectory(d); System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\""); } catch { }
            };
            menu.Items.Add(sk);
            ToolStripMenuItem sz = new ToolStripMenuItem(Lang.T("大小", "Size"));
            string[] szNames = { Lang.T("小", "Small"), Lang.T("中", "Medium"), Lang.T("大", "Large") };
            for (int i = 0; i < 3; i++)
            {
                int ii = i; ToolStripMenuItem mi = new ToolStripMenuItem(szNames[i]); mi.Checked = sizeIdx == i;
                mi.Click += delegate { sizeIdx = ii; SaveSettings(); Log("size " + SIZE_KEYS[ii]); lastKey = ""; Sync(); };
                sz.DropDownItems.Add(mi);
            }
            menu.Items.Add(sz);
            ToolStripMenuItem look = new ToolStripMenuItem(Lang.T("面板外观", "Panel look"));
            string[][] looks = { new string[] { "", Lang.T("跟随 Windows", "Follow Windows") }, new string[] { "light", Lang.T("浅色", "Light") }, new string[] { "dark", Lang.T("深色", "Dark") } };
            foreach (string[] lk in looks)
            {
                string key = lk[0]; ToolStripMenuItem mi = new ToolStripMenuItem(lk[1]); mi.Checked = (Theme.Mode ?? "") == key;
                mi.Click += delegate { Theme.Mode = key == "" ? null : key; SaveSettings(); Log("look " + (Theme.Mode ?? "system")); lastKey = ""; panel.Invalidate_(); };
                look.DropDownItems.Add(mi);
            }
            look.DropDownItems.Add(new ToolStripSeparator());
            foreach (bool simple in new bool[] { true, false })
            {
                bool sv = simple; ToolStripMenuItem mi = new ToolStripMenuItem(sv ? Lang.T("小窗：简洁", "Small window: simple") : Lang.T("小窗：详细", "Small window: detailed")); mi.Checked = PanelForm.SimpleStrip == sv;
                mi.Click += delegate { PanelForm.SimpleStrip = sv; SaveSettings(); Log("strip " + (sv ? "simple" : "full")); lastKey = ""; panel.Invalidate_(); };
                look.DropDownItems.Add(mi);
            }
            look.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem gl = new ToolStripMenuItem(Lang.T("毛玻璃", "Frosted glass")); gl.CheckOnClick = true; gl.Checked = PanelForm.GlassOn;
            gl.Click += delegate { PanelForm.GlassOn = gl.Checked; panel.GlassChanged(); SaveSettings(); Log("glass " + PanelForm.GlassOn); lastKey = ""; };
            look.DropDownItems.Add(gl);
            menu.Items.Add(look);
            menu.Items.Add(new ToolStripSeparator());
            // 2. what is shown
            ToolStripMenuItem fl = new ToolStripMenuItem(Lang.T("悬浮窗", "Floating window")); fl.CheckOnClick = true; fl.Checked = floatOn;
            fl.Click += delegate { floatOn = fl.Checked; if (!floatOn) pinned = false; SaveSettings(); Log("floatOn " + floatOn); lastKey = ""; };
            menu.Items.Add(fl);
            ToolStripMenuItem snd = new ToolStripMenuItem(Lang.T("静音", "Mute")); snd.CheckOnClick = true; snd.Checked = !sound;
            snd.Click += delegate { sound = !snd.Checked; SaveSettings(); Log(sound ? "sound on" : "muted"); };
            menu.Items.Add(snd);
            ToolStripItem hide = menu.Items.Add(userHidden ? Lang.T("显示小人", "Show the pet") : Lang.T("隐藏小人", "Hide the pet"));
            hide.Click += delegate { userHidden = !userHidden; Log(userHidden ? "user hid the window" : "user showed the window"); Sync(); };
            // 3. the rest
            ToolStripMenuItem more = new ToolStripMenuItem(Lang.T("更多", "More"));
            ToolStripMenuItem lt = new ToolStripMenuItem(Lang.T("长任务才庆祝", "Celebrate long tasks only"));
            foreach (int sec in LONG_CHOICES)
            {
                int ss = sec; ToolStripMenuItem mi = new ToolStripMenuItem(Lang.T("忙过 " + (ss / 60) + " 分钟", "after " + (ss / 60) + " min")); mi.Checked = longTaskSec == ss;
                mi.Click += delegate { longTaskSec = ss; Model.LongTaskMs = ss * 1000L; SaveSettings(); Log("longTaskSec " + ss); };
                lt.DropDownItems.Add(mi);
            }
            lt.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem chime = new ToolStripMenuItem(Lang.T("庆祝时响一声", "Chime when celebrating")); chime.CheckOnClick = true; chime.Checked = doneChime;
            chime.Click += delegate { doneChime = chime.Checked; SaveSettings(); Log("doneChime " + doneChime); };
            lt.DropDownItems.Add(chime);
            more.DropDownItems.Add(lt);
            if (!Env.Plugin)
            {
                ToolStripMenuItem hi = new ToolStripMenuItem(Lang.T("没有会话时隐藏", "Hide when no session")); hi.CheckOnClick = true; hi.Checked = hideIdle;
                hi.Click += delegate { hideIdle = hi.Checked; SaveSettings(); Log("hideIdle " + hideIdle); lastKey = ""; Sync(); };
                more.DropDownItems.Add(hi);
                ToolStripMenuItem au = new ToolStripMenuItem(Lang.T("开机自启", "Start at login")); au.CheckOnClick = true; au.Checked = AutostartOn();
                au.Click += delegate { SetAutostart(au.Checked); };
                more.DropDownItems.Add(au);
            }
            ToolStripMenuItem lg = new ToolStripMenuItem("Language / \u8bed\u8a00");
            foreach (string[] lo in new string[][] { new string[] { "en", "English" }, new string[] { "zh", "\u4e2d\u6587" } })
            {
                string code = lo[0]; ToolStripMenuItem mi = new ToolStripMenuItem(lo[1]); mi.Checked = (code == "zh") == Lang.Zh;
                mi.Click += delegate { Lang.Set(code); SaveSettings(); Log("lang " + code); lastKey = ""; lastTraySig = ""; panel.Invalidate_(); Sync(); };
                lg.DropDownItems.Add(mi);
            }
            more.DropDownItems.Insert(0, lg);
            ToolStripItem openStats = more.DropDownItems.Add(Lang.T("\u6253\u5f00\u7edf\u8ba1\u6587\u4ef6\u5939\uff08\u6700\u8fd1 30 \u5929\u660e\u7ec6\uff09", "Open the statistics folder (30 days of detail)"));
            openStats.Click += delegate
            {
                string d = Path.Combine(Env.DataDir(), "stats");
                try { Directory.CreateDirectory(d); System.Diagnostics.Process.Start("explorer.exe", "\"" + d + "\""); } catch { }
            };
            ToolStripMenuItem hm = new ToolStripMenuItem(Lang.T("无更新就隐藏会话", "Hide sessions with no update"));
            foreach (int mins in new int[] { 0, 3, 5, 10, 30 })
            {
                int mm = mins; ToolStripMenuItem mi = new ToolStripMenuItem(mm == 0 ? Lang.T("不隐藏", "Never") : Lang.T(mm + " 分钟", mm + " min")); mi.Checked = Backend.HideAfterMin == mm;
                mi.Click += delegate { Backend.HideAfterMin = mm; SaveSettings(); Log("hideAfterMin " + mm); lastKey = ""; };
                hm.DropDownItems.Add(mi);
            }
            more.DropDownItems.Add(hm);
            ToolStripMenuItem capi = new ToolStripMenuItem(Lang.T("用登录账号查 Claude 额度", "Read Claude limits from the account")); capi.CheckOnClick = true; capi.Checked = ClaudeApi.Enabled;
            capi.Click += delegate { ClaudeApi.Enabled = capi.Checked; SaveSettings(); Log("claudeApi " + ClaudeApi.Enabled); };
            more.DropDownItems.Add(capi);
            if (!Env.Plugin && linked)
            {
                more.DropDownItems.Add(new ToolStripSeparator());
                ToolStripItem dis = more.DropDownItems.Add(Lang.T("断开 Claude Code", "Disconnect Claude Code"));
                dis.Click += delegate { string e = Connector.Disconnect(); Log("disconnect " + (e == null ? "ok" : "FAILED: " + e.Replace("\r\n", " | "))); RefreshLinked(); Flash(e == null ? Lang.T("已断开\n设置已还原", "Disconnected\nSettings restored") : Lang.T("断开失败\n详情见日志", "Disconnect failed\nsee the log"), 6000); };
            }
            menu.Items.Add(more);
            // 4. the connection is only here when it needs you
            menu.Items.Add(new ToolStripSeparator());
            string stale = Env.Plugin || !linked ? null : Connector.StaleReason();
            if (!Env.Plugin && !linked)
            {
                ToolStripItem con = menu.Items.Add(Lang.T("连接 Claude Code…", "Connect Claude Code…"));
                con.Click += delegate { ShowWizard(); };
            }
            else if (stale != null)
            {
                ToolStripItem fix = menu.Items.Add(Lang.T("修复 Claude Code 连接", "Repair the Claude Code connection"));
                fix.ToolTipText = stale;
                fix.Click += delegate { string e = Connector.Connect(); Log("repair connection " + (e == null ? "ok" : "FAILED: " + e.Replace("\r\n", " | "))); RefreshLinked(); Flash(e == null ? Lang.T("连接已改到当前程序", "Connection now points to this program") : Lang.T("修复失败\n详情见日志", "Repair failed\nsee the log"), 6000); };
            }
            ToolStripItem quit = menu.Items.Add(Lang.T("退出", "Quit"));
            quit.Click += delegate { Log("quit (menu)"); Quit(); };
            StyleDrops(menu.Items, rd, mf);
            Log("menu opened: " + string.Join(" | ", menu.Items.Cast<ToolStripItem>().Where(i => !(i is ToolStripSeparator)).Select(i => i.Text).ToArray()) + (stale != null ? " [connection: " + stale + "]" : ""));
        }
        static void StyleDrops(ToolStripItemCollection items, MenuStyle rd, Font mf)
        {
            foreach (ToolStripItem it in items)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem; if (mi == null || !mi.HasDropDownItems) continue;
                ToolStripDropDownMenu dd = mi.DropDown as ToolStripDropDownMenu;
                if (dd != null) { dd.Renderer = rd; dd.Font = mf; dd.ShowImageMargin = false; dd.ShowCheckMargin = true; }
                StyleDrops(mi.DropDownItems, rd, mf);
            }
        }

        // The very first Show of a context menu returned with the menu not visible (seen with real mouse input, 2026-11-03); a second try always worked.
        void ShowMenu()
        {
            try
            {
                for (int i = 0; i < 3 && !menu.Visible; i++)
                {
                    menu.Show(Cursor.Position);
                    if (!menu.Visible) { Log("menu.Show #" + (i + 1) + " did not show the menu"); Application.DoEvents(); }
                }
            }
            catch (Exception e) { Log("menu.Show failed: " + e.GetType().Name + " " + e.Message); }
        }

        public void ShowWizard()
        {
            if (wizard != null && !wizard.IsDisposed) { wizard.Activate(); return; }
            wizard = new WizardForm(this); wizard.Show();
        }

        // "Start at login" = a shortcut in the user's Startup folder (no registry writes, easy to see and delete by hand)
        string StartupLink()
        {
            string dir = Env.Get("PET_STARTUP_DIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Path.Combine(dir, "claude-companion-pet.lnk");
        }
        public bool AutostartOn() { return File.Exists(StartupLink()); }
        public void SetAutostart(bool on)
        {
            try
            {
                string link = StartupLink();
                if (!on) { if (File.Exists(link)) File.Delete(link); }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(link));
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    object sh = Activator.CreateInstance(t);
                    object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { link });
                    Type st = sc.GetType();
                    st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Application.ExecutablePath });
                    st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                    st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, sc, new object[] { "claude-companion desktop pet" });
                    st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, sc, null);
                }
                Log("autostart " + (on ? "on" : "off") + " link=" + link);
            }
            catch (Exception e) { Log("autostart failed: " + e.Message); }
        }

        void Quit() { try { panel.Close(); } catch { } tray.Visible = false; tray.Dispose(); timer.Stop(); Application.Exit(); }

        // ---------- mouse: drag the pet, right-click for the menu ----------
        protected override void WndProc(ref Message m)
        {
            if (Env.Get("PET_TRACE") != null && ((m.Msg >= 0x201 && m.Msg <= 0x209) || m.Msg == 0x21 || m.Msg == 0x1C || m.Msg == 0x1F || m.Msg == 0x215 || m.Msg == 0x2A))
                Log("msg 0x" + m.Msg.ToString("x") + " lp " + (short)(m.LParam.ToInt64() & 0xFFFF) + "," + (short)((m.LParam.ToInt64() >> 16) & 0xFFFF) + " menuVisible=" + menu.Visible);
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE: m.Result = (IntPtr)3; return; // never take the focus from the terminal
                case WM_LBUTTONDOWN:
                    // While the context menu is open (and for a moment after it closed) Windows hands the click that dismisses it on to the window
                    // under the pointer; that click only closes the menu, it is not a click on the pet (real-mouse test R2, 2026-11-03).
                    if (menuOpen || menu.Visible || Env.Now() - menuClosedAt < 400)
                    {
                        Log("left button down ignored: it only closes the menu");
                        if (menu.Visible) menu.Close();
                        return;
                    }
                    pressed = true; dragging = false; lastTouch = Env.Now();
                    grabX = (short)(m.LParam.ToInt64() & 0xFFFF); grabY = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                    Native.SetCapture(Handle); return;
                case 0x2A: // WM_MOUSELEAVE
                    hoverPet = false; leaveAt = Env.Now(); return;
                case 0x8001: // tests only: "the user clicked somewhere else"
                    if (Env.Get("PET_TEST") != null && pinned) { pinned = false; Log("panel unpinned (click elsewhere)"); }
                    return;
                case WM_MOUSEMOVE:
                    if (!pressed && Env.Get("PET_NO_HOVER") == null)   // (PET_NO_HOVER: tests that must not be disturbed by the real pointer sitting over the pet)
                    {   // the pointer over the figure (not over the bubble) = hover: the panel comes up after a moment
                        int hx = (short)(m.LParam.ToInt64() & 0xFFFF), hy = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                        bool over = figRect.Contains(hx, hy);
                        if (over && !hoverPet)
                        {
                            hoverPet = true; hoverSince = Env.Now();
                            Native.TRACKMOUSEEVENT t = new Native.TRACKMOUSEEVENT(); t.cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)); t.dwFlags = 2; t.hwndTrack = Handle; Native.TrackMouseEvent(ref t);
                        }
                        else if (!over && hoverPet) { hoverPet = false; leaveAt = Env.Now(); }
                    }
                    if (pressed)
                    {   // a press that moves a few pixels becomes a drag (the pet is picked up); one that does not is a click
                        int x = (short)(m.LParam.ToInt64() & 0xFFFF), y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                        if (!dragging && (Math.Abs(x - grabX) >= SystemInformation.DragSize.Width || Math.Abs(y - grabY) >= SystemInformation.DragSize.Height)) { dragging = true; BeginLift(); }
                        if (dragging)
                        {
                            winX += x - grabX; winY += y - grabY;
                            Native.SetWindowPos(Handle, IntPtr.Zero, winX, winY, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | 4 /* NOZORDER */);
                        }
                    }
                    return;
                case WM_LBUTTONUP:
                    if (pressed)
                    {
                        pressed = false; Native.ReleaseCapture();
                        if (dragging)
                        {
                            dragging = false;
                            ax = winX + winW / 2; ay = winY + winH; SaveSettings(); Log("moved, anchor " + ax + "," + ay);
                            EndLift();
                        }
                        else
                        {
                            int cx = (short)(m.LParam.ToInt64() & 0xFFFF), cy = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                            Item first = view.Items.FirstOrDefault(i => i.Need);
                            if (view.HasBubble && flashText == "" && first != null && bubbleRect.Contains(cx, cy)) { RowClick(first, "bubble"); }
                            else PetClick();
                        }
                    }
                    return;
                // The right button DOWN used to fall through to Control.WndProc, which grabs the mouse (SetCapture) for the pet window and only lets it go on the
                // matching UP - but the UP is handled below without calling the base class. So after every right-click the pet kept the mouse: the next click
                // ANYWHERE on the screen was delivered to the pet as a click on it (right button release "counted as a click", click on the pet "folding" a panel,
                // the menu not showing the first time). Found with real mouse input 2026-11-03 (message trace: WM_LBUTTONDOWN at x=-320).
                case 0x204: case 0x207: return; // WM_RBUTTONDOWN, WM_MBUTTONDOWN: no capture
                case WM_RBUTTONUP:
                    {
                        Native.ReleaseCapture();
                        int x = (short)(m.LParam.ToInt64() & 0xFFFF), y = (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
                        Native.SetForegroundWindow(Handle);
                        // shown after this message is finished, at the pointer: the pet window may have changed size between button down and up
                        // (the bubble steps aside when the panel opens), and a menu opened in the middle of that did not always appear
                        BeginInvoke((MethodInvoker)delegate { ShowMenu(); });
                        return;
                    }
            }
            base.WndProc(ref m);
        }
    }
}
