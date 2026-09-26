using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using StrafeLab.Core;
using StrafeLab.Platform;

namespace StrafeLab.UI;
/// <summary>Offline review can coexist with a recorder. No RuntimeService, HID or GSI.</summary>
public sealed class AnalysisWindow : Window
{
    private readonly DemoMonitor _monitor=new(new SessionStore(),new SteamLocator().FindDemoRoots());
    private bool _closing,_ready;
    public AnalysisWindow()
    {
        Title="StrafeLab 1.7 · 战术分析台";Width=Math.Min(1360,SystemParameters.WorkArea.Width-40);Height=Math.Min(960,SystemParameters.WorkArea.Height-40);MinWidth=960;MinHeight=700;
        Background=TacticalDrawing.Background;Content=new AnalysisPage(_monitor);
        SourceInitialized+=(_,_)=>
        {
            var handle=new WindowInteropHelper(this).Handle;int dark=1,caption=0x000A0C0B,ink=0x00DDEBE8;
            _=DwmSetWindowAttribute(handle,20,ref dark,sizeof(int));
            _=DwmSetWindowAttribute(handle,35,ref caption,sizeof(int));
            _=DwmSetWindowAttribute(handle,36,ref ink,sizeof(int));
        };
        Loaded+=async(_,_)=>
        {
            if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1"){_monitor.Start();return;}
            var page=(AnalysisPage)Content;await page.RefreshAsync();
            var directory=Environment.GetEnvironmentVariable("STRAFELAB_DATA_DIR")!;
            await page.CaptureExtraSmokeViewsAsync(directory);
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory,"smoke.json"),
                System.Text.Json.JsonSerializer.Serialize(new{initialized=true,analysisOnly=true,width=ActualWidth,height=ActualHeight}));
            Close();
        };
        Closing+=async(_,e)=>
        {
            if(_ready)return;e.Cancel=true;if(_closing)return;_closing=true;IsEnabled=false;
            try{await _monitor.DisposeAsync();}finally{_ready=true;_ = Dispatcher.BeginInvoke(new Action(Close));}
        };
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
}
