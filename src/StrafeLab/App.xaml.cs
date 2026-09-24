using System.IO;
using System.Windows;
namespace StrafeLab;
public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        bool smoke=e.Args.Length>=2 && e.Args[0]=="--smoke";
        if(smoke)
        {
            Environment.SetEnvironmentVariable("STRAFELAB_TEST_MODE","1");
            Environment.SetEnvironmentVariable("STRAFELAB_DATA_DIR",Path.GetFullPath(e.Args[1]));
        }
        _instance=new Mutex(true,smoke?"Local\\StrafeLab-smoke":"Local\\StrafeLab-app",out var created);
        if(!created){if(smoke){Shutdown(2);return;}MessageBox.Show("StrafeLab 已在运行。","StrafeLab");Shutdown();return;}
        DispatcherUnhandledException+=(_,a)=>{
            var dir=Environment.GetEnvironmentVariable("STRAFELAB_DATA_DIR")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StrafeLab");
            Directory.CreateDirectory(dir);File.AppendAllText(Path.Combine(dir,"errors.log"),DateTime.UtcNow+" "+a.Exception+Environment.NewLine);
            MessageBox.Show("StrafeLab 遇到异常，详情已写入 errors.log。","StrafeLab");a.Handled=true;
            if(MainWindow!=null)MainWindow.Close();else Shutdown(1);
        };
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e){_instance?.Dispose();base.OnExit(e);}
}
