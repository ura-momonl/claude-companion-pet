// Entry point. One exe, several jobs:
//   pet.exe                      the desktop pet (single instance)
//   pet.exe --cc-hook            Claude Code hook (reads stdin)
//   pet.exe --cc-statusline      Claude Code status line (reads stdin)
//   pet.exe --cc-connect | --cc-disconnect | --cc-status    same as the buttons, from a command line (prints one line)
//   pet.exe --lang zh|en         force the interface language (default: follows Windows)
//   pet.exe --plugin             the pet as started by the Claude Code plugin: no first-run guide, hidden while there is no session,
//                                quits after 10 minutes without one (the SessionStart hook starts it again)
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace ClaudeCompanionPet
{
    static class Program
    {
        public static string MutexName() { return @"Local\claude-companion-pet-" + (Env.Get("PET_INSTANCE") ?? "default"); }

        public static PetForm Pet;
        static string lastProblem = ""; static long lastProblemAt;
        public static void Problem(string what, Exception e)
        {
            Diag.Error(what, e);
            string key = e.GetType().Name + e.Message; long now = Env.Now();
            if (key == lastProblem && now - lastProblemAt < 60000) return; // the same error from a 40 ms timer: say it once a minute
            lastProblem = key; lastProblemAt = now;
            try { if (Pet != null) Pet.Flash(Lang.T("出了点小问题（已记入日志）", "Something went wrong (written to the log)"), 6000); } catch { }
        }

        [STAThread]
        static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "";
            if (mode == "--cc-hook") return Hooks.RunHook();
            if (mode == "--cc-statusline") return Hooks.RunStatusLine(args);
            Env.Plugin = Array.IndexOf(args, "--plugin") >= 0;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--lang") Lang.Set(args[i + 1]);
            if (mode == "--cc-connect" || mode == "--cc-disconnect" || mode == "--cc-status")
            {
                Diag.Startup(mode);
                string r = mode == "--cc-connect" ? Connector.Connect() : mode == "--cc-disconnect" ? Connector.Disconnect() : null;
                string line = r ?? (mode == "--cc-status" ? (Connector.IsConnected() ? "connected" : "not connected") : "ok");
                try { using (Stream o = Console.OpenStandardOutput()) { byte[] b = new UTF8Encoding(false).GetBytes(line + "\n"); o.Write(b, 0, b.Length); } } catch { }
                return r == null ? 0 : 1;
            }
            Diag.Startup("pet");
            bool created;
            Mutex mutex = new Mutex(true, MutexName(), out created);
            if (!created) { Diag.Write("another pet is already running (mutex " + MutexName() + "): this one exits"); return 0; } // one pet is enough
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            // An error inside a click or a timer must not open the grey "unhandled exception" dialog (that is what made the pet look dead):
            // write it to the log, say so next to the pet, carry on.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object o, ThreadExceptionEventArgs e) { Problem("error in the window code", e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object o, UnhandledExceptionEventArgs e) { Exception ex = e.ExceptionObject as Exception; if (ex != null) Diag.Error("fatal error", ex); };
            Services.Start(); // usage + quota + session snapshot: background threads, the UI only reads Backend.ReadSnapshot()
            PetForm form = null;
            try { form = new PetForm(null); }
            catch (Exception e) { Diag.Error("the pet could not start", e); Services.Stop(); MessageBox.Show(Lang.T("小人没能启动：", "The pet could not start: ") + e.Message + "\r\n\r\n" + Lang.T("日志：", "Log: ") + Diag.Path_(), "claude-companion", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            Pet = form;
            Application.Run(new ApplicationContext());
            Services.Stop();
            GC.KeepAlive(mutex); GC.KeepAlive(form);
            return 0;
        }
    }
}
