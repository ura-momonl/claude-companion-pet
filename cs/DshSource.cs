// DSH (DeepSeek Harness) activity, from the DSH pet-bridge event files.
// The bridge plugin (a DSH profile plugin) appends one JSON object per line to %APPDATA%\dsh-pet-bridge\dsh-<pid>.jsonl.
// This reader only tails those files: no DSH API, no Node, no plugin install and no transcript text is read or kept.
// Without the bridge a dsh session has no activity source; DeepSeekSource still lists it from the session files, and this
// file then contributes nothing (no invented state).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeCompanionPet
{
    static class DshSource
    {
        const int MAX_FILES = 12, MAX_ROWS_PER_FILE = 8000, MAX_SESSIONS = 24;
        const long FILE_TAIL_BYTES = 2 * 1024 * 1024;   // the bridge rotates at 1 MB, so this covers a whole file + one rotation
        const long RECENT_EVENT_MS = 120000;            // an event this fresh proves a writer existed; it is not proof of a live process

        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        static readonly object gate = new object();
        static string cachedDir;
        static long checkedAt;
        static List<Dictionary<string, object>> cached = new List<Dictionary<string, object>>();
        static readonly Dictionary<string, FileCache> files = new Dictionary<string, FileCache>(StringComparer.Ordinal);

        class FileCache { public long Length, Stamp; public List<Dictionary<string, object>> Rows; }

        class Slot
        {
            public string Id = "", Project = "", Title = "";
            public string State = "unknown", Detail = "";
            public long First, Since, Updated;
            public int Pid; public bool PidAlive;
        }

        public static string BridgeDir()
        {
            string custom = Env.Get("PET_DSH_BRIDGE_DIR");
            if (!string.IsNullOrEmpty(custom)) return custom;
            string appData = Env.Get("APPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "dsh-pet-bridge");
        }

        // Called on the session-backend worker, never on the UI thread. Results are cached for three seconds
        // and are independent copies; changed files only are re-read (the bridge rotates at 1 MB).
        public static List<Dictionary<string, object>> ReadSessions()
        {
            lock (gate)
            {
                long now = Env.Now(); string dir = BridgeDir();
                if (dir != cachedDir || now - checkedAt >= 3000) { cached = Read(dir, now); cachedDir = dir; checkedAt = now; }
                return Copy(cached);
            }
        }

        // Overlay bridge activity on the session-file rows (DeepSeekSource) and add bridge-only sessions.
        // The session-file row keeps its name, project, cwd and context; only the observed activity fields change.
        public static List<Dictionary<string, object>> Apply(List<Dictionary<string, object>> sessions)
        {
            List<Dictionary<string, object>> bridge = ReadSessions();
            var byId = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> b in bridge) byId[J.Str(b, "session_id")] = b;
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (sessions != null)
                foreach (Dictionary<string, object> s in sessions)
                {
                    string id = J.Str(s, "session_id"); seen.Add(id);
                    Dictionary<string, object> b;
                    if (byId.TryGetValue(id, out b))
                    {
                        s["state"] = b["state"]; s["since"] = b["since"]; s["detail"] = b["detail"];
                        if (J.Long(b, "updated") > J.Long(s, "updated")) s["updated"] = b["updated"];
                        s["checked_at"] = b["checked_at"]; s["alive"] = b["alive"];
                        s["lifecycle"] = b["lifecycle"]; s["lifecycle_reason"] = b["lifecycle_reason"];
                        s["hooks_status"] = "bridge_jsonl";
                    }
                    result.Add(s);
                }
            foreach (Dictionary<string, object> b in bridge) if (!seen.Contains(J.Str(b, "session_id"))) result.Add(b);
            return result;
        }

        static List<Dictionary<string, object>> Copy(List<Dictionary<string, object>> rows)
        {
            List<Dictionary<string, object>> copy = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in rows)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in row) item[kv.Key] = kv.Value;
                copy.Add(item);
            }
            return copy;
        }

        static int PidOf(string file)
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith("dsh-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return 0;
            int n; return int.TryParse(name.Substring(4, name.Length - 10), out n) ? n : 0;
        }

        // Existence of the bridge writer's process, read-only. Unknown is not "dead": the callers keep "recent event" as a
        // weaker kind of evidence instead of inventing a state for a historical file.
        static bool ProcessAlive(int pid)
        {
            if (pid <= 0) return false;
            IntPtr h = OpenProcess(0x100000 /* SYNCHRONIZE */ | 0x1000 /* QUERY_LIMITED */, false, pid);
            if (h == IntPtr.Zero) return false;
            try { return WaitForSingleObject(h, 0) == 258; }
            finally { CloseHandle(h); }
        }

        static List<Dictionary<string, object>> Read(string dir, long now)
        {
            var result = new List<Dictionary<string, object>>();
            var master = new Dictionary<string, Slot>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(dir))
            {
                FileInfo[] list;
                try { list = new DirectoryInfo(dir).GetFiles("dsh*.jsonl"); }
                catch (IOException) { list = new FileInfo[0]; }
                catch (UnauthorizedAccessException) { list = new FileInfo[0]; }
                foreach (FileInfo fi in list.OrderByDescending(f => f.LastWriteTimeUtc).Take(MAX_FILES))
                {
                    seen.Add(fi.FullName);
                    long stamp = fi.LastWriteTimeUtc.Ticks;
                    FileCache cache;
                    if (!files.TryGetValue(fi.FullName, out cache) || cache.Length != fi.Length || cache.Stamp != stamp)
                    {
                        cache = new FileCache { Length = fi.Length, Stamp = stamp, Rows = ReadRows(fi.FullName) };
                        files[fi.FullName] = cache;
                    }
                    int pid = PidOf(fi.FullName);
                    bool alive = ProcessAlive(pid);
                    var perFile = new Dictionary<string, Slot>(StringComparer.Ordinal);
                    MergeFile(perFile, cache.Rows, fi.LastWriteTimeUtc.Ticks);
                    foreach (Slot slot in perFile.Values)
                    {
                        Slot cur;
                        if (!master.TryGetValue(slot.Id, out cur)) { cur = new Slot { Id = slot.Id }; master[slot.Id] = cur; }
                        if (slot.Updated >= cur.Updated)
                        {
                            cur.State = slot.State; cur.Detail = slot.Detail; cur.Since = slot.Since; cur.Updated = slot.Updated;
                            cur.Pid = pid; cur.PidAlive = alive;
                        }
                        if (cur.First <= 0 || (slot.First > 0 && slot.First < cur.First)) cur.First = slot.First;
                        if (cur.Project == "" && slot.Project != "") cur.Project = slot.Project;
                        if (cur.Title == "" && slot.Title != "") cur.Title = slot.Title;
                    }
                }
            }
            foreach (string missing in files.Keys.Where(k => !seen.Contains(k)).ToList()) files.Remove(missing);
            foreach (Slot slot in master.Values.OrderByDescending(s => s.Updated).Take(MAX_SESSIONS))
                result.Add(Describe(slot, now));
            return result;
        }

        static void MergeFile(Dictionary<string, Slot> map, List<Dictionary<string, object>> rows, long fileStamp)
        {
            foreach (Dictionary<string, object> row in rows)
            {
                string id = J.Str(row, "sessionId");
                if (id == "" || id.StartsWith("turn:", StringComparison.Ordinal) || id == "session:unknown") continue;
                long at = (long)Math.Round(J.Num(row, "ts", 0) * 1000);
                if (at <= 0) at = fileStamp / 10000; // ticks -> ms, only when a record has no timestamp
                Slot slot;
                if (!map.TryGetValue(id, out slot)) { slot = new Slot { Id = id, First = at }; map[id] = slot; }
                if (slot.First <= 0 || at < slot.First) slot.First = at;
                if (slot.Updated < at) slot.Updated = at;

                if (J.Str(row, "type") == "session/meta")
                {
                    string p = J.Str(row, "projectName"), t = J.Str(row, "sessionName");
                    if (p != "") slot.Project = p;
                    if (t != "") slot.Title = t;
                    continue;
                }
                string ev = J.Str(row, "event"), st = J.Str(row, "state");
                if (ev == "" || ev == "AgentStatus")
                {
                    if (st == "working" || st == "busy") Set(slot, "busy", "", at);
                    else if (st == "idle") Set(slot, "idle", "", at);
                    continue;
                }
                if (ev.StartsWith("bridge/", StringComparison.Ordinal) || ev.StartsWith("watchdog/", StringComparison.Ordinal) || ev.StartsWith("cordis/", StringComparison.Ordinal)) continue;
                switch (ev)
                {
                    case "turn/end":
                        // the bridge writes execution/failed just before turn/end; a failed turn must stay an error
                        if (!(slot.State == "error" && at - slot.Since <= 5000)) Set(slot, "done", "", at);
                        break;
                    case "execution/failed": Set(slot, "error", "", at); break;
                    case "approval/asked": case "approval/request": case "approval/requested": Set(slot, "waiting", "approval", at); break;
                    case "question/requested": Set(slot, "waiting", "answer", at); break;
                    default:
                        if (ev == "llm/retry" || ev == "model_access" || ev == "llm_error" || ev == "agent/request-error") break; // warnings; the turn may still continue
                        Set(slot, "busy", "", at); break;   // any other session event is activity (tool/result, step, command, approval/decided, user/message ...)
                }
            }
        }

        static void Set(Slot slot, string state, string detail, long at)
        {
            if (slot.State == state) { if (at > slot.Updated) slot.Updated = at; if (detail != "" && slot.Detail == "") slot.Detail = detail; return; }
            slot.State = state; slot.Detail = detail; slot.Since = at; if (at > slot.Updated) slot.Updated = at;
        }

        static Dictionary<string, object> Describe(Slot slot, long now)
        {
            string project = slot.Project != "" ? slot.Project : "DSH";
            string name = "DeepSeek · " + project + (slot.Title != "" ? " · " + Trim(slot.Title, 34) : " · " + Short(slot.Id));
            bool alive = slot.PidAlive || (slot.Updated > 0 && now - slot.Updated <= RECENT_EVENT_MS);
            string life = slot.PidAlive ? "bridge_process_alive" : alive ? "bridge_recent_event" : "bridge_process_gone";
            string why = slot.PidAlive ? "The DSH process that writes this bridge file (" + slot.Pid + ") is running."
                : alive ? "No readable process check; the bridge file got an event under two minutes ago, so a writer existed recently (not proof it is still running)."
                : slot.Pid > 0 ? "No such DSH process is running; these are its last recorded events." : "No owner pid in the file name; events only.";
            return new Dictionary<string, object> {
                {"provider", "deepseek"}, {"agent", "deepseek"}, {"session_id", slot.Id}, {"key", "deepseek:" + slot.Id},
                {"display_name", name}, {"project_dir", null}, {"cwd", null}, {"model", null},
                {"source", "dsh_bridge"}, {"created", slot.First > 0 ? (object)slot.First : null},
                {"updated", slot.Updated > 0 ? (object)slot.Updated : null}, {"checked_at", now},
                {"state", slot.State}, {"since", slot.Since > 0 ? (object)slot.Since : null}, {"detail", slot.Detail},
                {"alive", alive ? (object)true : null}, {"lifecycle", life}, {"lifecycle_reason", why},
                {"hooks_status", "bridge_jsonl"}, {"can_focus", false},
                {"focus_reason", "No verified DSH session-to-window mapping."},
                {"pid", null}, {"pid_start_ticks", null},
                {"context", null}
            };
        }

        static string Short(string id) { return id.Length >= 8 ? id.Substring(0, 4) + "…" + id.Substring(id.Length - 4) : id; }
        static string Trim(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        static List<Dictionary<string, object>> ReadRows(string path)
        {
            var rows = new List<Dictionary<string, object>>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - FILE_TAIL_BYTES); fs.Position = start;
                    byte[] buf = new byte[(int)Math.Min(FILE_TAIL_BYTES, fs.Length - start)];
                    int got = 0, n;
                    while (got < buf.Length && (n = fs.Read(buf, got, buf.Length - got)) > 0) got += n;
                    string text = Encoding.UTF8.GetString(buf, 0, got);
                    if (start > 0) { int nl = text.IndexOf('\n'); text = nl < 0 ? "" : text.Substring(nl + 1); }
                    foreach (string line in text.Split('\n'))
                    {
                        if (line.Length == 0 || line.Length > 131072) continue;
                        Dictionary<string, object> row;
                        try { row = J.Obj(J.Parse(line)); } catch { continue; }
                        if (row == null) continue;
                        rows.Add(row);
                        if (rows.Count > MAX_ROWS_PER_FILE) rows.RemoveAt(0);
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            return rows;
        }
    }
}
