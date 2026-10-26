param([int]$ProcessId)
# Sends WM_CLOSE to the visible top-level windows of a process, like clicking the window's X button.
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
}
"@
# Only the dashboard window, exactly what a click on its X button closes.
$sent = 0
[Win]::EnumWindows({ param($h, $l) $id = 0; [void][Win]::GetWindowThreadProcessId($h, [ref]$id); $t = New-Object System.Text.StringBuilder 256; [void][Win]::GetWindowText($h, $t, 256); if ($id -eq $ProcessId -and [Win]::IsWindowVisible($h) -and $t.ToString() -eq 'Mission Control') { [void][Win]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero); $script:sent++ }; $true }, [IntPtr]::Zero) | Out-Null
$sent
