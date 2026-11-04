// Current context is a last-request input measurement, not lifetime token usage.
// Only explicit statusline capacity is accepted as a denominator.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Globalization;

namespace ClaudeCompanionPet
{
    static class Ctx
    {
        class Entry { public long Len = -1, Mtime = -1, CheckedAt; public string Path, Prompt = ""; public long PromptAt; public Dictionary<string, object> Value; }
        static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        static readonly object gate = new object();
        public const double TIRED_AT = 80;
        const int MaxLine = 8 * 1024 * 1024, MaxScan = 16 * 1024 * 1024;
        static Dictionary<string, object> Obj(Dictionary<string, object> d, string k) { return J.Obj(d != null && d.ContainsKey(k) ? d[k] : null); }
        static Dictionary<string, object> Empty(string quality)
        {
            return new Dictionary<string, object> { { "used_tokens", null }, { "window_tokens", null }, { "used_percentage", null },
                { "input_tokens", null }, { "cache_creation_input_tokens", null }, { "cache_read_input_tokens", null },
                { "output_tokens", null }, { "source", "none" }, { "quality", quality }, { "observed_at", null }, { "window_source", "unknown" } };
        }
        static bool Counts(Dictionary<string, object> u, Dictionary<string, object> value)
        {
            long a, b, c, total;
            if (!Count(u, "input_tokens", out a) || !Count(u, "cache_creation_input_tokens", out b) || !Count(u, "cache_read_input_tokens", out c)) return false;
            try { total = checked(a + b + c); } catch { return false; }
            value["used_tokens"] = total; value["input_tokens"] = a; value["cache_creation_input_tokens"] = b; value["cache_read_input_tokens"] = c;
            value["output_tokens"] = u.ContainsKey("output_tokens") ? (object)J.Long(u, "output_tokens") : null;
            return true;
        }
        static bool Count(Dictionary<string, object> d, string key, out long count)
        {
            count = 0; object v;
            if (d == null || !d.TryGetValue(key, out v) || !(v is int || v is long || v is double || v is decimal)) return false;
            try { double n = Convert.ToDouble(v, CultureInfo.InvariantCulture); if (n < 0 || double.IsInfinity(n) || double.IsNaN(n) || Math.Truncate(n) != n) return false; count = Convert.ToInt64(v, CultureInfo.InvariantCulture); return true; }
            catch { return false; }
        }
        public static void Update(List<Sess> all, long now, Func<int, bool> alive)
        {
            lock (gate)
            {
                HashSet<string> ids = new HashSet<string>(all.Select(s => s.Provider + ":" + s.Id));
                foreach (string gone in cache.Keys.Where(k => !ids.Contains(k)).ToList()) cache.Remove(gone);
                foreach (Sess s in all)
                {
                    if (s.Provider != "claude") continue;
                    string key = s.Provider + ":" + s.Id;
                    if (string.IsNullOrEmpty(s.Transcript) || !File.Exists(s.Transcript)) { string found = FindTranscript(s.Id, now); if (found != "") s.Transcript = found; }   // hooks may be missing or point at a program that is gone: Claude Code keeps every conversation at ~/.claude/projects/<folder>/<session id>.jsonl anyway
                    Entry e; if (!cache.TryGetValue(key, out e)) { e = new Entry(); cache[key] = e; }
                    if (e.Value == null || now - e.CheckedAt >= 2000)
                    {
                        e.CheckedAt = now;
                        try
                        {
                            FileInfo fi = string.IsNullOrEmpty(s.Transcript) ? null : new FileInfo(s.Transcript);
                            long len = fi != null && fi.Exists ? fi.Length : -1, mt = fi != null && fi.Exists ? fi.LastWriteTimeUtc.Ticks : -1;
                            if (e.Value == null || len != e.Len || mt != e.Mtime || e.Path != s.Transcript)
                            {
                                e.Len = len; e.Mtime = mt; e.Path = s.Transcript;
                                e.Value = len >= 0 ? ReadMeasurement(s.Transcript) : Empty("unavailable");
                                if (len >= 0 && (e.Prompt == "" || now - e.PromptAt > 20000)) { e.Prompt = LastPrompt(s.Transcript); e.PromptAt = now; }
                            }
                        }
                        catch { e.Value = Empty("unreadable"); }
                    }
                    Dictionary<string, object> value = new Dictionary<string, object>(e.Value);
                    Dictionary<string, object> cw = Obj(s.Snap, "context_window");
                    long st = J.Long(s.Snap, "updated"), tt = J.Long(value, "observed_at");
                    if (cw != null && st >= tt && st >= s.ContextResetAt)
                    {
                        value = Empty("awaiting_usage"); value["source"] = "statusline"; value["observed_at"] = st;
                        if (Counts(Obj(cw, "current_usage"), value)) value["quality"] = "measured";
                        double pct = J.Num(cw, "used_percentage", -1);
                        if (pct >= 0) value["used_percentage"] = pct;
                    }
                    if (J.Long(value, "observed_at") < s.ContextResetAt) value = Empty("awaiting_usage_after_compaction");
                    if (s.ModelName == "") s.ModelName = J.Str(value, "model");
                    if (e.Prompt != "" && s.Name == s.ProjectName + " · " + SessionBackend.ShortId(s.Id)) s.Name = s.ProjectName + " · " + e.Prompt;   // no title from the session itself: name the conversation by what was last said in it
                    string snapModel = J.Str(s.Snap, "model");
                    long window = J.Long(cw, "context_window_size");
                    if (window > 0 && (J.Str(value, "source") == "statusline" || (snapModel != "" && s.ModelName != "" && snapModel == s.ModelName)))
                    {
                        value["window_tokens"] = window; value["window_source"] = "statusline";
                        if (value["used_tokens"] != null) value["used_percentage"] = 100.0 * J.Long(value, "used_tokens") / window;
                    }
                    if (J.Long(value, "observed_at") > 0 && now - J.Long(value, "observed_at") > 900000 && J.Str(value, "quality") == "measured") value["quality"] = "stale";
                    if (J.Long(value, "window_tokens") <= 0 && J.Long(value, "used_tokens") > 0)
                    {   // no status line gave the window: the model table (and, past 200k, the fact that only a 1M window can hold that much)
                        long w = ModelWindow(s.ModelName, J.Long(value, "used_tokens"));
                        if (w > 0) { value["window_tokens"] = w; value["window_source"] = "model_table"; value["used_percentage"] = 100.0 * J.Long(value, "used_tokens") / w; }
                    }
                    if (!string.IsNullOrEmpty(s.Transcript))
                    {   // an estimate of what the used part is made of (fixed head / your messages / the assistant / tool results), from the transcript
                        CtxSplit.Request(s.Transcript);
                        List<object> sp = CtxSplit.Get(s.Transcript, J.Long(value, "used_tokens")); if (sp != null) value["split"] = sp;
                    }
                    s.Context = value; s.Ctx = J.Num(value, "used_percentage", -1); s.CtxUsed = J.Long(value, "used_tokens"); s.CtxWindow = J.Long(value, "window_tokens");
                }
            }
        }

        static readonly Dictionary<string, KeyValuePair<long, string>> foundAt = new Dictionary<string, KeyValuePair<long, string>>();
        // <claude dir>/projects/<any folder>/<session id>.jsonl; a miss is remembered for 10 s so a session without a file is not searched for at every refresh
        static string FindTranscript(string id, long now)
        {
            if (string.IsNullOrEmpty(id) || id.IndexOfAny(new char[] { '/', '\\', ':' }) >= 0) return "";
            KeyValuePair<long, string> hit;
            if (foundAt.TryGetValue(id, out hit) && (hit.Value != "" ? File.Exists(hit.Value) : now - hit.Key < 10000)) return hit.Value;
            string path = "";
            try
            {
                string root = Path.Combine(Env.ClaudeDir(), "projects");
                if (Directory.Exists(root)) foreach (string d in Directory.GetDirectories(root)) { string f = Path.Combine(d, id + ".jsonl"); if (File.Exists(f)) { path = f; break; } }
            }
            catch { }
            foundAt[id] = new KeyValuePair<long, string>(now, path); return path;
        }
        // the last thing you said in the conversation (first line, cut short): what makes a session recognisable in the chip row
        static string LastPrompt(string path)
        {
            try
            {
                byte[] buf;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int n = (int)Math.Min(fs.Length, 8 * 1024 * 1024); fs.Position = fs.Length - n; buf = new byte[n];
                    int got = 0, r; while (got < n && (r = fs.Read(buf, got, n - got)) > 0) got += r;
                }
                string[] lines = Encoding.UTF8.GetString(buf).Split('\n');
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string ln = lines[i]; if (ln.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) < 0) continue;
                    Dictionary<string, object> d; try { d = J.Obj(J.Parse(ln.TrimStart('\uFEFF'))); } catch { continue; } if (d == null || J.Str(d, "type") != "user" || string.Equals(J.Str(d, "isSidechain"), "true", StringComparison.OrdinalIgnoreCase)) continue;
                    Dictionary<string, object> msg = Obj(d, "message"); object c = msg != null && msg.ContainsKey("content") ? msg["content"] : null;
                    string text = c as string;
                    if (text == null && c is System.Collections.IEnumerable) { StringBuilder all = new StringBuilder(); foreach (object o in (System.Collections.IEnumerable)c) { Dictionary<string, object> b = J.Obj(o); if (b != null && J.Str(b, "type") == "text") all.Append(J.Str(b, "text")).Append('\n'); } text = all.ToString(); }
                    string t = Gist(text); if (t != "") return t;
                }
            }
            catch { }
            return "";
        }
        static string Gist(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<(system-reminder|command-[a-z-]+|local-command-[a-z-]+)>.*?</\1>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
            string[] all = text.Split('\n'); string pick = all.FirstOrDefault(x => x.TrimStart().StartsWith("\u5f53\u524d\u8bf7\u6c42\uff1a")) ?? all.FirstOrDefault(x => x.TrimStart().StartsWith("Current request:"));
            if (pick != null) { pick = pick.Trim(); pick = pick.Substring(pick.IndexOfAny(new char[] { '\uff1a', ':' }) + 1); all = new string[] { pick }; }
            foreach (string raw in all)
            {
                string l = raw.Trim(); while (l.StartsWith("@")) { int sp = l.IndexOf(' '); l = sp < 0 ? "" : l.Substring(sp + 1).TrimStart(); }
                if (l == "" || l.StartsWith("<") || l.StartsWith("[") || l.StartsWith("Caveat:") || l.StartsWith("\u672c\u64cd\u4f5c ID") || l.StartsWith("\u672c\u6b21\u6267\u884c ID")) continue;
                l = l.Replace(" \u00b7 ", " ");
                return l.Length > 18 ? l.Substring(0, 18) + "\u2026" : l;
            }
            return "";
        }
        // context window of a model when nothing reported it; 0 = unknown
        static long ModelWindow(string model, long used)
        {
            string m = (model ?? "").ToLowerInvariant();
            if (m.Contains("[1m]") || m.Contains("-1m")) return 1000000;
            if (m.Contains("sonnet-5") || m.Contains("opus-5")) return 1000000;
            if (used > 200000) return 1000000;
            return 0;
        }
        public static string Short(long n) { return n >= 1000000 ? (n / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M" : n >= 1000 ? (n / 1000) + "k" : n.ToString(); }
        public static bool ReadTail(string path, string model, out long used, out long window)
        {
            Dictionary<string, object> d = ReadMeasurement(path); used = J.Long(d, "used_tokens"); window = 0;
            return d["used_tokens"] != null;
        }
        // Reverse bounded scan: memory depends on the largest recent record, never transcript size.
        // Oversize records report scan_limit rather than silently using an older usage sample.
        public static Dictionary<string, object> ReadMeasurement(string path)
        {
            Dictionary<string, object> result = Empty("no_usage");
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long pos = fs.Length, scanned = 0; bool partial = false;
                List<byte> reverse = new List<byte>(); byte[] block = new byte[65536];
                while (pos > 0 && scanned < MaxScan)
                {
                    int n = (int)Math.Min(block.Length, pos); pos -= n; fs.Position = pos;
                    int got = 0, read; while (got < n && (read = fs.Read(block, got, n - got)) > 0) got += read;
                    for (int i = got - 1; i >= 0; i--)
                    {
                        scanned++;
                        if (block[i] != 10) { reverse.Add(block[i]); if (reverse.Count > MaxLine || scanned > MaxScan) return Empty("scan_limit"); continue; }
                        if (reverse.Count == 0) continue;
                        if (ParseReverse(reverse, result, ref partial)) return BoundaryTime(result, path);
                        reverse.Clear();
                    }
                }
                if (pos == 0 && reverse.Count > 0 && ParseReverse(reverse, result, ref partial)) return BoundaryTime(result, path);
                if (pos > 0) return Empty("scan_limit");
            }
            return result;
        }
        static Dictionary<string, object> BoundaryTime(Dictionary<string, object> value, string path)
        {
            if (J.Str(value, "quality") == "awaiting_usage_after_compaction" && J.Long(value, "observed_at") == 0)
                value["observed_at"] = (long)(File.GetLastWriteTimeUtc(path) - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            return value;
        }
        static long Timestamp(Dictionary<string, object> d)
        {
            DateTimeOffset stamp;
            return DateTimeOffset.TryParse(J.Str(d, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp)
                ? (long)(stamp.UtcDateTime - new DateTime(1970, 1, 1)).TotalMilliseconds : 0;
        }
        static bool ParseReverse(List<byte> reversed, Dictionary<string, object> result, ref bool partial)
        {
            byte[] bytes = reversed.ToArray(); Array.Reverse(bytes); string line = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            try
            {
                Dictionary<string, object> d = J.Obj(J.Parse(line)); if (d == null) return false;
                if (J.Str(d, "subtype") == "compact_boundary") { result["quality"] = "awaiting_usage_after_compaction"; result["observed_at"] = Timestamp(d); return true; }
                if (string.Equals(J.Str(d, "isSidechain"), "true", StringComparison.OrdinalIgnoreCase)) return false;
                Dictionary<string, object> msg = Obj(d, "message"), u = Obj(msg, "usage");
                if (J.Str(msg, "role") != "assistant" || J.Str(msg, "model") == "<synthetic>" || !Counts(u, result)) return false;
                result["model"] = J.Str(msg, "model");
                long at = Timestamp(d);
                result["observed_at"] = at > 0 ? (object)at : null;
                result["source"] = "transcript"; result["quality"] = partial ? "partial_tail" : "measured";
                return true;
            }
            catch { partial = true; return false; }
        }
    }
}
