// Claude's 5-hour and weekly limits straight from the account usage endpoint, for people whose Claude Code runs without a status line (SDK / headless).
// Opt-in (menu: More settings). The Claude Code login token is read for each request only, sent to api.anthropic.com over https and never stored or logged;
// the log only gets the HTTP status or the error type. Read-only GET, once every 5 minutes at most, on a background thread.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace ClaudeCompanionPet
{
    static class ClaudeApi
    {
        public static volatile bool Enabled;
        static readonly object gate = new object();
        static Dictionary<string, object> latest; static long latestAt, lastTry; static bool running; static string lastNote = "";
        const long EVERY = 300000;
        // the same shape the status line reports: { five_hour: { used_percentage, resets_at (epoch s) }, seven_day: {...} } plus the time it was read
        public static Dictionary<string, object> Get(out long at) { lock (gate) { at = latestAt; return latest; } }
        public static void Tick(long now)
        {
            if (!Enabled) return;
            lock (gate) { if (running || now - lastTry < (latest == null ? 60000 : EVERY)) return; running = true; lastTry = now; }
            Thread t = new Thread(Fetch); t.IsBackground = true; t.Name = "pet-claude-limits"; t.Start();
        }
        static void Note(string n) { if (n != lastNote) { lastNote = n; Diag.Write("claude limits: " + n); } }
        static void Fetch()
        {
            try
            {
                string token = null;
                try { Dictionary<string, object> cr = J.ReadFile(Path.Combine(Env.ClaudeDir(), ".credentials.json")); Dictionary<string, object> o = J.Obj(cr != null && cr.ContainsKey("claudeAiOauth") ? cr["claudeAiOauth"] : null); token = J.Str(o, "accessToken"); } catch { }
                if (string.IsNullOrEmpty(token)) { Note("no Claude Code login found"); return; }
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
                HttpWebRequest rq = (HttpWebRequest)WebRequest.Create("https://api.anthropic.com/api/oauth/usage");
                rq.Method = "GET"; rq.Timeout = 10000; rq.ReadWriteTimeout = 10000; rq.Accept = "application/json"; rq.UserAgent = "claude-companion-pet";
                rq.Headers["Authorization"] = "Bearer " + token; rq.Headers["anthropic-beta"] = "oauth-2025-04-20";
                string body;
                using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse()) using (StreamReader sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();
                Dictionary<string, object> d = J.Obj(J.Parse(body)); if (d == null) { Note("answer is not an object"); return; }
                Dictionary<string, object> rl = new Dictionary<string, object>();
                foreach (string k in new string[] { "five_hour", "seven_day" })
                {
                    Dictionary<string, object> w = J.Obj(d.ContainsKey(k) ? d[k] : null); if (w == null) continue;
                    double pct = J.Num(w, "utilization", -1); if (pct < 0) continue;
                    DateTimeOffset rs2; long resetS = 0;
                    if (DateTimeOffset.TryParse(J.Str(w, "resets_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out rs2)) resetS = rs2.ToUnixTimeSeconds();
                    rl[k] = new Dictionary<string, object> { { "used_percentage", pct }, { "resets_at", resetS } };
                }
                if (rl.Count == 0) { Note("answer has no limits"); return; }
                lock (gate) { latest = rl; latestAt = Env.Now(); }
                Note("ok");
            }
            catch (WebException e) { HttpWebResponse r = e.Response as HttpWebResponse; Note(r != null ? "HTTP " + (int)r.StatusCode : "network " + e.Status); }
            catch (Exception e) { Note("failed " + e.GetType().Name); }
            finally { lock (gate) running = false; }
        }
    }
}
