using System.Diagnostics;
using System.Runtime.InteropServices;
namespace StrafeLab.Platform;
public static class ForegroundGame
{
    private static readonly object Gate=new();
    private static IntPtr _lastWindow;
    private static bool _lastResult;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint id);
    public static bool IsCs2()
    {
        var window=GetForegroundWindow();
        lock(Gate)
        {
            if(window==_lastWindow)return _lastResult;
            _lastWindow=window;GetWindowThreadProcessId(window,out var id);
            try { using var p=Process.GetProcessById((int)id);return _lastResult=p.ProcessName.Equals("cs2",StringComparison.OrdinalIgnoreCase); }
            catch{return _lastResult=false;}
        }
    }
}
