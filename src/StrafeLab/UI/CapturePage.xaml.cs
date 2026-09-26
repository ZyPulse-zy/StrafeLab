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
    private DemoMonitor? _monitor;
    private CacheCleanupPlan? _cachePlan;
    private bool _cacheBusy;
    private Task _cacheRefreshTask=Task.CompletedTask;
    public CapturePage(){InitializeComponent();Refresh();Loaded+=async(_,_)=>await RefreshCacheAsync();}
    public void SetMonitor(DemoMonitor monitor)=>_monitor=monitor;
    public Task RefreshCacheAsync()=>_cacheBusy?_cacheRefreshTask:_cacheRefreshTask=LoadCacheAsync();
    private async Task LoadCacheAsync()
    {
        if(_cacheBusy)return;
        _cacheBusy=true;cleanCache.IsEnabled=false;refreshCache.IsEnabled=false;
        try
        {
            _cachePlan=await Task.Run(()=>new DemoCacheCleanup(_root).Inspect());
            cacheSize.Text=DemoCacheCleanup.FormatBytes(_cachePlan.CacheBytes);
            cacheReclaimable.Text=DemoCacheCleanup.FormatBytes(_cachePlan.ReclaimableBytes);
            cacheDetail.Text=_cachePlan.Error??$"{_cachePlan.Files.Count} 个文件可清理 · 保留 {DemoCacheCleanup.FormatBytes(_cachePlan.RetainedBytes)} 缓存（同步索引、来源缺失或无法确认的文件）";
            protectedSize.Text=$"录制 {DemoCacheCleanup.FormatBytes(_cachePlan.RecordingBytes)} · 报告 {DemoCacheCleanup.FormatBytes(_cachePlan.ReportBytes)}";
        }
        catch(Exception ex){_cachePlan=null;cacheDetail.Text="读取占用失败："+ex.Message;}
        finally{_cacheBusy=false;refreshCache.IsEnabled=true;cleanCache.IsEnabled=_monitor!=null&&_cachePlan?.Error==null&&_cachePlan?.Files.Count>0;}
    }
    private async void RefreshCache_Click(object sender,RoutedEventArgs e)=>await RefreshCacheAsync();
    private async void CleanCache_Click(object sender,RoutedEventArgs e)
    {
        if(_cacheBusy||_cachePlan==null||_monitor==null)return;
        var preview=_cachePlan;
        if(MessageBox.Show(Window.GetWindow(this),$"预计释放 {DemoCacheCleanup.FormatBytes(preview.ReclaimableBytes)}，共 {preview.Files.Count} 个缓存文件。\n\n原始 Demo、对局记录和分析报告会保留。需要时会重新解压。\n正在使用、来源缺失或预览后发生变化的文件会跳过。",
            "清理解压缓存",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)!=MessageBoxResult.OK)return;
        await RunCleanupAsync(preview);
    }
    private async Task RunCleanupAsync(CacheCleanupPlan preview)
    {
        _cacheBusy=true;cleanCache.IsEnabled=false;refreshCache.IsEnabled=false;cacheFeedback.Text="正在核对并清理…";
        try
        {
            var result=await _monitor!.CleanCacheAsync(preview);
            cacheFeedback.Text=$"已释放 {DemoCacheCleanup.FormatBytes(result.FreedBytes)} · 清理 {result.DeletedFiles} 个文件"+
                (result.SkippedFiles>0?$" · 跳过 {result.SkippedFiles} 个使用中或已变化的文件":"")+
                (result.Error!=null?$"\n{result.Error}":"");
        }
        catch(Exception ex){cacheFeedback.Text="清理未完成："+ex.Message;}
        finally{_cacheBusy=false;refreshCache.IsEnabled=true;await RefreshCacheAsync();}
    }
    public async Task CheckCacheForSmokeAsync()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")throw new InvalidOperationException("仅允许隔离测试。");
        await RefreshCacheAsync();pageScroll.ScrollToTop();
        if(_cachePlan?.Error!=null)throw new InvalidOperationException(_cachePlan.Error);
    }
    public async Task CheckCacheCleanupForSmokeAsync()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")throw new InvalidOperationException("仅允许隔离测试。");
        if(!System.IO.File.Exists(System.IO.Path.Combine(_root,"cache-cleanup-smoke.marker")))return;
        await RefreshCacheAsync();
        if(_cachePlan?.ReclaimableBytes is not >0)throw new InvalidOperationException("清理测试夹具缺失。");
        long expected=_cachePlan.ReclaimableBytes;
        await RunCleanupAsync(_cachePlan);
        cacheSection.BringIntoView();
        if(_cachePlan?.ReclaimableBytes!=0||!cacheFeedback.Text.Contains("已释放"))throw new InvalidOperationException("清理界面未更新。");
        System.IO.File.WriteAllText(System.IO.Path.Combine(_root,"cache-cleanup-smoke.json"),
            System.Text.Json.JsonSerializer.Serialize(new{expected,remaining=_cachePlan.CacheBytes,message=cacheFeedback.Text}));
    }
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
        stop.IsEnabled=_status!=null&&!test;
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
