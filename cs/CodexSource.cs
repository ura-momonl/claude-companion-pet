// Codex 0.166 local metadata reader. No RPC, model turns, hook installation or credentials.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeCompanionPet
{
    static class CodexJson
    {
        internal static object Parse(string s) { return new JavaScriptSerializer { MaxJsonLength = 1048576 }.DeserializeObject(s); }
        internal static string Json(object o) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(o); }
        internal static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        internal static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        internal static string Str(Dictionary<string, object> d, string k) { return Get(d, k) as string ?? ""; }
        internal static long Now() { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds; }
        internal static long? Time(string s) { DateTimeOffset t; return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out t) ? (long?)(t.UtcDateTime - new DateTime(1970, 1, 1)).TotalMilliseconds : null; }
        internal static double? Number(object o)
        {
            if (!(o is int || o is long || o is double || o is decimal)) return null;
            double n = Convert.ToDouble(o, CultureInfo.InvariantCulture); return double.IsNaN(n) || double.IsInfinity(n) ? null : (double?)n;
        }
        internal static string Home()
        {
            return Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
        internal static Dictionary<string, object> Clone(Dictionary<string, object> d) { return Obj(Parse(Json(d))); }
    }

    static class CodexSource
    {
        // Calls are serialized, bounded and cached; SessionBackend should call from its worker, not the UI.
        static readonly object gate = new object();
        static long checkedAt;
        static string checkedHome;
        static List<Dictionary<string, object>> cached = new List<Dictionary<string, object>>();
        class Record { internal long Length, Stamp; internal Dictionary<string, object> Value; }
        static readonly Dictionary<string, Record> records = new Dictionary<string, Record>();
        public static List<Dictionary<string, object>> ReadSessions()
        {
            lock (gate)
            {
                long now = CodexJson.Now(); string home = CodexJson.Home();
                if (home != checkedHome || now - checkedAt >= 4000)
                {
                    cached = Read(home, now); checkedAt = now; checkedHome = home;
                }
                return cached.Select(CodexJson.Clone).ToList();
            }
        }

        internal static List<Dictionary<string, object>> Read(string home, long now)
        {
            var result = new List<Dictionary<string, object>>();
            var retained = new HashSet<string>();
            var locks = new Dictionary<string, bool?>();
            string lockDir = Path.Combine(home, "thread-writer-locks");
            if (Directory.Exists(lockDir))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(lockDir, "*.lock").OrderByDescending(File.GetLastWriteTimeUtc).Take(1024))
                    {
                        string id = Path.GetFileNameWithoutExtension(f); Guid g;
                        if (Guid.TryParse(id, out g)) locks[id] = WriterLocked(f);
                    }
                }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            // Recent history is navigation only. Older *live* threads are included via writer-lock identity.
            var paths = new List<string>();
            string root = Path.Combine(home, "sessions");
            try
            {
                // Directory names are cheap to enumerate; never recursively read transcript contents.
                foreach (string year in (Directory.Exists(root) ? Directory.GetDirectories(root) : new string[0]).OrderByDescending(x => x))
                foreach (string month in Directory.GetDirectories(year).OrderByDescending(x => x))
                foreach (string day in Directory.GetDirectories(month).OrderByDescending(x => x))
                {
                    bool recent = String.CompareOrdinal(day, Path.Combine(root, DateTime.UtcNow.AddDays(-2).ToString("yyyy/MM/dd").Replace('/', Path.DirectorySeparatorChar))) >= 0;
                    foreach (string f in Directory.GetFiles(day, "rollout-*.jsonl"))
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        string id = name.Length >= 36 ? name.Substring(name.Length - 36) : "";
                        bool? locked; locks.TryGetValue(id, out locked);
                        if (recent || locked == true) paths.Add(f);
                    }
                }
                foreach (string f in paths.OrderByDescending(p => {
                    string n = Path.GetFileNameWithoutExtension(p); bool? held;
                    return n.Length >= 36 && locks.TryGetValue(n.Substring(n.Length - 36), out held) && held == true;
                }).ThenByDescending(File.GetLastWriteTimeUtc).Take(128))
                {
                    retained.Add(f);
                    try { var s = ReadRollout(f, locks, now); if (s != null) result.Add(s); }
                    catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (string old in records.Keys.Where(x => !retained.Contains(x)).ToList()) records.Remove(old);
            foreach (var entry in locks.Where(x => x.Value == true))
            {
                if (result.Any(x => (string)x["session_id"] == entry.Key)) continue;
                result.Add(new Dictionary<string, object> {
                    {"provider", "codex"}, {"agent", "codex"}, {"session_id", entry.Key}, {"key", "codex:" + entry.Key},
                    {"display_name", "Codex · " + entry.Key.Substring(0, 8) + "…" + entry.Key.Substring(32)},
                    {"project_dir", null}, {"cwd", null}, {"model", null}, {"source", "unverified"},
                    {"created", null}, {"updated", null}, {"checked_at", now}, {"state", "unknown"}, {"since", null},
                    {"detail", "Live writer; no persisted metadata or connected activity hooks."},
                    {"alive", true}, {"lifecycle", "writer_lock_held"}, {"lifecycle_reason", "OS lock held; transcript not materialized/readable or outside the scan bound."},
                    {"hooks_status", "not_connected"}, {"can_focus", false}, {"focus_reason", "No verified session-to-window mapping."}, {"pid", null},
                    {"context", Context(null, null, null, "unavailable", null, null, null)}
                });
            }
            return result;
        }

        // File existence or mtime is NOT a liveness signal. Only OS lock contention counts.
        // 0.166 uses a writer lock spanning byte zero; the isolated test demonstrates acquisition/release.
        internal static bool? WriterLocked(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    try { fs.Lock(0, 1); fs.Unlock(0, 1); return false; }
                    catch (IOException e) { return (System.Runtime.InteropServices.Marshal.GetHRForException(e) & 65535) == 33 ? (bool?)true : null; }
                }
            }
            catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }

        static string Slice(string path, int count, bool tail)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = tail ? Math.Max(0, fs.Length - count) : 0; fs.Position = start;
                byte[] b = new byte[(int)Math.Min(count, fs.Length - start)]; int n, got = 0;
                while (got < b.Length && (n = fs.Read(b, got, b.Length - got)) > 0) got += n;
                string s = Encoding.UTF8.GetString(b, 0, got);
                if (start > 0) { int nl = s.IndexOf('\n'); s = nl < 0 ? "" : s.Substring(nl + 1); }
                // Only complete JSONL records; concurrent final partial writes are ignored.
                int end = s.LastIndexOf('\n'); return end < 0 ? "" : s.Substring(0, end);
            }
        }

        static Dictionary<string, object> ReadRollout(string path, Dictionary<string, bool?> locks, long now)
        {
            var fi = new FileInfo(path); Record record;
            if (records.TryGetValue(path, out record) && record.Length == fi.Length && record.Stamp == fi.LastWriteTimeUtc.Ticks)
            {
                var copy = CodexJson.Clone(record.Value); bool? locked;
                if (!locks.TryGetValue((string)copy["session_id"], out locked)) locked = null;
                SetLifecycle(copy, locked, now); return copy;
            }
            string first = Slice(path, 65536, false).Split('\n')[0];
            Dictionary<string, object> head;
            try { head = CodexJson.Obj(CodexJson.Parse(first)); } catch { return null; }
            if (CodexJson.Str(head, "type") != "session_meta") return null;
            var meta = CodexJson.Obj(CodexJson.Get(head, "payload"));
            string id = CodexJson.Str(meta, "id"); Guid guid; if (!Guid.TryParse(id, out guid)) return null;
            bool? writer; if (!locks.TryGetValue(id, out writer)) writer = null;
            string project = CodexJson.Str(meta, "cwd"), model = "", currentCwd = project;
            object used = null, window = null, contextAt = null;
            object input = null, output = null, cachedInput = null; string quality = "unavailable";
            long? observedAt = CodexJson.Time(CodexJson.Str(head, "timestamp"));
            foreach (string line in Slice(path, 262144, true).Split('\n'))
            {
                Dictionary<string, object> d; try { d = CodexJson.Obj(CodexJson.Parse(line)); } catch { continue; }
                var p = CodexJson.Obj(CodexJson.Get(d, "payload")); string type = CodexJson.Str(d, "type");
                long? at = CodexJson.Time(CodexJson.Str(d, "timestamp")); if (at != null) observedAt = at;
                if (type == "compacted" || (type == "event_msg" && CodexJson.Str(p, "type") == "context_compacted"))
                { used = window = input = output = cachedInput = null; contextAt = at; quality = "awaiting_usage_after_compaction"; }
                if (type == "turn_context") {
                    string nextModel = CodexJson.Str(p, "model");
                    if (model != "" && nextModel != "" && nextModel != model) { used = window = input = output = cachedInput = null; contextAt = at; quality = "awaiting_usage_after_model_change"; }
                    if (nextModel != "") model = nextModel;
                    string cwd = CodexJson.Str(p, "cwd"); if (cwd != "") currentCwd = cwd;
                }
                if (type != "event_msg" || CodexJson.Str(p, "type") != "token_count") continue;
                var info = CodexJson.Obj(CodexJson.Get(p, "info"));
                var last = CodexJson.Obj(CodexJson.Get(info, "last_token_usage"));
                double? n = CodexJson.Number(CodexJson.Get(last, "total_tokens"));
                double? w = CodexJson.Number(CodexJson.Get(info, "model_context_window"));
                if (n != null && n >= 0) {
                    used = n; window = w != null && w > 0 ? (object)w : null; contextAt = at; quality = window == null ? "partial" : "last_reported";
                    input = CodexJson.Number(CodexJson.Get(last, "input_tokens")); output = CodexJson.Number(CodexJson.Get(last, "output_tokens")); cachedInput = CodexJson.Number(CodexJson.Get(last, "cached_input_tokens"));
                }
            }
            string shortId = id.Substring(0, 8) + "…" + id.Substring(id.Length - 4);
            object rawSource = CodexJson.Get(meta, "source");
            string source = rawSource as string ?? (CodexJson.Get(CodexJson.Obj(rawSource), "subagent") == null ? "unknown" : "subagent");
            var value = new Dictionary<string, object> {
                {"provider", "codex"}, {"agent", "codex"}, {"session_id", id}, {"key", "codex:" + id},
                {"display_name", "Codex · " + Path.GetFileName(project.TrimEnd('\\', '/')) + " · " + shortId},
                {"project_dir", project}, {"cwd", currentCwd}, {"model", model},
                {"source", source}, {"originator", CodexJson.Str(meta, "originator")},
                {"cli_version", CodexJson.Str(meta, "cli_version")}, {"created", CodexJson.Time(CodexJson.Str(meta, "timestamp"))},
                {"updated", observedAt}, {"checked_at", now},
                {"state", "unknown"}, {"since", null},
                {"detail", "No per-session activity hooks consumed; a writer lock proves ownership, not working/idle/approval."},
                {"alive", writer == true ? (object)true : null},
                {"lifecycle", writer == true ? "writer_lock_held" : writer == false ? "no_writer_lock" : "unverified"},
                {"lifecycle_reason", writer == true ? "OS byte-range lock held by a writer (PID/window unknown)." : writer == false ? "Historical record; no writer currently holds its lock. Not proof of an ended session." : "No readable writer-lock evidence; historical metadata only."},
                {"hooks_status", "not_connected"}, {"can_focus", false}, {"focus_reason", "No verified session-to-window mapping."},
                {"pid", null}, {"context", Context(used, window, contextAt, quality, input, output, cachedInput)}
            };
            records[path] = new Record { Length = fi.Length, Stamp = fi.LastWriteTimeUtc.Ticks, Value = value };
            return CodexJson.Clone(value);
        }

        static Dictionary<string, object> Context(object used, object window, object at, string quality, object input, object output, object cachedInput)
        {
            return new Dictionary<string, object> {
                {"used_tokens", used}, {"window_tokens", window}, {"observed_at", at}, {"quality", quality},
                {"used_percentage", used != null && window != null ? (object)(100.0 * Convert.ToDouble(used) / Convert.ToDouble(window)) : null},
                {"input_tokens", input}, {"output_tokens", output}, {"cache_read_input_tokens", cachedInput}, {"cache_creation_input_tokens", null},
                {"source", used == null ? "none" : "codex_rollout"}, {"window_source", window == null ? "unknown" : "codex_token_count"},
                {"reason", "Last token_count.last_token_usage.total_tokens (input plus output), not lifetime usage; cached input already included. Reported window, never inferred from model. Bounded tail; may lag the current request."}
            };
        }

        static void SetLifecycle(Dictionary<string, object> s, bool? writer, long now)
        {
            s["checked_at"] = now;
            s["alive"] = writer == true ? (object)true : null;
            s["lifecycle"] = writer == true ? "writer_lock_held" : writer == false ? "no_writer_lock" : "unverified";
            s["lifecycle_reason"] = writer == true ? "OS byte-range lock held by a writer (PID/window unknown)." : writer == false ? "Historical record; no writer currently holds its lock. Not proof of an ended session." : "No readable writer-lock evidence; historical metadata only.";
        }
    }
}
