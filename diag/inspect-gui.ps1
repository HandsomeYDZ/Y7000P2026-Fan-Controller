$ErrorActionPreference = 'Stop'
trap { $_ | Out-String | Set-Content (Join-Path $PSScriptRoot 'inspect-gui-error.log'); exit 1 }
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class GuiWindow {
  public delegate bool Callback(IntPtr hwnd, IntPtr param);
  [DllImport("user32.dll")] public static extern bool EnumWindows(Callback callback, IntPtr param);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
$gui = Get-Process -Name 'Lenovo Fan Controller' | Sort-Object StartTime -Descending | Select-Object -First 1
$script:guiHandle = [IntPtr]::Zero
[GuiWindow]::EnumWindows({param($handle,$unused)
    [uint32]$windowProcessId = 0
    [void][GuiWindow]::GetWindowThreadProcessId($handle,[ref]$windowProcessId)
    if ($windowProcessId -eq $gui.Id) {
        $title = [Text.StringBuilder]::new(256)
        [void][GuiWindow]::GetWindowText($handle,$title,256)
        if ($title.ToString() -eq 'Legion Fan Controller') { $script:guiHandle = $handle }
    }
    return $true
}, [IntPtr]::Zero) | Out-Null
if ($script:guiHandle -eq [IntPtr]::Zero) { throw 'Main application window not found.' }
[void][GuiWindow]::ShowWindow($script:guiHandle, 5)
[void][GuiWindow]::SetForegroundWindow($script:guiHandle)
Start-Sleep -Milliseconds 600
$root = [System.Windows.Automation.AutomationElement]::FromHandle($script:guiHandle)
$items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
$items | ForEach-Object { [pscustomobject]@{ Name=$_.Current.Name; Id=$_.Current.AutomationId; Type=$_.Current.ControlType.ProgrammaticName; Bounds=$_.Current.BoundingRectangle.ToString() } } | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $PSScriptRoot 'gui-inspection.json')
Add-Type -AssemblyName System.Drawing
$rect = $root.Current.BoundingRectangle
$bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width, [int]$rect.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $PSScriptRoot 'gui-preview.png'))
} finally { $graphics.Dispose(); $bitmap.Dispose() }
