param([int]$WaitSec = 22)

# Find the gakumas Unity window and click the TAP TO START banner.
# Pure ASCII on purpose: PS 5.1 decodes BOM-less files as GBK.
# Mouse friendly: saves/restores the cursor and skips if the user is moving the mouse.

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class TapStart {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }

  public static IntPtr Find(uint pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr lp) {
      uint wp; GetWindowThreadProcessId(h, out wp);
      if (wp == pid) {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(h, sb, 256);
        if (sb.ToString() == "UnityWndClass") { found = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static bool UserMoving() {
    POINT a; GetCursorPos(out a);
    System.Threading.Thread.Sleep(280);
    POINT b; GetCursorPos(out b);
    return (Math.Abs(a.X - b.X) > 3 || Math.Abs(a.Y - b.Y) > 3);
  }

  public static string Tap(IntPtr h, double rx, double ry) {
    POINT before; GetCursorPos(out before);
    ShowWindow(h, 9);
    SetForegroundWindow(h);
    System.Threading.Thread.Sleep(500);
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w <= 0 || ht <= 0) return "bad-rect";
    int x = r.L + (int)(w * rx), y = r.T + (int)(ht * ry);
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(110);
    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(70);
    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    System.Threading.Thread.Sleep(150);
    SetCursorPos(before.X, before.Y);
    return "tap " + x + "," + y;
  }
}
"@ -ErrorAction SilentlyContinue

Start-Sleep -Seconds $WaitSec

$proc = Get-Process -Name gakumas -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "NO_GAME"; exit 1 }

$h = [TapStart]::Find([uint32]$proc.Id)
if ($h -eq [IntPtr]::Zero) { Write-Output "NO_WINDOW"; exit 2 }

# Do not fight the user: if the cursor is being moved, leave it alone.
if ([TapStart]::UserMoving()) { Write-Output "SKIP_USER_ACTIVE"; exit 3 }

# the banner sits near the bottom in both portrait and landscape layouts
Write-Output ([TapStart]::Tap($h, 0.50, 0.80))
Start-Sleep -Milliseconds 1200
Write-Output ([TapStart]::Tap($h, 0.50, 0.86))
Write-Output "CLICKED"
exit 0
