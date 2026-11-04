# Dev tool: dot-source this to get an isolated environment for pet.exe (nothing of the real user profile is touched):
#   . tools\iso-env.ps1 -Root <empty temp dir>
# HOME/USERPROFILE/APPDATA/TEMP point into <Root>; node is removed from PATH, so what works here works on a PC without Node.
param([Parameter(Mandatory)][string]$Root)
New-Item -ItemType Directory -Force -Path "$Root\home\.claude", "$Root\appdata", "$Root\tmp", "$Root\state", "$Root\startup" | Out-Null
$env:USERPROFILE = "$Root\home"; $env:HOME = "$Root\home"; $env:APPDATA = "$Root\appdata"
$env:TEMP = "$Root\tmp"; $env:TMP = "$Root\tmp"
$env:CLAUDE_COMPANION_DIR = "$Root\state"; $env:PET_DATA = "$Root\appdata\claude-companion-pet"; $env:PET_STARTUP_DIR = "$Root\startup"
$env:PET_LOG = "$Root\pet.log"; $env:PET_INSTANCE = "iso-" + (Split-Path $Root -Leaf)
$env:CLAUDE_CONFIG_DIR = "$Root\home\.claude"
$env:PATH = (($env:PATH -split ';') | Where-Object { $_ -and -not (Test-Path (Join-Path $_ 'node.exe')) -and -not (Test-Path (Join-Path $_ 'npm.cmd')) }) -join ';'
"isolated: $Root ; node on PATH: " + [bool](Get-Command node -ErrorAction SilentlyContinue)
