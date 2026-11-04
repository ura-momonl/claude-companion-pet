# Builds the portable package: dist\claude-companion-pet\ (pet.exe + skins + README.txt) and dist\claude-companion-pet.zip.
#   powershell -File tools\package.ps1
param([string]$ZipName = 'claude-companion-pet.zip')   # file name of the zip inside dist (the folder it was made from stays in dist_stageclaude-companion-pet)
$ErrorActionPreference = 'Stop'
$pet = Split-Path $PSScriptRoot -Parent
Set-Location $pet
& cmd.exe /c ".\build.cmd"
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
# staged apart from dist\claude-companion-pet: a pet.exe that is running from there (the user's copy) keeps its folder untouched
$dist = Join-Path $pet 'dist'; $dir = Join-Path $dist '_stage\claude-companion-pet'
if (Test-Path (Join-Path $dist '_stage')) { [IO.Directory]::Delete((Join-Path $dist '_stage'), $true) }
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item pet.exe $dir
Copy-Item prices.json $dir   # the price table the usage module reads (next to pet.exe); without it every amount would be marked unpriced
Copy-Item skins $dir -Recurse
Copy-Item skins\README.md "$dir\skins\README.md" -Force
$txt = @'
claude-companion-pet  /  桌面小人

中文
1. 把整个文件夹解压到任意位置（别放在会被清理的临时文件夹里），双击 pet.exe。
2. 按窗口里的三步走：选样子 -> 点“连接 Claude Code” -> 完成。
3. 新开一个 Claude Code 会话。它需要你的时候，小人会招手并冒出气泡。不弹窗。
小人需要你时冒气泡。小人旁边没有绑着面板：屏幕上另有一个独立的悬浮窗（默认在右下角，按住拖到哪里它就留在哪里；右键小人 -> “显示悬浮窗”可开关；默认是毛玻璃底，系统不支持时自动改成实色，也可在右键菜单关掉），平时只显示各账号剩余的额度；鼠标停在上面会展开成完整面板（顶部三个页签：剩余 / 上下文 / 用量），点一下固定、再点一下或点别处收起；面板里 ⓘ 上停一下会浮出说明。“剩余”页是 Codex 等账号实际报告的额度和会话；“上下文”页看某个会话已用多少 token（窗口大小不知道时不画百分比）；“用量”页是最近 今天 / 7 天 / 30 天的 token 和等值 API 价（不是订阅账单，未计价的部分会写明，统计还在进行时会标“统计中”）。点“回到窗口”或那一行就回到那个终端窗口（后台会话和还不能对应到窗口的 Codex 会话会标明原因，不能跳转）。任务忙超过 2 分钟做完才庆祝（右键 -> “长任务完成才庆祝”可改，提示音默认关）。
出问题时：向导窗口左下角点“打开日志文件夹”，或打开 %APPDATA%\claude-companion-pet\pet.log（程序运行记录；只含路径和错误原因，不含设置内容、令牌或对话），把它发给我。
不想用了：右键小人 -> “断开 Claude Code 连接”，设置一键还原；然后删除文件夹即可。
换皮肤：把皮肤文件夹放进 skins 里，右键 -> 皮肤；做法见 skins\README.md。
额度、用量和 Codex 会话是只读的：程序只读取本机 Claude / Codex 的日志和 Codex 自己报告的额度，不改它们。价格表是 pet.exe 旁边的 prices.json。
不需要安装 Node.js、不需要管理员权限。需要 Windows 10/11（自带 .NET Framework 4）。
第一次双击时若 Windows 弹出“已保护你的电脑”（程序没有付费签名）：点“更多信息” -> “仍要运行”。从网上下载的 zip 可先在 zip 文件上右键 -> 属性 -> 勾选“解除锁定”再解压，就不会弹。
Codex 额度和会话只在本机装了并登录过 Codex 时才有；没有的话该行会写没读到，不影响其他功能。

English
1. Unzip the whole folder anywhere (not a temp folder that gets cleaned up) and double-click pet.exe.
2. Follow the three steps in the window: pick a look -> "Connect Claude Code" -> Finish.
3. Start a new Claude Code session. When it needs you the pet waves and a speech bubble appears. No pop-ups.
The pet shows a speech bubble when it needs you. The panel is not attached to the pet: there is a separate floating window on the screen (bottom-right at first; drag it anywhere and it stays there; right-click the pet -> "Show the floating window" turns it on or off; frosted glass by default, a solid card when the system cannot do it, or switch it off in the menu). It normally shows only the remaining quota of each account; rest the pointer on it and it opens into the full panel (three tabs: Remaining / Context / Usage); click to keep it open, click again or elsewhere to fold it. Rest the pointer on an (i) for the explanation. Remaining = the quota windows each account actually reports, then the sessions; Context = how many tokens one session has used (no percentage while the window size is unknown); Usage = tokens and API-equivalent price for today / 7 / 30 days (not a subscription bill; unpriced parts are stated, and a count still in progress says so). Click "Open window" or the row to go back to that terminal window (a background session has no terminal window: it is marked as such and is not a link). It only celebrates a task that worked longer than 2 minutes (right-click -> "Celebrate only long tasks"; sound is off by default).
If something goes wrong: click "Open the log folder" in the guide window, or open %APPDATA%\claude-companion-pet\pet.log (a record of what the program did: paths and error reasons only, never your settings, tokens or conversations).
To remove: right-click the pet -> "Disconnect from Claude Code" (your settings are restored in one click), then delete the folder.
Skins: put a skin folder into "skins", then right-click -> Skin; see skins\README.md.
Quota, usage and Codex sessions are read-only: the program only reads the local Claude / Codex logs and the quota Codex itself reports. The price table is prices.json next to pet.exe.
No Node.js and no administrator rights needed. Windows 10/11 (the built-in .NET Framework 4).
If Windows shows "Windows protected your PC" the first time (the program is not paid-signed): click "More info" -> "Run anyway". For a zip downloaded from the web, right-click the zip -> Properties -> tick "Unblock" before extracting and it will not appear.
Codex quota and sessions only appear if Codex is installed and signed in on this PC; otherwise that row says it could not be read and nothing else is affected.
'@
[IO.File]::WriteAllText("$dir\README.txt", $txt, (New-Object Text.UTF8Encoding $true))
$zip = Join-Path $dist $ZipName
if (Test-Path $zip) { Remove-Item $zip }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($dir, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$size = (Get-Item $zip).Length; $raw = (Get-ChildItem $dir -Recurse -File | Measure-Object Length -Sum).Sum
"zip: {0:N0} KB ({1:N2} MB), unpacked: {2:N2} MB, pet.exe: {3:N0} KB" -f ($size / 1KB), ($size / 1MB), ($raw / 1MB), ((Get-Item pet.exe).Length / 1KB)
