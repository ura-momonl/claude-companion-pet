// Small shared helpers: environment/paths, language, JSON in and out (no external libraries: .NET Framework 4, C# 5).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeCompanionPet
{
    static class Env
    {
        public static bool Plugin; // started by the Claude Code plugin (--plugin)
        public static string Get(string name) { string v = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? null : v; }
        // Tests point USERPROFILE / APPDATA / CLAUDE_CONFIG_DIR at a temp folder; GetFolderPath would ignore those.
        public static string Home() { return Get("USERPROFILE") ?? Get("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        public static string ClaudeDir() { return Get("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home(), ".claude"); }
        public static string ExePath() { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        public static string ExeDir() { return Path.GetDirectoryName(ExePath()); }
        public static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        static string dataDir, dataDirWhy = "";
        // Where settings live: %APPDATA%\claude-companion-pet, or next to the program when that folder cannot be written.
        public static string DataDir()
        {
            if (dataDir != null) return dataDir;
            string d = Get("PET_DATA") ?? Path.Combine(Get("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "claude-companion-pet");
            try { Directory.CreateDirectory(d); string probe = Path.Combine(d, ".probe"); File.WriteAllText(probe, "x"); File.Delete(probe); }
            catch (Exception e)
            {
                dataDirWhy = d + " is not writable (" + e.GetType().Name + ": " + e.Message + ")";
                d = Path.Combine(ExeDir(), "data"); try { Directory.CreateDirectory(d); } catch { }
            }
            dataDir = d; return d;
        }
        public static string DataDirWhy() { DataDir(); return dataDirWhy; }
        public static void DebugLog(string msg) { string f = Get("PET_LOG"); if (f != null) { try { File.AppendAllText(f, DateTime.UtcNow.ToString("o") + " " + msg + Environment.NewLine); } catch { } } }
        public static string StateDir() { return Get("CLAUDE_COMPANION_DIR") ?? Path.Combine(Path.GetTempPath(), "claude-companion"); }
    }

    // Always-on, small diagnostic log (pet.log in the data folder, or next to the program / in %TEMP% when that cannot be written).
    // It records what the program did and why something failed: paths, whether files exist and can be read or written, error types and
    // messages. It never records the content of settings.json, tokens, prompts or conversations.
    static class Diag
    {
        static string file; static readonly object gate = new object(); static bool started;
        public static string Path_() { Init(); return file; }
        static void Init()
        {
            if (file != null) return;
            string[] dirs = new string[] { Env.DataDir(), System.IO.Path.Combine(Env.ExeDir(), "data"), System.IO.Path.GetTempPath() };
            foreach (string d in dirs)
            {
                try
                {
                    Directory.CreateDirectory(d); string f = System.IO.Path.Combine(d, "pet.log");
                    if (File.Exists(f) && new FileInfo(f).Length > 262144) { string[] all = File.ReadAllLines(f, Encoding.UTF8); File.WriteAllLines(f, all.Skip(all.Length / 2).ToArray(), new UTF8Encoding(false)); }
                    File.AppendAllText(f, "", new UTF8Encoding(false)); file = f; return;
                }
                catch { }
            }
            file = "";
        }
        public static void Write(string msg)
        {
            try
            {
                lock (gate)
                {
                    Init(); if (string.IsNullOrEmpty(file)) return;
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + System.Diagnostics.Process.GetCurrentProcess().Id + "] " + msg + "\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }
        public static void Error(string what, Exception e)
        {
            Write(what + ": " + e.GetType().Name + " (0x" + System.Runtime.InteropServices.Marshal.GetHRForException(e).ToString("x8") + "): " + e.Message + "\r\n" + e.StackTrace);
        }
        // can a file be created in this folder? (used to say clearly why connecting is impossible)
        public static string CanWriteDir(string dir)
        {
            try { Directory.CreateDirectory(dir); string probe = System.IO.Path.Combine(dir, ".companion-probe-" + Environment.TickCount); File.WriteAllText(probe, "x"); File.Delete(probe); return null; }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }
        public static string FileInfoLine(string f)
        {
            try
            {
                if (!File.Exists(f)) return f + " (does not exist)";
                FileInfo fi = new FileInfo(f); string rd = "read ok";
                try { using (FileStream fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { fs.ReadByte(); } } catch (Exception e) { rd = "READ FAILS: " + e.GetType().Name + " " + e.Message; }
                return f + " (" + fi.Length + " bytes, modified " + fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + ", attributes " + fi.Attributes + ", " + rd + ")";
            }
            catch (Exception e) { return f + " (cannot inspect: " + e.Message + ")"; }
        }
        // one block at program start: where everything is and whether it can be read or written
        public static void Startup(string mode)
        {
            if (started) return; started = true;
            try
            {
                string exe = Env.ExePath(), claude = Env.ClaudeDir(), settings = System.IO.Path.Combine(claude, "settings.json");
                Write("---- start (" + mode + ") build " + BuildStamp() + " ----");
                Write("os " + Environment.OSVersion + " net " + Environment.Version + " ui-culture " + System.Globalization.CultureInfo.CurrentUICulture.Name + " 64bit " + Environment.Is64BitProcess + " user " + Environment.UserName);
                Write("exe " + exe + " ; args " + string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(a => a.Length > 40 ? a.Substring(0, 40) + "..." : a).ToArray()));
                Write("integrity: " + Integrity.Describe());
                Write("home " + Env.Home() + " ; claude dir " + claude + (Env.Get("CLAUDE_CONFIG_DIR") != null ? " (from CLAUDE_CONFIG_DIR)" : ""));
                Write("settings " + FileInfoLine(settings));
                string w = CanWriteDir(claude); Write("claude dir writable: " + (w ?? "yes"));
                Write("data dir " + Env.DataDir() + (Env.DataDirWhy() != "" ? " ; FALLBACK: " + Env.DataDirWhy() : " (ok)"));
                Write("log file " + Path_());
                Write("env PET_DATA=" + (Env.Get("PET_DATA") != null) + " PET_LOG=" + (Env.Get("PET_LOG") != null) + " CLAUDE_COMPANION_DIR=" + (Env.Get("CLAUDE_COMPANION_DIR") != null) + " ; state dir " + Env.StateDir());
            }
            catch (Exception e) { Write("startup diagnostics failed: " + e.Message); }
        }
        static string BuildStamp() { try { return File.GetLastWriteTime(Env.ExePath()).ToString("yyyy-MM-dd HH:mm") + ", " + new FileInfo(Env.ExePath()).Length + " bytes"; } catch { return "?"; } }
    }

    static class Lang
    {
        public static bool Zh = false;   // English until the user picks Chinese (menu: More > Language, or --lang zh)
        public static string T(string zh, string en) { return Zh ? zh : en; }
        public static void Set(string lang) { if (lang == "zh") Zh = true; else if (lang == "en") Zh = false; }
    }

    static class J
    {
        static readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
        public static object Parse(string s) { return ser.DeserializeObject(s); }
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static string Str(Dictionary<string, object> d, string key)
        {
            object v; return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
        }
        public static long Long(Dictionary<string, object> d, string key)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }
        public static double Num(Dictionary<string, object> d, string key, double dflt)
        {
            object v; if (d == null || !d.TryGetValue(key, out v) || v == null) return dflt;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return dflt; }
        }
        public static Dictionary<string, object> ReadFile(string file)
        {
            try { return Obj(Parse(File.ReadAllText(file, Encoding.UTF8))); } catch { return null; }
        }
        public static string Compact(object o) { return ser.Serialize(o); }

        // Indented output (2 spaces, like the files Claude Code writes itself).
        public static string Pretty(object o) { StringBuilder sb = new StringBuilder(); Write(sb, o, 0); return sb.ToString(); }
        static void Write(StringBuilder sb, object o, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            string s = o as string;
            if (s != null) { Quote(sb, s); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            IDictionary dict = o as IDictionary;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n"); bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(",\n"); first = false;
                    sb.Append(' ', 2 * (depth + 1)); Quote(sb, Convert.ToString(e.Key)); sb.Append(": "); Write(sb, e.Value, depth + 1);
                }
                sb.Append('\n').Append(' ', 2 * depth).Append('}'); return;
            }
            IEnumerable list = o as IEnumerable;
            if (list != null)
            {
                List<object> items = new List<object>(); foreach (object x in list) items.Add(x);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(",\n");
                    sb.Append(' ', 2 * (depth + 1)); Write(sb, items[i], depth + 1);
                }
                sb.Append('\n').Append(' ', 2 * depth).Append(']'); return;
            }
            IFormattable f = o as IFormattable;
            sb.Append(f != null ? f.ToString(null, CultureInfo.InvariantCulture) : Convert.ToString(o, CultureInfo.InvariantCulture));
        }
        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
