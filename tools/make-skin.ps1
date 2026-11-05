# Make a pet skin from GIFs / PNG pictures / folders of PNG pictures. Needs nothing but Windows PowerShell (no ffmpeg, no Node).
#
#   powershell -File make-skin.ps1 -Name mycat idle.gif busy.gif done.gif
#   powershell -File make-skin.ps1 -Name mycat C:\art\walk_frames\ -State busy
#
# What each input becomes (the state comes from the file name):
#   idle.gif / busy.png / C:\art\approval\          -> that state (idle busy approval answer done error quota,
#                                                     plus lift held drop happy sleepy asleep wake, see skins\README.md)
#   idle-stretch.gif, idle_2.gif                    -> one more variant of idle (the pet plays one, rests, plays another)
# A file name that is not a state name needs -State (it then applies to all inputs given in that run).
#
# The skin is written to %APPDATA%\claude-companion-pet\skins\<name>\  (right-click the pet > Skin shows it at once).
#   -Out <dir>    write the skin folder there instead
#   -Size <px>    shrink pictures taller or wider than this (default 160; 0 = keep; never enlarges). 128 px art looks best unscaled.
#   -Pixel        pixel-art mode: shrink with nearest-neighbour (no blur). Use -Scale 2 or more to show small art bigger.
#   -Scale <n>    screen pixels per picture pixel written to skin.txt (default 1)
#   -FrameMs <n>  milliseconds per picture (default: the GIF's own delay, else 120)
[CmdletBinding(PositionalBinding = $false)]
param(
  [Parameter(Mandatory = $true)][string]$Name,
  [string]$Out,
  [int]$Size = 160,
  [switch]$Pixel,
  [double]$Scale = 1,
  [int]$FrameMs = 0,
  [string]$State,
  [Parameter(ValueFromRemainingArguments = $true)][string[]]$Inputs
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$known = 'idle', 'busy', 'approval', 'answer', 'done', 'error', 'quota', 'lift', 'held', 'drop', 'happy', 'sleepy', 'asleep', 'wake', 'rest'
if (-not $Inputs -or $Inputs.Count -eq 0) { throw 'Give at least one GIF, PNG or folder of PNG pictures (see the header of this script).' }
if ($Name -notmatch '^[\w\- ]+$') { throw 'Name: letters, digits, space, - and _ only.' }
$id = ($Name.Trim() -replace ' ', '-').ToLowerInvariant()
if (-not $Out) { $Out = Join-Path $env:APPDATA "claude-companion-pet\skins\$id" }
if (Test-Path $Out) { throw "$Out already exists. Pick another -Name or delete that folder." }

function Resize-Frame([System.Drawing.Image]$img) {
  $w = $img.Width; $h = $img.Height
  $big = [Math]::Max($w, $h)
  if ($Size -gt 0 -and $big -gt $Size) { $f = $Size / $big; $w = [Math]::Max(1, [int][Math]::Round($w * $f)); $h = [Math]::Max(1, [int][Math]::Round($h * $f)) }
  $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CompositingMode = 'SourceCopy'
  if ($Pixel) { $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half' } else { $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality' }
  $g.DrawImage($img, 0, 0, $w, $h); $g.Dispose()
  return $bmp
}

$delays = New-Object System.Collections.Generic.List[int]
$summary = @()
function Save-Frames($frames, [string]$dir) {
  New-Item -ItemType Directory -Force $dir | Out-Null
  $n = 0
  foreach ($b in $frames) { $b.Save((Join-Path $dir ('f{0:D3}.png' -f $n)), [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); $n++ }
  return $n
}

foreach ($in in $Inputs) {
  $path = (Resolve-Path -LiteralPath $in).Path
  $isDir = Test-Path -LiteralPath $path -PathType Container
  $base = if ($isDir) { Split-Path $path -Leaf } else { [IO.Path]::GetFileNameWithoutExtension($path) }
  $st = $State; $variant = $null
  if (-not $st) {
    $m = [regex]::Match($base.ToLowerInvariant(), '^([a-z]+)(?:[-_ .]+(.+))?$')
    if ($m.Success -and $known -contains $m.Groups[1].Value) { $st = $m.Groups[1].Value; if ($m.Groups[2].Success) { $variant = $m.Groups[2].Value } }
    else { throw "'$base' is not a state name (idle busy approval answer done error quota ...). Rename it, e.g. idle-$base, or pass -State." }
  } elseif ($base.ToLowerInvariant() -ne $st) { $variant = ($base -replace '[^\w\-]', '_') }
  $dest = Join-Path $Out $st
  if ($variant) { $dest = Join-Path $dest $variant }

  $frames = New-Object System.Collections.Generic.List[System.Drawing.Bitmap]
  if ($isDir) {
    $files = Get-ChildItem -LiteralPath $path -File | Where-Object { $_.Extension -match '^\.(png|gif|jpg|jpeg|bmp)$' } | Sort-Object Name
    if (-not $files) { throw "No pictures in $path" }
    foreach ($f in $files) { $img = [System.Drawing.Image]::FromFile($f.FullName); $frames.Add((Resize-Frame $img)); $img.Dispose() }
  } else {
    $img = [System.Drawing.Image]::FromFile($path)
    try {
      $dim = New-Object System.Drawing.Imaging.FrameDimension $img.FrameDimensionsList[0]
      $count = $img.GetFrameCount($dim)
      $d = $null
      if ($count -gt 1) { try { $d = $img.GetPropertyItem(0x5100).Value } catch { } }
      for ($i = 0; $i -lt $count; $i++) {
        [void]$img.SelectActiveFrame($dim, $i)
        $frames.Add((Resize-Frame $img))
        if ($d -and $d.Length -ge 4 * ($i + 1)) { $delays.Add([BitConverter]::ToInt32($d, 4 * $i) * 10) }
      }
    } finally { $img.Dispose() }
  }
  if ($frames.Count -gt 80) { Write-Warning "$base has $($frames.Count) pictures; the pet keeps them all in memory. 80 or fewer is plenty." }
  $w = $frames[0].Width; $h = $frames[0].Height
  $cnt = Save-Frames $frames $dest
  $summary += ('{0,-10} {1,3} picture(s) {2}x{3}  <- {4}' -f $st, $cnt, $w, $h, (Split-Path $path -Leaf))
}

if (-not (Test-Path (Join-Path $Out 'idle'))) {
  Remove-Item -Recurse -Force $Out
  throw 'A skin needs an idle state (idle.gif or idle.png): every state without its own pictures shows idle.'
}
$ms = $FrameMs
if ($ms -le 0) {
  $good = @($delays | Where-Object { $_ -ge 20 } | Sort-Object)
  $ms = if ($good.Count) { $good[[int]($good.Count / 2)] } else { 120 }
}
$gifMode = if ($delays.Count) { 'loop' } else { 'pingpong' }
$txt = @"
name=$($Name.Trim())
scale=$Scale
frame_ms=$ms
mode=$gifMode
"@
[IO.File]::WriteAllText((Join-Path $Out 'skin.txt'), $txt, (New-Object Text.UTF8Encoding $false))
"Skin '$($Name.Trim())' written to $Out"
$summary
"frame_ms=$ms, mode=$gifMode. Right-click the pet > Skin to pick it. Edit skin.txt to change speed or looping (see skins\README.md)."
