// "Connect Claude Code" / "Disconnect": adds (removes) pet.exe as the hooks and the status line in Claude Code's settings.json.
// Before the first change the original file is copied next to it (settings.json.companion-backup-<time>). "Disconnect" removes only
// what this program added and puts the user's own status line back; anything the user changed in the meantime stays.
// Pure file work: runs from a button, the right-click menu, or the command line (--cc-connect / --cc-disconnect / --cc-status).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ClaudeCompanionPet
{
    static class Connector
    {
        const string HOOK_FLAG = "--cc-hook", STATUS_FLAG = "--cc-statusline";
        static readonly string[][] EVENTS = new string[][] {
            new string[] { "SessionStart" }, new string[] { "SessionEnd" }, new string[] { "UserPromptSubmit" },
            new string[] { "PreToolUse", "AskUserQuestion" }, new string[] { "PermissionRequest" }, new string[] { "PostToolUse" },
            new string[] { "PostToolUseFailure" }, new string[] { "Notification" }, new string[] { "Stop" }, new string[] { "StopFailure" },
        };

        public static string SettingsPath() { return Path.Combine(Env.ClaudeDir(), "settings.json"); }
        static string MemoPath() { return Path.Combine(Env.DataDir(), "connect.json"); }

        static bool IsOurs(object cmd) { string c = cmd as string; return c != null && (c.Contains(HOOK_FLAG) || c.Contains(STATUS_FLAG)) && c.Contains("pet.exe"); }

        public static bool IsConnected()
        {
            try { string t = File.ReadAllText(SettingsPath(), Encoding.UTF8); return t.Contains(HOOK_FLAG) && t.Contains("pet.exe"); } catch { return false; }
        }

        // null = the hooks in settings.json run this program; otherwise why they do not (the program they name was moved or replaced, or is another copy)
        public static bool LastMissing;
        public static bool HookExeMissing() { return StaleReason() != null && LastMissing; }
        public static string StaleReason()
        {
            LastMissing = false;
            try
            {
                Dictionary<string, object> st = J.ReadFile(SettingsPath()); Dictionary<string, object> hooks = st == null ? null : J.Obj(st.ContainsKey("hooks") ? st["hooks"] : null);
                if (hooks == null) return null;
                foreach (KeyValuePair<string, object> ev in hooks)
                {
                    if (!(ev.Value is System.Collections.IEnumerable) || ev.Value is string) continue;
                    foreach (object g in (System.Collections.IEnumerable)ev.Value)
                    {
                        Dictionary<string, object> gd = J.Obj(g); object hs = gd != null && gd.ContainsKey("hooks") ? gd["hooks"] : null;
                        if (!(hs is System.Collections.IEnumerable) || hs is string) continue;
                        foreach (object h in (System.Collections.IEnumerable)hs)
                        {
                            string cmd = J.Str(J.Obj(h), "command"); if (!IsOurs(cmd) || !cmd.StartsWith("\"")) continue;
                            int end = cmd.IndexOf('"', 1); if (end < 0) continue;
                            string path = cmd.Substring(1, end - 1).Replace('/', '\\');
                            if (!File.Exists(path)) { LastMissing = true; return Lang.T("连接指向的程序已不存在", "the connected program is gone"); }
                            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(Env.ExePath()), StringComparison.OrdinalIgnoreCase)) return Lang.T("连接指向另一份程序", "points to another copy");
                            return null;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // plain-words reason for a failed file operation (the raw message follows it, so nothing is hidden)
        static string Why(Exception e)
        {
            string raw = e.GetType().Name + ": " + e.Message;
            if (e is UnauthorizedAccessException) return Lang.T("没有权限（访问被拒绝）。", "no permission (access denied). ") + raw;
            if (e is IOException && ((System.Runtime.InteropServices.Marshal.GetHRForException(e) & 0xFFFF) == 32 || (System.Runtime.InteropServices.Marshal.GetHRForException(e) & 0xFFFF) == 33))
                return Lang.T("文件正被别的程序占用。", "the file is in use by another program. ") + raw;
            if (e is DirectoryNotFoundException || e is FileNotFoundException) return Lang.T("找不到文件或文件夹。", "file or folder not found. ") + raw;
            return raw;
        }
        static string Fail(string what, string file, Exception e)
        {
            Diag.Error("connect: " + what + " (" + file + ")", e);
            return Lang.T("没能" + what + "：", "Could not " + what + ": ") + Why(e) + "\r\n" + Lang.T("文件：", "File: ") + file + "\r\n" + Lang.T("日志：", "Log: ") + Diag.Path_();
        }

        static Dictionary<string, object> Load(out string error)
        {
            error = null; string file = SettingsPath();
            if (!File.Exists(file)) { Diag.Write("connect: settings.json does not exist yet; a new one will be created"); return new Dictionary<string, object>(); }
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception e) { error = Fail(Lang.T("读取设置文件", "read the settings file"), file, e); return null; }
            if (text.Trim() == "") return new Dictionary<string, object>();
            Dictionary<string, object> d;
            try { d = J.Obj(J.Parse(text)); } catch (Exception e) { Diag.Error("connect: settings.json is not valid JSON (" + file + ")", e); error = Lang.T("settings.json 不是有效的 JSON，没有改动：", "settings.json is not valid JSON, nothing was changed: ") + e.Message + "\r\n" + Lang.T("文件：", "File: ") + file; return null; }
            if (d == null) { Diag.Write("connect: settings.json is not a JSON object"); error = Lang.T("settings.json 的内容不是对象，没有改动。", "settings.json is not a JSON object, nothing was changed.") + "\r\n" + file; }
            return d;
        }

        // removes our hook entries (and our status line); returns the user's own status line that we had wrapped, if any
        static void StripOurs(Dictionary<string, object> s)
        {
            if (s.ContainsKey("hooks") && J.Obj(s["hooks"]) != null)
            {
                Dictionary<string, object> hooks = J.Obj(s["hooks"]);
                foreach (string ev in hooks.Keys.ToList())
                {
                    IEnumerable groups = hooks[ev] as IEnumerable; if (groups == null || hooks[ev] is string) continue;
                    List<object> keep = new List<object>();
                    foreach (object g in groups)
                    {
                        Dictionary<string, object> gd = J.Obj(g);
                        IEnumerable inner = gd != null && gd.ContainsKey("hooks") ? gd["hooks"] as IEnumerable : null;
                        if (inner == null) { keep.Add(g); continue; }
                        List<object> kept = new List<object>(); int dropped = 0;
                        foreach (object h in inner) { Dictionary<string, object> hd = J.Obj(h); if (hd != null && IsOurs(hd.ContainsKey("command") ? hd["command"] : null)) dropped++; else kept.Add(h); }
                        if (dropped == 0) { keep.Add(g); continue; }
                        if (kept.Count > 0) { gd["hooks"] = kept.ToArray(); keep.Add(gd); }
                    }
                    if (keep.Count > 0) hooks[ev] = keep.ToArray(); else hooks.Remove(ev);
                }
            }
        }

        static string Exe() { return Env.ExePath().Replace('\\', '/'); }

        public static string Connect()
        {
            string file = SettingsPath();
            Diag.Write("connect: start; settings " + Diag.FileInfoLine(file) + "; exe " + Exe());
            string err; Dictionary<string, object> s = Load(out err);
            if (s == null) { Diag.Write("connect: FAILED while reading"); return err; }
            string cw = Diag.CanWriteDir(Path.GetDirectoryName(file));
            if (cw != null) { Diag.Write("connect: FAILED, folder not writable: " + cw); return Lang.T("没能写入设置：Claude Code 的设置文件夹不能写入（" + cw + "）。", "Could not write the settings: the Claude Code settings folder is not writable (" + cw + ").") + "\r\n" + Path.GetDirectoryName(file) + "\r\n" + Lang.T("日志：", "Log: ") + Diag.Path_(); }
            try
            {
                string backup = null;
                if (File.Exists(file))
                {
                    backup = file + ".companion-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Copy(file, backup, false);
                }
                // what the user had before us (kept across reconnects)
                Dictionary<string, object> memo = J.ReadFile(MemoPath()) ?? new Dictionary<string, object>();
                bool hadHooks = s.ContainsKey("hooks");
                Dictionary<string, object> own = s.ContainsKey("statusLine") ? J.Obj(s["statusLine"]) : null;
                string ownCmd = own == null ? null : J.Str(own, "command");
                if (!memo.ContainsKey("hadHooks")) memo["hadHooks"] = hadHooks;
                if (own != null && !IsOurs(ownCmd)) memo["statusLine"] = own;      // the user's own status line: wrapped, restored on disconnect
                else if (!memo.ContainsKey("statusLine")) memo["statusLine"] = null;
                StripOurs(s);

                Dictionary<string, object> orig = J.Obj(memo.ContainsKey("statusLine") ? memo["statusLine"] : null);
                string cmd = "\"" + Exe() + "\" " + STATUS_FLAG;
                if (orig != null && J.Str(orig, "command") != "") cmd += " --chain " + Convert.ToBase64String(Encoding.UTF8.GetBytes(J.Str(orig, "command")));
                Dictionary<string, object> sl = new Dictionary<string, object>();
                if (orig != null) foreach (KeyValuePair<string, object> kv in orig) sl[kv.Key] = kv.Value;
                sl["type"] = "command"; sl["command"] = cmd;
                s["statusLine"] = sl;

                Dictionary<string, object> hooks = s.ContainsKey("hooks") && J.Obj(s["hooks"]) != null ? J.Obj(s["hooks"]) : new Dictionary<string, object>();
                foreach (string[] ev in EVENTS)
                {
                    List<object> groups = new List<object>();
                    if (hooks.ContainsKey(ev[0]) && hooks[ev[0]] is IEnumerable && !(hooks[ev[0]] is string)) foreach (object g in (IEnumerable)hooks[ev[0]]) groups.Add(g);
                    Dictionary<string, object> h = new Dictionary<string, object>(); h["type"] = "command"; h["command"] = "\"" + Exe() + "\" " + HOOK_FLAG; h["timeout"] = 10;
                    Dictionary<string, object> grp = new Dictionary<string, object>();
                    if (ev.Length > 1) grp["matcher"] = ev[1];
                    grp["hooks"] = new object[] { h };
                    groups.Add(grp);
                    hooks[ev[0]] = groups.ToArray();
                }
                s["hooks"] = hooks;

                memo["settings"] = file; memo["backup"] = backup; memo["connectedAt"] = DateTime.Now.ToString("s"); memo["exe"] = Exe();
                File.WriteAllText(file, J.Pretty(s) + "\n", new UTF8Encoding(false));
                try { File.WriteAllText(MemoPath(), J.Pretty(memo) + "\n", new UTF8Encoding(false)); } catch (Exception e) { Diag.Error("connect: could not save the undo memo (disconnect still removes our hooks)", e); }
                if (!IsConnected()) { Diag.Write("connect: FAILED, written but reading it back does not show the connection"); return Lang.T("写入后复查失败：设置文件里没有看到连接内容。", "Check after writing failed: the connection is not in the settings file.") + "\r\n" + file + "\r\n" + Lang.T("日志：", "Log: ") + Diag.Path_(); }
                Diag.Write("connect: ok; backup " + (backup ?? "(none, there was no settings.json)") + "; hooks for " + EVENTS.Length + " events");
                return null;
            }
            catch (Exception e) { return Fail(Lang.T("写入设置", "write the settings"), file, e); }
        }

        public static string LastBackup() { Dictionary<string, object> m = J.ReadFile(MemoPath()); string b = J.Str(m, "backup"); return b == "" ? null : b; }

        public static string Disconnect()
        {
            Diag.Write("disconnect: start; settings " + Diag.FileInfoLine(SettingsPath()));
            string err; Dictionary<string, object> s = Load(out err);
            if (s == null) { Diag.Write("disconnect: FAILED while reading"); return err; }
            try
            {
                Dictionary<string, object> memo = J.ReadFile(MemoPath()) ?? new Dictionary<string, object>();
                StripOurs(s);
                if (s.ContainsKey("hooks") && J.Obj(s["hooks"]) != null && J.Obj(s["hooks"]).Count == 0 && !(memo.ContainsKey("hadHooks") && memo["hadHooks"] is bool && (bool)memo["hadHooks"])) s.Remove("hooks");
                Dictionary<string, object> cur = s.ContainsKey("statusLine") ? J.Obj(s["statusLine"]) : null;
                if (cur != null && IsOurs(J.Str(cur, "command")))
                {
                    Dictionary<string, object> orig = J.Obj(memo.ContainsKey("statusLine") ? memo["statusLine"] : null);
                    if (orig != null) s["statusLine"] = orig; else s.Remove("statusLine");
                }
                string file = SettingsPath();
                if (File.Exists(file) || s.Count > 0) { Directory.CreateDirectory(Path.GetDirectoryName(file)); File.WriteAllText(file, J.Pretty(s) + "\n", new UTF8Encoding(false)); }
                try { File.Delete(MemoPath()); } catch { }
                Diag.Write("disconnect: ok");
                return null;
            }
            catch (Exception e) { return Fail(Lang.T("写入设置", "write the settings"), SettingsPath(), e); }
        }
    }
}
