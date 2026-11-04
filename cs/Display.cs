// Display adapter: the one place where the product reads Backend.ReadSnapshot() and turns its public meaning into what the pet and the panel show.
// It decides nothing about sessions, liveness, windows, context, usage or quota: those come from the snapshot (backend/SESSIONS.md, USAGE.md, CODEX.md).
// What it does decide is wording, and it never turns an unknown into a number: null stays "unknown", a stale figure says stale, a partly priced sum says
// partly priced, a window that is not there is not drawn as an empty bar.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClaudeCompanionPet
{
    // Product lifecycle of the three data modules. The snapshot is the only thing the UI reads.
    static class Services
    {
        static bool started;
        public static void Start()
        {
            if (started) return;
            started = true;
            try { UsageBackend.Start(); } catch (Exception e) { Diag.Error("usage start", e); }
            try { QuotaBackend.Start(); } catch (Exception e) { Diag.Error("quota start", e); }
            try { Backend.ConfigureSources(CodexSource.ReadSessions, UsageBackend.ReadSnapshot, QuotaBackend.ReadSnapshot); Backend.Start(); }
            catch (Exception e) { Diag.Error("session backend start", e); }
            Diag.Write("backend started (sessions + usage + quota)");
        }
        // snapshot consumer first, then the modules it reads from
        public static void Stop()
        {
            if (!started) return;
            started = false;
            try { Backend.Stop(); } catch (Exception e) { Diag.Error("session backend stop", e); }
            try { if (!UsageBackend.Stop(3000)) Diag.Write("usage worker did not stop within 3 s"); } catch (Exception e) { Diag.Error("usage stop", e); }
            try { QuotaBackend.Stop(); } catch (Exception e) { Diag.Error("quota stop", e); }
            Diag.Write("backend stopped");
        }
    }

    class QRow { public string Provider = "", Label = "", Tag = "", Reset = ""; public double Pct = -1; public bool Muted, Warn; }
    class Bar { public string Label = ""; public double Claude, Codex, Other, In, CacheRead, CacheWrite, Out; }
    class ModelRow { public string Provider = "", Label = ""; public long Tokens; public string Cost = ""; public bool Priced; }
    class UsageView
    {
        public string Period = "7d", State = "", Note = "";
        public bool HasNumbers, Complete = true, Busy;   // Busy: still counting / not running / failed: the status line is drawn as a warning
        public long Total, Requests, Input, CacheRead, CacheWrite, Output;
        public string Cost = "", CostNote = "", ByProvider = "", Footer = "";
        public List<Bar> Bars = new List<Bar>(); public string BarFrom = "", BarTo = "";
        public List<ModelRow> Models = new List<ModelRow>(); public int MoreModels; public long MoreTokens; public string MoreCost = "";
    }
    class SplitRow { public string Key = "", Label = ""; public long Tokens; public bool Estimated; }
    class CtxView
    {
        public string WindowSource = "";
        public List<SplitRow> Split;   // what the used part is made of (fixed head / your messages / the assistant / tool results), when known
        public Item Item; public bool HasUsed, HasWindow; public long Used, Window; public double Pct = -1;
        public string Headline = "", Quality = "", Source = "", Remain = "";
        public long Input = -1, CacheRead = -1, CacheWrite = -1, Output = -1; public bool Codex;
    }

    class Board
    {
        public View View = new View();
        public string Status = "not_ready"; public long GeneratedAt;
        public Dictionary<string, object> Usage, Accounts, Sources;
        public List<QRow> Quota = new List<QRow>();
        // what is left (percent) of the tightest fresh window of one provider; -1 = no fresh number
        public double Left(string provider)
        {
            double m = -1;
            foreach (QRow q in Quota) { if (q.Provider != provider || q.Muted || q.Pct < 0) continue; double l = Math.Min(100, Math.Max(0, 100 - q.Pct)); if (m < 0 || l < m) m = l; }
            return m;
        }
        public bool Has(string provider) { foreach (QRow q in Quota) if (q.Provider == provider) return true; return false; }
        public string QuotaOutName = ""; public long QuotaOutReset;
        public Dictionary<string, Dictionary<string, object>> Sess = new Dictionary<string, Dictionary<string, object>>(); // provider:id -> snapshot row
        public string Sig = "";
    }

    static class Display
    {
        // ---------- small readers over the snapshot (null stays null) ----------
        static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }
        static List<object> L(object o) { List<object> r = o as List<object>; if (r != null) return r; IEnumerable e = o as IEnumerable; if (e == null || o is string) return new List<object>(); r = new List<object>(); foreach (object x in e) r.Add(x); return r; }
        static Dictionary<string, object> Sub(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? D(v) : null; }
        static bool Has(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null; }
        static long LongOr(Dictionary<string, object> d, string k, long dflt) { return Has(d, k) ? J.Long(d, k) : dflt; }
        static bool Bool(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v; }

        // ---------- words ----------
        public static string Rel(long ms)
        {
            long m = Math.Max(0, ms / 60000);
            if (m < 1) return Lang.T("不到 1 分钟", "under 1 min");
            if (m < 60) return Lang.T(m + " 分钟", m + " min");
            long h = m / 60; if (h < 48) return Lang.T(h + " 小时" + (m % 60 > 0 ? " " + (m % 60) + " 分" : ""), h + " h" + (m % 60 > 0 ? " " + (m % 60) + " min" : ""));
            long d = h / 24; return Lang.T(d + " 天" + (h % 24 > 0 ? " " + (h % 24) + " 小时" : ""), d + " d" + (h % 24 > 0 ? " " + (h % 24) + " h" : ""));
        }
        public static string Tok(long n)
        {
            if (n >= 1000000000L) return (n / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + "B";
            if (n >= 10000000L) return (n / 1e6).ToString("0", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000000L) return (n / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M";
            if (n >= 10000L) return (n / 1000) + "k";
            if (n >= 1000L) return (n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
            return n.ToString(CultureInfo.InvariantCulture);
        }
        public static string Usd(double v) { return v >= 100 ? "$" + Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v >= 1 ? "$" + v.ToString("0.00", CultureInfo.InvariantCulture) : "$" + v.ToString("0.000", CultureInfo.InvariantCulture); }
        public static string ProviderLabel(string p) { return ProviderName(p); }
        static string ProviderName(string p) { return p == "codex" ? "Codex" : p == "claude" ? "Claude" : p; }
        static string ShortHour(long ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture); }

        public static string WindowNote(string reason, string provider)
        {
            switch (reason)
            {
                case "no_usable_window": return Lang.T("后台会话（没有终端窗口）", "Background session (no terminal window)");
                case "no_verified_process_mapping": return provider == "codex" ? Lang.T("Codex 会话：还不能对应到某个窗口", "Codex session: cannot be matched to a window yet") : Lang.T("没有可核实的进程，不能跳转", "No verified process, cannot jump");
                case "ambiguous_window": return Lang.T("有多个可能的窗口，不乱跳", "Several windows could match, not guessing");
                case "window_probe_unavailable": return Lang.T("现在探测不了窗口", "Cannot probe windows right now");
                case "process_not_verified_alive": return Lang.T("会话是否还在运行未确认，不能跳转", "Not confirmed running, cannot jump");
            }
            return Lang.T("不能跳转（" + reason + "）", "Cannot jump (" + reason + ")");
        }
        // TryJump failed before any window was raised (as opposed to Windows refusing the foreground)
        public static bool IsPreRaiseFailure(string reason)
        {
            switch (reason) { case "session_not_found": case "no_usable_window": case "ambiguous_window": case "window_probe_unavailable": case "process_exited": case "process_gone": case "pid_reused": case "process_access_unavailable": case "process_start_unavailable": case "process_wait_unavailable": case "legacy_pid_unverified": case "no_process_identity": return true; }
            return false;
        }
        static string StateText(Dictionary<string, object> s, string state, string detail)
        {
            string q = J.Str(s, "state_quality"), prov = J.Str(s, "provider");
            if (q == "stale_event") return Lang.T("久没动静 · 状态未知", "no events for a while · status unknown");
            if (q == "activity_unavailable") return Lang.T("已打开 · Codex 不报告活动", "open · Codex reports no activity");
            if (q == "liveness_unknown") return Lang.T("状态未知 · 存活未确认", "status unknown · not confirmed running");
            switch (state)
            {
                case "waiting": return Model.Label(detail == "answer" ? "answer" : "approval");
                case "error": return (Model.Label("error") + " " + detail).Trim();
                case "unknown": return Model.Label("unknown");
            }
            return Model.Label(state);
        }

        // ---------- the snapshot -> pet view ----------
        static int Rank(string st) { switch (st) { case "waiting": return 4; case "error": return 3; case "busy": return 2; case "done": return 1; } return 0; }

        public static Board Build(Dictionary<string, object> snap, long now)
        {
            Board b = new Board(); View v = b.View;
            b.Status = J.Str(snap, "status"); b.GeneratedAt = J.Long(snap, "generated_at");
            b.Usage = Sub(snap, "usage"); b.Accounts = Sub(snap, "accounts"); b.Sources = Sub(snap, "sources");
            List<Dictionary<string, object>> vis = new List<Dictionary<string, object>>();
            foreach (object o in L(snap.ContainsKey("sessions") ? snap["sessions"] : null)) { Dictionary<string, object> s = D(o); if (s != null && Bool(s, "visible")) vis.Add(s); }
            vis = vis.OrderByDescending(s => Rank(J.Str(s, "state"))).ThenByDescending(s => J.Long(s, "last_event_at")).ToList();
            List<Item> live = new List<Item>();
            foreach (Dictionary<string, object> s in vis.Take(8))
            {
                string state = J.Str(s, "state"), detail = J.Str(s, "state_detail"), prov = J.Str(s, "provider");
                Dictionary<string, object> win = Sub(s, "window"), life = Sub(s, "liveness"), ctx = Sub(s, "context");
                long since = LongOr(s, "state_since", LongOr(s, "last_observed_state_since", LongOr(s, "last_event_at", 0)));
                string name = J.Str(s, "display_name"); if (prov == "codex" && name.StartsWith("Codex · ", StringComparison.Ordinal)) name = name.Substring(8);
                bool need = state == "waiting" || state == "error";
                Item it = new Item
                {
                    Id = J.Str(s, "session_id"), Provider = prov, Name = name, Agent = ProviderName(prov), State = state, Detail = detail, Cwd = J.Str(s, "cwd"),
                    Since = since, BusyMs = J.Long(s, "busy_ms"), Need = need, Label = StateText(s, state, detail),
                    Life = J.Str(life, "status"), LifeReason = J.Str(life, "reason"), StateQuality = J.Str(s, "state_quality"),
                    CanJump = Bool(win, "can_jump"), NoWin = !Bool(win, "can_jump"), WindowReason = J.Str(win, "reason"), Context = ctx
                };
                if (J.Str(ctx, "quality") == "measured" && Has(ctx, "used_percentage")) it.Ctx = J.Num(ctx, "used_percentage", -1);
                live.Add(it); b.Sess[prov + ":" + it.Id] = s;
            }
            v.Items = live;
            // quota: the pet only changes mood for a window that was actually read and is full
            BuildQuota(b, now);
            Item primary = live.Count > 0 ? live[0] : null;
            List<Item> needs = live.Where(i => i.Need).ToList();
            string st = primary == null ? "idle" : primary.State;
            bool longTask = primary != null && Model.LongTaskMs > 0 && primary.BusyMs >= Model.LongTaskMs;
            if (st == "done" && primary.Since > 0 && now - primary.Since > (longTask ? 120000 : 6000)) st = "idle";
            v.Mode = live.Count > 0 ? "active" : "idle";
            if (st == "waiting") v.Mood = primary.Detail == "answer" ? "answer" : "approval";
            else if (st == "error") v.Mood = "error";
            else if (b.QuotaOutName != "") v.Mood = "quotaOut";
            else if (st == "done" && !longTask) v.Mood = "doneQuiet";
            else v.Mood = st == "busy" || st == "done" ? st : "idle";
            v.Tired = primary != null && primary.Ctx >= Ctx.TIRED_AT && (v.Mood == "idle" || v.Mood == "done" || v.Mood == "doneQuiet");
            Func<Item, string> reason = i => i.Label;
            Func<Item, string> age = i => i.Since > 0 ? " · " + Model.Duration(now - i.Since) : "";
            if (needs.Count > 0)
            {
                Item f = needs[0]; int more = needs.Count - 1;
                v.HasBubble = true; v.BKind = f.State == "error" ? "error" : f.Detail == "answer" ? "answer" : "approval";
                if (more == 0) { v.BProject = f.Name; v.BReason = reason(f) + age(f); }
                else { v.BProject = Lang.T(needs.Count + " 个会话在等你", needs.Count + " sessions need you"); v.BReason = f.Name + " · " + reason(f) + age(f); v.BMore = Lang.T("点小人看全部", "click me for the list"); }
            }
            else if (v.Mood == "quotaOut")
            {
                v.HasBubble = true; v.BKind = "quota"; v.BProject = Lang.T(b.QuotaOutName + " 额度见底", b.QuotaOutName + " quota is full");
                v.BReason = b.QuotaOutReset > now ? Lang.T("约 " + Rel(b.QuotaOutReset - now) + " 后重置", "resets in about " + Rel(b.QuotaOutReset - now)) : Lang.T("重置时刻未知或已过，未确认恢复", "reset time unknown or passed, recovery not confirmed");
            }
            v.NeedCount = needs.Count; v.LiveCount = live.Count;
            if (needs.Count > 0 && live.Count > 1) { v.Badge = needs.Count; v.BadgeKind = "need"; }
            else if (needs.Count > 1) { v.Badge = needs.Count; v.BadgeKind = "need"; }
            else if (live.Count > 1) { v.Badge = live.Count; v.BadgeKind = "count"; }
            foreach (Item i in live.Take(6)) v.Rows.Add(new string[] { i.Name, i.State, i.Label });
            v.Tooltip = primary == null ? Lang.T("没有在运行的会话", "no sessions") : primary.Name + ": " + (v.HasBubble ? v.BReason : primary.Label) + (live.Count > 1 ? " (+" + (live.Count - 1) + ")" : "");
            v.Sig = v.Mode + "|" + v.Mood + "|" + (v.HasBubble ? v.BKind + v.BProject + v.BMore : "") + (v.Tired ? "|tired" : "") + (v.Badge > 0 ? "|badge" + v.Badge : "");
            b.Sig = b.GeneratedAt + "|" + b.Status;
            return b;
        }

        // ---------- quota (accounts) ----------
        static string QStatus(string s)
        {
            switch (s)
            {
                case "ok": case "": return "";
                case "partial": return Lang.T("数据不全", "incomplete");
                case "stale": return Lang.T("数据较旧", "old data");
                case "error": return Lang.T("没读到 · 请确认已登录", "not read · is it signed in?");
                case "stopped": return Lang.T("已停止", "stopped");
                case "reset_unconfirmed": return Lang.T("重置后未更新", "not updated since reset");
                case "unavailable": return Lang.T("没有额度数据源", "no quota source");
                case "not_started": return Lang.T("未启动", "not started");
                case "querying": return Lang.T("读取中…", "reading…");
            }
            return s;
        }
        static string WinLabel(string label)
        {
            if (label == "5h") return Lang.T("5 小时", "5 h");
            if (label == "weekly") return Lang.T("每周", "weekly");
            return label;
        }
        static void BuildQuota(Board b, long now)
        {
            Dictionary<string, object> top = b.Accounts;
            if (top == null) { b.Quota.Add(new QRow { Provider = "", Label = Lang.T("额度", "Quota"), Tag = Lang.T("额度模块未接入", "quota module not connected"), Muted = true }); return; }
            string topStatus = J.Str(top, "status");
            List<object> accts = L(top.ContainsKey("accounts") ? top["accounts"] : null);
            if (accts.Count == 0) { b.Quota.Add(new QRow { Provider = "", Label = Lang.T("额度", "Quota"), Tag = QStatus(topStatus == "ok" ? "unavailable" : topStatus), Muted = true }); return; }
            foreach (object ao in accts)
            {
                Dictionary<string, object> a = D(ao); if (a == null) continue;
                string prov = J.Str(a, "provider"), aStatus = J.Str(a, "status"); List<object> ws = L(a.ContainsKey("windows") ? a["windows"] : null);
                string topEff = J.Str(a, "source") == "claude_statusline" ? "ok" : topStatus;   // the Codex quota module's overall state says nothing about the numbers Claude Code itself reported
                if (ws.Count == 0) { b.Quota.Add(new QRow { Provider = prov, Label = Lang.T("额度", "Quota"), Tag = QStatus(aStatus == "ok" ? (topStatus == "ok" ? "partial" : topStatus) : aStatus), Muted = true }); continue; }
                foreach (object wo in ws)
                {
                    Dictionary<string, object> w = D(wo); if (w == null) continue;
                    string ws2 = J.Str(w, "status"); string eff = ws2 != "" && ws2 != "ok" ? ws2 : (aStatus != "" && aStatus != "ok" ? aStatus : topEff == "stale" || topEff == "error" || topEff == "stopped" ? topEff : "ok");
                    QRow r = new QRow { Provider = prov, Label = WinLabel(J.Str(w, "label")), Tag = QStatus(eff) };
                    string bn = J.Str(w, "bucket_name"); if (bn != "" && bn != J.Str(w, "label") && L(a.ContainsKey("buckets") ? a["buckets"] : null).Count > 1) r.Label = bn + " " + r.Label;
                    r.Pct = Has(w, "used_percent") ? J.Num(w, "used_percent", -1) : -1;
                    long reset = LongOr(w, "resets_at", 0);
                    if (reset > now) r.Reset = Rel(reset - now); else if (reset > 0 && eff == "ok") r.Reset = Lang.T("重置时刻已过", "reset time passed");
                    r.Muted = eff != "ok"; r.Warn = eff == "ok" && r.Pct >= 90;
                    b.Quota.Add(r);
                    if (eff == "ok" && r.Pct >= 100 && b.QuotaOutName == "") { b.QuotaOutName = ProviderName(prov) + " " + r.Label; b.QuotaOutReset = reset; }
                }
            }
            if (b.Quota.Count > 5) b.Quota.RemoveRange(5, b.Quota.Count - 5);
        }

        // ---------- context of one session ----------
        public static CtxView Context(Board b, Item it)
        {
            CtxView c = new CtxView { Item = it, Codex = it.Provider == "codex" };
            Dictionary<string, object> x = it.Context;
            string q = J.Str(x, "quality");
            c.HasUsed = Has(x, "used_tokens"); c.Used = J.Long(x, "used_tokens");
            c.HasWindow = Has(x, "window_tokens") && J.Long(x, "window_tokens") > 0; c.Window = J.Long(x, "window_tokens");
            if (Has(x, "used_percentage")) c.Pct = J.Num(x, "used_percentage", -1);
            else if (c.HasUsed && c.HasWindow) c.Pct = 100.0 * c.Used / c.Window;
            if (!c.HasWindow) c.Pct = -1; // no denominator, no percentage
            if (x != null && x.ContainsKey("split"))
            {
                List<object> sp = L(x["split"]);
                if (sp.Count > 0)
                {
                    c.Split = new List<SplitRow>();
                    foreach (object o in sp)
                    {
                        Dictionary<string, object> d = D(o); if (d == null) continue; string key = J.Str(d, "key");
                        c.Split.Add(new SplitRow { Key = key, Label = Lang.T(CtxSplit.Label(key, true), CtxSplit.Label(key, false)), Tokens = J.Long(d, "tokens"), Estimated = J.Str(d, "estimated").ToLower() == "true" });
                    }
                }
            }
            c.WindowSource = J.Str(x, "window_source");
            c.Input = LongOr(x, "input_tokens", -1); c.CacheRead = LongOr(x, "cache_read_input_tokens", -1); c.CacheWrite = LongOr(x, "cache_creation_input_tokens", -1); c.Output = LongOr(x, "output_tokens", -1);
            long obs = LongOr(x, "observed_at", 0), now = Env.Now(); string when = obs > 0 ? Lang.T(Rel(now - obs) + "前", Rel(now - obs) + " ago") : Lang.T("采样时刻未知", "sample time unknown");
            if (x == null) c.Quality = Lang.T("这个会话没有上下文数据", "no context data for this session");
            else switch (q)
            {
                case "measured": c.Quality = Lang.T("最近一次请求的实测输入占用 · ", "input size of the latest request · ") + when; break;
                case "last_reported": c.Quality = Lang.T("Codex 最近一次报告 · ", "last reported by Codex · ") + when + Lang.T(" · 不是实时精确值", " · not a live exact value"); break;
                case "stale": c.Quality = Lang.T("已过期 · ", "stale · ") + when; break;
                case "partial": case "partial_tail": c.Quality = Lang.T("数据不完整 · 来自此前完整记录 · ", "incomplete · from an earlier full record · ") + when; break;
                case "awaiting_usage": c.Quality = Lang.T("等待这个会话的下一次计数", "waiting for the next count of this session"); break;
                case "awaiting_usage_after_compaction": c.Quality = Lang.T("刚压缩过 · 等下一次计数", "just compacted · waiting for the next count"); break;
                case "awaiting_usage_after_model_change": c.Quality = Lang.T("刚换了模型 · 等下一次计数", "model just changed · waiting for the next count"); break;
                case "scan_limit": c.Quality = Lang.T("记录过大，没能读到计数", "record too large to read the count"); break;
                case "unreadable": c.Quality = Lang.T("读不到记录", "cannot read the record"); break;
                case "no_usage": case "unavailable": c.Quality = Lang.T("还没有计数", "no count yet"); break;
                default: c.Quality = q == "" ? Lang.T("没有说明", "no note") : q; break;
            }
            string rs = J.Str(x, "reason"); if (rs != "" && c.Codex) c.Quality += " · " + rs;
            c.Headline = c.HasUsed ? Tok(c.Used) : Lang.T("未知", "unknown");
            c.Source = Lang.T("来源 ", "source ") + J.Str(x, "source") + Lang.T(" · 窗口 ", " · window ") + (c.HasWindow ? J.Str(x, "window_source") : Lang.T("未知", "unknown"));
            if (c.HasUsed && c.HasWindow) c.Remain = Lang.T("剩余约 ", "about ") + Tok(Math.Max(0, c.Window - c.Used)) + Lang.T("", " left");
            return c;
        }

        // ---------- usage ----------
        static string CostText(Dictionary<string, object> cost, out string note, bool cap)
        {
            note = ""; if (cost == null) return "—";
            string status = J.Str(cost, "status"); double usd = J.Num(cost, "usd", 0); long unp = J.Long(cost, "unpriced_tokens"), tools = J.Long(cost, "unpriced_tool_requests");
            if (status == "full") return "≈ " + Usd(usd);
            if (status == "empty") return "—";
            if (status == "none") { note = Lang.T("暂无价格", "no price"); return "—"; }
            note = Lang.T("未含 " + (unp > 0 ? Tok(unp) + " 未计价" : "") + (unp > 0 && tools > 0 ? "、" : "") + (tools > 0 ? tools + " 次工具调用" : ""), "excludes " + (unp > 0 ? Tok(unp) + " unpriced" : "") + (unp > 0 && tools > 0 ? ", " : "") + (tools > 0 ? tools + " tool calls" : ""));
            return "≥ " + Usd(usd);
        }
        public static UsageView Usage(Board b, string period)
        {
            UsageView u = new UsageView { Period = period };
            Dictionary<string, object> s = b.Usage;
            if (s == null) { u.State = Lang.T("用量模块未接入", "usage module not connected"); return u; }
            string state = J.Str(s, "state"); Dictionary<string, object> scan = Sub(s, "scan"), wins = Sub(s, "windows");
            switch (state)
            {
                case "not_started": u.State = Lang.T("用量统计还没启动", "usage counting has not started"); return u;
                case "starting": u.Busy = true; u.State = Lang.T("正在启动，读取上次的缓存…", "starting, reading the last cache…"); break;
                case "cached": u.Busy = true; u.State = Lang.T("缓存数字 · 正在更新", "cached figures · updating"); break;
                case "scanning": u.Busy = true; u.State = Lang.T("统计中", "counting"); break;
                case "error": u.Busy = true; u.State = Lang.T("统计出错：", "counting failed: ") + J.Str(scan, "error"); break;
                case "stopping": case "stopped": u.Busy = true; u.State = Lang.T("统计已停止 · 数字停在停止前", "counting stopped · figures are from before"); break;
                case "ready": long asOf = J.Long(s, "data_as_of"); u.State = asOf > 0 ? Lang.T("统计完整 · " + Rel(Env.Now() - asOf) + "前更新", "complete · updated " + Rel(Env.Now() - asOf) + " ago") : Lang.T("统计完整", "complete"); break;
                default: u.State = state; break;
            }
            Dictionary<string, object> w = Sub(wins, period);
            if (w == null) { u.State += " · " + Lang.T("还没有这个周期的数字", "no figures for this period yet"); return u; }
            u.HasNumbers = true; u.Complete = Bool(w, "complete");
            Dictionary<string, object> tk = Sub(w, "tokens");
            u.Total = J.Long(tk, "total"); u.Input = J.Long(tk, "input"); u.CacheRead = J.Long(tk, "cache_read"); u.CacheWrite = J.Long(tk, "cache_write"); u.Output = J.Long(tk, "output"); u.Requests = J.Long(w, "requests");
            string cn; u.Cost = CostText(Sub(w, "cost"), out cn, false); u.CostNote = cn;
            Dictionary<string, object> bp = Sub(w, "by_provider"); List<string> parts = new List<string>();
            foreach (string p in new string[] { "claude", "codex" })
            {
                Dictionary<string, object> a = Sub(bp, p); if (a == null || J.Long(a, "requests") == 0) continue;
                string pn; string ct = CostText(Sub(a, "cost"), out pn, true); parts.Add(ProviderName(p) + " " + (ct == "—" ? Tok(J.Long(Sub(a, "tokens"), "total")) + Lang.T(" 无价格", " unpriced") : ct.Replace("≈ ", "")));
            }
            u.ByProvider = string.Join(" · ", parts.ToArray());
            if (!u.Complete) u.Footer = Lang.T("统计中：这个周期的起点还没扫到，数字是下限", "still counting: the start of this period is not scanned yet, figures are lower bounds");
            // bars
            List<object> src = L(period == "today" ? (s.ContainsKey("hourly_today") ? s["hourly_today"] : null) : (s.ContainsKey("daily") ? s["daily"] : null));
            int take = period == "7d" ? 7 : period == "today" ? src.Count : src.Count;
            foreach (object o in src.Skip(Math.Max(0, src.Count - take)))
            {
                Dictionary<string, object> r = D(o); Dictionary<string, object> bp2 = Sub(r, "by_provider");
                Dictionary<string, object> tk2 = Sub(r, "tokens");
                Bar bar = bp2 == null ? new Bar { Label = J.Str(r, "date"), Other = J.Long(tk2, "total") }
                    : new Bar { Label = J.Str(r, "date"), Claude = J.Long(Sub(Sub(bp2, "claude"), "tokens"), "total"), Codex = J.Long(Sub(Sub(bp2, "codex"), "tokens"), "total") };
                bar.In = J.Long(tk2, "input"); bar.CacheRead = J.Long(tk2, "cache_read"); bar.CacheWrite = J.Long(tk2, "cache_write"); bar.Out = J.Long(tk2, "output");
                u.Bars.Add(bar);
            }
            if (u.Bars.Count > 0) { u.BarFrom = period == "today" ? "0:00" : ShortDate(u.Bars[0].Label); u.BarTo = period == "today" ? Lang.T("现在", "now") : ShortDate(u.Bars[u.Bars.Count - 1].Label); }
            // models: top three by tokens, the rest folded
            List<Dictionary<string, object>> ms = L(w.ContainsKey("models") ? w["models"] : null).Select(o => D(o)).Where(m => m != null).ToList();
            int shown = 0;
            foreach (Dictionary<string, object> m in ms)
            {
                if (shown < (ms.Count > 3 ? 3 : ms.Count)) { string mn; string mc = CostText(Sub(m, "cost"), out mn, true); u.Models.Add(new ModelRow { Provider = J.Str(m, "provider"), Label = J.Str(m, "label") != "" ? J.Str(m, "label") : J.Str(m, "model"), Tokens = J.Long(Sub(m, "tokens"), "total"), Cost = mn != "" && mc == "—" ? mn : mc, Priced = Bool(m, "priced") }); shown++; }
                else { u.MoreModels++; u.MoreTokens += J.Long(Sub(m, "tokens"), "total"); }
            }
            return u;
        }
        static string ShortDate(string d) { return d != null && d.Length >= 10 ? d.Substring(5, 5) : d; }
    }
}
