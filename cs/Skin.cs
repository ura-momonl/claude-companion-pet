// Skins: a skin is just a folder. Replace or add one by putting files in place; no code, no rebuild.
//
//   skins/<id>/skin.txt          optional, key=value lines (see below)
//   skins/<id>/<state>/*.png     frames of one state in file-name order; ONE png = a still picture
//   skins/<id>/<state>/<variant>/*.png   several variants of one state: one is played through, then the pet rests a random
//                                         gap on its first picture, then another variant (never the same twice in a row)
//   states: idle busy approval answer done error quota   (quota = out of quota)
//
// skin.txt keys: name, name.zh, scale (screen pixels per image pixel, default 1), frame_ms (default 120),
//                mode = loop | pingpong | once (default loop), <state>.mode / <state>.frame_ms to override one state,
//                gap_ms = min-max (or one number; default 4000-10000) rest between two variants, <state>.gap_ms per state,
//                align = x,y  (optional) feet target in image pixels: each variant is shifted as a whole (never the files) so that
//                the median feet centre / lowest opaque row of its pictures land there, so switching variants does not make the feet jump.
//                <state>.align = off | x,y  per state (pictures that should keep their own height, e.g. the pet held in the air, use off),
//                <state>.start = play  (a resting state starts with its animation instead of a rest),
//                <state>.rest = <folder>  (while the state rests between two animations it loops the pictures of that folder, e.g. breathing, instead of
//                standing on one picture; the folder's own keys: rest.mode=loop, rest.frame_ms=...),
//                <state>.from = dir[:a-b],dir[:a-b]...  more variants taken from other folders of the skin (optionally only pictures a..b,
//                counted from 0), so an animation can be reused without copying files.
// A state that has a gap_ms of its own rests and replays even with one variant (used for "waving at you" every few seconds).
// A state without its own folder shows the "idle" pictures (approval and answer use each other first).
// Behaviour states (no fall-back, a skin may leave them out): lift (picked up: start), held (picked up: hanging, loops while held),
// drop (put down), happy (one of these plays on a click), sleepy (getting drowsy), asleep (loops), wake (waking up).
// Skins are looked up in <program folder>\skins and in %APPDATA%\claude-companion-pet\skins.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCompanionPet
{
    class Skin
    {
        public static readonly string[] STATES = { "idle", "busy", "approval", "answer", "done", "error", "quota" };
        public string Id, Dir, Name, NameZh;
        public float Scale = 1f;
        int frameMs = 120; string mode = "loop";
        bool align; int alignX, alignY;
        readonly Dictionary<string, string> over = new Dictionary<string, string>();
        readonly Dictionary<string, Variant[]> variants = new Dictionary<string, Variant[]>();
        readonly Dictionary<string, Sched> scheds = new Dictionary<string, Sched>();
        readonly HashSet<string> seen = new HashSet<string>();
        static readonly Random rnd = new Random();

        class Variant { public string Name; public Bitmap[] Fr; }
        class Seg { public Variant V; public long Start, Play, End; }
        class Sched { public List<Seg> Segs = new List<Seg>(); public long Last, Built; public Variant Prev; }

        // one play-through of a behaviour state (lift, held, drop, happy, sleepy, asleep, wake): a variant picked at random (not the one just played)
        public class Clip
        {
            public Bitmap[] Fr; public int Step; public string Mode, Name, Variant;
            public long Length { get { return (long)Fr.Length * Step; } }
            public bool Done(long t) { return Mode == "once" && t >= Length; }
            public Bitmap At(long t)
            {
                if (Fr.Length == 1) return Fr[0];
                long idx = t / Step;
                if (Mode == "once") return Fr[(int)Math.Min(idx, Fr.Length - 1)];
                if (Mode == "pingpong") { long cycle = 2 * Fr.Length - 2; long p = idx % cycle; return Fr[(int)(p < Fr.Length ? p : cycle - p)]; }
                return Fr[(int)(idx % Fr.Length)];
            }
        }
        readonly Dictionary<string, Variant> lastClip = new Dictionary<string, Variant>();
        public Clip GetClip(string state, string dfltMode)
        {
            if (!Has(state)) return null;
            Variant[] vs = Variants(state); if (vs.Length == 0) return null;
            Variant v, prev; lastClip.TryGetValue(state, out prev);
            do v = vs[rnd.Next(vs.Length)]; while (v == prev && vs.Length > 1);
            lastClip[state] = v;
            return new Clip { Fr = v.Fr, Step = Step(state), Mode = Opt(state, "mode", dfltMode), Name = state, Variant = v.Name == "" ? state : v.Name };
        }

        public string Title { get { return Lang.Zh && !string.IsNullOrEmpty(NameZh) ? NameZh : Name; } }

        public static List<Skin> Discover()
        {
            List<Skin> list = new List<Skin>();
            foreach (string root in new string[] { Path.Combine(Env.ExeDir(), "skins"), Path.Combine(Env.ExeDir(), "..", "skins"), Path.Combine(Env.DataDir(), "skins") })
            {
                string[] dirs; try { dirs = Directory.GetDirectories(root); } catch { continue; }
                foreach (string d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    string id = Path.GetFileName(d);
                    if (list.Any(x2 => x2.Id == id)) continue;
                    Skin s = Skin.Open(d); if (s != null) list.Add(s);
                }
            }
            return list;
        }

        public static Skin Open(string dir)
        {
            if (!Directory.Exists(dir)) return null;
            Skin s = new Skin(); s.Dir = dir; s.Id = Path.GetFileName(dir); s.Name = s.Id;
            string txt = Path.Combine(dir, "skin.txt");
            if (File.Exists(txt))
                foreach (string line in File.ReadAllLines(txt, Encoding.UTF8))
                {
                    int i = line.IndexOf('='); if (i <= 0 || line.TrimStart().StartsWith("#")) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int hash = v.IndexOf('#'); if (hash >= 0) v = v.Substring(0, hash).Trim(); // trailing comment
                    float f; int n;
                    if (k == "name") s.Name = v; else if (k == "name.zh") s.NameZh = v;
                    else if (k == "scale" && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) && f > 0) s.Scale = f;
                    else if (k == "frame_ms" && int.TryParse(v, out n) && n >= 20) s.frameMs = n;
                    else if (k == "mode") s.mode = v;
                    else if (k == "align")
                    {
                        string[] xy = v.Split(','); int ax, ay;
                        if (xy.Length == 2 && int.TryParse(xy[0].Trim(), out ax) && int.TryParse(xy[1].Trim(), out ay)) { s.align = true; s.alignX = ax; s.alignY = ay; }
                    }
                    else s.over[k] = v;
                }
            return Directory.GetDirectories(dir).Any() ? s : null;
        }

        // where the variants of a state come from: its folder (pictures directly in it = one variant; each sub-folder with pictures = one more)
        // and the "<state>.from=" list (one variant each: a folder of the skin, optionally only pictures a..b)
        class Src { public string Dir; public int A = -1, B = -1; }
        List<Src> Sources(string state)
        {
            List<Src> res = new List<Src>();
            string p = Path.Combine(Dir, state);
            if (Directory.Exists(p))
            {
                if (Directory.GetFiles(p, "*.png").Length > 0) res.Add(new Src { Dir = p });
                foreach (string d in Directory.GetDirectories(p).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    if (Directory.GetFiles(d, "*.png").Length > 0) res.Add(new Src { Dir = d });
            }
            string from;
            if (over.TryGetValue(state + ".from", out from))
                foreach (string item in from.Split(','))
                {
                    string path = item.Trim(); if (path == "") continue;
                    Src sr = new Src(); int c = path.LastIndexOf(':');
                    if (c > 0)
                    {
                        string[] ab = path.Substring(c + 1).Split('-'); int a, b;
                        if (ab.Length == 2 && int.TryParse(ab[0], out a) && int.TryParse(ab[1], out b) && a >= 0 && b >= a) { sr.A = a; sr.B = b; path = path.Substring(0, c); }
                    }
                    sr.Dir = Path.Combine(Dir, path.Replace('/', Path.DirectorySeparatorChar));
                    if (Directory.Exists(sr.Dir) && Directory.GetFiles(sr.Dir, "*.png").Length > 0) res.Add(sr);
                }
            return res;
        }
        string StateDir(string state) { return Sources(state).Count > 0 ? Path.Combine(Dir, state) : null; }
        // which state's pictures are used for `state`
        public string Resolve(string state)
        {
            if (StateDir(state) != null) return state;
            if (state == "approval" && StateDir("answer") != null) return "answer";
            if (state == "answer" && StateDir("approval") != null) return "approval";
            return StateDir("idle") != null ? "idle" : STATES.FirstOrDefault(s => StateDir(s) != null);
        }
        public bool Has(string state) { return StateDir(state) != null; }

        Variant[] Variants(string state)
        {
            string use = Resolve(state); if (use == null) return new Variant[0];
            Variant[] vs;
            if (variants.TryGetValue(use, out vs)) return vs;
            List<Variant> list = new List<Variant>();
            foreach (Src sr in Sources(use))
            {
                string d = sr.Dir;
                Variant v = new Variant(); v.Name = Path.GetFileName(d) == use ? "" : Path.GetFileName(d);
                List<Bitmap> fl = new List<Bitmap>();
                string[] files = Directory.GetFiles(d, "*.png").OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                if (sr.A >= 0) files = files.Skip(sr.A).Take(sr.B - sr.A + 1).ToArray();
                foreach (string f in files)
                    try
                    {
                        using (Image img = Image.FromFile(f))
                        {
                            Bitmap b = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb); // a copy: the file is not kept open
                            using (Graphics g = Graphics.FromImage(b)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(img, 0, 0, img.Width, img.Height); }
                            fl.Add(b);
                        }
                    }
                    catch { }
                if (fl.Count == 0) continue;
                v.Fr = fl.ToArray();
                Align(v, use);
                list.Add(v);
            }
            variants[use] = vs = list.ToArray();
            return vs;
        }

        // shift all pictures of a variant by the same whole pixels so that its median lowest row / feet centre match the target
        void Align(Variant v, string use)
        {
            bool on = align; int tx = alignX, ty = alignY;
            string o = Opt(use, "align", "");
            if (o == "off") on = false;
            else if (o != "") { string[] xy = o.Split(','); int a, b; if (xy.Length == 2 && int.TryParse(xy[0].Trim(), out a) && int.TryParse(xy[1].Trim(), out b)) { on = true; tx = a; ty = b; } }
            if (!on) return;
            List<int> bots = new List<int>(); List<double> xs = new List<double>();
            foreach (Bitmap b in v.Fr)
            {
                int bottom = -1, x0 = int.MaxValue, x1 = -1;
                BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    byte[] px = new byte[bd.Stride * b.Height]; System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, px, 0, px.Length);
                    for (int y = b.Height - 1; y >= 0 && bottom < 0; y--)
                        for (int x = 0; x < b.Width; x++) if (px[y * bd.Stride + x * 4 + 3] > 0) { bottom = y; break; }
                    if (bottom < 0) continue;
                    for (int y = Math.Max(0, bottom - 3); y <= bottom; y++) // the feet: the lowest four rows
                        for (int x = 0; x < b.Width; x++) if (px[y * bd.Stride + x * 4 + 3] > 0) { if (x < x0) x0 = x; if (x > x1) x1 = x; }
                }
                finally { b.UnlockBits(bd); }
                bots.Add(bottom); xs.Add((x0 + x1) / 2.0);
            }
            if (bots.Count == 0) return;
            double mb = Median(bots.Select(x => (double)x).ToList()), mx = Median(xs);
            int dx = (int)Math.Round(tx - mx, MidpointRounding.AwayFromZero), dy = (int)Math.Round(ty - mb, MidpointRounding.AwayFromZero);
            if (dx == 0 && dy == 0) return;
            for (int i = 0; i < v.Fr.Length; i++)
            {
                Bitmap src = v.Fr[i], dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(dst)) { g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy; g.DrawImage(src, new Rectangle(dx, dy, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel); }
                v.Fr[i] = dst; src.Dispose();
            }
        }
        static double Median(List<double> a) { a.Sort(); return a.Count % 2 == 1 ? a[a.Count / 2] : (a[a.Count / 2 - 1] + a[a.Count / 2]) / 2.0; }

        // the pictures of the first variant (wizard preview) / its first picture (tray icon)
        public Bitmap[] Frames(string state) { Variant[] vs = Variants(state); return vs.Length == 0 ? new Bitmap[0] : vs[0].Fr; }
        public Bitmap Still(string state) { Bitmap[] fr = Frames(state); return fr.Length == 0 ? null : fr[0]; }

        string Opt(string use, string key, string dflt) { string v; return over.TryGetValue(use + "." + key, out v) ? v : dflt; }
        int Step(string use) { int n; return int.TryParse(Opt(use, "frame_ms", ""), out n) && n >= 20 ? n : frameMs; }
        string Mode(string use) { return Opt(use, "mode", mode); }
        // how long one play-through of a variant lasts
        long Cycle(Variant v, string use)
        {
            int n = v.Fr.Length; if (n == 1) return Step(use);
            return (Mode(use) == "pingpong" ? 2 * n - 2 : n) * (long)Step(use);
        }
        Bitmap FrameAt(Variant v, string use, long t)
        {
            Bitmap[] fr = v.Fr; if (fr.Length == 1) return fr[0];
            string m = Mode(use); long idx = t / Step(use);
            if (m == "once") return fr[Math.Min(idx, fr.Length - 1)];
            if (m == "pingpong") { long cycle = 2 * fr.Length - 2; long p = idx % cycle; return fr[p < fr.Length ? p : cycle - p]; }
            return fr[idx % fr.Length];
        }
        void Gap(string use, out int lo, out int hi)
        {
            lo = 4000; hi = 10000;
            string g = Opt(use, "gap_ms", ""); if (g == "") over.TryGetValue("gap_ms", out g);
            if (string.IsNullOrEmpty(g)) return;
            string[] p = g.Split('-'); int a, b;
            if (p.Length == 1 && int.TryParse(p[0].Trim(), out a) && a >= 0) { lo = hi = a; }
            else if (p.Length == 2 && int.TryParse(p[0].Trim(), out a) && int.TryParse(p[1].Trim(), out b) && a >= 0 && b >= a) { lo = a; hi = b; }
        }

        // the picture of `state` after it has been showing for elapsedMs.
        // One variant: it just plays on (loop / pingpong / once). Several: rest a random gap on the first picture of one, play it through,
        // rest again, play another one (not the one just played). The plan is made on the fly and starts over whenever the state is entered again.
        public Bitmap Pick(string state, long elapsedMs)
        {
            Variant[] vs = Variants(state); if (vs.Length == 0) return null;
            string use = Resolve(state);
            bool rests = vs.Length > 1 || over.ContainsKey(use + ".gap_ms");
            if (!rests) return FrameAt(vs[0], use, elapsedMs);
            Sched sc;
            if (!scheds.TryGetValue(use, out sc) || elapsedMs + 1000 < sc.Last) { sc = new Sched(); scheds[use] = sc; }
            sc.Last = elapsedMs;
            int lo, hi; Gap(use, out lo, out hi);
            if (sc.Segs.Count == 0)
            {
                Variant v0 = vs[rnd.Next(vs.Length)];
                // the very first time a state is shown after the program started it plays at once (a pet that stands still for 4-10 s at start looks broken)
                bool first = seen.Add(use);
                long gap0 = first || Opt(use, "start", "") == "play" ? 0 : lo + (long)(rnd.NextDouble() * (hi - lo));
                sc.Segs.Add(new Seg { V = v0, Start = 0, Play = 0, End = gap0 }); sc.Prev = v0; sc.Built = gap0;
            }
            while (sc.Built <= elapsedMs)
            {
                Variant v; do v = vs[rnd.Next(vs.Length)]; while (v == sc.Prev && vs.Length > 1);
                long play = Cycle(v, use), gap = lo + (long)(rnd.NextDouble() * (hi - lo));
                sc.Segs.Add(new Seg { V = v, Start = sc.Built, Play = play, End = sc.Built + play + gap });
                sc.Built += play + gap; sc.Prev = v;
            }
            Seg cur = sc.Segs[0];
            for (int i = sc.Segs.Count - 1; i >= 0; i--) if (sc.Segs[i].Start <= elapsedMs) { cur = sc.Segs[i]; break; }
            if (sc.Segs.Count > 40) sc.Segs.RemoveRange(0, 20); // long sessions: forget the distant past
            long into = elapsedMs - cur.Start;
            if (into >= cur.Play)
            {   // resting between two animations: a looping rest animation (<state>.rest=<folder>) when the skin has one, else the first picture
                string rd = Opt(use, "rest", "");
                if (rd != "" && StateDir(rd) != null) { Variant[] rv = Variants(rd); if (rv.Length > 0 && rv[0].Fr.Length > 1) return FrameAt(rv[0], rd, into - cur.Play); }
            }
            return FrameAt(cur.V, use, Math.Min(into, cur.Play));
        }
    }
}
