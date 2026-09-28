param([Parameter(Mandatory=$true)][int]$DesktopProcessId)
$ErrorActionPreference = 'Stop'
$process = Get-Process -Id $DesktopProcessId
if ($process.ProcessName -ne 'Lumibelle') { throw 'Expected the disposable Lumibelle desktop process.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class LumibelleWindowTest {
    delegate bool Enumerate(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(Enumerate callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    public static bool Close(uint process) {
        bool sent=false;
        EnumWindows((window,state) => {
            GetWindowThreadProcessId(window,out var owner); var text=new StringBuilder(256); GetWindowText(window,text,text.Capacity);
            if(owner==process && text.ToString()=="Lumibelle") sent=PostMessage(window,0x0010,IntPtr.Zero,IntPtr.Zero);
            return true;
        },IntPtr.Zero);
        return sent;
    }
}
'@
if (-not [LumibelleWindowTest]::Close($DesktopProcessId)) { throw 'No Lumibelle window received the close request.' }
