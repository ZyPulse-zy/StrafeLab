using System.IO;
using System.Windows;
using StrafeLab.Core;
using StrafeLab.Platform;
using StrafeLab.UI;

namespace StrafeLab;
public partial class App : Application
{
    private Mutex? _instance;
    private CollectorHost? _collector;
    private LocalControlServer? _dashboardControl;
    private bool _exiting;
    protected override async void OnStartup(StartupEventArgs e)
    {
        bool smoke=e.Args.Length>=2&&e.Args[0]=="--smoke";
        bool collector=e.Args.Contains("--collector");
        bool worker=e.Args.Contains("--analysis-worker");
        bool realtime=e.Args.Contains("--realtime");
        if(smoke)
        {
            Environment.SetEnvironmentVariable("STRAFELAB_TEST_MODE","1");
            Environment.SetEnvironmentVariable("STRAFELAB_DATA_DIR",Path.GetFullPath(e.Args[1]));
        }
        base.OnStartup(e);
        if(worker)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try{await BackgroundDemoWorker.RunAsync();Shutdown();}
            catch(Exception ex){Log(ex);Shutdown(1);}return;
        }
        _instance=new Mutex(true,smoke?@"Local\StrafeLab-smoke":collector||realtime?@"Local\StrafeLab-app":@"Local\StrafeLab-analysis",out var created);
        if(!created)
        {
            if(realtime)MessageBox.Show("后台采集已在运行。请先从托盘退出采集，再打开实时诊断窗口。","StrafeLab");
            if(!collector&&!realtime&&!smoke)
            {
                await LocalControlServer.SendAsync("dashboard","show");
                if(!e.Args.Contains("--analysis-only"))await AppLauncher.EnsureCollectorAsync();
            }
            Shutdown(smoke?2:0);return;
        }
        DispatcherUnhandledException+=(_,a)=>
        {
            Log(a.Exception);a.Handled=true;
            if(smoke||collector){Dispatcher.BeginInvoke(new Action(()=>Shutdown(1)));return;}
            MessageBox.Show("StrafeLab 遇到异常，详情已写入 errors.log。","StrafeLab");
            if(MainWindow!=null)MainWindow.Close();else Shutdown(1);
        };
        if(collector)
        {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try{_collector=new CollectorHost();await _collector.StartAsync();}
            catch(Exception ex){Log(ex);await ExitCollectorAsync();}return;
        }
        MainWindow=realtime?new MainWindow():new AnalysisWindow();
        if(!smoke&&!realtime)
        {
            _dashboardControl=new LocalControlServer("dashboard",async command=>
            {
                if(command!="show")return "invalid";
                await Dispatcher.InvokeAsync(()=>{MainWindow.Show();MainWindow.WindowState=WindowState.Normal;MainWindow.Activate();});
                return "ok";
            });
        }
        if(smoke){MainWindow.ShowActivated=false;MainWindow.ShowInTaskbar=false;MainWindow.WindowStartupLocation=WindowStartupLocation.Manual;MainWindow.Left=-20000;MainWindow.Top=-20000;}
        if(e.Args.Contains("--minimized"))MainWindow.WindowState=WindowState.Minimized;
        MainWindow.Show();
        if(!smoke&&!realtime&&!e.Args.Contains("--analysis-only"))await AppLauncher.EnsureCollectorAsync();
    }
    public async Task ExitCollectorAsync()
    {
        if(_exiting)return;_exiting=true;
        try{if(_collector!=null)await _collector.DisposeAsync();}
        catch(Exception ex){Log(ex);}
        finally{_collector=null;Shutdown();}
    }
    private static void Log(Exception exception)
    {try{var dir=new SessionStore().RootDirectory;File.AppendAllText(Path.Combine(dir,"errors.log"),DateTime.UtcNow+" "+exception+Environment.NewLine);}catch{}}
    protected override void OnExit(ExitEventArgs e)
    {
        _collector?.CheckpointForSessionEnd();
        if(_dashboardControl!=null)_=_dashboardControl.DisposeAsync();
        _instance?.Dispose();base.OnExit(e);
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {_collector?.CheckpointForSessionEnd();base.OnSessionEnding(e);}
}
