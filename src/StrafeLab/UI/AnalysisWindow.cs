using System.Windows;
using System.Windows.Media;
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
        Title="StrafeLab 1.5 · 分析与统计";Width=Math.Min(1320,SystemParameters.WorkArea.Width-40);Height=Math.Min(940,SystemParameters.WorkArea.Height-40);MinWidth=1040;MinHeight=700;
        Background=new SolidColorBrush(Color.FromRgb(18,22,25));Content=new AnalysisPage(_monitor);
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
}
