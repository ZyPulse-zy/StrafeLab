using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using StrafeLab.Core;
using StrafeLab.Platform;

namespace StrafeLab.UI;
public partial class CapturePage : UserControl
{
    private readonly string _root=new SessionStore().RootDirectory;
    private bool _updating=true;
    private CollectorStatus? _status;
    public CapturePage(){InitializeComponent();Refresh();}
    public void Refresh()
    {
        _updating=true;
        bool test=Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")=="1";
        _status=test?null:CollectorStatus.Read(_root);
        state.Text=_status?.State??"采集未运行";
        memory.Text=_status==null?"—":$"{_status.WorkingSetBytes/1048576d:0.#} MB";
        privateMemory.Text=_status==null?"启动后显示实测值":$"专用内存 {_status.PrivateBytes/1048576d:0.#} MB";
        gsi.Text="GSI · "+(_status?.Gsi??"未连接");hall.Text="Hall · "+(_status?.Hall??"未占用设备");
        inputs.Text=$"本局输入边沿 {_status?.Inputs??0} 条";
        pause.Content=_status?.Paused==true?"恢复采集":"暂停采集";pause.IsEnabled=_status!=null&&!test;start.IsEnabled=_status==null&&!test;
        hallEnabled.IsChecked=_status?.HallEnabled??true;hallEnabled.IsEnabled=_status!=null&&!test;
        startup.IsChecked=!test&&StartupRegistration.IsEnabled;startup.IsEnabled=!test;
        startupDetail.Text=test?"测试模式：自启动与采集操作已隔离":startup.IsChecked==true?"已登记当前用户的登录启动项；Windows 可以在任务管理器中禁用或延迟启动。":"未设置登录启动；也可随时从桌面快捷方式启动。";
        _updating=false;
    }
    private async void Start_Click(object sender,RoutedEventArgs e)
    {await AppLauncher.EnsureCollectorAsync();message.Text="已发送启动请求，状态将在几秒内更新。";}
    private async void Pause_Click(object sender,RoutedEventArgs e)
    {message.Text=await LocalControlServer.SendAsync("collector",_status?.Paused==true?"resume":"pause")=="ok"?"已发送请求；暂停会保存当前记录，恢复后继续。":"采集进程没有响应，请稍后重试。";}
    private async void Stop_Click(object sender,RoutedEventArgs e)
    {if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")=="1")return;message.Text=await LocalControlServer.SendAsync("collector","exit")=="ok"?"正在保存并退出后台采集。":"采集进程未运行。";}
    private void Startup_Changed(object sender,RoutedEventArgs e)
    {
        if(_updating)return;
        try{StartupRegistration.Set(startup.IsChecked==true,Environment.ProcessPath!,_root);message.Text=startup.IsChecked==true?"自启动已开启，登录后进入托盘。":"自启动已关闭。";}
        catch(Exception ex){message.Text="设置失败："+ex.Message;}
        Refresh();
    }
    private async void Hall_Changed(object sender,RoutedEventArgs e)
    {
        if(_updating)return;
        bool enable=hallEnabled.IsChecked==true;
        message.Text=await LocalControlServer.SendAsync("collector",enable?"hall-on":"hall-off")=="ok"?
            "已发送请求，请等待下方 Hall 状态更新后再操作官方驱动。":"采集进程没有响应。";
    }
    private void OpenData_Click(object sender,RoutedEventArgs e)=>Process.Start(new ProcessStartInfo(_root){UseShellExecute=true});
}
