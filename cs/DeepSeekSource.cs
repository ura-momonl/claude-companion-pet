// DeepSeek side of the backend: local DeepSeek Harness (dsh) session metadata, and the prepaid balance of a DeepSeek API key.
// Read-only. Nothing under the dsh home is written, no dsh file is locked or modified, and no credential is read unless the user put one
// where this file says it looks (environment DEEPSEEK_API_KEY, or <state dir>\deepseek.key). No hooks, no RPC, no model turns.
//
// What the data can and cannot say (see backend/DEEPSEEK.md for the evidence):
//   * session identity, project, title, created and last write  -> read
//   * busy / waiting-for-approval / done                         -> only with the DSH pet-bridge plugin (cs/DshSource.cs);
//                                                                   the session files alone carry no activity event
//   * token counts and context usage                             -> NOT readable here: the records are zstd, and this exe carries no decoder
// The pet therefore never invents a state for a dsh session: it reports the last write time and, when the bridge is missing,
// says the rest is unknown.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace ClaudeCompanionPet
{
    static class DeepSeekSource
    {
        // Calls are serialized, bounded and cached; SessionBackend calls this from its worker, never from the UI.
        static readonly object gate = new object();
        static long checkedAt;
        static string checkedHome;
        static List<Dictionary<string, object>> cached = new List<Dictionary<string, object>>();
        class Meta { internal long Length, Stamp; internal Dictionary<string, object> Value; }
        static readonly Dictionary<string, Meta> metas = new Dictionary<string, Meta>();
        const int ScanLimit = 60;      // newest dsh sessions considered; the pane shows a handful anyway
        const int MetaMaxBytes = 262144;

        public static List<Dictionary<string, object>> ReadSessions()
        {
            lock (gate)
            {
                long now = Env.Now(); string home = Home();
                if (home != checkedHome || now - checkedAt >= 4000) { cached = Read(home, now); checkedAt = now; checkedHome = home; }
                return cached.Select(Copy).ToList();
            }
        }

        internal static string Home()
        {
            string dsh = Env.Get("DSH_HOME");
            return !String.IsNullOrEmpty(dsh) ? dsh : Path.Combine(Env.Home(), ".dsh");
        }

        internal static List<Dictionary<string, object>> Read(string home, long now)
        {
            var result = new List<Dictionary<string, object>>();
            string root = Path.Combine(home, "sessions");
            if (!Directory.Exists(root)) return result;
            var found = new List<Dictionary<string, object>>();
            try
            {
                // <home>\sessions\<project key>\<session id>\session.v4.jsonl.zstd — the project key is a lossy encoding of the cwd,
                // so the cwd itself comes from the projcache metadata below, not from the folder name.
                foreach (string project in Directory.GetDirectories(root))
                    foreach (string dir in Directory.GetDirectories(project))
                    {
                        string file = Path.Combine(dir, "session.v4.jsonl.zstd");
                        if (!File.Exists(file)) continue;
                        long at; long size;
                        try { FileInfo fi = new FileInfo(file); at = ToMs(fi.LastWriteTimeUtc); size = fi.Length; } catch { continue; }
                        found.Add(new Dictionary<string, object> { { "id", Path.GetFileName(dir) }, { "file", file }, { "at", at }, { "size", size } });
                    }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            foreach (Dictionary<string, object> f in found.OrderByDescending(x => (long)x["at"]).Take(ScanLimit))
            {
                try { Dictionary<string, object> s = Build(home, (string)f["id"], (string)f["file"], (long)f["at"], now); if (s != null) result.Add(s); }
                catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            }
            var live = new HashSet<string>(result.Select(x => (string)x["session_id"]));
            foreach (string old in metas.Keys.Where(k => !live.Contains(k)).ToList()) metas.Remove(old);
            // dsh's title is the first thing said in a session, so two sessions often carry the same text; keep them distinguishable.
            foreach (var same in result.GroupBy(x => (string)x["display_name"]).Where(g => g.Count() > 1).ToList())
                foreach (Dictionary<string, object> s in same) s["display_name"] = (string)s["display_name"] + " · " + Short((string)s["session_id"]);
            return result;
        }

        static Dictionary<string, object> Build(string home, string id, string file, long at, long now)
        {
            Dictionary<string, object> meta = Metadata(home, id);
            Dictionary<string, object> identity = J.Obj(meta.ContainsKey("identity") ? meta["identity"] : null);
            Dictionary<string, object> rows = J.Obj(meta.ContainsKey("rows") ? meta["rows"] : null);
            Dictionary<string, object> titleRow = J.Obj(rows.ContainsKey("title") ? rows["title"] : null);
            string cwd = J.Str(identity, "cwd");
            string title = J.Str(titleRow, "val");
            long created = J.Long(identity, "createdAt");
            // A project name for the row: the folder dsh was started in, else the encoded key dsh used for the session folder.
            string projectName = cwd == "" ? "DeepSeek" : FolderName(cwd);
            string name = "DeepSeek · " + projectName + (title != "" ? " · " + Trim(title, 34) : " · " + Short(id));
            return new Dictionary<string, object> {
                { "provider", "deepseek" }, { "agent", "deepseek" }, { "session_id", id }, { "key", "deepseek:" + id },
                { "display_name", name },
                { "project_dir", cwd == "" ? null : cwd }, { "cwd", cwd == "" ? null : cwd }, { "model", "" },
                { "source", "dsh_session_file" },
                { "created", created > 0 ? (object)created : null }, { "updated", at }, { "checked_at", now },
                { "state", "unknown" }, { "since", null },
                { "detail", Lang.T("只读到最后一次写入的时间；dsh 不提供活动或审批事件，忙、等批准、做完都判断不了。",
                                   "Only the last write time is readable; dsh exposes no activity or approval events, so busy/approval/done is unknown.") },
                { "alive", null },
                { "lifecycle", "file_activity_only" },
                { "lifecycle_reason", Lang.T("会话文件在最近一次写入后没有被锁住，也没有退出记录：文件活动不是存活证明。",
                                            "The session file carries no lock and no exit record: file activity is not proof of a live process.") },
                { "hooks_status", "not_connected" }, { "can_focus", false }, { "focus_reason", "no_verified_process_mapping" },
                { "pid", null }, { "pid_start_ticks", null },
                { "context", Context() }
            };
        }

        // record.identity.cwd / createdAt and record.rows.title.val from the dsh projcache; cached by size + mtime, never guessed.
        static Dictionary<string, object> Metadata(string home, string id)
        {
            string path = Path.Combine(home, "storages", "session_projcache", "sessions", id + ".json");
            try
            {
                if (!File.Exists(path)) return new Dictionary<string, object>();
                FileInfo fi = new FileInfo(path);
                Meta m; if (metas.TryGetValue(id, out m) && m.Length == fi.Length && m.Stamp == fi.LastWriteTimeUtc.Ticks) return m.Value;
                if (fi.Length <= 0 || fi.Length > MetaMaxBytes) return new Dictionary<string, object>();
                Dictionary<string, object> root = J.ReadFile(path);
                Dictionary<string, object> record = J.Obj(root.ContainsKey("record") ? root["record"] : null);
                Dictionary<string, object> value = record == null ? new Dictionary<string, object>() : record;
                metas[id] = new Meta { Length = fi.Length, Stamp = fi.LastWriteTimeUtc.Ticks, Value = value };
                return value;
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentException) { }
            return new Dictionary<string, object>();
        }

        static Dictionary<string, object> Context()
        {
            return new Dictionary<string, object> {
                { "used_tokens", null }, { "window_tokens", null }, { "observed_at", null }, { "quality", "unavailable" },
                { "used_percentage", null }, { "input_tokens", null }, { "output_tokens", null },
                { "cache_read_input_tokens", null }, { "cache_creation_input_tokens", null },
                { "source", "none" }, { "window_source", "unknown" },
                { "reason", Lang.T("dsh 的会话记录是 zstd 压缩的 jsonl，这个程序没有解码器，读不到 token 计数；不从文件大小推算。",
                                   "dsh keeps its transcripts as zstd-compressed jsonl; this exe has no decoder, so there is no token count to read and none is inferred.") }
            };
        }

        internal static long ToMs(DateTime utc) { return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        internal static string FolderName(string path)
        {
            string c = (path ?? "").TrimEnd('/', '\\');
            if (c == "") return "";
            try { string n = Path.GetFileName(c.Replace('/', Path.DirectorySeparatorChar)); return n == "" ? c : n; } catch { return c; }
        }

        static string Short(string id) { return id.Length >= 8 ? id.Substring(0, 4) + "…" + id.Substring(id.Length - 4) : id; }
        static string Trim(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        static Dictionary<string, object> Copy(Dictionary<string, object> d) { return J.Obj(J.Parse(J.Compact(d))); }
    }

    // Prepaid balance of one DeepSeek API key. Off unless the user put a key where KeyPath() says; the key is never written to a log,
    // never put into the snapshot, and only ever sent to api.deepseek.com.
    static class DeepSeekBalance
    {
        const string Endpoint = "https://api.deepseek.com/user/balance";
        const int TimeoutMs = 15000;
        const int IntervalMs = 300000;
        public const string KeyFileName = "deepseek.key";
        // Off by default: no key is read and no request is made until the user turns this on in the pet's menu
        // (settings.txt dsBalance=1). Start() is deliberately a no-op while it is off.
        public static volatile bool Enabled;

        static readonly object gate = new object();
        static Thread worker;
        static ManualResetEvent stop;
        static Dictionary<string, object> snapshot = Empty("not_started", "DeepSeek balance has not started.");

        internal static string KeyPath() { return Path.Combine(Env.StateDir(), KeyFileName); }

        // Called from the menu only after the user turned the balance on; reads the same two places the worker does.
        public static bool HasKey() { return Key() != null; }

        // Env first, then the file the README tells the user to create. No other credential store is searched.
        internal static string Key()
        {
            string k = Env.Get("DEEPSEEK_API_KEY");
            if (!String.IsNullOrEmpty(k) && k.Trim() != "") return k.Trim();
            try
            {
                string p = KeyPath();
                if (File.Exists(p)) { string t = File.ReadAllText(p).Trim(); return t == "" ? null : t; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }

        static Dictionary<string, object> Empty(string status, string reason)
        {
            return new Dictionary<string, object> { { "status", status }, { "reason", reason }, { "updated_at", null }, { "balance", null } };
        }

        public static void Start()
        {
            lock (gate)
            {
                if (!Enabled || worker != null) return;
                stop = new ManualResetEvent(false);
                worker = new Thread(Run) { IsBackground = true, Name = "pet-deepseek-balance" };
                worker.Start();
            }
        }

        public static void Stop()
        {
            Thread t;
            lock (gate) { t = worker; if (t == null) return; stop.Set(); }
            t.Join(3000);
            lock (gate)
            {
                if (t.IsAlive) return;
                worker = null; stop.Dispose(); stop = null;
                snapshot["status"] = "stopped"; snapshot["reason"] = "DeepSeek balance stopped; the last figure is historical.";
            }
        }

        public static Dictionary<string, object> ReadSnapshot()
        {
            lock (gate)
            {
                Dictionary<string, object> copy = J.Obj(J.Parse(J.Compact(snapshot)));
                long now = Env.Now(); copy["generated_at"] = now;
                long at = J.Long(copy, "updated_at");
                if (at > 0 && now - at > 600000 && J.Str(copy, "status") == "ok")
                { copy["status"] = "stale"; copy["reason"] = "The last successful balance read is over ten minutes old."; }
                return copy;
            }
        }

        static void Run()
        {
            while (!stop.WaitOne(0))
            {
                Dictionary<string, object> next;
                try
                {
                    string key = Key();
                    next = key == null
                        ? Empty("no_key", "No DeepSeek API key: set DEEPSEEK_API_KEY, or write the key into " + KeyPath() + ". Nothing else is read.")
                        : Query(key);
                }
                catch (Exception e) { next = Empty("error", "Balance request failed (" + e.GetType().Name + ")."); }
                if (next["updated_at"] == null) next["updated_at"] = Env.Now();
                lock (gate) snapshot = next;
                if (stop.WaitOne(IntervalMs)) break;
            }
        }

        internal static Dictionary<string, object> Query(string key)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2 on .NET 4.0
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Endpoint);
            req.Method = "GET"; req.Timeout = TimeoutMs; req.ReadWriteTimeout = TimeoutMs;
            req.UserAgent = "claude-companion-pet";
            req.Headers["Authorization"] = "Bearer " + key;   // never logged, never serialized
            string body;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd();
            }
            catch (WebException e)
            {
                HttpWebResponse r = e.Response as HttpWebResponse;
                int code = r == null ? 0 : (int)r.StatusCode;
                if (r != null) r.Close();
                string reason = code == 401 ? "The DeepSeek API rejected this key (HTTP 401)." : code == 403 ? "The key is not allowed to read the balance (HTTP 403)."
                    : code > 0 ? "DeepSeek answered HTTP " + code + "." : "No answer from api.deepseek.com (" + e.Status + ").";
                Dictionary<string, object> failed = Empty("error", reason); failed["http_status"] = code > 0 ? (object)code : null; return failed;
            }
            return ParseBalance(body, Env.Now());
        }

        // Split from the request so the response handling can be checked against the shapes api.deepseek.com documents.
        internal static Dictionary<string, object> ParseBalance(string body, long now)
        {
            Dictionary<string, object> parsed = J.Obj(J.Parse(body));
            List<object> infos = new List<object>();
            object raw = parsed.ContainsKey("balance_infos") ? parsed["balance_infos"] : null;
            System.Collections.IEnumerable list = raw as System.Collections.IEnumerable;
            if (list != null && !(raw is string)) foreach (object o in list)
            {
                Dictionary<string, object> b = J.Obj(o); if (b == null) continue;
                string cur = J.Str(b, "currency");
                infos.Add(new Dictionary<string, object> {
                    { "currency", cur }, { "total", Money(J.Str(b, "total_balance")) },
                    { "granted", Money(J.Str(b, "granted_balance")) }, { "topped_up", Money(J.Str(b, "topped_up_balance")) }
                });
            }
            if (infos.Count == 0) { Dictionary<string, object> none = Empty("error", "DeepSeek answered without any balance line."); return none; }
            // Several currency lines can come back in any order. Take one that actually has money (a USD 0.00 listed
            // first is not this account's balance), preferring CNY on a tie, the same rule the dsh pet uses.
            Dictionary<string, object> pick = null; bool pickPositive = false, pickCny = false;
            foreach (object o in infos)
            {
                Dictionary<string, object> b = J.Obj(o); if (b == null) continue;
                double v = J.Num(b, "total", -1); bool positive = v > 0; bool cny = J.Str(b, "currency").ToUpperInvariant() == "CNY";
                if (pick == null || (positive && !pickPositive) || (positive == pickPositive && cny && !pickCny)) { pick = b; pickPositive = positive; pickCny = cny; }
            }
            Dictionary<string, object> result = new Dictionary<string, object> {
                { "status", "ok" }, { "reason", "" }, { "updated_at", now },
                { "is_available", parsed.ContainsKey("is_available") ? parsed["is_available"] : null },
                { "balance", new Dictionary<string, object> {
                    { "primary", pick }, { "all", infos },
                    { "source", "deepseek_balance_api" }, { "note", "Prepaid API balance of the key you configured, as api.deepseek.com reports it. Not a subscription quota and not this session's spending." } } }
            };
            return result;
        }

        internal static double? Money(string s)
        {
            double v; return Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? (double?)v : null;
        }

        // Puts the DeepSeek account into the snapshot's accounts list; the other providers' entries are left untouched.
        // While the user has the balance switched off, no account line is added (there is nothing to read or claim).
        public static Dictionary<string, object> Merge(Dictionary<string, object> accounts, long now)
        {
            if (!Enabled) return accounts;
            return MergeWith(accounts, ReadSnapshot(), now);
        }

        internal static Dictionary<string, object> MergeWith(Dictionary<string, object> accounts, Dictionary<string, object> sn, long now)
        {
            string status = J.Str(sn, "status");
            long at = J.Long(sn, "updated_at");
            Dictionary<string, object> acct = new Dictionary<string, object> {
                { "provider", "deepseek" }, { "account_key", "deepseek:local" },
                { "status", status == "ok" || status == "stale" ? status : status == "" ? "no_key" : status },
                { "source", "deepseek_balance_api" },
                { "updated_at", at > 0 ? (object)at : null },
                { "balance", sn.ContainsKey("balance") ? sn["balance"] : null },
                { "windows", new object[0] },
                { "reason", J.Str(sn, "reason") }
            };
            if (accounts == null)
                return new Dictionary<string, object> { { "status", status }, { "updated_at", acct["updated_at"] },
                    { "accounts", new List<object> { acct } } };
            List<object> list = new List<object>(); bool placed = false;
            object cur; if (accounts.TryGetValue("accounts", out cur))
                foreach (object o in (System.Collections.IEnumerable)cur)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d != null && J.Str(d, "provider") == "deepseek") { if (!placed) list.Add(acct); placed = true; } else list.Add(o);
                }
            if (!placed) list.Add(acct);
            Dictionary<string, object> outp = new Dictionary<string, object>(accounts);
            outp["accounts"] = list;
            return outp;
        }
    }
}
