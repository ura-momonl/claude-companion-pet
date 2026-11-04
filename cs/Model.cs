// Compatibility Sess/Item/View plus Claude hook storage. SessionBackend owns session meaning.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeCompanionPet
{
    class Sess
    {
        public string Id, State = "idle", Detail = "", Cwd = "", Name = "", Transcript = "", ModelName = "", Agent = "Claude";
        public long Since, Updated, Mtime, BusyMs;
        public double Ctx = -1; public long CtxUsed, CtxWindow; // context window use in percent (-1 = not known), filled by Ctx.Update
        public int Pid;
        public string Provider = "claude", ProjectDir = "", ProjectName = "", Life = "unknown", LifeReason = "", LastState = "", StateQuality = "observed", WindowReason = "not_checked";
        public long PidStartTicks, Created, Ended, ContextResetAt;
        public bool Visible = true, CanJump;
        public string OwnershipEvidence = "", StateReason = "", LifeDetail = ""; public long SourceCheckedAt;
        public Dictionary<string, object> Context;
        public Dictionary<string, object> Snap;
    }

    static class Store
    {
        public static string Safe(string id)
        {
            if (!string.IsNullOrEmpty(id) && id.Length <= 80 && Regex.IsMatch(id, "^[a-z0-9_-]+$") && !id.StartsWith("sid-", StringComparison.Ordinal) && !Regex.IsMatch(id, "^(con|prn|aux|nul|com[0-9]|lpt[0-9])$")) return id;
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                return "sid-" + BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(id ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static string FileFor(string id, string ext) { return Path.Combine(Env.StateDir(), Safe(id) + "." + ext); }

        public static void WriteAtomic(string file, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        // Covers read-modify-write across all hook/statusline processes, not only the final rename.
        public static FileStream Lock(string id)
        {
            Directory.CreateDirectory(Env.StateDir());
            string file = FileFor(id, "lock");
            for (int i = 0; ; i++)
            {
                try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (i >= 500) throw; System.Threading.Thread.Sleep(10); }
            }
        }
        public static void Remove(string id)
        {
            foreach (string ext in new string[] { "json", "snap" }) { try { File.Delete(FileFor(id, ext)); } catch { } }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        // Legacy process-existence helper, NOT session liveness (which also requires creation identity).
        public static bool PidAlive(int pid)
        {
            if (pid <= 0) return false; // unknown is not evidence of life
            IntPtr h = OpenProcess(0x100000 /* SYNCHRONIZE */ | 0x1000 /* QUERY_LIMITED */, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return WaitForSingleObject(h, 0) == 258; }
            finally { CloseHandle(h); }
        }

        public static List<Sess> List()
        {
            string status; return List(out status);
        }
        public static List<Sess> List(out string status)
        {
            List<Sess> res = new List<Sess>();
            string dir = Env.StateDir();
            string[] files;
            status = "ok";
            try { files = Directory.GetFiles(dir, "*.json"); }
            catch (DirectoryNotFoundException) { status = "no_hook_data"; return res; }
            catch { status = "unavailable"; return res; }
            foreach (string f in files)
            {
                Dictionary<string, object> d = J.ReadFile(f);
                if (d == null) { status = "partial"; continue; }
                if (J.Str(d, "state") == "") continue;
                Sess s = new Sess();
                s.Id = J.Str(d, "session_id");
                if (s.Id == "") s.Id = Path.GetFileNameWithoutExtension(f); // legacy state, never group by cwd
                s.State = J.Str(d, "state"); s.Detail = J.Str(d, "detail"); s.Cwd = J.Str(d, "cwd");
                s.Since = J.Long(d, "since"); s.Updated = J.Long(d, "updated"); s.Pid = (int)J.Long(d, "pid");
                s.Transcript = J.Str(d, "transcript"); s.ModelName = J.Str(d, "model"); s.BusyMs = J.Long(d, "busy_ms");
                string ag = J.Str(d, "agent"); s.Agent = ag.StartsWith("codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : "Claude";
                s.Provider = s.Agent == "Codex" ? "codex" : "claude";
                s.PidStartTicks = J.Long(d, "pid_start_ticks"); s.Created = J.Long(d, "created"); s.Ended = J.Long(d, "ended"); s.ContextResetAt = J.Long(d, "context_reset_at");
                s.ProjectDir = J.Str(d, "project_dir");
                try { s.Mtime = (long)(File.GetLastWriteTimeUtc(f) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; } catch { }
                s.Snap = J.ReadFile(Path.ChangeExtension(f, ".snap"));
                if (s.ProjectDir == "") s.ProjectDir = J.Str(s.Snap, "project_dir");
                if (s.ProjectDir == "") s.ProjectDir = s.Cwd; // legacy: explicit fallback, not identity
                s.ProjectName = SessionBackend.ProjectName(s.ProjectDir);
                string title = J.Str(d, "session_name"); if (title == "") title = J.Str(s.Snap, "session_name");
                s.Name = (title == "" ? s.ProjectName : title) + " · " + SessionBackend.ShortId(s.Id);
                SessionBackend.Assess(s, Env.Now());
                res.Add(s);
            }
            return res.GroupBy(s => s.Provider + "\0" + s.Id).Select(g => g.OrderByDescending(s => s.Updated).First()).ToList();
        }
    }

    // one line of the session list
    class Item
    {
        public string Id, Name, State, Detail, Label, Cwd, Agent = "Claude";   // State: waiting error busy done idle
        public bool NoWin;                                   // a session with no terminal window to go back to (claude -p started by another program); filled by the pet
        public string Provider, Life, LifeReason, StateQuality, WindowReason;
        public bool CanJump; public Dictionary<string, object> Context;
        public long Since, BusyMs; public int Pid; public double Ctx = -1; public bool Need;
    }

    class View
    {
        public string Mode = "idle";          // active (a live session) | idle
        public string Mood = "idle";          // idle busy approval answer done error quotaOut
        public bool HasBubble;
        public string BKind = "", BProject = "", BReason = "", BMore = "";
        public string Sig = "", Tooltip = "";
        public List<string[]> Rows = new List<string[]>(); // {name, state, label}
        public List<Item> Items = new List<Item>();        // the session list of the panel (up to 8), the ones that need you first
        public int NeedCount, LiveCount, Badge; public string BadgeKind = ""; // badge on the pet's corner: sessions that need you (orange), else the session count when there are two or more (calm)
        public bool Tired;                                  // the context window of the main session is nearly full
        public double Quota = -1;
        public string SkinState { get { return Mood == "quotaOut" ? "quota" : Mood == "doneQuiet" ? "idle" : Mood; } }
    }

    static class Model
    {
        const long DONE_CALM = 120000, DONE_QUIET = 6000;
        public static long LongTaskMs = 120000; // a task that worked longer than this gets the celebration when it finishes; shorter ones only change quietly

        public static string Label(string key)
        {
            switch (key)
            {
                case "idle": return Lang.T("待命", "ready");
                case "busy": return Lang.T("忙着呢", "working");
                case "done": return Lang.T("做完了", "done");
                case "approval": return Lang.T("等你批准", "needs approval");
                case "answer": return Lang.T("等你回答", "has a question");
                case "error": return Lang.T("出错了", "error");
                case "unknown": return Lang.T("状态未知", "status unknown");
                case "ended": return Lang.T("已结束", "ended");
                case "quotaOut": return Lang.T("额度用完", "out of quota");
            }
            return key;
        }
        public static string Duration(long ms)
        {
            long s = Math.Max(0, ms / 1000);
            if (s < 60) return s + "s";
            long m = s / 60; if (m < 60) return m + "m";
            long h = m / 60; return h + "h" + (m % 60).ToString("00") + "m";
        }
        static int Rank(string st) { switch (st) { case "waiting": return 4; case "error": return 3; case "busy": return 2; case "done": return 1; } return 0; }

        // quota belongs to the account: the freshest snapshot that has rate limits; the fullest window counts
        public static double QuotaOf(List<Sess> live, out long resetAt)
        {
            resetAt = 0; double best = -1; long bestSnapTime = -1;
            foreach (Sess s in live)
            {
                Dictionary<string, object> rl = s.Snap == null ? null : J.Obj(s.Snap.ContainsKey("rate_limits") ? s.Snap["rate_limits"] : null);
                if (rl == null) continue;
                long t = J.Long(s.Snap, "updated"); if (t < bestSnapTime) continue;
                double pct = -1; long at = 0;
                foreach (string k in new string[] { "five_hour", "seven_day", "spend_limit" })
                {
                    Dictionary<string, object> w = J.Obj(rl.ContainsKey(k) ? rl[k] : null);
                    double p = J.Num(w, "used_percentage", -1);
                    if (p > pct) { pct = p; at = J.Long(w, "resets_at"); }
                }
                if (pct >= 0) { best = pct; resetAt = at; bestSnapTime = t; }
            }
            return best;
        }

        public static View Compute(List<Sess> all, long now, Func<int, bool> alive)
        {
            View v = new View();
            foreach (Sess s in all) SessionBackend.Assess(s, now);
            List<Sess> live = all.Where(s => s.Visible).ToList();
            live = live.OrderByDescending(s => Rank(s.State)).ThenByDescending(s => s.Updated).ToList();
            v.Mode = live.Count > 0 ? "active" : "idle";
            Sess primary = live.Count > 0 ? live[0] : null;
            List<Sess> need = live.Where(s => s.State == "waiting" || s.State == "error").ToList();
            long resetAt; v.Quota = QuotaOf(live, out resetAt);

            string st = primary == null ? "idle" : primary.State;
            bool longTask = primary != null && LongTaskMs > 0 && primary.BusyMs >= LongTaskMs;
            if (st == "done" && now - primary.Since > (longTask ? DONE_CALM : DONE_QUIET)) st = "idle";
            if (st == "waiting") v.Mood = primary.Detail == "answer" ? "answer" : "approval";
            else if (st == "error") v.Mood = "error";
            else if (v.Quota >= 100) v.Mood = "quotaOut";
            else if (st == "done" && !longTask) v.Mood = "doneQuiet"; // a short task: the picture stays calm, only the lamp turns green
            else v.Mood = st == "busy" || st == "done" ? st : "idle";
            v.Tired = primary != null && primary.Ctx >= Ctx.TIRED_AT && (v.Mood == "idle" || v.Mood == "done" || v.Mood == "doneQuiet");

            Func<Sess, string> reason = s => s.State == "error" ? (Label("error") + " " + s.Detail).Trim() : Label(s.Detail == "answer" ? "answer" : "approval");
            if (need.Count > 0)
            {
                Sess f = need[0]; int more = need.Count - 1;
                v.HasBubble = true; v.BKind = f.State == "error" ? "error" : f.Detail == "answer" ? "answer" : "approval";
                if (more == 0) { v.BProject = f.Name; v.BReason = reason(f) + " · " + Duration(now - (f.Since > 0 ? f.Since : now)); }
                else
                {   // several: say how many; the list is in the panel
                    v.BProject = Lang.T(need.Count + " 个会话在等你", need.Count + " sessions need you");
                    v.BReason = f.Name + " · " + reason(f) + " · " + Duration(now - (f.Since > 0 ? f.Since : now));
                    v.BMore = Lang.T("点小人看全部", "click me for the list");
                }
            }
            else if (v.Mood == "quotaOut")
            {
                v.HasBubble = true; v.BKind = "quota"; v.BProject = Lang.T("额度见底", "Out of quota");
                v.BReason = resetAt > 0 ? Lang.T("约 " + Duration(resetAt * 1000 - now) + " 后恢复", "back in about " + Duration(resetAt * 1000 - now)) : Lang.T("等额度恢复", "wait for the reset");
            }
            v.NeedCount = need.Count; v.LiveCount = live.Count;
            if (need.Count > 0 && live.Count > 1) { v.Badge = need.Count; v.BadgeKind = "need"; }
            else if (need.Count > 1) { v.Badge = need.Count; v.BadgeKind = "need"; }
            else if (live.Count > 1) { v.Badge = live.Count; v.BadgeKind = "count"; }
            foreach (Sess s in live.Take(8))
                v.Items.Add(new Item { Id = s.Id, Name = s.Name, Agent = s.Agent, State = s.State, Detail = s.Detail, Cwd = s.Cwd, Since = s.Since > 0 ? s.Since : s.Updated, BusyMs = s.BusyMs, Pid = s.Pid, Ctx = s.Ctx, Need = s.State == "waiting" || s.State == "error",
                    Provider = s.Provider, Life = s.Life, LifeReason = s.LifeReason, StateQuality = s.StateQuality, CanJump = s.CanJump, NoWin = !s.CanJump, WindowReason = s.WindowReason, Context = s.Context,
                    Label = s.State == "waiting" || s.State == "error" ? reason(s) : Label(s.State) });
            foreach (Sess s in live.Take(6))
                v.Rows.Add(new string[] { s.Name, s.State, s.State == "waiting" || s.State == "error" ? reason(s) : Label(s.State == "busy" || s.State == "done" ? s.State : "idle") });
            v.Tooltip = primary == null ? Lang.T("没有在运行的会话", "no sessions")
                : primary.Name + ": " + (v.HasBubble ? v.BReason : Label(v.Mood)) + (live.Count > 1 ? " (+" + (live.Count - 1) + ")" : "");
            v.Sig = v.Mode + "|" + v.Mood + "|" + (v.HasBubble ? v.BKind + v.BProject + v.BMore : "") + (v.Tired ? "|tired" : "") + (v.Badge > 0 ? "|badge" + v.Badge : "");
            return v;
        }
    }
}
