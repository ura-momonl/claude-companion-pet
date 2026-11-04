// pet.exe as the hook and the status line of Claude Code (no Node needed):
//   pet.exe --cc-hook                     reads the hook JSON on stdin, records the session state (never prints, never fails)
//   pet.exe --cc-statusline [--chain b64] reads the status JSON on stdin, keeps the quota numbers, prints one line
// Same state files as the node hooks (src/events.mjs, src/handle.mjs, src/statusline.mjs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCompanionPet
{
    static class Hooks
    {
        static string ReadStdin()
        {
            try
            {
                using (Stream s = Console.OpenStandardInput())
                using (StreamReader r = new StreamReader(s, new UTF8Encoding(false)))
                    return r.ReadToEnd();
            }
            catch { return ""; }
        }
        static void Out(string text)
        {
            try { using (Stream o = Console.OpenStandardOutput()) { byte[] b = new UTF8Encoding(false).GetBytes(text); o.Write(b, 0, b.Length); o.Flush(); } } catch { }
        }

        // ---------- hook ----------
        public static int RunHook()
        {
            try
            {
                Dictionary<string, object> input = J.Obj(J.Parse(ReadStdin()));
                Handle(input, Env.Now());
                // Installed as a plugin (Claude Code sets CLAUDE_PLUGIN_ROOT for its hooks): a new session starts the pet if it is not running
                if (J.Str(input, "hook_event_name") == "SessionStart" && Env.Get("CLAUDE_PLUGIN_ROOT") != null) EnsurePet();
            }
            catch (Exception e) { Diag.Error("hook", e); } // a broken hook input must never disturb the session; it is logged
            return 0;
        }

        static void EnsurePet()
        {
            System.Threading.Mutex m;
            if (System.Threading.Mutex.TryOpenExisting(Program.MutexName(), out m)) { m.Dispose(); return; } // already running
            ProcessStartInfo psi = new ProcessStartInfo(Env.ExePath(), "--plugin");
            psi.UseShellExecute = true; psi.WorkingDirectory = Env.ExeDir(); // ShellExecute: the pet inherits none of the hook's pipes, so Claude Code does not wait for it
            Process.Start(psi);
        }

        // Hook event -> new state; null when the event does not change the state.
        public static Dictionary<string, object> NextState(Dictionary<string, object> input, Dictionary<string, object> prev, long now)
        {
            string ev = J.Str(input, "hook_event_name"), tool = J.Str(input, "tool_name");
            string state, detail = "";
            switch (ev)
            {
                case "SessionStart": state = J.Str(input, "source") == "compact" && prev != null ? J.Str(prev, "state") : "idle"; detail = J.Str(input, "source") == "compact" ? J.Str(prev, "detail") : ""; break;
                case "UserPromptSubmit": case "PostToolUse": case "PostToolUseFailure": state = "busy"; break;
                case "PreToolUse": if (tool == "AskUserQuestion") { state = "waiting"; detail = "answer"; } else state = "busy"; break;
                case "PermissionRequest": state = "waiting"; detail = tool == "AskUserQuestion" ? "answer" : "approval"; break;
                case "Notification":
                    string nt = J.Str(input, "notification_type");
                    if (nt == "permission_prompt") { state = "waiting"; detail = prev != null && J.Str(prev, "state") == "waiting" ? J.Str(prev, "detail") : "approval"; }
                    else if (nt == "elicitation_dialog" || nt == "elicitation_url_dialog") { state = "waiting"; detail = "answer"; }
                    else return null;
                    break;
                case "Stop": state = "done"; break;
                case "StopFailure": state = "error"; detail = J.Str(input, "error") == "" ? "unknown" : J.Str(input, "error"); break;
                case "SessionEnd": state = "ended"; break;
                case "PreCompact": case "PostCompact": case "CwdChanged": case "PostModelSwitch":
                    state = prev == null ? "unknown" : J.Str(prev, "state"); detail = J.Str(prev, "detail"); break;
                default: return null;
            }
            bool same = prev != null && J.Str(prev, "state") == state && J.Str(prev, "detail") == detail;
            Dictionary<string, object> n = prev == null ? new Dictionary<string, object>() : new Dictionary<string, object>(prev);
            n["state"] = state; n["detail"] = detail;
            n["since"] = same ? J.Long(prev, "since") : now;
            n["updated"] = now;
            n["session_id"] = J.Str(input, "session_id");
            string cwd = J.Str(input, "cwd"); if (cwd == "" && prev != null) cwd = J.Str(prev, "cwd");
            n["cwd"] = cwd;
            n["created"] = prev == null || J.Long(prev, "created") == 0 ? now : J.Long(prev, "created");
            if (J.Str(n, "project_dir") == "" && cwd != "") n["project_dir"] = cwd;
            string title = J.Str(input, "session_title"); if (title == "") title = J.Str(input, "session_name");
            if (title != "") n["session_name"] = title;
            n["last_event"] = ev;
            if (ev == "SessionStart") { n["ended"] = 0L; n["start_source"] = J.Str(input, "source"); }
            if (ev == "SessionEnd") n["ended"] = now;
            if (ev == "PreCompact" || ev == "PostCompact" || (ev == "SessionStart" && J.Str(input, "source") == "compact")) n["context_reset_at"] = now;
            // where the conversation file is (a path only; the pet reads the token counts at its end, nothing else) and which model the session started with
            string tp = J.Str(input, "transcript_path"); if (tp == "" && prev != null) tp = J.Str(prev, "transcript");
            if (tp != "") n["transcript"] = tp;
            string model = J.Str(input, "model"); if (model == "" && prev != null) model = J.Str(prev, "model");
            if (model != "") n["model"] = model;
            // working time of the current turn (busy stretches only, waiting for you is not counted): "long task" = this is over the threshold when it ends
            bool newTurn = ev == "UserPromptSubmit" || (ev == "SessionStart" && J.Str(input, "source") != "compact");
            long busy = prev == null || newTurn ? 0 : J.Long(prev, "busy_ms");
            if (prev != null && !newTurn && J.Str(prev, "state") == "busy" && J.Long(prev, "updated") > 0)
                busy += Math.Max(0, Math.Min(now - J.Long(prev, "updated"), 300000));
            n["busy_ms"] = busy;
            return n;
        }

        public static void Handle(Dictionary<string, object> input, long now)
        {
            string sid = J.Str(input, "session_id");
            if (sid == "") return;
            string ev = J.Str(input, "hook_event_name");
            int owner = ProcTree.FindClaudePid(); long born = ProcessEvidence.StartTicks(owner);
            using (Store.Lock(sid))
            {
                Dictionary<string, object> prev = J.ReadFile(Store.FileFor(sid, "json"));
                if (prev != null) now = Math.Max(now, J.Long(prev, "updated"));
                if (prev != null && born > 0 && J.Long(prev, "pid_start_ticks") > born) return;
                // A late event from a previous process must not undo a resume or an explicit end.
                if (prev != null && ev != "SessionStart")
                {
                    if (J.Long(prev, "ended") > 0) return;
                }
                Dictionary<string, object> next = NextState(input, prev, now);
                if (next == null) return;
                if (owner > 0 && born > 0) { next["pid"] = owner; next["pid_start_ticks"] = born; }
                else if (ev == "SessionStart" || prev == null) { next["pid"] = 0; next["pid_start_ticks"] = 0L; }
                if (ev == "SessionStart") { try { File.Delete(Store.FileFor(sid, "snap")); } catch (FileNotFoundException) { } }
                Store.WriteAtomic(Store.FileFor(sid, "json"), J.Compact(next));
            }
        }

        // ---------- status line ----------
        public static int RunStatusLine(string[] args)
        {
            string raw = ReadStdin();
            Dictionary<string, object> status = null;
            try { status = J.Obj(J.Parse(raw)); } catch { }
            try
            {
                string sid = J.Str(status, "session_id");
                if (sid != "")
                {
                    SaveStatus(status, Env.Now());
                }
            }
            catch (Exception e) { Diag.Error("statusline could not write its snapshot", e); }
            string chain = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--chain") { try { chain = Encoding.UTF8.GetString(Convert.FromBase64String(args[i + 1])); } catch { } }
            if (chain != null) { Out(RunOriginal(chain, raw)); return 0; }
            Out(StatusText(status) + "\n");
            return 0;
        }

        public static void SaveStatus(Dictionary<string, object> status, long now)
        {
            string sid = J.Str(status, "session_id"); if (sid == "") return;
            long born = ProcessEvidence.StartTicks(ProcTree.FindClaudePid());
            using (Store.Lock(sid))
            {
                Dictionary<string, object> state = J.ReadFile(Store.FileFor(sid, "json"));
                if (state != null && (J.Long(state, "ended") > 0 || (born > 0 && J.Long(state, "pid_start_ticks") > born))) return;
                Dictionary<string, object> prev = J.ReadFile(Store.FileFor(sid, "snap"));
                if (prev != null && J.Long(prev, "updated") > now) return;
                Dictionary<string, object> snap = new Dictionary<string, object>();
                snap["updated"] = now; snap["session_id"] = sid;
                Dictionary<string, object> ws = J.Obj(status.ContainsKey("workspace") ? status["workspace"] : null);
                string cwd = J.Str(ws, "current_dir"); if (cwd == "") cwd = J.Str(status, "cwd");
                snap["cwd"] = cwd; snap["project_dir"] = J.Str(ws, "project_dir");
                snap["session_name"] = J.Str(status, "session_name");
                Dictionary<string, object> rawCw = J.Obj(status.ContainsKey("context_window") ? status["context_window"] : null);
                Dictionary<string, object> cw = rawCw == null ? null : Numbers(rawCw, new string[] { "context_window_size", "used_percentage", "remaining_percentage" });
                if (cw != null)
                {
                    Dictionary<string, object> u = J.Obj(rawCw.ContainsKey("current_usage") ? rawCw["current_usage"] : null);
                    cw["current_usage"] = u == null ? null : Numbers(u, new string[] { "input_tokens", "output_tokens", "cache_creation_input_tokens", "cache_read_input_tokens" });
                }
                snap["context_window"] = cw;
                snap["ctx"] = cw != null && cw.ContainsKey("used_percentage") ? cw["used_percentage"] : null;
                Dictionary<string, object> rawLimits = J.Obj(status.ContainsKey("rate_limits") ? status["rate_limits"] : null), limits = null;
                if (rawLimits != null)
                {
                    limits = new Dictionary<string, object>();
                    foreach (string k in new string[] { "five_hour", "seven_day", "spend_limit" })
                    {
                        Dictionary<string, object> w = J.Obj(rawLimits.ContainsKey(k) ? rawLimits[k] : null);
                        if (w != null)
                        {
                            Dictionary<string, object> filtered = Numbers(w, new string[] { "used_percentage", "resets_at", "used_usd", "limit_usd" });
                            string period = J.Str(w, "period"); if (period == "daily" || period == "weekly" || period == "monthly") filtered["period"] = period;
                            limits[k] = filtered;
                        }
                    }
                }
                snap["rate_limits"] = limits;
                Dictionary<string, object> model = J.Obj(status.ContainsKey("model") ? status["model"] : null);
                snap["model"] = J.Str(model, "id");
                Store.WriteAtomic(Store.FileFor(sid, "snap"), J.Compact(snap));
                if (state != null && J.Str(state, "state") != "ended" && now >= J.Long(state, "updated"))
                {
                    if (cwd != "") state["cwd"] = cwd;
                    string project = J.Str(ws, "project_dir");
                    if (project != "" && J.Str(state, "project_source") != "statusline") { state["project_dir"] = project; state["project_source"] = "statusline"; }
                    if (J.Str(snap, "session_name") != "") state["session_name"] = snap["session_name"];
                    if (J.Str(snap, "model") != "") state["model"] = snap["model"];
                    Store.WriteAtomic(Store.FileFor(sid, "json"), J.Compact(state));
                }
            }
        }
        static Dictionary<string, object> Numbers(Dictionary<string, object> d, string[] keys)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (string k in keys)
            {
                object v; if (!d.TryGetValue(k, out v)) continue;
                if (v == null) { result[k] = null; continue; }
                if (!(v is int || v is long || v is double || v is decimal)) continue;
                double n = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
                if (!double.IsNaN(n) && !double.IsInfinity(n) && n >= 0) result[k] = v;
            }
            return result;
        }

        static string StatusText(Dictionary<string, object> status)
        {
            string line = "claude-companion";
            try
            {
                Dictionary<string, object> rl = J.Obj(status != null && status.ContainsKey("rate_limits") ? status["rate_limits"] : null);
                if (rl != null)
                    foreach (string[] k in new string[][] { new string[] { "five_hour", "5h" }, new string[] { "seven_day", "7d" } })
                    {
                        Dictionary<string, object> w = J.Obj(rl.ContainsKey(k[0]) ? rl[k[0]] : null);
                        double p = J.Num(w, "used_percentage", -1);
                        if (p >= 0) line += " · " + k[1] + " " + Math.Round(p) + "%";
                    }
            }
            catch { }
            return line;
        }

        // The user's own status line (kept by "connect") still runs and prints; we only listen on the side.
        static string RunOriginal(string command, string stdin)
        {
            try
            {
                string bash = null;
                foreach (string c in new string[] { Env.Get("CLAUDE_CODE_GIT_BASH_PATH"), @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" })
                    if (c != null && File.Exists(c)) { bash = c; break; }
                ProcessStartInfo psi;
                if (bash != null) { psi = new ProcessStartInfo(bash); psi.Arguments = "-c \"" + command.Replace("\"", "\\\"") + "\""; }
                else { psi = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\""); }
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                using (Process p = Process.Start(psi))
                {
                    byte[] b = new UTF8Encoding(false).GetBytes(stdin);
                    p.StandardInput.BaseStream.Write(b, 0, b.Length); p.StandardInput.Close();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(5000);
                    return o;
                }
            }
            catch { return ""; }
        }
    }

    // Which claude process is above this one (so a killed session, which never fires SessionEnd, can be dropped).
    // Walks the parent chain with NtQueryInformationProcess (works for any process we may open; no process snapshot).
    static class ProcTree
    {
        [StructLayout(LayoutKind.Sequential)]
        struct PBI { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }
        [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PBI pbi, int len, out int ret);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public static int ParentOf(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return 0;
            try { PBI pbi = new PBI(); int ret; return NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf(typeof(PBI)), out ret) == 0 ? (int)pbi.InheritedFromUniqueProcessId.ToInt64() : 0; }
            finally { CloseHandle(h); }
        }
        public static string NameOf(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try { StringBuilder sb = new StringBuilder(520); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? Path.GetFileName(sb.ToString()).ToLowerInvariant() : ""; }
            finally { CloseHandle(h); }
        }

        public static int FindClaudePid()
        {
            try
            {
                int cur = Process.GetCurrentProcess().Id;
                for (int i = 0; i < 14; i++)
                {
                    int parent = ParentOf(cur);
                    if (parent <= 4) break;
                    string name = NameOf(parent);
                    if (name == "claude.exe") return parent;
                    if (name == "node.exe" && NodeIsClaude(parent)) return parent; // the npm build of Claude Code
                    cur = parent;
                }
            }
            catch (Exception ex) { Env.DebugLog("FindClaudePid: " + ex); }
            return 0;
        }

        static bool NodeIsClaude(int pid)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId=" + pid))
                    foreach (System.Management.ManagementBaseObject o in s.Get())
                    {
                        string c = Convert.ToString(o["CommandLine"]) ?? "";
                        if (c.ToLowerInvariant().Replace('/', '\\').Contains("\\claude-code\\")) return true;
                    }
            }
            catch { }
            return false;
        }
    }
}
