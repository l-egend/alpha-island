$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class MediaDiagnosticWindows {
    public delegate bool Callback(IntPtr handle, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    public static IntPtr[] Read(int[] ids) {
        var result = new List<IntPtr>();
        EnumWindows((h,l) => { uint id; GetWindowThreadProcessId(h, out id); if (Array.IndexOf(ids,(int)id)>=0) result.Add(h); return true; },IntPtr.Zero);
        return result.ToArray();
    }
}
'@
$ids = @(Get-Process cloudmusic -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
foreach ($handle in [MediaDiagnosticWindows]::Read($ids)) {
    try {
        $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
        if ($root.Current.Name.Length -eq 0) { continue }
        Write-Output ('WINDOW class=' + $root.Current.ClassName + ' id=' + $handle)
        $elements = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
        Write-Output ('ELEMENTS=' + $elements.Count)
        foreach ($element in $elements) {
            $name = $element.Current.Name
            if ($name -match '播放|暂停|随机|循环|顺序|shuffle|repeat|pause|play|volume' -or $element.Current.ControlType.ProgrammaticName -match 'Button|Slider') {
                Write-Output ($element.Current.ControlType.ProgrammaticName + ' ' + $name + ' id=' + $element.Current.AutomationId)
            }
        }
    } catch { Write-Output $_.Exception.GetType().Name }
}
