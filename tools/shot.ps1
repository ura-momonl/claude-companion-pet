# Dev tool: screenshot the pet window as it really appears on the desktop, without other windows in the picture.
#   powershell -File tools/shot.ps1 -ProcessId <pet pid> -Out file.png [-Backdrop '#d9dde6'] [-Margin 0]
# PrintWindow does not work for the pet: it is a per-pixel-alpha layered window (UpdateLayeredWindow), and PrintWindow
# returns a blank bitmap for those (checked: it comes back uniformly light grey). So: put a flat-colour, non-activating,
# top-most backdrop window exactly under the pet (above every other window, below the pet), copy that screen rectangle,
# and close the backdrop. What is captured is the real desktop composition of the pet over the flat colour; nothing the
# user has open can be in the picture because the backdrop covers the whole rectangle. The backdrop is up for ~1 s.
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Out, [string]$Title = 'claude-companion-pet', [string]$Backdrop = '#d9dde6', [int]$Margin = 0, [int]$Hold = 350)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Windows.Forms;
public class PW {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(uint pid, string title) {
    IntPtr res = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (sb.ToString() == title) { res = h; return false; } } return true; }, IntPtr.Zero);
    return res;
  }
}
public class Backdrop : Form {
  protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 8; return cp; } } // toolwindow, noactivate, topmost
  protected override bool ShowWithoutActivation { get { return true; } }
}
'@
[void][PW]::SetProcessDPIAware()
$h = [PW]::Find([uint32]$ProcessId, $Title)
if ($h -eq [IntPtr]::Zero) { Write-Output 'NOWINDOW'; exit 3 }
$r = New-Object PW+RECT; [void][PW]::GetWindowRect($h, [ref]$r)
$x = $r.L - $Margin; $y = $r.T - $Margin; $w = $r.R - $r.L + 2 * $Margin; $ht = $r.B - $r.T + 2 * $Margin
$bd = New-Object Backdrop
$bd.FormBorderStyle = 'None'; $bd.ShowInTaskbar = $false; $bd.StartPosition = 'Manual'; $bd.BackColor = [System.Drawing.ColorTranslator]::FromHtml($Backdrop)
$bd.SetBounds($x, $y, $w, $ht); $bd.Show(); [System.Windows.Forms.Application]::DoEvents()
# the pet goes back on top of the backdrop (both are top-most; the one raised last is higher): NOMOVE|NOSIZE|NOACTIVATE
[void][PW]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x13)
Start-Sleep -Milliseconds $Hold
$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size); $g.Dispose()
$bd.Close()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output "OK rect=$($r.L),$($r.T) ${w}x${ht}"
