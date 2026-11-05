// Session meaning has one owner. UI consumes this snapshot, not filenames/cwd/PID guesses.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClaudeCompanionPet
{
    static class ProcessEvidence
    {
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
        public static long StartTicks(int pid)
        {
            if (pid <= 0) return 0;
            IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return 0;
            try { long a, b, c, d; return GetProcessTimes(h, out a, out b, out c, out d) ? a : 0; }
            finally { CloseHandle(h); }
        }
        public static string Check(int pid, long expected, out string reason)
        {
            reason = "no_process_identity";
            if (pid <= 0) return "unknown";
            IntPtr h = OpenProcess(0x101000, false, pid);
            if (h == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                reason = error == 87 ? "process_gone" : "process_access_unavailable";
                return error == 87 ? "dead" : "unknown";
            }
            try
            {
                uint wait = WaitForSingleObject(h, 0);
                if (wait == 0) { reason = "process_exited"; return "dead"; }
                long a, b, c, d;
                if (!GetProcessTimes(h, out a, out b, out c, out d)) { reason = "process_start_unavailable"; return "unknown"; }
                if (expected <= 0) { reason = "legacy_pid_unverified"; return "unknown"; }
                if (a != expected) { reason = "pid_reused"; return "dead"; }
                if (wait != 258) { reason = "process_wait_unavailable"; return "unknown"; }
                reason = "pid_and_start_match"; return "alive";
            }
            finally { CloseHandle(h); }
        }
    }

    static class SessionBackend
    {
        public static string ProjectName(string path)
        {
            string c = (path ?? "").TrimEnd('/', '\\');
            if (c == "") return "Unknown project";
            try { string name = Path.GetFileName(c.Replace('/', Path.DirectorySeparatorChar)); return name == "" ? c : name; }
            catch { return "Unknown project"; }
        }
        public static string ShortId(string id)
        {
            // A digest suffix distinguishes arbitrary IDs sharing a prefix, not only UUIDs.
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
            {
                string hash = BitConverter.ToString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(id ?? ""))).Replace("-", "").ToLowerInvariant();
                return (id ?? "").Length >= 8 ? id.Substring(0, 4) + "-" + hash.Substring(0, 6) : (id ?? "") + "-" + hash.Substring(0, 6);
            }
        }
        public static void Assess(Sess s, long now)
        {
            if (s.LastState == "") s.LastState = s.State;
            s.State = s.LastState; s.StateQuality = "observed";
            if (s.Provider == "deepseek" && (s.OwnershipEvidence == "bridge_process_alive" || s.OwnershipEvidence == "bridge_recent_event"))
            {   // DSH's bridge reports real session events, so the state is observed, not inferred from a file time. The
                // process that writes the bridge file is the liveness evidence; a recent event is weaker evidence when the
                // pid cannot be checked (protected process). Nothing in the panel claims a jump for a dsh session.
                bool held = now - s.SourceCheckedAt < 15000;
                s.Life = held ? "alive" : "unknown"; s.LifeReason = held ? s.OwnershipEvidence : "bridge_evidence_stale";
                if (!held) { s.State = "unknown"; s.StateQuality = "liveness_unknown"; }
                else if ((s.State == "busy" || s.State == "waiting") && now - s.Updated > 900000) { s.State = "unknown"; s.StateQuality = "stale_event"; }
                s.Visible = held || now - s.Updated < 21600000;
                s.CanJump = false; s.WindowReason = "no_verified_process_mapping"; return;
            }
            if (s.Provider == "deepseek" && s.OwnershipEvidence == "file_activity_only")
            {   // dsh gives us a last-write time and nothing else: no lock, no process, no approval event. Keep it visible while it is recent,
                // but never dress the file activity up as a state.
                s.Life = "unknown"; s.LifeReason = "file_activity_only";
                s.State = "unknown"; s.StateQuality = "file_activity_only";
                s.Visible = s.Updated > 0 && now - s.Updated < 21600000;
                s.CanJump = false; s.WindowReason = "no_verified_process_mapping"; return;
            }
            if (s.Provider == "codex" && s.OwnershipEvidence != "")
            {
                bool held = s.OwnershipEvidence == "writer_lock_held" && now - s.SourceCheckedAt < 15000;
                s.Life = held ? "alive" : "unknown"; s.LifeReason = held ? "writer_lock_held" : s.OwnershipEvidence == "writer_lock_held" ? "ownership_evidence_stale" : s.OwnershipEvidence;
                s.Visible = held; s.State = "unknown"; s.StateQuality = "activity_unavailable";
                s.CanJump = false; s.WindowReason = "no_verified_process_mapping"; return;
            }
            if (s.Ended > 0 || s.LastState == "ended") { s.Life = "dead"; s.LifeReason = "session_end"; }
            else { string why; s.Life = ProcessEvidence.Check(s.Pid, s.PidStartTicks, out why); s.LifeReason = why; }
            s.Visible = s.Life != "dead" && (s.Life == "alive" || now - s.Updated < 21600000);
            if (s.Life == "dead") { s.State = "ended"; s.StateQuality = "terminated"; }
            else if (s.Life == "unknown") { s.State = "unknown"; s.StateQuality = "liveness_unknown"; }
            else if ((s.State == "busy" || s.State == "waiting") && now - s.Updated > 900000)
            { s.State = "unknown"; s.StateQuality = "stale_event"; }
            s.CanJump = false; s.WindowReason = s.Life == "alive" ? "not_checked" : "process_not_verified_alive";
        }
        public static void CheckWindow(Sess s)
        {
            s.CanJump = false;
            if (s.Life != "alive") { s.WindowReason = "process_not_verified_alive"; return; }
            if (s.Pid <= 0 || s.PidStartTicks <= 0) { s.WindowReason = "no_verified_process_mapping"; return; }
            try
            {
                string how; TermWindow.Win w = TermWindow.Find(s.Pid, s.ProjectName, out how);
                if (w == null) { s.WindowReason = "no_usable_window"; return; }
                if (how.IndexOf(" of ", StringComparison.Ordinal) >= 0) { s.WindowReason = "ambiguous_window"; return; }
                s.CanJump = true; s.WindowReason = "window_available";
            }
            catch { s.WindowReason = "window_probe_unavailable"; }
        }
        public static Dictionary<string, object> Serialize(Sess s)
        {
            return new Dictionary<string, object> {
                { "provider", s.Provider }, { "session_id", s.Id }, { "key", s.Provider + ":" + s.Id }, { "display_name", s.Name },
                { "project", new Dictionary<string, object> { { "name", s.ProjectName }, { "path", s.ProjectDir } } },
                { "cwd", s.Cwd }, { "model", s.ModelName }, { "created_at", s.Created > 0 ? (object)s.Created : null },
                { "state", s.State }, { "last_observed_state", s.LastState }, { "state_detail", s.Detail },
                { "state_reason", s.StateReason },
                { "state_since", s.StateQuality == "observed" ? (s.Since > 0 ? (object)s.Since : null) : s.LifeReason == "session_end" ? (object)s.Ended : null },
                { "last_observed_state_since", s.Since > 0 ? (object)s.Since : null }, { "state_quality", s.StateQuality }, { "last_event_at", s.Updated > 0 ? (object)s.Updated : null },
                { "visible", s.Visible }, { "liveness", new Dictionary<string, object> { { "status", s.Life }, { "reason", s.LifeReason }, { "detail", s.LifeDetail } } },
                { "window", new Dictionary<string, object> { { "can_jump", s.CanJump }, { "reason", s.WindowReason }, { "scope", s.CanJump ? "window_not_tab" : "none" } } },
                { "context", s.Context }, { "busy_ms", s.BusyMs }
            };
        }
        // Codex source metadata is private input, not a second public session protocol.
        public static Sess FromSource(Dictionary<string, object> d)
        {
            Sess s = new Sess(); s.Id = J.Str(d, "session_id"); s.Provider = J.Str(d, "provider");
            if (s.Provider == "") s.Provider = "codex";
            s.Agent = s.Provider == "codex" ? "Codex" : "Claude";
            s.Cwd = J.Str(d, "cwd"); s.ProjectDir = J.Str(d, "project_dir"); if (s.ProjectDir == "") s.ProjectDir = s.Cwd;
            s.ProjectName = ProjectName(s.ProjectDir); string title = J.Str(d, "display_name");
            s.Name = title == "" ? s.ProjectName + " · " + ShortId(s.Id) : title;
            s.State = J.Str(d, "state"); if (s.State == "") s.State = "unknown";
            s.Detail = J.Str(d, "detail"); s.Updated = J.Long(d, "updated"); s.Since = J.Long(d, "since"); s.Created = J.Long(d, "created"); s.Ended = J.Long(d, "ended");
            s.StateReason = s.Detail; s.LifeDetail = J.Str(d, "lifecycle_reason"); s.OwnershipEvidence = J.Str(d, "lifecycle"); s.SourceCheckedAt = J.Long(d, "checked_at");
            s.Pid = (int)J.Long(d, "pid"); s.PidStartTicks = J.Long(d, "pid_start_ticks"); s.ModelName = J.Str(d, "model");
            s.Context = J.Obj(d.ContainsKey("context") ? d["context"] : null);
            if (s.Context != null)
            {
                s.Context = new Dictionary<string, object>(s.Context);
                foreach (string key in new string[] { "used_tokens", "window_tokens", "used_percentage", "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens", "observed_at" }) if (!s.Context.ContainsKey(key)) s.Context[key] = null;
                // The source already owns count semantics, sample time, quality and provenance.
                if (!s.Context.ContainsKey("source")) s.Context["source"] = "unknown";
                if (!s.Context.ContainsKey("window_source")) s.Context["window_source"] = "unknown";
                s.Ctx = J.Num(s.Context, "used_percentage", -1); s.CtxUsed = J.Long(s.Context, "used_tokens"); s.CtxWindow = J.Long(s.Context, "window_tokens");
            }
            Assess(s, Env.Now()); return s;
        }
    }

    static class Backend
    {
        static readonly object gate = new object();
        static readonly object lifecycle = new object();
        public static volatile int HideAfterMin = 5;   // minutes without any update before a session leaves the live list; 0 = never
        static readonly ManualResetEvent stop = new ManualResetEvent(false);
        static Thread worker;
        static Func<List<Dictionary<string, object>>> extraSessions;
        static Func<Dictionary<string, object>> usageSource, accountSource;
        static Dictionary<string, object> snapshot;
        static List<Sess> sessions = new List<Sess>();
        // Set once before Start. Each optional module owns its own Start/Stop and metadata semantics.
        public static void ConfigureSources(Func<List<Dictionary<string, object>>> otherSessions, Func<Dictionary<string, object>> usage, Func<Dictionary<string, object>> accounts)
        {
            lock (lifecycle) { if (worker != null) throw new InvalidOperationException("Configure before Backend.Start"); extraSessions = otherSessions; usageSource = usage; accountSource = accounts; }
        }
        public static void Start()
        {
            lock (lifecycle)
            {
                if (worker != null) return;
                stop.Reset(); worker = new Thread(Run); worker.IsBackground = true; worker.Name = "pet-session-snapshot"; worker.Start();
            }
        }
        public static void Stop()
        {
            lock (lifecycle)
            {
                if (worker == null) return; stop.Set();
                if (!worker.Join(5000)) throw new TimeoutException("Session backend has not stopped"); worker = null;
            }
        }
        static void Run()
        {
            do { try { Refresh(); } catch (Exception e) { Diag.Error("session snapshot", e); } } while (!stop.WaitOne(2000));
        }
        // Claude Code hands its own status line the account's 5-hour and weekly limits (rate_limits: used_percentage + resets_at in epoch seconds) while an
        // interactive session is open. No credential is read: the numbers are what Claude Code itself passed to the status line, so they are as old as the
        // last interactive session's last update (marked stale after 30 minutes) and are only known for subscription accounts.
        static Dictionary<string, object> WithClaudeLimits(Dictionary<string, object> accounts, long now)
        {
            if (accounts == null) return null;
            Dictionary<string, object> best = null; long bestAt = -1;
            string[] files = new string[0];
            try { files = Directory.GetFiles(Env.StateDir(), "*.snap"); } catch { }
            foreach (string f in files)
            {   // the status-line snapshots stay after their session ended: the account's limits do not belong to one session
                Dictionary<string, object> sn = J.ReadFile(f); if (sn == null) continue;
                Dictionary<string, object> rl = J.Obj(sn.ContainsKey("rate_limits") ? sn["rate_limits"] : null);
                long t = J.Long(sn, "updated"); if (rl == null || t <= bestAt) continue;
                best = rl; bestAt = t;
            }
            ClaudeApi.Tick(now);
            long apiAt; Dictionary<string, object> apiRl = ClaudeApi.Get(out apiAt);
            if (apiRl != null && apiAt > bestAt) { best = apiRl; bestAt = apiAt; }   // read from the account just now: newer than whatever a status line left behind
            if (best == null) return accounts;
            List<object> wins = new List<object>(); bool anyOk = false;
            foreach (string[] k in new string[][] { new string[] { "five_hour", "5h", "300" }, new string[] { "seven_day", "weekly", "10080" } })
            {
                Dictionary<string, object> w = J.Obj(best.ContainsKey(k[0]) ? best[k[0]] : null); if (w == null) continue;
                double p = J.Num(w, "used_percentage", -1); if (p < 0) continue;
                long rs = J.Long(w, "resets_at"), resetMs = rs > 0 ? rs * 1000 : 0;
                string st = resetMs > 0 && now >= resetMs ? "reset_unconfirmed" : now - bestAt > 30 * 60 * 1000 ? "stale" : "ok";
                if (st == "ok") anyOk = true;
                wins.Add(new Dictionary<string, object> { { "label", k[1] }, { "used_percent", p }, { "duration_minutes", int.Parse(k[2]) }, { "resets_at", resetMs > 0 ? (object)resetMs : null }, { "updated_at", bestAt }, { "status", st } });
            }
            if (wins.Count == 0) return accounts;
            Dictionary<string, object> acct = new Dictionary<string, object> { { "provider", "claude" }, { "account_key", "claude:local" }, { "status", anyOk ? "ok" : "stale" },
                { "source", best == apiRl ? "claude_account" : "claude_statusline" }, { "updated_at", bestAt }, { "windows", wins } };
            List<object> list = new List<object>(); bool placed = false;
            object cur; if (accounts.TryGetValue("accounts", out cur) && cur is System.Collections.IEnumerable)
                foreach (object o in (System.Collections.IEnumerable)cur)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null && J.Str(d, "provider") == "claude") { if (!placed) list.Add(acct); placed = true; } else list.Add(o);
                }
            if (!placed) list.Add(acct);
            Dictionary<string, object> outp = new Dictionary<string, object>(accounts); outp["accounts"] = list; return outp;
        }

        // Synchronous refresh is also available to CLI/export consumers; never run on a UI timer.
        public static Dictionary<string, object> Refresh()
        {
            lock (refreshGate)
            {
                long now = Env.Now(); string readStatus; List<Sess> all = Store.List(out readStatus);
                Dictionary<string, object> sources = new Dictionary<string, object> { { "claude_sessions", readStatus }, { "other_sessions", extraSessions == null ? "not_connected" : "ok" }, { "usage", usageSource == null ? "not_connected" : "ok" }, { "accounts", accountSource == null ? "not_connected" : "ok" } };
                if (extraSessions != null)
                {
                    try { foreach (Dictionary<string, object> d in extraSessions() ?? new List<Dictionary<string, object>>()) { Sess s = SessionBackend.FromSource(d); if (s.Id != "" && !all.Any(x => x.Provider == s.Provider && x.Id == s.Id)) all.Add(s); } }
                    catch { sources["other_sessions"] = "unavailable"; }
                }
                Ctx.Update(all, now, Store.PidAlive);
                foreach (Sess s in all) SessionBackend.CheckWindow(s);
                if (HideAfterMin > 0)
                    foreach (Sess s in all)   // no event for that long: it drops out of the live list (a session waiting for you stays for 30 minutes); its next event brings it back
                        if (s.Visible && s.Updated > 0 && now - s.Updated > HideAfterMin * 60000L && !((s.LastState == "waiting" || s.LastState == "error") && now - s.Updated < 30 * 60000L)) s.Visible = false;
                Dictionary<string, object> usage = ReadSource(usageSource, sources, "usage"), accounts = ReadSource(accountSource, sources, "accounts");
                accounts = WithClaudeLimits(accounts, now);
                // DeepSeek has no subscription windows: its account line is the prepaid balance of the configured API key.
                try { Dictionary<string, object> dsb = DeepSeekBalance.ReadSnapshot(); sources["deepseek_balance"] = J.Str(dsb, "status"); accounts = DeepSeekBalance.Merge(accounts, now); }
                catch { sources["deepseek_balance"] = "unavailable"; }
                QuotaLog.Record(accounts, now);
                Dictionary<string, object> next = new Dictionary<string, object> { { "schema_version", 1 }, { "generated_at", now }, { "status", "ready" },
                    { "sessions", all.OrderByDescending(s => s.Visible).ThenByDescending(s => s.Updated).Select(s => SessionBackend.Serialize(s)).ToList() },
                    { "usage", usage }, { "accounts", accounts }, { "sources", sources } };
                lock (gate) { snapshot = next; sessions = all; }
                return ReadSnapshot();
            }
        }
        static readonly object refreshGate = new object();
        static Dictionary<string, object> ReadSource(Func<Dictionary<string, object>> source, Dictionary<string, object> sources, string key)
        {
            if (source == null) return null;
            try { return source(); } catch { sources[key] = "unavailable"; return null; }
        }
        public static Dictionary<string, object> ReadSnapshot()
        {
            lock (gate)
            {
                if (snapshot == null) return new Dictionary<string, object> { { "schema_version", 1 }, { "generated_at", null }, { "status", "not_ready" }, { "sessions", new List<object>() }, { "usage", null }, { "accounts", null } };
                Dictionary<string, object> copy = (Dictionary<string, object>)Clone(snapshot);
                if (Env.Now() - J.Long(copy, "generated_at") > 10000) copy["status"] = "stale";
                return copy;
            }
        }
        static object Clone(object o)
        {
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null) { Dictionary<string, object> copy = new Dictionary<string, object>(); foreach (string k in d.Keys) copy[k] = Clone(d[k]); return copy; }
            if (o is IEnumerable && !(o is string)) { List<object> copy = new List<object>(); foreach (object x in (IEnumerable)o) copy.Add(Clone(x)); return copy; }
            return o;
        }
        public static bool TryJump(string provider, string sessionId, out string reason)
        {
            Sess s;
            lock (gate) s = sessions.Find(x => x.Provider == provider && x.Id == sessionId);
            reason = "session_not_found"; if (s == null) return false;
            string why; if (ProcessEvidence.Check(s.Pid, s.PidStartTicks, out why) != "alive") { reason = why; return false; }
            try
            {
                TermWindow.Win w = TermWindow.Find(s.Pid, s.ProjectName, out why);
                if (w == null || why.IndexOf(" of ", StringComparison.Ordinal) >= 0) { reason = w == null ? "no_usable_window" : "ambiguous_window"; return false; }
                if (ProcessEvidence.Check(s.Pid, s.PidStartTicks, out why) != "alive") { reason = why; return false; }
                bool ok; reason = TermWindow.Raise(w.H, out ok); return ok;
            }
            catch { reason = "window_probe_unavailable"; return false; }
        }
    }
}
