// Recent token usage and API-equivalent price, from the local Claude Code and Codex transcripts.
// Runs on its own background thread: scans transcript files incrementally (newest first), de-duplicates requests by their stable id,
// keeps only numbers and model names in a small cache, and publishes an immutable snapshot that ReadSnapshot() returns at once.
// Format and rules: backend/USAGE.md. Prices: backend/prices.json (checked against the vendors' pricing pages).
// .NET Framework 4 / C# 5, no external libraries.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ClaudeCompanionPet
{
    // Entry point for the rest of the program. One lifecycle = one engine = one worker thread = the only writer of the cache file.
    // A new worker is created only after the previous one has really exited (it writes its final cache before it ends).
    static class UsageBackend
    {
        static readonly object life = new object();         // guards worker/runEngine/want; never held while waiting for the thread
        static Thread worker;                                // the live worker, cleared by the worker itself when it exits
        static UsageEngine runEngine;                        // engine of the live worker
        static bool want;                                    // last request was Start (true) or Stop (false)
        static volatile UsageEngine current;                 // newest engine, read without a lock by ReadSnapshot
        static int liveWorkers, peakWorkers;                 // observable facts for tests: workers alive now / most ever alive at once
        internal static int LiveWorkers { get { return Thread.VolatileRead(ref liveWorkers); } }
        internal static int PeakWorkers { get { return Thread.VolatileRead(ref peakWorkers); } }

        // Starts scanning. Idempotent while a worker runs. If the previous worker was asked to stop but has not finished,
        // no second worker is created: the start is queued and happens when the old worker has exited. Returns true when a worker is
        // running (not stopping) now; false means "queued behind a worker that is still stopping".
        public static bool Start()
        {
            lock (life)
            {
                want = true;
                if (worker != null) return !runEngine.StopRequested;
                Launch();
                return true;
            }
        }

        // Asks the worker to stop and waits up to timeoutMs. Returns true only when the worker has really exited (final cache written);
        // false means it is still stopping, and snapshots say state="stopping" until it is done. A queued Start is cancelled.
        public static bool Stop() { return Stop(3000); }
        public static bool Stop(int timeoutMs)
        {
            Thread t;
            lock (life)
            {
                want = false; t = worker;
                if (t == null) return true;
                runEngine.RequestStop();
            }
            return t.Join(timeoutMs);
        }

        static void Launch()   // under 'life', no live worker
        {
            UsageEngine eng = new UsageEngine(UsageConfig.FromEnvironment());
            UsageEngine prev = current; if (prev != null) eng.SeedFrom(prev.Latest);   // a restart keeps showing the last numbers (state 'starting') until the cache or scan replaces them
            Thread t = new Thread(delegate() { WorkerMain(eng); });
            t.IsBackground = true; t.Name = "usage-scan"; t.Priority = ThreadPriority.BelowNormal;
            worker = t; runEngine = eng; current = eng;
            int n = Interlocked.Increment(ref liveWorkers);
            int pk; while ((pk = Thread.VolatileRead(ref peakWorkers)) < n && Interlocked.CompareExchange(ref peakWorkers, n, pk) != pk) { }
            t.Start();
        }

        static void WorkerMain(UsageEngine eng)
        {
            try { eng.Run(); }
            finally
            {
                eng.MarkStopped();
                lock (life)
                {
                    worker = null; runEngine = null; Interlocked.Decrement(ref liveWorkers);
                    if (want) { try { Launch(); } catch (Exception e) { want = false; try { Diag.Error("usage restart", e); } catch { } } }
                }
            }
        }

        // Returns an independent copy of the newest snapshot. Never scans files, never waits for the worker.
        // After the worker has stopped (or while it is stopping) the numbers and times stay, but state is "stopped"/"stopping" and producer says so.
        public static Dictionary<string, object> ReadSnapshot()
        {
            UsageEngine eng = current;
            if (eng == null) return UsageEngine.NotStarted(Env.Now());
            return UsageEngine.Clone(eng.Latest);
        }
    }

    class UsageConfig
    {
        public List<string> ClaudeRoots = new List<string>();
        public List<string> CodexRoots = new List<string>();
        public string CacheDir;       // null: no cache file (used by tests)
        public string PricesPath;     // null: search next to the program
        public int WindowDays = 30;
        public int IntervalMs = 15000;
        public TimeZoneInfo Tz = TimeZoneInfo.Local;
        public Func<long> Clock = Env.Now;

        // Environment overrides (for isolated tests): PET_USAGE_DIR (cache folder), PET_USAGE_PRICES, PET_USAGE_DAYS, PET_USAGE_INTERVAL (seconds),
        // CLAUDE_CONFIG_DIR (already used by the program), CODEX_HOME.
        public static UsageConfig FromEnvironment()
        {
            UsageConfig c = new UsageConfig();
            c.ClaudeRoots.Add(Path.Combine(Env.ClaudeDir(), "projects"));
            string codex = Env.Get("CODEX_HOME") ?? Path.Combine(Env.Home(), ".codex");
            c.CodexRoots.Add(Path.Combine(codex, "sessions"));
            c.CodexRoots.Add(Path.Combine(codex, "archived_sessions"));
            c.CacheDir = Env.Get("PET_USAGE_DIR") ?? Path.Combine(Env.DataDir(), "usage");
            c.PricesPath = Env.Get("PET_USAGE_PRICES");
            int n;
            if (int.TryParse(Env.Get("PET_USAGE_DAYS") ?? "", out n) && n >= 1 && n <= 90) c.WindowDays = n;
            if (int.TryParse(Env.Get("PET_USAGE_INTERVAL") ?? "", out n) && n >= 1 && n <= 3600) c.IntervalMs = n * 1000;
            return c;
        }
    }

    // ---------------------------------------------------------------- prices

    class PriceTier
    {
        public long Max = -1;                              // largest prompt size (tokens) this tier covers; -1: no limit
        public double[] P = new double[] { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN }; // input, cache read, write 5m, write 1h, output
    }

    class PriceModel
    {
        public string Id, Label, Provider;
        public double UsMult;
        public List<PriceTier> Tiers = new List<PriceTier>();
        public List<PriceTier> FastTiers;
    }

    class PriceBook
    {
        public bool Loaded; public string Error, Version, Checked, PricesFile;
        public List<object> Sources = new List<object>();
        public Dictionary<string, PriceModel> ById = new Dictionary<string, PriceModel>(StringComparer.OrdinalIgnoreCase);

        static readonly string[] Fields = new string[] { "input", "cache_read", "cache_write_5m", "cache_write_1h", "output" };

        public static string Locate(string configured)
        {
            if (!string.IsNullOrEmpty(configured)) return configured;
            string exe; try { exe = Env.ExeDir(); } catch { return null; }
            string[] cand = new string[] { Path.Combine(exe, "prices.json"), Path.Combine(exe, "backend", "prices.json"), Path.Combine(exe, "..", "backend", "prices.json") };
            foreach (string c in cand) if (File.Exists(c)) return c;
            return null;
        }

        public static PriceBook Load(string configured)
        {
            PriceBook b = new PriceBook();
            string path = Locate(configured); b.PricesFile = path;
            if (path == null || !File.Exists(path)) { b.Error = "prices.json not found"; return b; }
            try
            {
                Dictionary<string, object> root = J.ReadFile(path);
                if (root == null) { b.Error = "prices.json is not valid JSON"; return b; }
                b.Version = J.Str(root, "version"); b.Checked = J.Str(root, "checked");
                object so; if (root.TryGetValue("sources", out so)) { System.Collections.IEnumerable l = so as System.Collections.IEnumerable; if (l != null) foreach (object x in l) b.Sources.Add(x); }
                object mo; root.TryGetValue("models", out mo);
                System.Collections.IEnumerable models = mo as System.Collections.IEnumerable;
                if (models != null)
                    foreach (object m in models)
                    {
                        Dictionary<string, object> d = J.Obj(m); if (d == null) continue;
                        PriceModel pm = new PriceModel(); pm.Label = J.Str(d, "label"); pm.Provider = J.Str(d, "provider"); pm.UsMult = J.Num(d, "us_multiplier", 0);
                        pm.Tiers = ReadTiers(d, "tiers"); pm.FastTiers = d.ContainsKey("fast_tiers") ? ReadTiers(d, "fast_tiers") : null;
                        object mt; d.TryGetValue("match", out mt);
                        System.Collections.IEnumerable ids = mt as System.Collections.IEnumerable;
                        if (ids != null) foreach (object id in ids) { string s = Convert.ToString(id, CultureInfo.InvariantCulture); if (pm.Id == null) pm.Id = s; b.ById[s] = pm; }
                    }
                b.Loaded = b.ById.Count > 0; if (!b.Loaded) b.Error = "prices.json has no models";
            }
            catch (Exception e) { b.Error = e.GetType().Name + ": " + e.Message; }
            return b;
        }

        static List<PriceTier> ReadTiers(Dictionary<string, object> d, string key)
        {
            List<PriceTier> list = new List<PriceTier>(); object o; d.TryGetValue(key, out o);
            System.Collections.IEnumerable en = o as System.Collections.IEnumerable; if (en == null) return list;
            foreach (object t in en)
            {
                Dictionary<string, object> td = J.Obj(t); if (td == null) continue;
                PriceTier pt = new PriceTier(); object mx;
                if (td.TryGetValue("max_prompt_tokens", out mx) && mx != null) pt.Max = J.Long(td, "max_prompt_tokens");
                for (int i = 0; i < Fields.Length; i++) { object v; if (td.TryGetValue(Fields[i], out v) && v != null) pt.P[i] = J.Num(td, Fields[i], double.NaN); }
                list.Add(pt);
            }
            return list;
        }

        // Model name as written in the transcripts -> price entry (strips "[1m]" style suffixes and a trailing -YYYYMMDD).
        public PriceModel Find(string model)
        {
            if (string.IsNullOrEmpty(model)) return null;
            string m = model; int br = m.IndexOf('['); if (br > 0) m = m.Substring(0, br);
            PriceModel pm; if (ById.TryGetValue(m, out pm)) return pm;
            if (m.Length > 9 && m[m.Length - 9] == '-')
            {
                bool digits = true; for (int i = m.Length - 8; i < m.Length; i++) if (m[i] < '0' || m[i] > '9') digits = false;
                if (digits && ById.TryGetValue(m.Substring(0, m.Length - 9), out pm)) return pm;
            }
            return null;
        }
    }

    // ---------------------------------------------------------------- records

    // One API request (or one Codex response). Token fields are disjoint parts of the prompt: In = uncached input.
    struct UsageRec
    {
        public long Id, Ts, In, Cr, Cw5, Cw1, CwU, Out; // CwU: cache-write tokens whose cache duration is unknown
        public int Model, FileKey, Tool;                 // Tool: server-side tool requests (web search / fetch), not priced
        public byte Prov, Kind, Flags;                   // Prov 0 claude 1 codex; Kind 0 real request id, 1 derived from Codex cumulative counter; Flags below
    }

    class FileState
    {
        public long Offset, Mtime, HeadLen, HeadHash;
        public string SessionId = "", LastModel = "", Tier = "";
        public bool HasRec; public long LastTotal; public int Derived;
        public List<string> Turns = new List<string>();  // "turnId=model", newest last, at most 16
        public bool Seen;
    }

    class Agg
    {
        public long In, Cr, Cw, Cw5, Cw1, CwU, Out, Req, Unpriced, Tool; public double Usd;
        public long Total { get { return In + Cr + Cw + Out; } }
    }

    // ---------------------------------------------------------------- engine

    class UsageEngine
    {
        public const byte FlagFast = 1, FlagUs = 2, FlagNoPrice = 4;

        readonly UsageConfig cfg;
        List<UsageRec> recs = new List<UsageRec>();
        Dictionary<long, int> index = new Dictionary<long, int>();
        List<string> models = new List<string>(); Dictionary<string, int> modelIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        Dictionary<long, FileState> files = new Dictionary<long, FileState>();
        PriceBook prices = new PriceBook(); long pricesStamp = -1; string pricesPath;
        volatile Dictionary<string, object> latest; volatile bool stop; volatile string lifecycle = ""; long stoppedAt;
        readonly ManualResetEvent wake = new ManualResetEvent(false);   // set by RequestStop to cut the idle wait short
        long scanFrom = 0;            // everything at/after this time is complete (newest-first scan)
        bool dirty; long savedAt, dataAsOf;
        long bytesRead, linesParsed, badLines, oldSkipped, unmodeledIter;
        long lastScanMs; string lastError = ""; string state = "starting";
        int filesTotal, filesDone; long bytesTotal, bytesDone;
        bool cacheLoaded;
        Dictionary<string, string[]> sourceInfo = new Dictionary<string, string[]>();

        public UsageEngine(UsageConfig c) { cfg = c; latest = NotStarted(cfg.Clock()); }

        // Newest published snapshot. When the producer is stopping/stopped the numbers and times stay as they were, but the state says so.
        public Dictionary<string, object> Latest
        {
            get
            {
                Dictionary<string, object> l = latest; string lc = lifecycle;
                if (lc == "") return l;
                Dictionary<string, object> c = new Dictionary<string, object>(l);
                Dictionary<string, object> pr = new Dictionary<string, object>();
                pr["state"] = lc; pr["stopped_at"] = lc == "stopped" ? (object)stoppedAt : null;
                c["state"] = lc; c["producer"] = pr;
                return c;
            }
        }
        public bool StopRequested { get { return stop; } }

        public void SeedFrom(Dictionary<string, object> old)
        {
            if (old == null || !old.ContainsKey("windows")) return;
            Dictionary<string, object> c = Clone(old);
            c["state"] = "starting"; Dictionary<string, object> prod = new Dictionary<string, object>(); prod["state"] = "running"; prod["stopped_at"] = null; c["producer"] = prod;
            latest = c;
        }
        public long BytesRead { get { return bytesRead; } }
        public int RecordCount { get { return recs.Count; } }
        public long LinesParsed { get { return linesParsed; } }
        public long BadLines { get { return badLines; } }
        // A stop is final for this engine: nothing clears it, so a stop requested just before ScanOnce starts is not lost.
        public void RequestStop() { stop = true; if (lifecycle == "") lifecycle = "stopping"; try { wake.Set(); } catch (ObjectDisposedException) { } }
        public void MarkStopped() { stoppedAt = cfg.Clock(); lifecycle = "stopped"; }

        public static Dictionary<string, object> NotStarted(long now)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["schema"] = "usage.v1"; d["state"] = "not_started"; d["generated_at"] = now;
            Dictionary<string, object> prod = new Dictionary<string, object>(); prod["state"] = "not_started"; prod["stopped_at"] = null; d["producer"] = prod;
            d["note"] = "UsageBackend.Start() has not been called; there are no numbers yet (not zero usage).";
            return d;
        }

        // Worker body: load the cache, then scan until stopped. Returns after the final cache write, so a successor never overlaps it.
        public void Run()
        {
            try
            {
                try { Diag.Write("usage: backend started"); } catch { }
                if (!stop) try { LoadCache(); } catch (Exception e) { lastError = "cache: " + e.Message; }
                while (!stop)
                {
                    try { ScanOnce(); } catch (Exception e) { lastError = e.GetType().Name + ": " + e.Message; state = "error"; try { Diag.Error("usage scan", e); } catch { } Publish(); }
                    if (stop) break;
                    wake.WaitOne(cfg.IntervalMs);
                }
            }
            finally { try { SaveCache(); } catch { } }
        }

        // ------------------------------------------------------------ scanning

        class Cand { public string Path; public byte Prov; public long Len, Mtime, Hash; public FileState St; }

        public void ScanOnce()
        {
            if (stop) return;
            Stopwatch sw = Stopwatch.StartNew();
            long now = cfg.Clock();
            long[] dayStart = DayStarts(now); long windowStart = dayStart[cfg.WindowDays - 1];
            ReloadPrices();
            List<Cand> cands = Enumerate(windowStart);
            cands.Sort(delegate(Cand a, Cand b) { return b.Mtime.CompareTo(a.Mtime); });
            List<Cand> work = new List<Cand>(); long total = 0;
            foreach (Cand c in cands)
            {
                FileState st; files.TryGetValue(c.Hash, out st); c.St = st;
                if (st != null) st.Seen = true;
                if (st == null || st.Offset != c.Len || st.Mtime != c.Mtime) { work.Add(c); total += c.Len - (st != null && st.Offset <= c.Len ? st.Offset : 0); }
            }
            filesTotal = work.Count; filesDone = 0; bytesTotal = total; bytesDone = 0;
            if (work.Count > 0) state = "scanning";
            long lastPub = Stopwatch.GetTimestamp(), lastSave = lastPub;
            long freq = Stopwatch.Frequency;
            for (int i = 0; i < work.Count; i++)
            {
                if (stop) break;
                Cand c = work[i];
                // everything newer than this file is complete, so the covered range starts at its modification time
                scanFrom = Math.Max(windowStart, c.Mtime);
                ProcessFile(c, windowStart);
                filesDone = i + 1;
                long t = Stopwatch.GetTimestamp();
                if ((t - lastPub) > freq * 2) { Publish(); lastPub = t; }
                if ((t - lastSave) > freq * 30) { SaveCache(); lastSave = t; }
            }
            if (!stop)
            {
                scanFrom = windowStart; state = "ready"; dataAsOf = now;
                Prune(windowStart);
                List<long> gone = new List<long>(); foreach (KeyValuePair<long, FileState> kv in files) if (!kv.Value.Seen) gone.Add(kv.Key);
                foreach (long k in gone) { files.Remove(k); dirty = true; }
                if (work.Count > 0) dirty = true;
            }
            foreach (FileState f in files.Values) f.Seen = false;
            lastScanMs = sw.ElapsedMilliseconds;
            Publish();
            if (dirty) SaveCache();
        }

        List<Cand> Enumerate(long windowStart)
        {
            List<Cand> list = new List<Cand>(); long minM = windowStart - 3600000; sourceInfo.Clear();
            int okC = 0, okX = 0; bool anyC = false, anyX = false;
            foreach (string root in cfg.ClaudeRoots) { if (Directory.Exists(root)) { anyC = true; okC += AddFiles(list, new DirectoryInfo(root), 0, minM); } }
            long cutoffDay = windowStart - 60L * 86400000L;
            foreach (string root in cfg.CodexRoots)
            {
                if (!Directory.Exists(root)) continue; anyX = true;
                if (Path.GetFileName(root.TrimEnd('\\', '/')).Equals("sessions", StringComparison.OrdinalIgnoreCase)) okX += AddCodexSessions(list, root, minM, cutoffDay);
                else okX += AddFiles(list, new DirectoryInfo(root), 1, minM);
            }
            sourceInfo["claude"] = new string[] { anyC ? "ok" : "missing", okC.ToString(CultureInfo.InvariantCulture) };
            sourceInfo["codex"] = new string[] { anyX ? "ok" : "missing", okX.ToString(CultureInfo.InvariantCulture) };
            return list;
        }

        int AddFiles(List<Cand> list, DirectoryInfo di, byte prov, long minM)
        {
            int n = 0;
            try
            {
                foreach (FileInfo fi in di.EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
                {
                    long m = ToMs(fi.LastWriteTimeUtc); n++;
                    if (m < minM) continue;
                    Cand c = new Cand(); c.Path = fi.FullName; c.Prov = prov; c.Len = fi.Length; c.Mtime = m; c.Hash = Fnv(fi.FullName.ToLowerInvariant()); list.Add(c);
                }
            }
            catch (Exception e) { lastError = "enumerate " + di.Name + ": " + e.Message; }
            return n;
        }

        // sessions/YYYY/MM/DD/*.jsonl: date folders far older than the window are skipped (a session keeps living in its start-date folder, so keep a margin)
        int AddCodexSessions(List<Cand> list, string root, long minM, long cutoffMs)
        {
            int n = 0; DateTime cut = FromMs(cutoffMs).Date;
            try
            {
                foreach (DirectoryInfo y in new DirectoryInfo(root).EnumerateDirectories())
                {
                    int yy; if (!int.TryParse(y.Name, out yy)) continue; if (yy < cut.Year) continue;
                    foreach (DirectoryInfo mo in y.EnumerateDirectories())
                    {
                        int mm; if (!int.TryParse(mo.Name, out mm)) continue; if (yy == cut.Year && mm < cut.Month) continue;
                        foreach (DirectoryInfo dd in mo.EnumerateDirectories())
                        {
                            int d; if (!int.TryParse(dd.Name, out d)) continue;
                            if (yy == cut.Year && mm == cut.Month && d < cut.Day) continue;
                            n += AddFiles(list, dd, 1, minM);
                        }
                    }
                }
                foreach (FileInfo fi in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly))
                {
                    long m = ToMs(fi.LastWriteTimeUtc); n++; if (m < minM) continue;
                    Cand c = new Cand(); c.Path = fi.FullName; c.Prov = 1; c.Len = fi.Length; c.Mtime = m; c.Hash = Fnv(fi.FullName.ToLowerInvariant()); list.Add(c);
                }
            }
            catch (Exception e) { lastError = "enumerate codex: " + e.Message; }
            return n;
        }

        void ReloadPrices()
        {
            string p = PriceBook.Locate(cfg.PricesPath); long stamp = -2;
            try { if (p != null && File.Exists(p)) stamp = File.GetLastWriteTimeUtc(p).Ticks ^ new FileInfo(p).Length; } catch { }
            if (stamp == pricesStamp && p == pricesPath) return;
            pricesStamp = stamp; pricesPath = p; prices = PriceBook.Load(cfg.PricesPath);
        }

        // ------------------------------------------------------------ reading one file

        void ProcessFile(Cand c, long windowStart)
        {
            FileState st = c.St;
            if (st == null) { st = new FileState(); files[c.Hash] = st; }
            st.Seen = true;
            try
            {
                using (FileStream fs = new FileStream(c.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    long size = fs.Length;
                    if (st.Offset > 0)
                    {
                        bool reset = size < st.Offset;
                        if (!reset) reset = HeadHashOf(fs, st.HeadLen) != st.HeadHash;
                        if (!reset) { fs.Seek(st.Offset - 1, SeekOrigin.Begin); if (fs.ReadByte() != 10) reset = true; }
                        if (reset) ResetFile(st);
                    }
                    if (st.Offset == 0) { st.HeadLen = Math.Min(4096, size); st.HeadHash = HeadHashOf(fs, st.HeadLen); }
                    fs.Seek(st.Offset, SeekOrigin.Begin);
                    int fileKey = (int)(c.Hash ^ (c.Hash >> 32));
                    ReadLines(fs, c, st, fileKey, windowStart);
                    st.Mtime = c.Mtime;
                }
            }
            catch (Exception e) { lastError = "read file: " + e.GetType().Name + ": " + e.Message; }
        }

        void ResetFile(FileState st)
        {
            st.Offset = 0; st.SessionId = ""; st.LastModel = ""; st.HasRec = false; st.LastTotal = 0; st.Turns.Clear(); st.Tier = ""; st.Derived = 0;
        }

        static long HeadHashOf(FileStream fs, long len)
        {
            if (len <= 0) return 0;
            byte[] b = new byte[len]; fs.Seek(0, SeekOrigin.Begin); int got = 0;
            while (got < len) { int r = fs.Read(b, got, (int)len - got); if (r <= 0) break; got += r; }
            return Fnv(b, 0, got);
        }

        byte[] buf = new byte[1 << 20];

        void ReadLines(FileStream fs, Cand c, FileState st, int fileKey, long windowStart)
        {
            int have = 0; long pos = st.Offset;              // pos: file offset of buf[0]
            while (!stop)
            {
                if (have == buf.Length) Array.Resize(ref buf, buf.Length * 2);
                int r = fs.Read(buf, have, buf.Length - have);
                if (r <= 0) break;
                bytesRead += r; bytesDone += r;
                int from = have; have += r; int lineStart = 0;
                while (true)
                {
                    int nl = Array.IndexOf<byte>(buf, 10, from, have - from);
                    if (nl < 0) break;
                    HandleLine(c, st, fileKey, windowStart, lineStart, nl - lineStart);
                    lineStart = nl + 1; from = lineStart;
                    st.Offset = pos + lineStart;
                }
                if (lineStart > 0) { Buffer.BlockCopy(buf, lineStart, buf, 0, have - lineStart); have -= lineStart; pos += lineStart; }
                if (buf.Length > (16 << 20) && have < (1 << 20)) { byte[] small = new byte[1 << 20]; Buffer.BlockCopy(buf, 0, small, 0, have); buf = small; }   // do not keep a huge buffer after one huge line
            }
        }

        // ------------------------------------------------------------ line handlers

        static readonly byte[] PatUsage = Encoding.ASCII.GetBytes("usage\"");
        static readonly byte[][] CodexPats = new byte[][] {
            Encoding.ASCII.GetBytes("\"token_usage_record\""), Encoding.ASCII.GetBytes("\"token_count\""), Encoding.ASCII.GetBytes("\"turn_context\""),
            Encoding.ASCII.GetBytes("\"session_meta\""), Encoding.ASCII.GetBytes("\"thread_settings_applied\"") };

        static int Find(byte[] b, int s, int e, byte[] pat)
        {
            int last = e - pat.Length;
            while (s <= last)
            {
                int i = Array.IndexOf<byte>(b, pat[0], s, last - s + 1);
                if (i < 0) return -1;
                int k = 1; while (k < pat.Length && b[i + k] == pat[k]) k++;
                if (k == pat.Length) return i;
                s = i + 1;
            }
            return -1;
        }

        void HandleLine(Cand c, FileState st, int fileKey, long windowStart, int s, int len)
        {
            if (len < 20) return;
            int e = s + len; if (buf[e - 1] == 13) e--;
            try
            {
                if (c.Prov == 0)
                {
                    if (Find(buf, s, e, PatUsage) < 0) return;
                    linesParsed++; ClaudeLine(Encoding.UTF8.GetString(buf, s, e - s), c, fileKey, windowStart);
                }
                else
                {
                    int head = Math.Min(e, s + 400); bool hit = false;
                    for (int i = 0; i < CodexPats.Length && !hit; i++) hit = Find(buf, s, head, CodexPats[i]) >= 0;
                    if (!hit) return;
                    linesParsed++; CodexLine(Encoding.UTF8.GetString(buf, s, e - s), c, st, fileKey, windowStart);
                }
            }
            catch (Exception) { badLines++; }
        }

        // -- minimal JSON cursor: finds the spans of chosen keys of one object without building the tree

        static int Ws(string s, int p) { while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++; return p; }

        static int SkipStr(string s, int p)
        {
            p++;
            while (p < s.Length)
            {
                char ch = s[p];
                if (ch == '\\') { p += 2; continue; }
                if (ch == '"') return p + 1;
                p++;
            }
            throw new FormatException("unterminated string");
        }

        static int SkipVal(string s, int p)
        {
            p = Ws(s, p); if (p >= s.Length) throw new FormatException("eof");
            char c = s[p];
            if (c == '"') return SkipStr(s, p);
            if (c == '{' || c == '[')
            {
                int depth = 0;
                do
                {
                    char ch = s[p];
                    if (ch == '"') { p = SkipStr(s, p); continue; }
                    if (ch == '{' || ch == '[') depth++; else if (ch == '}' || ch == ']') depth--;
                    p++;
                } while (depth > 0 && p < s.Length);
                if (depth != 0) throw new FormatException("unbalanced");
                return p;
            }
            while (p < s.Length && s[p] != ',' && s[p] != '}' && s[p] != ']' && s[p] != ' ' && s[p] != '\r' && s[p] != '\n' && s[p] != '\t') p++;
            return p;
        }

        // spans of the wanted keys in the object that starts at p (vs = -1 when absent)
        static bool Fields(string s, int p, string[] want, int[] vs, int[] ve)
        {
            for (int i = 0; i < want.Length; i++) { vs[i] = -1; ve[i] = -1; }
            p = Ws(s, p); if (p >= s.Length || s[p] != '{') return false;
            p++;
            while (true)
            {
                p = Ws(s, p); if (p >= s.Length) return false;
                if (s[p] == '}') return true;
                if (s[p] != '"') return false;
                int ks = p + 1; p = SkipStr(s, p); int kl = p - 1 - ks;
                p = Ws(s, p); if (p >= s.Length || s[p] != ':') return false;
                p = Ws(s, p + 1);
                int vb = p; p = SkipVal(s, p);
                for (int i = 0; i < want.Length; i++)
                    if (want[i].Length == kl && string.CompareOrdinal(s, ks, want[i], 0, kl) == 0) { vs[i] = vb; ve[i] = p; break; }
                p = Ws(s, p); if (p < s.Length && s[p] == ',') p++;
            }
        }

        static string StrOf(string s, int vs, int ve)
        {
            if (vs < 0 || s[vs] != '"') return "";
            string t = s.Substring(vs + 1, ve - vs - 2);
            if (t.IndexOf('\\') >= 0) { try { t = (string)J.Parse(s.Substring(vs, ve - vs)); } catch { } }
            return t;
        }

        static long NumOf(string s, int vs, int ve)
        {
            if (vs < 0) return 0;
            int p = vs; bool neg = false; if (p < ve && s[p] == '-') { neg = true; p++; }
            long v = 0; bool any = false;
            while (p < ve && s[p] >= '0' && s[p] <= '9') { v = v * 10 + (s[p] - '0'); p++; any = true; }
            if (!any) return 0;
            if (p < ve && (s[p] == '.' || s[p] == 'e' || s[p] == 'E')) { double d; if (double.TryParse(s.Substring(vs, ve - vs), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return (long)d; }
            return neg ? -v : v;
        }

        static readonly string[] ClaudeTop = new string[] { "type", "timestamp", "requestId", "message", "uuid" };
        static readonly string[] ClaudeMsg = new string[] { "id", "model", "usage" };
        static readonly string[] ClaudeUse = new string[] { "input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens", "output_tokens", "cache_creation", "speed", "service_tier", "inference_geo", "server_tool_use", "iterations" };
        static readonly string[] CcKeys = new string[] { "ephemeral_5m_input_tokens", "ephemeral_1h_input_tokens" };
        static readonly string[] ToolKeys = new string[] { "web_search_requests", "web_fetch_requests" };
        static readonly string[] TypeKey = new string[] { "type" };

        void ClaudeLine(string s, Cand c, int fileKey, long windowStart)
        {
            int[] a = new int[5], ae = new int[5];
            if (!Fields(s, 0, ClaudeTop, a, ae)) { badLines++; return; }
            if (StrOf(s, a[0], ae[0]) != "assistant" || a[3] < 0) return;
            int[] m = new int[3], me = new int[3];
            if (!Fields(s, a[3], ClaudeMsg, m, me) || m[2] < 0) return;
            string model = StrOf(s, m[1], me[1]);
            if (model == "<synthetic>") return;
            int[] u = new int[10], ue = new int[10];
            if (!Fields(s, m[2], ClaudeUse, u, ue)) { badLines++; return; }
            UsageRec r = new UsageRec(); r.Prov = 0; r.Kind = 0; r.FileKey = fileKey;
            long inp = NumOf(s, u[0], ue[0]), cw = NumOf(s, u[1], ue[1]), cr = NumOf(s, u[2], ue[2]), outp = NumOf(s, u[3], ue[3]);
            long w5 = 0, w1 = 0;
            if (u[4] >= 0 && s[u[4]] == '{')
            {
                int[] cc = new int[2], cce = new int[2];
                if (Fields(s, u[4], CcKeys, cc, cce)) { w5 = NumOf(s, cc[0], cce[0]); w1 = NumOf(s, cc[1], cce[1]); }
            }
            long wu = cw - w5 - w1; if (wu < 0) { wu = 0; }
            r.In = inp; r.Cr = cr; r.Cw5 = w5; r.Cw1 = w1; r.CwU = wu; r.Out = outp;
            if (u[8] >= 0 && s[u[8]] == '{')
            {
                int[] tk = new int[2], tke = new int[2];
                if (Fields(s, u[8], ToolKeys, tk, tke)) r.Tool = (int)(NumOf(s, tk[0], tke[0]) + NumOf(s, tk[1], tke[1]));
            }
            string speed = StrOf(s, u[5], ue[5]), tier = StrOf(s, u[6], ue[6]), geo = StrOf(s, u[7], ue[7]);
            if (speed == "fast") r.Flags |= FlagFast; else if (speed != "" && speed != "standard") r.Flags |= FlagNoPrice;
            if (tier != "" && tier != "standard") r.Flags |= FlagNoPrice;
            if (geo == "us") r.Flags |= FlagUs;
            if (u[9] >= 0 && s[u[9]] == '[')
            {   // iterations other than plain "message" (e.g. advisor calls) would be billed separately: count them so the snapshot can say so
                int p = u[9] + 1;
                while (true)
                {
                    p = Ws(s, p); if (p >= ue[9] || s[p] == ']') break;
                    if (s[p] == '{') { int[] ty = new int[1], tye = new int[1]; if (Fields(s, p, TypeKey, ty, tye) && StrOf(s, ty[0], tye[0]) != "message") unmodeledIter++; }
                    p = SkipVal(s, p); p = Ws(s, p); if (p < s.Length && s[p] == ',') p++;
                }
            }
            if (r.In + r.Cr + r.Cw5 + r.Cw1 + r.CwU + r.Out + r.Tool == 0) return;
            string id = StrOf(s, m[0], me[0]), rq = StrOf(s, a[2], ae[2]);
            if (id != "") r.Id = Fnv("c:" + id); else if (rq != "") r.Id = Fnv("cr:" + rq + ":" + (r.In + r.Out)); else r.Id = Fnv("cu:" + StrOf(s, a[4], ae[4]) + fileKey);
            r.Ts = ParseTime(StrOf(s, a[1], ae[1])); if (r.Ts == 0) r.Ts = c.Mtime;
            r.Model = ModelIndex(model == "" ? "unknown" : model);
            Add(r, windowStart);
        }

        static readonly string[] CxTop = new string[] { "timestamp", "type", "payload" };
        static readonly string[] CxRec = new string[] { "response_id", "turn_id", "usage" };
        static readonly string[] CxUse = new string[] { "input_tokens", "cached_input_tokens", "cache_write_input_tokens", "output_tokens", "total_tokens" };
        static readonly string[] CxCtx = new string[] { "turn_id", "model" };
        static readonly string[] CxMeta = new string[] { "id" };
        static readonly string[] CxEvt = new string[] { "type", "info", "thread_settings" };
        static readonly string[] CxInfo = new string[] { "total_token_usage", "last_token_usage" };
        static readonly string[] CxTier = new string[] { "service_tier" };

        void CodexLine(string s, Cand c, FileState st, int fileKey, long windowStart)
        {
            int[] a = new int[3], ae = new int[3];
            if (!Fields(s, 0, CxTop, a, ae)) { badLines++; return; }
            string type = StrOf(s, a[1], ae[1]); if (a[2] < 0) return;
            long ts = ParseTime(StrOf(s, a[0], ae[0])); if (ts == 0) ts = c.Mtime;
            if (type == "session_meta")
            {
                int[] x = new int[1], xe = new int[1];
                if (Fields(s, a[2], CxMeta, x, xe) && st.SessionId == "") st.SessionId = StrOf(s, x[0], xe[0]);
            }
            else if (type == "turn_context")
            {
                int[] x = new int[2], xe = new int[2];
                if (Fields(s, a[2], CxCtx, x, xe))
                {
                    string model = StrOf(s, x[1], xe[1]), turn = StrOf(s, x[0], xe[0]);
                    if (model != "") { st.LastModel = model; if (turn != "") { st.Turns.Add(turn + "=" + model); if (st.Turns.Count > 16) st.Turns.RemoveAt(0); } }
                }
            }
            else if (type == "token_usage_record")
            {
                int[] x = new int[3], xe = new int[3];
                if (!Fields(s, a[2], CxRec, x, xe) || x[2] < 0) return;
                int[] u = new int[5], ue = new int[5];
                if (!Fields(s, x[2], CxUse, u, ue)) { badLines++; return; }
                if (!st.HasRec) { st.HasRec = true; if (st.Derived > 0) { PurgeDerived(fileKey); st.Derived = 0; } }
                UsageRec r = new UsageRec(); r.Prov = 1; r.Kind = 0; r.FileKey = fileKey; r.Ts = ts;
                CodexTokens(ref r, NumOf(s, u[0], ue[0]), NumOf(s, u[1], ue[1]), NumOf(s, u[2], ue[2]), NumOf(s, u[3], ue[3]));
                if (r.In + r.Cr + r.Cw5 + r.Cw1 + r.CwU + r.Out == 0) return;
                string resp = StrOf(s, x[0], xe[0]); string turn = StrOf(s, x[1], xe[1]);
                r.Id = resp != "" ? Fnv("x:" + resp) : Fnv("xu:" + st.SessionId + ":" + turn + ":" + ts + ":" + r.In + ":" + r.Out);
                r.Model = ModelIndex(CodexModel(st, turn));
                if (st.Tier != "" && st.Tier != "default") r.Flags |= FlagNoPrice;
                Add(r, windowStart);
            }
            else if (type == "event_msg")
            {
                int[] x = new int[3], xe = new int[3];
                if (!Fields(s, a[2], CxEvt, x, xe)) return;
                string et = StrOf(s, x[0], xe[0]);
                if (et == "thread_settings_applied" && x[2] >= 0)
                {
                    int[] t = new int[1], te = new int[1];
                    if (Fields(s, x[2], CxTier, t, te) && t[0] >= 0) st.Tier = StrOf(s, t[0], te[0]);
                }
                else if (et == "token_count" && x[1] >= 0 && s[x[1]] == '{' && !st.HasRec)
                {   // only for transcripts that carry no per-response records: use the per-request counter, de-duplicated by the cumulative total
                    int[] i = new int[2], ie = new int[2];
                    if (!Fields(s, x[1], CxInfo, i, ie) || i[0] < 0) return;
                    int[] tu = new int[5], tue = new int[5];
                    if (!Fields(s, i[0], CxUse, tu, tue)) return;
                    long total = NumOf(s, tu[4], tue[4]);
                    if (total <= st.LastTotal) return;          // repeated event with an unchanged cumulative counter
                    int[] lu = new int[5], lue = new int[5];
                    UsageRec r = new UsageRec(); r.Prov = 1; r.Kind = 1; r.FileKey = fileKey; r.Ts = ts;
                    if (i[1] >= 0 && Fields(s, i[1], CxUse, lu, lue)) CodexTokens(ref r, NumOf(s, lu[0], lue[0]), NumOf(s, lu[1], lue[1]), NumOf(s, lu[2], lue[2]), NumOf(s, lu[3], lue[3]));
                    else return;
                    st.LastTotal = total;
                    if (r.In + r.Cr + r.Cw5 + r.Cw1 + r.CwU + r.Out == 0) return;
                    r.Id = Fnv("xt:" + st.SessionId + ":" + total);
                    r.Model = ModelIndex(CodexModel(st, "")); st.Derived++;
                    if (st.Tier != "" && st.Tier != "default") r.Flags |= FlagNoPrice;
                    Add(r, windowStart);
                }
            }
        }

        // OpenAI counts cached and cache-written tokens inside input_tokens; split them into disjoint parts. Reasoning tokens are inside output_tokens.
        static void CodexTokens(ref UsageRec r, long input, long cached, long write, long output)
        {
            if (cached > input) cached = input;
            if (write > input - cached) write = input - cached;
            r.In = input - cached - write; r.Cr = cached; r.CwU = write; r.Out = output;
        }

        static string CodexModel(FileState st, string turn)
        {
            if (turn != "") for (int i = st.Turns.Count - 1; i >= 0; i--) { string t = st.Turns[i]; if (t.StartsWith(turn + "=", StringComparison.Ordinal)) return t.Substring(turn.Length + 1); }
            return st.LastModel != "" ? st.LastModel : "unknown";
        }

        // ------------------------------------------------------------ record store

        int ModelIndex(string m)
        {
            int i; if (modelIdx.TryGetValue(m, out i)) return i;
            i = models.Count; models.Add(m); modelIdx[m] = i; return i;
        }

        void Add(UsageRec r, long windowStart)
        {
            if (r.Ts < windowStart) { oldSkipped++; return; }
            int i;
            if (index.TryGetValue(r.Id, out i))
            {   // same request seen again (streamed lines, resumed copy, re-read): keep the earliest time and the largest counters
                UsageRec o = recs[i];
                bool ch = false;
                if (r.Ts < o.Ts) { o.Ts = r.Ts; ch = true; }
                if (r.In > o.In) { o.In = r.In; ch = true; } if (r.Cr > o.Cr) { o.Cr = r.Cr; ch = true; } if (r.Cw5 > o.Cw5) { o.Cw5 = r.Cw5; ch = true; } if (r.Cw1 > o.Cw1) { o.Cw1 = r.Cw1; ch = true; }
                if (r.CwU > o.CwU) { o.CwU = r.CwU; ch = true; } if (r.Out > o.Out) { o.Out = r.Out; ch = true; } if (r.Tool > o.Tool) { o.Tool = r.Tool; ch = true; }
                if ((o.Flags | r.Flags) != o.Flags) { o.Flags |= r.Flags; ch = true; }
                if (models[o.Model] == "unknown" && models[r.Model] != "unknown") { o.Model = r.Model; ch = true; }
                if (ch) { recs[i] = o; dirty = true; }
                return;
            }
            index[r.Id] = recs.Count; recs.Add(r); dirty = true;
        }

        void Rebuild(List<UsageRec> keep)
        {
            recs = keep; index = new Dictionary<long, int>(keep.Count * 2);
            for (int i = 0; i < keep.Count; i++) index[keep[i].Id] = i;
            dirty = true;
        }

        void PurgeDerived(int fileKey)
        {
            List<UsageRec> keep = new List<UsageRec>(recs.Count); bool any = false;
            foreach (UsageRec r in recs) { if (r.Kind == 1 && r.FileKey == fileKey) any = true; else keep.Add(r); }
            if (any) Rebuild(keep);
        }

        void Prune(long windowStart)
        {
            bool any = false; foreach (UsageRec r in recs) if (r.Ts < windowStart) { any = true; break; }
            if (!any) return;
            List<UsageRec> keep = new List<UsageRec>(recs.Count);
            foreach (UsageRec r in recs) if (r.Ts >= windowStart) keep.Add(r);
            Rebuild(keep);
        }

        // ------------------------------------------------------------ pricing and snapshot

        // returns the price in USD of the priced components; tokens of components without a price go to 'unpriced'
        void Price(UsageRec r, out double usd, out long unpriced)
        {
            usd = 0; unpriced = r.CwU;
            long[] tok = new long[] { r.In, r.Cr, r.Cw5, r.Cw1, r.Out };
            PriceModel pm = prices.Loaded ? prices.Find(models[r.Model]) : null;
            List<PriceTier> tiers = pm == null ? null : ((r.Flags & FlagFast) != 0 ? pm.FastTiers : pm.Tiers);
            if (pm == null || tiers == null || tiers.Count == 0 || (r.Flags & FlagNoPrice) != 0)
            {
                for (int i = 0; i < 5; i++) unpriced += tok[i];
                return;
            }
            long prompt = r.In + r.Cr + r.Cw5 + r.Cw1 + r.CwU;
            PriceTier t = tiers[tiers.Count - 1];
            foreach (PriceTier x in tiers) if (x.Max < 0 || prompt <= x.Max) { t = x; break; }
            double mult = (r.Flags & FlagUs) != 0 && pm.UsMult > 0 ? pm.UsMult : 1.0;
            for (int i = 0; i < 5; i++)
            {
                if (tok[i] == 0) continue;
                if (double.IsNaN(t.P[i])) unpriced += tok[i]; else usd += tok[i] * t.P[i] * mult / 1e6;
            }
        }

        long[] DayStarts(long nowMs)
        {
            int n = cfg.WindowDays; long[] st = new long[n];
            DateTime local = TimeZoneInfo.ConvertTimeFromUtc(FromMs(nowMs), cfg.Tz).Date;
            for (int i = 0; i < n; i++)
            {
                DateTime d = DateTime.SpecifyKind(local.AddDays(-i), DateTimeKind.Unspecified);
                if (cfg.Tz.IsInvalidTime(d)) d = d.AddHours(1);
                st[i] = ToMs(TimeZoneInfo.ConvertTimeToUtc(d, cfg.Tz));
            }
            return st;
        }

        class WinAcc
        {
            public Agg Total = new Agg(); public Agg[] Prov = new Agg[] { new Agg(), new Agg() };
            public Dictionary<string, Agg> Models = new Dictionary<string, Agg>();
        }

        void Publish()
        {
            try { latest = BuildSnapshot(); } catch (Exception e) { lastError = "snapshot: " + e.Message; }
            try { WriteStats(); } catch (Exception e) { lastError = "stats: " + e.Message; }
        }

        // Detailed background statistics, kept for the window only (30 days): one line per local hour x provider x model with the requests, the four token kinds, the equivalent price and the
        // unpriced tokens. Rewritten whole at most every 5 minutes next to the usage cache (<data folder>\stats\hourly-30d.csv), so older hours drop out by themselves. Numbers and model names only.
        long lastStatsAt;
        void WriteStats()
        {
            if (cfg.CacheDir == null) return;
            long now = cfg.Clock(); if (lastStatsAt != 0 && now - lastStatsAt < 300000) return;
            if (recs.Count == 0) return;
            lastStatsAt = now;
            long from = DayStarts(now)[cfg.WindowDays - 1];
            SortedDictionary<string, Agg> g = new SortedDictionary<string, Agg>(StringComparer.Ordinal);
            foreach (UsageRec r in recs)
            {
                if (r.Ts < from) continue;
                double usd; long unp; Price(r, out usd, out unp);
                DateTime lt = TimeZoneInfo.ConvertTimeFromUtc(FromMs(r.Ts), cfg.Tz);
                string key = lt.ToString("yyyy-MM-dd HH':00'", CultureInfo.InvariantCulture) + "," + ProvName[r.Prov] + ",\"" + models[r.Model].Replace("\"", "") + "\"";
                Agg a; if (!g.TryGetValue(key, out a)) { a = new Agg(); g[key] = a; }
                AddTo(a, r, usd, unp);
            }
            string dir = Path.Combine(Path.GetDirectoryName(cfg.CacheDir.TrimEnd('\\', '/')), "stats"); Directory.CreateDirectory(dir);
            StringBuilder sb = new StringBuilder("hour_local,provider,model,requests,input,cache_read,cache_write,output,tool_requests,usd_equivalent,unpriced_tokens\r\n");
            foreach (KeyValuePair<string, Agg> kv in g)
                sb.Append(kv.Key).Append(',').Append(kv.Value.Req).Append(',').Append(kv.Value.In).Append(',').Append(kv.Value.Cr).Append(',').Append(kv.Value.Cw).Append(',').Append(kv.Value.Out).Append(',')
                  .Append(kv.Value.Tool).Append(',').Append(kv.Value.Usd.ToString("0.0000", CultureInfo.InvariantCulture)).Append(',').Append(kv.Value.Unpriced).Append("\r\n");
            string file = Path.Combine(dir, "hourly-30d.csv"), tmp = file + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        static readonly string[] ProvName = new string[] { "claude", "codex" };

        Dictionary<string, object> BuildSnapshot()
        {
            long now = cfg.Clock(); int days = cfg.WindowDays;
            long[] ds = DayStarts(now);
            WinAcc[] win = new WinAcc[] { new WinAcc(), new WinAcc(), new WinAcc() };    // today, 7d, 30d(window)
            int[] winDays = new int[] { 1, Math.Min(7, days), days };
            Agg[] day = new Agg[days]; Agg[][] dayProv = new Agg[days][]; for (int i = 0; i < days; i++) { day[i] = new Agg(); dayProv[i] = new Agg[] { new Agg(), new Agg() }; }
            Agg[] hour = new Agg[24]; Agg[][] hourProv = new Agg[24][]; for (int i = 0; i < 24; i++) { hour[i] = new Agg(); hourProv[i] = new Agg[] { new Agg(), new Agg() }; }
            long[] last = new long[2];
            foreach (UsageRec r in recs)
            {
                int d = -1; for (int i = 0; i < days; i++) if (r.Ts >= ds[i]) { d = i; break; }
                if (d < 0) continue;
                double usd; long unp; Price(r, out usd, out unp);
                if (r.Ts > last[r.Prov]) last[r.Prov] = r.Ts;
                AddTo(day[d], r, usd, unp); AddTo(dayProv[d][r.Prov], r, usd, unp);
                if (d == 0) { int h = (int)((r.Ts - ds[0]) / 3600000L); if (h > 23) h = 23; if (h < 0) h = 0; AddTo(hour[h], r, usd, unp); AddTo(hourProv[h][r.Prov], r, usd, unp); }
                string key = ProvName[r.Prov] + "|" + models[r.Model];
                for (int w = 0; w < 3; w++)
                {
                    if (d >= winDays[w]) continue;
                    AddTo(win[w].Total, r, usd, unp); AddTo(win[w].Prov[r.Prov], r, usd, unp);
                    Agg m; if (!win[w].Models.TryGetValue(key, out m)) { m = new Agg(); win[w].Models[key] = m; }
                    AddTo(m, r, usd, unp);
                }
            }
            Dictionary<string, object> snap = new Dictionary<string, object>();
            snap["schema"] = "usage.v1";
            snap["state"] = state;
            Dictionary<string, object> prod = new Dictionary<string, object>(); prod["state"] = "running"; prod["stopped_at"] = null; snap["producer"] = prod;
            snap["generated_at"] = now;
            snap["data_as_of"] = dataAsOf > 0 ? dataAsOf : (savedAt > 0 ? savedAt : 0);
            snap["note"] = "API-equivalent estimate from local transcripts at list prices; not a subscription bill.";
            Dictionary<string, object> scan = new Dictionary<string, object>();
            long cov = scanFrom > 0 ? scanFrom : (cacheLoaded ? ds[days - 1] : 0);
            if (state == "ready") cov = ds[days - 1];
            scan["complete"] = state == "ready"; scan["covered_from"] = cov; scan["window_start"] = ds[days - 1];
            scan["files_total"] = filesTotal; scan["files_done"] = filesDone; scan["bytes_total"] = bytesTotal; scan["bytes_done"] = bytesDone;
            scan["last_scan_ms"] = lastScanMs; scan["cache_loaded"] = cacheLoaded; scan["error"] = lastError == "" ? null : lastError;
            scan["unmodeled_iterations"] = unmodeledIter; scan["bad_lines"] = badLines;
            snap["scan"] = scan;
            Dictionary<string, object> src = new Dictionary<string, object>();
            for (int p = 0; p < 2; p++)
            {
                string[] si; sourceInfo.TryGetValue(ProvName[p], out si);
                Dictionary<string, object> o = new Dictionary<string, object>();
                o["status"] = si == null ? "unknown" : si[0]; o["files_seen"] = si == null ? 0 : int.Parse(si[1], CultureInfo.InvariantCulture);
                o["last_request_at"] = last[p] > 0 ? (object)last[p] : null;
                src[ProvName[p]] = o;
            }
            snap["sources"] = src;
            Dictionary<string, object> pr = new Dictionary<string, object>();
            pr["loaded"] = prices.Loaded; pr["version"] = prices.Version; pr["checked"] = prices.Checked; pr["error"] = prices.Error; pr["sources"] = prices.Sources;
            snap["pricing"] = pr;
            string[] wn = new string[] { "today", "7d", "30d" };
            Dictionary<string, object> windows = new Dictionary<string, object>();
            for (int w = 0; w < 3; w++)
            {
                if (w == 2 && days < 30) wn[2] = days + "d";
                if (w == 1 && days < 7) wn[1] = days + "d";
                long from = ds[winDays[w] - 1];
                Dictionary<string, object> o = AggObj(win[w].Total);
                o["from"] = from; o["to"] = now; o["days"] = winDays[w]; o["complete"] = cov <= from;
                Dictionary<string, object> bp = new Dictionary<string, object>();
                for (int p = 0; p < 2; p++) bp[ProvName[p]] = AggObj(win[w].Prov[p]);
                o["by_provider"] = bp;
                List<KeyValuePair<string, Agg>> ml = new List<KeyValuePair<string, Agg>>(win[w].Models);
                ml.Sort(delegate(KeyValuePair<string, Agg> x, KeyValuePair<string, Agg> y) { int cmp = y.Value.Total.CompareTo(x.Value.Total); return cmp != 0 ? cmp : string.CompareOrdinal(x.Key, y.Key); });
                List<object> mo = new List<object>();
                foreach (KeyValuePair<string, Agg> kv in ml)
                {
                    int bar = kv.Key.IndexOf('|'); string mname = kv.Key.Substring(bar + 1);
                    Dictionary<string, object> mdl = AggObj(kv.Value);
                    PriceModel pm = prices.Loaded ? prices.Find(mname) : null;
                    mdl["provider"] = kv.Key.Substring(0, bar); mdl["model"] = mname; mdl["label"] = pm != null ? pm.Label : mname; mdl["priced"] = pm != null;
                    mo.Add(mdl);
                }
                o["models"] = mo;
                windows[wn[w]] = o;
            }
            snap["windows"] = windows;
            List<object> dl = new List<object>();
            for (int i = days - 1; i >= 0; i--)
            {
                Dictionary<string, object> o = AggObj(day[i]);
                DateTime ld = TimeZoneInfo.ConvertTimeFromUtc(FromMs(ds[i]).AddMinutes(1), cfg.Tz);
                o["date"] = ld.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); o["from"] = ds[i]; o["to"] = i == 0 ? now : ds[i - 1];
                Dictionary<string, object> bp = new Dictionary<string, object>();
                for (int p = 0; p < 2; p++) bp[ProvName[p]] = AggObj(dayProv[i][p]);
                o["by_provider"] = bp;
                dl.Add(o);
            }
            snap["daily"] = dl;
            List<object> hl = new List<object>();
            for (int h = 0; h < 24; h++) { Dictionary<string, object> o = AggObj(hour[h]); o["from"] = ds[0] + h * 3600000L; Dictionary<string, object> hp = new Dictionary<string, object>(); for (int p = 0; p < 2; p++) hp[ProvName[p]] = AggObj(hourProv[h][p]); o["by_provider"] = hp; hl.Add(o); }
            snap["hourly_today"] = hl;
            return snap;
        }

        static void AddTo(Agg g, UsageRec r, double usd, long unpriced)
        {
            g.In += r.In; g.Cr += r.Cr; g.Cw5 += r.Cw5; g.Cw1 += r.Cw1; g.CwU += r.CwU; g.Cw += r.Cw5 + r.Cw1 + r.CwU; g.Out += r.Out;
            g.Req++; g.Usd += usd; g.Unpriced += unpriced; g.Tool += r.Tool;
        }

        // tokens: input = uncached input, cache_read, cache_write (all durations), output (includes reasoning); total = sum of the four
        static Dictionary<string, object> AggObj(Agg g)
        {
            Dictionary<string, object> o = new Dictionary<string, object>();
            o["requests"] = g.Req;
            Dictionary<string, object> t = new Dictionary<string, object>();
            t["input"] = g.In; t["cache_read"] = g.Cr; t["cache_write"] = g.Cw; t["output"] = g.Out; t["total"] = g.Total;
            t["cache_write_5m"] = g.Cw5; t["cache_write_1h"] = g.Cw1; t["cache_write_unknown"] = g.CwU;
            o["tokens"] = t;
            Dictionary<string, object> c = new Dictionary<string, object>();
            c["usd"] = Math.Round(g.Usd, 6);
            c["unpriced_tokens"] = g.Unpriced; c["unpriced_tool_requests"] = g.Tool;
            c["status"] = g.Req == 0 ? "empty" : (g.Unpriced == 0 && g.Tool == 0 ? "full" : (g.Usd > 0 ? "partial" : "none"));
            o["cost"] = c;
            return o;
        }

        public static Dictionary<string, object> Clone(Dictionary<string, object> d)
        {
            Dictionary<string, object> c = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kv in d) c[kv.Key] = CloneVal(kv.Value);
            return c;
        }

        static object CloneVal(object v)
        {
            Dictionary<string, object> d = v as Dictionary<string, object>; if (d != null) return Clone(d);
            List<object> l = v as List<object>;
            if (l != null) { List<object> n = new List<object>(l.Count); foreach (object x in l) n.Add(CloneVal(x)); return n; }
            return v;
        }

        // ------------------------------------------------------------ cache file (numbers and model names only; paths are stored as hashes)

        string CacheFile() { return cfg.CacheDir == null ? null : Path.Combine(cfg.CacheDir, "usage-cache.v1.txt"); }

        public void SaveCache()
        {
            string f = CacheFile(); if (f == null) return;
            long ws = DayStarts(cfg.Clock())[cfg.WindowDays - 1];
            Directory.CreateDirectory(cfg.CacheDir);
            string tmp = f + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp"; savedAt = cfg.Clock();
            using (StreamWriter w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            {
                w.Write("H\t1\t" + savedAt + "\t" + cfg.WindowDays + "\t" + (state == "ready" ? ws : scanFrom) + "\t" + ws + "\n");
                for (int i = 0; i < models.Count; i++) w.Write("M\t" + i + "\t" + Clean(models[i]) + "\n");
                foreach (KeyValuePair<long, FileState> kv in files)
                {
                    FileState s = kv.Value;
                    w.Write("F\t" + kv.Key + "\t" + s.Offset + "\t" + s.Mtime + "\t" + s.HeadLen + "\t" + s.HeadHash + "\t" + Clean(s.SessionId) + "\t" + (s.HasRec ? 1 : 0) + "\t" + s.LastTotal + "\t" + Clean(s.LastModel) + "\t" + Clean(s.Tier) + "\t" + Clean(string.Join(";", s.Turns.ToArray())) + "\t" + s.Derived + "\n");
                }
                foreach (UsageRec r in recs)
                    w.Write("R\t" + r.Prov + "\t" + r.Kind + "\t" + r.FileKey + "\t" + r.Id + "\t" + r.Ts + "\t" + r.Model + "\t" + r.Flags + "\t" + r.In + "\t" + r.Cr + "\t" + r.Cw5 + "\t" + r.Cw1 + "\t" + r.CwU + "\t" + r.Out + "\t" + r.Tool + "\n");
            }
            try { if (File.Exists(f)) File.Replace(tmp, f, null); else File.Move(tmp, f); }
            catch { try { File.Delete(tmp); } catch { } throw; }
            dirty = false;
        }

        static string Clean(string s) { return s == null ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '); }

        public void LoadCache()
        {
            string f = CacheFile(); if (f == null || !File.Exists(f)) return;
            string[] lines = File.ReadAllLines(f, Encoding.UTF8);
            if (lines.Length == 0) return;
            string[] h = lines[0].Split('\t');
            if (h.Length < 6 || h[0] != "H" || h[1] != "1") return;
            if (int.Parse(h[3], CultureInfo.InvariantCulture) != cfg.WindowDays) return;   // other window: rescan from scratch
            long ws = DayStarts(cfg.Clock())[cfg.WindowDays - 1];
            List<string> mm = new List<string>(); List<UsageRec> rl = new List<UsageRec>(); Dictionary<long, FileState> fl = new Dictionary<long, FileState>();
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('\t');
                if (p[0] == "M" && p.Length >= 3) { int ix = int.Parse(p[1], CultureInfo.InvariantCulture); while (mm.Count <= ix) mm.Add("unknown"); mm[ix] = p[2]; }
                else if (p[0] == "F" && p.Length >= 13)
                {
                    FileState s = new FileState(); long key = long.Parse(p[1], CultureInfo.InvariantCulture);
                    s.Offset = long.Parse(p[2], CultureInfo.InvariantCulture); s.Mtime = long.Parse(p[3], CultureInfo.InvariantCulture);
                    s.HeadLen = long.Parse(p[4], CultureInfo.InvariantCulture); s.HeadHash = long.Parse(p[5], CultureInfo.InvariantCulture);
                    s.SessionId = p[6]; s.HasRec = p[7] == "1"; s.LastTotal = long.Parse(p[8], CultureInfo.InvariantCulture); s.LastModel = p[9]; s.Tier = p[10];
                    if (p[11] != "") s.Turns.AddRange(p[11].Split(';'));
                    s.Derived = int.Parse(p[12], CultureInfo.InvariantCulture);
                    fl[key] = s;
                }
                else if (p[0] == "R" && p.Length >= 15)
                {
                    UsageRec r = new UsageRec();
                    r.Prov = byte.Parse(p[1], CultureInfo.InvariantCulture); r.Kind = byte.Parse(p[2], CultureInfo.InvariantCulture); r.FileKey = int.Parse(p[3], CultureInfo.InvariantCulture);
                    r.Id = long.Parse(p[4], CultureInfo.InvariantCulture); r.Ts = long.Parse(p[5], CultureInfo.InvariantCulture); r.Model = int.Parse(p[6], CultureInfo.InvariantCulture);
                    r.Flags = byte.Parse(p[7], CultureInfo.InvariantCulture); r.In = long.Parse(p[8], CultureInfo.InvariantCulture); r.Cr = long.Parse(p[9], CultureInfo.InvariantCulture);
                    r.Cw5 = long.Parse(p[10], CultureInfo.InvariantCulture); r.Cw1 = long.Parse(p[11], CultureInfo.InvariantCulture); r.CwU = long.Parse(p[12], CultureInfo.InvariantCulture);
                    r.Out = long.Parse(p[13], CultureInfo.InvariantCulture); r.Tool = int.Parse(p[14], CultureInfo.InvariantCulture);
                    if (r.Ts >= ws) rl.Add(r);
                }
            }
            models = mm; modelIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < models.Count; i++) modelIdx[models[i]] = i;
            files = fl; Rebuild(rl); dirty = false; cacheLoaded = true;
            savedAt = long.Parse(h[2], CultureInfo.InvariantCulture); scanFrom = long.Parse(h[4], CultureInfo.InvariantCulture);
            if (scanFrom < ws) scanFrom = ws;
            state = "cached"; ReloadPrices(); Publish();
        }

        // ------------------------------------------------------------ small helpers

        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        static long ToMs(DateTime utc) { return (long)(utc - Epoch).TotalMilliseconds; }
        static DateTime FromMs(long ms) { return Epoch.AddMilliseconds(ms); }

        public static long ParseTime(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            // fast path for 2026-11-03T22:43:43.877Z
            if (s.Length >= 20 && s[4] == '-' && s[7] == '-' && s[10] == 'T' && s[13] == ':' && s[16] == ':' && s[s.Length - 1] == 'Z')
            {
                try
                {
                    int y = D(s, 0, 4), mo = D(s, 5, 2), d = D(s, 8, 2), h = D(s, 11, 2), mi = D(s, 14, 2), se = D(s, 17, 2), ms = 0;
                    if (s.Length > 20 && s[19] == '.') { string fr = s.Substring(20, s.Length - 21); if (fr.Length > 3) fr = fr.Substring(0, 3); while (fr.Length < 3) fr += "0"; ms = D(fr, 0, 3); }
                    return ToMs(new DateTime(y, mo, d, h, mi, se, DateTimeKind.Utc)) + ms;
                }
                catch { }
            }
            DateTime t;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t)) return ToMs(t);
            return 0;
        }

        static int D(string s, int at, int n) { int v = 0; for (int i = 0; i < n; i++) { char c = s[at + i]; if (c < '0' || c > '9') throw new FormatException(); v = v * 10 + (c - '0'); } return v; }

        static long Fnv(string s) { return Fnv(Encoding.UTF8.GetBytes(s), 0, -1); }
        static long Fnv(byte[] b, int start, int len)
        {
            ulong h = 14695981039346656037UL; int e = len < 0 ? b.Length : start + len;
            for (int i = start; i < e; i++) { h ^= b[i]; h *= 1099511628211UL; }
            return unchecked((long)h);
        }
    }
}
