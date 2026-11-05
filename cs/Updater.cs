// "Check for updates" (right-click > More): asks GitHub for the latest release of this program, and if it is newer offers to install it. Manual only: nothing is
// contacted in the background. The zip is downloaded over https, checked against the SHA-256 that GitHub lists for the file, unpacked by a small PowerShell script that waits
// for this program to exit, copies pet.exe / skins / prices.json over the program folder (settings and statistics live in %APPDATA% and are not touched) and starts it again.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeCompanionPet
{
    static class Updater
    {
        public const string Version = "2.21";
        public class Info { public string Tag = "", Url = "", Sha = "", Notes = ""; }
        static string Api() { return Env.Get("PET_UPDATE_API") ?? "https://api.github.com/repos/ura-momonl/claude-companion-pet/releases/latest"; }
        public static string Current() { return Env.Get("PET_VERSION") ?? Version; }
        static WebClient Client() { try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } WebClient w = new WebClient(); w.Headers["User-Agent"] = "claude-companion-pet/" + Current(); w.Headers["Accept"] = "application/vnd.github+json"; w.Encoding = Encoding.UTF8; return w; }
        // null + error text when GitHub could not be asked
        public static Info Latest(out string error)
        {
            error = null;
            try
            {
                string body; using (WebClient w = Client()) body = w.DownloadString(Api());
                Dictionary<string, object> d = J.Obj(J.Parse(body)); if (d == null) { error = "unexpected answer"; return null; }
                Info i = new Info { Tag = J.Str(d, "tag_name"), Notes = J.Str(d, "body") };
                System.Collections.IEnumerable assets = d.ContainsKey("assets") ? d["assets"] as System.Collections.IEnumerable : null;
                if (assets != null) foreach (object ao in assets)
                {
                    Dictionary<string, object> a = J.Obj(ao); if (a == null || !J.Str(a, "name").EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    i.Url = J.Str(a, "browser_download_url"); string dg = J.Str(a, "digest"); if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) i.Sha = dg.Substring(7).ToLowerInvariant(); break;
                }
                if (i.Tag == "" || i.Url == "") { error = "the release has no zip"; return null; }
                return i;
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; return null; }
        }
        static int[] Parts(string v) { List<int> r = new List<int>(); foreach (string p in (v ?? "").TrimStart('v', 'V').Split('.', '-')) { int n; if (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out n)) r.Add(n); else break; } return r.ToArray(); }
        public static bool Newer(string tag, string current)
        {
            int[] a = Parts(tag), b = Parts(current);
            for (int k = 0; k < Math.Max(a.Length, b.Length); k++) { int x = k < a.Length ? a[k] : 0, y = k < b.Length ? b[k] : 0; if (x != y) return x > y; }
            return false;
        }
        // downloads, verifies and starts the updater script; null = on its way (the caller quits the program), otherwise the reason it did not work
        public static string Install(Info i)
        {
            try
            {
                string exeDir = Env.ExeDir(); string w = Diag.CanWriteDir(exeDir);
                if (w != null) return Lang.T("程序所在文件夹不能写入，没法原地更新：", "The program folder is not writable, so it cannot update in place: ") + exeDir;
                string tmp = Path.Combine(Path.GetTempPath(), "pet-update-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(tmp);
                string zip = Path.Combine(tmp, "update.zip");
                using (WebClient c = Client()) c.DownloadFile(i.Url, zip);
                if (i.Sha != "")
                {
                    string have; using (SHA256 h = SHA256.Create()) using (FileStream fs = File.OpenRead(zip)) have = BitConverter.ToString(h.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                    if (have != i.Sha) { try { Directory.Delete(tmp, true); } catch { } return Lang.T("下载的文件和 GitHub 给出的校验值对不上，已放弃。", "The download does not match the checksum GitHub lists; given up."); }
                }
                string ps = Path.Combine(tmp, "update.ps1");
                File.WriteAllText(ps, string.Join("\r\n", new string[] {
                    "param([int]$WaitPid, [string]$Zip, [string]$Dest, [string]$Tmp)",
                    "$ErrorActionPreference = 'Stop'",
                    "try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue } catch { }",
                    "Start-Sleep -Milliseconds 400",
                    "$x = Join-Path $Tmp 'x'; Expand-Archive -LiteralPath $Zip -DestinationPath $x -Force",
                    "$src = if (Test-Path (Join-Path $x 'pet.exe')) { $x } else { (Get-ChildItem $x -Directory | Select-Object -First 1).FullName }",
                    "Copy-Item -Path (Join-Path $src '*') -Destination $Dest -Recurse -Force",
                    "Start-Process -FilePath (Join-Path $Dest 'pet.exe')",
                    "Start-Sleep 2; Remove-Item -LiteralPath $Tmp -Recurse -Force -ErrorAction SilentlyContinue" }), new UTF8Encoding(true));
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + ps + "\" -WaitPid " + Process.GetCurrentProcess().Id + " -Zip \"" + zip + "\" -Dest \"" + exeDir + "\" -Tmp \"" + tmp + "\"");
                psi.CreateNoWindow = true; psi.UseShellExecute = false; Process.Start(psi);
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }
    }
}
