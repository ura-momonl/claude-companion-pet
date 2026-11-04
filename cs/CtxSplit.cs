// What the used part of a Claude context window is made of: a rough breakdown worked out from the conversation file, so the numbers are estimates and the pet
// agree. Sizes are estimated from characters; the parts are scaled so that they add up to the tokens the provider reported for the latest request. The fixed
// part (system prompt, tool and skill definitions, instruction files) is not in the transcript as text: the first and the latest request of the session
// pin it down together with the estimate's scale (reported = fixed + scale x estimated); when the two points give no sensible answer the fixed part is the
// first request minus its estimate. The transcript is read once, then only what was appended since, on a background thread. A compaction starts again.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace ClaudeCompanionPet
{
    static class CtxSplit
    {
        static readonly string[][] PARTS = {
            new string[] { "summary", "压缩摘要", "Compaction summary" }, new string[] { "user", "用户消息", "Your messages" }, new string[] { "assistant", "助手文字", "Assistant text" },
            new string[] { "call", "工具调用", "Tool calls" }, new string[] { "read", "读文件", "File reads" }, new string[] { "cmd", "命令输出", "Command output" },
            new string[] { "search", "搜索结果", "Search results" }, new string[] { "web", "网页内容", "Web content" }, new string[] { "result", "其他工具结果", "Other tool results" } };
        static string ToolPart(string name)
        {
            switch (name) { case "Read": case "NotebookRead": return "read"; case "Bash": case "PowerShell": case "BashOutput": return "cmd"; case "Grep": case "Glob": return "search"; case "WebFetch": case "WebSearch": return "web"; }
            return "result";
        }
        class Point { public double T, E; public Dictionary<string, double> List; }
        class State
        {
            public long Pos; public bool Seen, Partial; public long RequestedAt;
            public Dictionary<string, double> Acc = new Dictionary<string, double>(); public Dictionary<string, string> Names = new Dictionary<string, string>();
            public Point First, Last;
            public double Sum() { double s = 0; foreach (double v in Acc.Values) s += v; return s; }
            public void Add(string key, double tokens) { if (tokens <= 0) return; double c; Acc.TryGetValue(key, out c); Acc[key] = c + tokens; }
            public void Reset() { Acc.Clear(); Names.Clear(); First = Last = null; }
        }
        static readonly Dictionary<string, State> states = new Dictionary<string, State>();
        static readonly object gate = new object();
        static Thread worker;
        const long FirstScanMax = 64L * 1024 * 1024, PassMax = 24L * 1024 * 1024;

        public static void Request(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (gate)
            {
                State s; if (!states.TryGetValue(path, out s)) { s = new State(); states[path] = s; }
                s.RequestedAt = Env.Now();
                if (worker == null) { worker = new Thread(Run) { IsBackground = true, Name = "pet-context-split" }; worker.Start(); }
            }
        }

        // the parts of the context as of the latest request (they add up to `total` when the transcript is a little behind), or null while unknown
        public static List<object> Get(string path, long total)
        {
            if (string.IsNullOrEmpty(path) || total <= 0) return null;
            Point first, last;
            lock (gate)
            {
                State s; if (!states.TryGetValue(path, out s) || s.First == null || s.Last == null) return null;
                first = s.First; last = s.Last;
            }
            if (!(last.T > 0) || !(first.T > 0) || !(last.E > 0)) return null;
            double scale = (last.T - first.T) / (last.E - first.E), fixedT = first.T - scale * first.E;
            if (!(last.E - first.E > 2000) || !(scale >= 0.2 && scale <= 1.5) || !(fixedT >= 0))
            {   // too little growth or an odd fit: trust the first request for the fixed part
                fixedT = Math.Max(0, Math.Min(last.T, first.T - first.E)); scale = (last.T - fixedT) / last.E;
            }
            List<KeyValuePair<string, double>> parts = new List<KeyValuePair<string, double>>();
            double sum = 0;
            foreach (string[] p in PARTS) { double e; if (last.List.TryGetValue(p[0], out e) && e > 0) { double tk = Math.Round(e * scale); if (tk > 0) { parts.Add(new KeyValuePair<string, double>(p[0], tk)); sum += tk; } } }
            fixedT = Math.Round(fixedT);
            double diff = total - (fixedT + sum);    // rounding and a transcript a little behind the live count both go to the fixed part, so the parts always add up
            fixedT = Math.Max(0, fixedT + diff);
            List<object> r = new List<object>();
            r.Add(Row("fixed", fixedT, false));
            foreach (KeyValuePair<string, double> kv in parts) r.Add(Row(kv.Key, kv.Value, true));
            return r;
        }
        static Dictionary<string, object> Row(string key, double tokens, bool est)
        {
            return new Dictionary<string, object> { { "key", key }, { "tokens", (long)Math.Round(Math.Max(0, tokens)) }, { "estimated", est } };
        }
        public static string Label(string key, bool zh)
        {
            if (key == "fixed") return zh ? "固定开销（系统提示、工具）" : "Fixed overhead (system prompt, tools)";
            foreach (string[] p in PARTS) if (p[0] == key) return zh ? p[1] : p[2];
            return key;
        }

        static void Run()
        {
            while (true)
            {
                try
                {
                    List<string> paths = new List<string>();
                    lock (gate)
                    {
                        long now = Env.Now(); List<string> old = new List<string>();
                        foreach (KeyValuePair<string, State> kv in states) { if (now - kv.Value.RequestedAt < 10 * 60 * 1000) paths.Add(kv.Key); else old.Add(kv.Key); }
                        foreach (string o in old) states.Remove(o);
                    }
                    foreach (string p in paths) { try { Advance(p); } catch { } }
                }
                catch { }
                Thread.Sleep(2500);
            }
        }

        static void Advance(string path)
        {
            State s; lock (gate) { if (!states.TryGetValue(path, out s)) return; }
            FileInfo fi = new FileInfo(path); if (!fi.Exists) return;
            long len = fi.Length;
            if (len < s.Pos) lock (gate) { s.Pos = 0; s.Seen = false; s.Partial = false; s.Reset(); }
            if (len == s.Pos && s.Seen) return;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = s.Pos; bool skipFirst = false;
                if (!s.Seen && len - start > FirstScanMax) { start = len - FirstScanMax; skipFirst = true; lock (gate) s.Partial = true; }
                long want = Math.Min(len - start, PassMax); if (want <= 0) { s.Seen = true; return; }
                byte[] buf = new byte[want]; fs.Position = start; int got = 0, rd; while (got < want && (rd = fs.Read(buf, got, (int)(want - got))) > 0) got += rd;
                int end = got; while (end > 0 && buf[end - 1] != 10) end--;       // only whole lines
                if (end == 0) return;
                int i = 0; if (skipFirst) { while (i < end && buf[i] != 10) i++; i++; }   // we started in the middle of a line
                List<Dictionary<string, object>> lines = new List<Dictionary<string, object>>();
                while (i < end)
                {
                    int j = i; while (j < end && buf[j] != 10) j++;
                    if (j > i)
                    {
                        // cheap filter first: only the line kinds that matter are parsed
                        string line = Encoding.UTF8.GetString(buf, i, j - i);
                        if (line.Contains("\"type\":\"user\"") || line.Contains("\"type\":\"assistant\"") || line.Contains("compact_boundary"))
                            try { Dictionary<string, object> d = J.Obj(J.Parse(line.TrimStart('﻿'))); if (d != null) lines.Add(d); } catch { }
                    }
                    i = j + 1;
                }
                lock (gate) { foreach (Dictionary<string, object> d in lines) Take(s, d); s.Pos = start + end; s.Seen = true; }
            }
        }

        static void Take(State s, Dictionary<string, object> d)
        {
            string type = J.Str(d, "type");
            if (type == "system" && J.Str(d, "subtype") == "compact_boundary") { s.Reset(); return; }
            if (string.Equals(J.Str(d, "isSidechain"), "true", StringComparison.OrdinalIgnoreCase)) return;
            Dictionary<string, object> msg = J.Obj(d.ContainsKey("message") ? d["message"] : null); if (msg == null) return;
            object content = msg.ContainsKey("content") ? msg["content"] : null;
            if (type == "user")
            {
                string key = string.Equals(J.Str(d, "isCompactSummary"), "true", StringComparison.OrdinalIgnoreCase) ? "summary" : "user";
                string text = content as string;
                if (text != null) { s.Add(key, Est(text)); return; }
                foreach (Dictionary<string, object> b in Blocks(content))
                {
                    string ty = J.Str(b, "type");
                    if (ty == "text") s.Add(key, Est(J.Str(b, "text")));
                    else if (ty == "image") s.Add(key, 1500);
                    else if (ty == "tool_result") { string nm; s.Names.TryGetValue(J.Str(b, "tool_use_id"), out nm); s.Add(ToolPart(nm ?? ""), Est(TextOf(b.ContainsKey("content") ? b["content"] : null))); }
                }
            }
            else if (type == "assistant")
            {
                Dictionary<string, object> u = J.Obj(msg.ContainsKey("usage") ? msg["usage"] : null);
                double input = u == null ? 0 : J.Long(u, "input_tokens") + J.Long(u, "cache_read_input_tokens") + J.Long(u, "cache_creation_input_tokens");
                if (u != null && s.First == null && input > 0) s.First = new Point { T = input, E = s.Sum() };   // before this reply: its own output is not part of what it was sent
                foreach (Dictionary<string, object> b in Blocks(content))
                {
                    string ty = J.Str(b, "type");
                    if (ty == "text") s.Add("assistant", Est(J.Str(b, "text")));
                    else if (ty == "tool_use") { s.Names[J.Str(b, "id")] = J.Str(b, "name"); s.Add("call", Est(J.Str(b, "name") + (b.ContainsKey("input") ? J.Compact(b["input"]) : "{}"))); }
                }
                if (u != null && input > 0) s.Last = new Point { T = input + J.Long(u, "output_tokens"), E = s.Sum(), List = new Dictionary<string, double>(s.Acc) };
            }
        }
        static IEnumerable<Dictionary<string, object>> Blocks(object content)
        {
            System.Collections.IEnumerable en = content as System.Collections.IEnumerable;
            if (en == null || content is string) yield break;
            foreach (object o in en) { Dictionary<string, object> b = J.Obj(o); if (b != null) yield return b; }
        }
        static string TextOf(object v)
        {
            if (v == null) return "";
            string s = v as string; if (s != null) return s;
            Dictionary<string, object> d = J.Obj(v);
            if (d != null) { if (d.ContainsKey("text") && d["text"] is string) return (string)d["text"]; string ty = J.Str(d, "type"); return ty == "image" || ty == "input_image" ? "" : J.Compact(d); }
            System.Collections.IEnumerable en = v as System.Collections.IEnumerable;
            if (en != null) { StringBuilder sb = new StringBuilder(); foreach (object o in en) sb.Append(TextOf(o)); return sb.ToString(); }
            return "";
        }
        // characters to tokens, deliberately high: about 1.3 per CJK character, 0.4 per other character
        static double Est(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int cjk = 0; foreach (char ch in s) if ((ch >= 0x2E80 && ch <= 0x9FFF) || (ch >= 0xFF00 && ch <= 0xFFEF)) cjk++;
            return Math.Ceiling(cjk * 1.3 + (s.Length - cjk) * 0.4);
        }
    }
}
