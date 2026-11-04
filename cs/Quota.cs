// A private, background app-server connection. Owns only the process it creates.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ClaudeCompanionPet
{
    static class QuotaBackend
    {
        static readonly object gate = new object();
        static Thread worker;
        static ManualResetEvent stop;
        static CodexQuotaConnection connection;
        static Dictionary<string, object> snapshot = Empty("not_started", "Quota backend has not started.");

        static Dictionary<string, object> Empty(string status, string reason)
        {
            return new Dictionary<string, object> { {"status", status}, {"reason", reason}, {"updated_at", null},
                {"attempt_at", null}, {"accounts", new object[] { new Dictionary<string, object> {
                    {"provider", "codex"}, {"account_key", "codex:local"}, {"status", status}, {"updated_at", null}, {"windows", new object[0]}
                }, new Dictionary<string, object> { {"provider", "claude"}, {"status", "unavailable"},
                    {"reason", "No approved quota metadata source connected; credentials are not read."}, {"windows", new object[0]} } } } };
        }

        public static void Start()
        {
            lock (gate)
            {
                if (worker != null) return;
                stop = new ManualResetEvent(false);
                snapshot = Empty("querying", "Reading Codex quota in background.");
                snapshot["attempt_at"] = CodexJson.Now();
                worker = new Thread(Run) { IsBackground = true, Name = "Pet-Codex-quota" }; worker.Start();
            }
        }
        public static void Stop()
        {
            Thread t; CodexQuotaConnection c;
            lock (gate) { t = worker; if (t == null) return; stop.Set(); c = connection; }
            if (c != null) c.Cancel();
            t.Join(3000);
            lock (gate)
            {
                if (t.IsAlive) return; // never create a second connection while the first worker still exists
                worker = null; connection = null; stop.Dispose(); stop = null;
                snapshot["status"] = "stopped"; snapshot["reason"] = "Quota backend stopped; retained numbers are historical.";
            }
        }
        public static Dictionary<string, object> ReadSnapshot()
        {
            lock (gate)
            {
                var copy = CodexJson.Clone(snapshot); long now = CodexJson.Now(); copy["generated_at"] = now;
                double? updated = CodexJson.Number(CodexJson.Get(copy, "updated_at"));
                bool stale = updated != null && now - updated > 600000;
                if (stale && CodexJson.Str(copy, "status") == "ok") { copy["status"] = "stale"; copy["reason"] = "Last successful read is over ten minutes old."; }
                var accounts = CodexJson.Get(copy, "accounts") as IEnumerable;
                if (accounts != null) foreach (object item in accounts)
                {
                    var a = CodexJson.Obj(item); if (CodexJson.Str(a, "provider") != "codex") continue;
                    string status = CodexJson.Str(copy, "status"); if (status != "ok") a["status"] = status;
                    var windows = CodexJson.Get(a, "windows") as IEnumerable;
                    if (windows != null) foreach (object o in windows)
                    {
                        var w = CodexJson.Obj(o); double? reset = CodexJson.Number(CodexJson.Get(w, "resets_at"));
                        if (status != "ok") w["status"] = status;
                        else if (CodexJson.Str(w, "status") == "ok" && reset != null && now >= reset) w["status"] = "reset_unconfirmed";
                    }
                }
                return copy;
            }
        }

        static void Run()
        {
            CodexQuotaConnection c = null;
            try
            {
                string exe = ResolveExe(); c = new CodexQuotaConnection(exe, stop);
                lock (gate) connection = c;
                c.Open();
                while (!stop.WaitOne(0))
                {
                    long attempt = CodexJson.Now();
                    lock (gate) snapshot["attempt_at"] = attempt;
                    var data = Normalize(c.ReadLimits(), CodexJson.Now()); data["attempt_at"] = attempt;
                    lock (gate) snapshot = data;
                    if (stop.WaitOne(300000)) break; // one query per five minutes; one process per Start
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    snapshot["status"] = stop.WaitOne(0) ? "stopped" : "error";
                    snapshot["reason"] = stop.WaitOne(0) ? "Quota backend stopped." : "Codex quota unavailable (" + e.GetType().Name + "); retained values are not current. Stop/Start to reconnect.";
                    if (c != null) snapshot["failed_stage"] = c.LastMethod;
                }
            }
            finally { if (c != null) c.Dispose(); }
        }

        internal static string ResolveExe()
        {
            string explicitExe = Environment.GetEnvironmentVariable("PET_CODEX_EXE");
            if (!String.IsNullOrEmpty(explicitExe))
            {
                if (!Path.IsPathRooted(explicitExe) || !File.Exists(explicitExe) || !explicitExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("Native Codex executable required.");
                return explicitExe;
            }
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(dir)) continue;
                string native = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(native)) return native;
                string npm = Path.Combine(dir.Trim('"'), "node_modules", "@openai", "codex", "node_modules", "@openai");
                foreach (string arch in new string[] { "codex-win32-x64", "codex-win32-arm64" })
                {
                    string candidate = Path.Combine(npm, arch, "vendor", arch.EndsWith("x64") ? "x86_64-pc-windows-msvc" : "aarch64-pc-windows-msvc", "bin", "codex.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
            throw new FileNotFoundException("Set PET_CODEX_EXE to the installed native Codex executable.");
        }

        internal static Dictionary<string, object> Normalize(Dictionary<string, object> response, long now)
        {
            if (response == null) throw new InvalidDataException("No quota response.");
            var map = CodexJson.Obj(CodexJson.Get(response, "rateLimitsByLimitId"));
            var buckets = new Dictionary<string, object>();
            if (map != null) foreach (var kv in map) buckets[kv.Key] = kv.Value;
            var legacy = CodexJson.Obj(CodexJson.Get(response, "rateLimits"));
            string legacyId = CodexJson.Str(legacy, "limitId");
            if (legacy != null && !buckets.ContainsKey(legacyId == "" ? "codex" : legacyId)) buckets[legacyId == "" ? "codex" : legacyId] = legacy;
            var windows = new List<Dictionary<string, object>>(); var groups = new List<Dictionary<string, object>>(); bool partial = buckets.Count == 0;
            foreach (var entry in buckets)
            {
                var b = CodexJson.Obj(entry.Value); if (b == null) { partial = true; continue; }
                var slots = new Dictionary<string, object>();
                foreach (string slot in new string[] { "primary", "secondary" })
                {
                    object raw; bool present = b.TryGetValue(slot, out raw);
                    if (!present || raw == null) { slots[slot] = present ? "absent" : "unknown"; if (!present) partial = true; continue; }
                    var w = CodexJson.Obj(raw); if (w == null) { slots[slot] = "invalid"; partial = true; continue; }
                    double? used = CodexJson.Number(CodexJson.Get(w, "usedPercent"));
                    double? duration = CodexJson.Number(CodexJson.Get(w, "windowDurationMins"));
                    double? reset = CodexJson.Number(CodexJson.Get(w, "resetsAt"));
                    if (used < 0 || used > 100) used = null;
                    if (duration <= 0 || duration > 52560000 || (duration != null && duration != Math.Floor(duration.Value))) duration = null;
                    if (reset <= 0 || reset > 253402300799 || (reset != null && reset != Math.Floor(reset.Value))) reset = null;
                    bool complete = used != null && duration != null && reset != null;
                    slots[slot] = complete ? "reported" : "partial"; if (!complete) partial = true;
                    windows.Add(new Dictionary<string, object> {
                        {"bucket_id", entry.Key}, {"bucket_name", CodexJson.Get(b, "limitName")}, {"slot", slot},
                        {"used_percent", used}, {"duration_minutes", duration}, {"resets_at", reset * 1000}, {"updated_at", now},
                        {"status", !complete ? "partial" : now >= reset * 1000 ? "reset_unconfirmed" : "ok"},
                        {"label", duration == 300 ? "5h" : duration == 10080 ? "weekly" : duration == null ? "unknown window" : duration + " min"}
                    });
                }
                groups.Add(new Dictionary<string, object> { {"bucket_id", entry.Key}, {"window_slots", slots},
                    {"rate_limit_reached_type", CodexJson.Get(b, "rateLimitReachedType")}, {"spend_control_reached", CodexJson.Get(b, "spendControlReached")} });
            }
            string rawId = CodexJson.Str(response, "accountId"), accountKey = "codex:local";
            if (rawId != "") using (var hash = SHA256.Create()) accountKey = "codex:" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(rawId))).Replace("-", "").ToLowerInvariant();
            var account = new Dictionary<string, object> {
                {"provider", "codex"}, {"account_key", accountKey}, {"identity_quality", rawId == "" ? "local_auth_unidentified" : "hashed_account_id"},
                {"status", partial ? "partial" : "ok"}, {"source", "codex_app_server"}, {"updated_at", now},
                {"ordinary_usage_allowed", CodexJson.Get(response, "ordinaryUsageAllowed") is bool ? CodexJson.Get(response, "ordinaryUsageAllowed") : null},
                {"windows", windows}, {"buckets", groups},
                {"reason", "Reported windows only. Null/missing are not zero. Reset timestamps never prove recovery."}
            };
            var output = Empty("ok", "Codex quota read succeeded; no reset or model request was made.");
            output["updated_at"] = now; var arr = (object[])output["accounts"]; arr[0] = account;
            return output;
        }
    }

    // Sequential, bounded JSON-RPC, with cancellation that kills this owned child only.
    sealed class CodexQuotaConnection : IDisposable
    {
        readonly string exe;
        readonly ManualResetEvent stop;
        readonly AutoResetEvent received = new AutoResetEvent(false);
        readonly object gate = new object();
        Process child; int seq, awaiting; Dictionary<string, object> reply; bool closed;
        internal string LastMethod { get; private set; }
        internal CodexQuotaConnection(string exe, ManualResetEvent stop) { this.exe = exe; this.stop = stop; }
        internal int ProcessId { get { lock (gate) return child == null ? 0 : child.Id; } }
        internal void Open()
        {
            lock (gate)
            {
                if (stop.WaitOne(0)) throw new OperationCanceledException();
                child = new Process { StartInfo = new ProcessStartInfo(exe, "app-server --listen stdio://") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
                }};
                child.OutputDataReceived += OnLine; child.ErrorDataReceived += delegate { }; // drain, never log credentials/server payloads
                child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            }
            Rpc("initialize", new Dictionary<string, object> { {"clientInfo", new Dictionary<string, object> { {"name", "companion_pet_quota"}, {"version", "0.1.0"} }} });
            Send(new Dictionary<string, object> { {"method", "initialized"} });
        }
        void OnLine(object sender, DataReceivedEventArgs args)
        {
            if (args.Data == null) { lock (gate) { if (!closed) received.Set(); } return; }
            Dictionary<string, object> d; try { d = CodexJson.Obj(CodexJson.Parse(args.Data)); } catch { return; }
            lock (gate)
            {
                // No notifications, server requests, approvals or unrelated session subscriptions are consumed.
                if (!closed && CodexJson.Number(CodexJson.Get(d, "id")) == awaiting && awaiting != 0 && CodexJson.Get(d, "method") == null) { reply = d; received.Set(); }
            }
        }
        void Send(object message)
        {
            lock (gate) { if (closed || stop.WaitOne(0)) throw new OperationCanceledException(); child.StandardInput.WriteLine(CodexJson.Json(message)); child.StandardInput.Flush(); }
        }
        Dictionary<string, object> Rpc(string method, object parameters)
        {
            LastMethod = method;
            int id; lock (gate) { awaiting = id = ++seq; reply = null; received.Reset(); }
            Send(new Dictionary<string, object> { {"id", id}, {"method", method}, {"params", parameters} });
            if (WaitHandle.WaitAny(new WaitHandle[] { stop, received }, 30000) != 1) throw new TimeoutException("Quota RPC stopped or timed out: " + method);
            lock (gate)
            {
                awaiting = 0;
                if (reply == null) throw new IOException("Codex app-server closed.");
                if (CodexJson.Get(reply, "error") != null) throw new InvalidOperationException("Codex quota RPC rejected.");
                var result = CodexJson.Obj(CodexJson.Get(reply, "result")); if (result == null) throw new InvalidDataException("No RPC result."); return result;
            }
        }
        internal Dictionary<string, object> ReadLimits()
        {
            return Rpc("account/rateLimits/read", new Dictionary<string, object> { {"excludeResetCreditDetails", true} });
        }
        internal void Cancel()
        {
            lock (gate)
            {
                if (closed) return; closed = true;
                if (child != null) { try { child.StandardInput.Close(); } catch { } try { if (!child.HasExited) child.Kill(); } catch { } }
                received.Set();
            }
        }
        public void Dispose()
        {
            Cancel();
            // Ensure async output callbacks finish before releasing their wait handle.
            if (child != null) { try { child.WaitForExit(1000); } catch { } child.OutputDataReceived -= OnLine; child.Dispose(); }
            received.Dispose();
        }
    }
}
