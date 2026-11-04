// A running record of the quota situation, next to the hourly usage statistics: whenever a quota window changes (or every 10 minutes while it stays the same) one line is appended to
// <data folder>\stats\quota-30d.csv. Lines older than 30 days are dropped (the file is rewritten about once an hour). Numbers, window names and times only.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCompanionPet
{
    static class QuotaLog
    {
        const string HEADER = "recorded_local,recorded_ms,provider,bucket,window,duration_min,used_percent,remaining_percent,resets_local,status,source";
        const long KEEP_MS = 30L * 24 * 3600 * 1000, SAME_EVERY_MS = 10L * 60 * 1000, PRUNE_EVERY_MS = 3600L * 1000;
        static readonly Dictionary<string, string> last = new Dictionary<string, string>();      // key -> the values of the last line written (without the time)
        static readonly Dictionary<string, long> lastAt = new Dictionary<string, long>();
        static long prunedAt; static string failed = "";
        static string File_() { return Path.Combine(Path.Combine(Env.DataDir(), "stats"), "quota-30d.csv"); }
        static string Cell(object o) { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
        static string Num(object o, string fmt)
        {
            if (o == null) return ""; try { return Convert.ToDouble(o, CultureInfo.InvariantCulture).ToString(fmt, CultureInfo.InvariantCulture); } catch { return ""; }
        }
        static string Local(long ms) { return ms <= 0 ? "" : TimeZoneInfo.ConvertTimeFromUtc(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms), TimeZoneInfo.Local).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        public static void Record(Dictionary<string, object> accounts, long now)
        {
            if (accounts == null) return;
            try
            {
                IEnumerable list = accounts.ContainsKey("accounts") ? accounts["accounts"] as IEnumerable : null; if (list == null) return;
                StringBuilder add = new StringBuilder();
                foreach (object ao in list)
                {
                    Dictionary<string, object> a = ao as Dictionary<string, object>; if (a == null) continue;
                    IEnumerable ws = a.ContainsKey("windows") ? a["windows"] as IEnumerable : null; if (ws == null || ws is string) continue;
                    string prov = Cell(a.ContainsKey("provider") ? a["provider"] : null), src = Cell(a.ContainsKey("source") ? a["source"] : null);
                    foreach (object wo in ws)
                    {
                        Dictionary<string, object> w = wo as Dictionary<string, object>; if (w == null) continue;
                        object used = w.ContainsKey("used_percent") ? w["used_percent"] : null; if (used == null) continue;
                        string bucket = Cell(w.ContainsKey("bucket_id") ? w["bucket_id"] : null), label = Cell(w.ContainsKey("label") ? w["label"] : null);
                        string key = prov + "|" + bucket + "|" + label;
                        double reset = 0; try { object r = w.ContainsKey("resets_at") ? w["resets_at"] : null; if (r != null) reset = Convert.ToDouble(r, CultureInfo.InvariantCulture); } catch { }
                        string usedS = Num(used, "0.##"), remS = Num(100 - Convert.ToDouble(used, CultureInfo.InvariantCulture), "0.##");
                        string vals = string.Join(",", new string[] { prov, "\"" + bucket.Replace("\"", "") + "\"", "\"" + label.Replace("\"", "") + "\"", Num(w.ContainsKey("duration_minutes") ? w["duration_minutes"] : null, "0"), usedS, remS, Local((long)reset), Cell(w.ContainsKey("status") ? w["status"] : null), src });
                        string prev; long prevAt;
                        if (last.TryGetValue(key, out prev) && prev == vals && lastAt.TryGetValue(key, out prevAt) && now - prevAt < SAME_EVERY_MS) continue;
                        last[key] = vals; lastAt[key] = now;
                        add.Append(Local(now)).Append(',').Append(now).Append(',').Append(vals).Append("\r\n");
                    }
                }
                if (add.Length == 0 && now - prunedAt < PRUNE_EVERY_MS) return;
                string file = File_(); Directory.CreateDirectory(Path.GetDirectoryName(file));
                if (!File.Exists(file)) File.WriteAllText(file, HEADER + "\r\n", new UTF8Encoding(false));
                if (add.Length > 0) File.AppendAllText(file, add.ToString(), new UTF8Encoding(false));
                if (now - prunedAt >= PRUNE_EVERY_MS)
                {   // drop what is older than 30 days (also right after a start: the first call prunes)
                    prunedAt = now;
                    List<string> keep = new List<string> { HEADER };
                    foreach (string line in File.ReadLines(file).Skip(1))
                    {
                        int c1 = line.IndexOf(','), c2 = c1 < 0 ? -1 : line.IndexOf(',', c1 + 1); long ms;
                        if (c2 > c1 && long.TryParse(line.Substring(c1 + 1, c2 - c1 - 1), out ms) && now - ms <= KEEP_MS) keep.Add(line);
                    }
                    string tmp = file + ".tmp"; File.WriteAllText(tmp, string.Join("\r\n", keep.ToArray()) + "\r\n", new UTF8Encoding(false));
                    File.Delete(file); File.Move(tmp, file);
                }
                failed = "";
            }
            catch (Exception e) { if (failed != e.Message) { failed = e.Message; Diag.Write("quota log: " + e.GetType().Name + " " + e.Message); } }
        }
    }
}
