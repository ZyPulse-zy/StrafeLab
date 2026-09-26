using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using StrafeLab.Core;
using StrafeLab.Platform;

namespace StrafeLab.UI;
public partial class AnalysisPage : UserControl
{
    private readonly DemoMonitor _monitor;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(4)};
    private IReadOnlyList<MatchReport> _reports=[];
    private List<ActionReview> _visible=[];
    private bool _updating=true,_refreshing;
    private string _reportStamp="";
    private Task _navigationTask=Task.CompletedTask;
    public AnalysisPage(DemoMonitor monitor)
    {
        _monitor=monitor;InitializeComponent();
        navOverview.IsChecked=true;
        progressPage.OpenMatch+=OpenProgressMatch;
        dateFilter.ItemsSource=new[]{"全部日期","最近 7 天","最近 30 天"};dateFilter.SelectedIndex=0;
        stanceFilter.ItemsSource=new[]{"全部姿态","站姿","慢走","蹲姿","蹲起变化","姿态未知"};stanceFilter.SelectedIndex=0;
        speedFilter.ItemsSource=new[]{"全部起速","起速 <50","起速 50–150","起速 ≥150","起速未知"};speedFilter.SelectedIndex=0;
        cohortFilter.ItemsSource=new[]{"明细：可评估反向动作","明细：全部候选","明细：蹲伏 / 蹲起","明细：慢走","明细：暂不评估"};cohortFilter.SelectedIndex=0;
        _timer.Tick+=async(_,_)=>await RefreshAsync();
        Loaded+=async(_,_)=>{_timer.Start();await RefreshAsync();};
        Unloaded+=(_,_)=>_timer.Stop();
    }
    public async Task RefreshAsync()
    {
        if(_refreshing)return;_refreshing=true;
        try
        {
            var reports=await Task.Run(_monitor.Reports);var state=_monitor.Snapshot();
            _updating=true;
            var stamp=string.Join("|",reports.Select(r=>r.SessionId+r.UpdatedAtUtc.Ticks));
            bool changed=stamp!=_reportStamp||mapFilter.Items.Count==0;
            if(changed)
            {
            _reportStamp=stamp;_reports=reports;progressPage.SetReports(reports);
            SetItems(mapFilter,new[]{"全部地图"}.Concat(reports.Select(r=>r.Map).Distinct().Order()).ToArray());
            SetItems(weaponFilter,new[]{"全部武器"}.Concat(reports.SelectMany(r=>r.Actions).Select(a=>a.Weapon).Distinct().Order()).ToArray());
            var selected=sessionFilter.SelectedValue as string;
            sessionFilter.ItemsSource=new[]{new SessionChoice("","全部对局")}.Concat(reports.Select(r=>new SessionChoice(r.SessionId,$"{r.LocalTime}  {r.Map}"))).ToArray();
            sessionFilter.SelectedValue=selected??"";if(sessionFilter.SelectedIndex<0)sessionFilter.SelectedIndex=0;
            }
            var root=rootList.SelectedItem as string;rootList.ItemsSource=state.Roots;rootList.SelectedItem=root;
            var job=(jobGrid.SelectedItem as DemoJob)?.Path;
            jobGrid.ItemsSource=state.Jobs.OrderByDescending(j=>j.WrittenUtc).ToArray();jobGrid.SelectedItem=state.Jobs.FirstOrDefault(j=>j.Path==job);
            enabledBox.IsChecked=state.Enabled;enabledBox.IsEnabled=!_monitor.IsReadOnly;backgroundStatus.Text=_monitor.Status;
            capturePage.Refresh();
            var capture=Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")=="1"?null:CollectorStatus.Read(new SessionStore().RootDirectory);
            captureBadge.Content=capture==null?"采集未运行":capture.Paused?"采集已暂停":"后台采集已开启";
            _updating=false;if(changed)Render();
        }
        catch(Exception ex){backgroundStatus.Text="读取报告失败："+ex.Message;}
        finally{_updating=false;_refreshing=false;}
    }
    private static void SetItems(ComboBox box,string[] items)
    {var selected=box.SelectedItem as string;box.ItemsSource=items;box.SelectedItem=selected;if(box.SelectedIndex<0)box.SelectedIndex=0;}
    private static string Number(double? value)=>value?.ToString("F1",CultureInfo.CurrentCulture)??"—";
    private void Render()
    {
        if(_updating)return;
        var since=dateFilter.SelectedIndex switch {1=>DateTime.Now.Date.AddDays(-6),2=>DateTime.Now.Date.AddDays(-29),_=>DateTime.MinValue};
        var map=mapFilter.SelectedItem as string;var weapon=weaponFilter.SelectedItem as string;var session=sessionFilter.SelectedValue as string;
        var reports=_reports.Where(r=>r.StartedAtUtc.ToLocalTime()>=since&&(map=="全部地图"||map==r.Map)).ToArray();
        bool Include(ActionReview a)=>(weapon=="全部武器"||weapon==a.Weapon)&&
            (stanceFilter.SelectedIndex==0||stanceFilter.SelectedItem as string==a.Stance)&&
            (speedFilter.SelectedIndex==0||speedFilter.SelectedItem as string==a.InitialSpeedBand);
        var selectedReports=reports.Where(r=>string.IsNullOrEmpty(session)||r.SessionId==session).ToArray();
        var candidates=selectedReports.SelectMany(r=>r.Actions).Where(Include).OrderByDescending(a=>a.LocalTime).ToList();
        var eligible=CounterStrafeCohort.Eligible(candidates);
        _visible=candidates.Where(a=>cohortFilter.SelectedIndex switch
        {0=>a.EligibleForStats,2=>a.ContextTags.Contains("crouch"),3=>a.ContextTags.Contains("walk"),4=>!a.EligibleForStats,_=>true}).ToList();
        sampleCount.Text=eligible.Count.ToString();lowRate.Text=candidates.Count.ToString();
        lowDetail.Text=$"{candidates.Count(a=>a.DemoTick.HasValue)} 已匹配 · {candidates.Count-eligible.Count} 暂不评估";
        overlapMedian.Text=Number(MatchAnalysis.Median(eligible.Select(a=>a.OverlapMs)))+" ms";
        gapMedian.Text="双键空档 "+Number(MatchAnalysis.Median(eligible.Select(a=>a.GapMs)))+" ms";
        timingMedian.Text=Number(MatchAnalysis.Median(eligible.Select(a=>(double?)a.ClickMs)))+" ms";
        cohortText.Text=$"初速 ≥34 u/s 才计入：取反向前三 tick 的水平速度中位数。慢走、蹲伏和蹲起不直接排除。{eligible.Count} 个动作通过初速、原方向运动及输入检查；按武器、姿态和起速分组，展示速度变化。";
        exclusionText.Text=string.Join(" · ",candidates.SelectMany(a=>a.ExclusionReasons).GroupBy(r=>r).OrderByDescending(g=>g.Count()).Select(g=>$"{CounterStrafeCohort.ReasonText(g.Key)} {g.Count()}"))+
            $"\n所选对局全武器：无反向关联点击 {selectedReports.Sum(r=>r.UnassociatedShotCount)}；输入核对未通过 {selectedReports.Sum(r=>r.ExcludedCount)}。排除原因可重叠，均不计急停分母。";
        directionGrid.ItemsSource=eligible.GroupBy(a=>a.Direction).OrderBy(g=>g.Key).Select(g=>new{Direction=g.Key,Count=g.Count(),
            Gap=Number(MatchAnalysis.Median(g.Select(a=>a.GapMs))),Overlap=Number(MatchAnalysis.Median(g.Select(a=>a.OverlapMs))),
            Hold=Number(MatchAnalysis.Median(g.Select(a=>a.HoldMs))),Click=Number(MatchAnalysis.Median(g.Select(a=>(double?)a.ClickMs))),
            Drop=Number(MatchAnalysis.Median(g.Select(a=>a.AxisSpeedDrop)))}).ToArray();
        groupGrid.ItemsSource=CounterStrafeCohort.Groups(candidates);
        _updating=true;matchGrid.ItemsSource=reports;_updating=false;actionGrid.ItemsSource=_visible;
        var trend=reports.OrderBy(r=>r.StartedAtUtc).Where(r=>r.Actions.Any(Include)).TakeLast(12).Select(r=>
            new TrendPoint(r.StartedAtUtc.ToLocalTime().ToString("MM-dd HH:mm"),r.Alignment?.IsReliable==true?CounterStrafeCohort.Eligible(r.Actions.Where(Include)).Count:null,MatchAnalysis.Median(CounterStrafeCohort.Eligible(r.Actions.Where(Include)).Select(a=>a.OverlapMs)))).ToArray();
        trendChart.SetPoints(trend);trendText.Text=$"当前日期、地图、武器、姿态、起速条件下最近 {trend.Length} 局；时长及速度变化均为描述性中位数，不是成功率。";
        actionDetail.Text=_visible.Count==0?"暂无该组动作。切换全部候选可查看原始时序与暂不评估原因。":"选择动作查看姿态、速度变化和上下文。";
        actionStory.Text="点击上方一条动作，看按键到开枪的过程。";inputTimeline.Show(null);
        RenderActionDetail();
    }
    private void OpenProgressMatch(string sessionId,string? key)
    {
        _updating=true;dateFilter.SelectedIndex=0;mapFilter.SelectedIndex=0;sessionFilter.SelectedValue=sessionId;
        var parts=key?.Split('|');
        if(parts?.Length==3){weaponFilter.SelectedItem=parts[0];stanceFilter.SelectedItem=parts[1];speedFilter.SelectedItem=parts[2];}
        else{weaponFilter.SelectedIndex=0;stanceFilter.SelectedIndex=0;speedFilter.SelectedIndex=0;}
        cohortFilter.SelectedIndex=0;_updating=false;Render();navDetails.IsChecked=true;
        actionGrid.SelectedIndex=_visible.Count>0?0:-1;
        _navigationTask=ScrollToActionAsync();
    }
    private async Task ScrollToActionAsync()
    {
        // Tab content needs layout before its ScrollableHeight is available.
        await Dispatcher.InvokeAsync(()=>Window.GetWindow(this)?.UpdateLayout(),DispatcherPriority.Loaded);
        await Dispatcher.InvokeAsync(()=>statsScroll.ScrollToVerticalOffset(actionGrid.TranslatePoint(new Point(0,0),(UIElement)statsScroll.Content).Y-12),DispatcherPriority.Background);
        await Dispatcher.InvokeAsync(()=>statsScroll.UpdateLayout(),DispatcherPriority.Background);
    }
    private void Filter_Changed(object sender,SelectionChangedEventArgs e){if(!_updating)Render();}
    private void Nav_Checked(object sender,RoutedEventArgs e)
    {
        if(tabs==null||sender is not RadioButton button)return;
        int index=int.Parse((string)button.Tag);
        pageTitle.Text=new[]{"趋势总览","键盘参数","对局明细","Demo 资料库","采集设置"}[index];
        pageSubtitle.Text=new[]{"看交接习惯的变化，再决定是否调参数","记录实际使用的方案，用同类对局做前后对比","从对局到动作，核对每一次按键与开枪","下载后的录像在这里排队、匹配和分析","管理托盘采集、自启动和资源占用"}[index];
        tabs.SelectedItem=index switch{2=>detailTab,3=>jobsTab,4=>captureTab,_=>progressTab};
        if(index<2)progressPage.ShowParameters(index==1);
    }
    private void CaptureBadge_Click(object sender,RoutedEventArgs e)=>navCapture.IsChecked=true;
    private void Match_Selected(object sender,SelectionChangedEventArgs e)
    {if(!_updating&&matchGrid.SelectedItem is MatchReport r)sessionFilter.SelectedValue=r.SessionId;}
    private void Action_Selected(object sender,SelectionChangedEventArgs e)
        =>RenderActionDetail();
    private void RenderActionDetail()
    {
        if(actionGrid.SelectedItem is not ActionReview a)return;
        var handoff=a.OverlapMs>0?$"相反两键同时按住 {Number(a.OverlapMs)} ms":a.GapMs.HasValue?$"松原键后隔 {Number(a.GapMs)} ms 按反向键":"两键交接时长缺失";
        actionStory.Text=$"第 {a.RoundText} 回合 · {ProgressAnalysis.WeaponName(a.Weapon)} · {a.Direction}（{a.Stance}）\n"+
            $"{handoff}；按反向键 {Number(a.ClickMs)} ms 后按下鼠标左键。"+
            (a.PriorSpeed.HasValue?$" 换向前速度 {a.PriorSpeed:0.####}，开枪时 {Number(a.Speed)} u/s。":"")+"\n"+
            (a.EligibleForStats?"这条计入统计。时间线帮你核对交接过程；仅凭这几个时刻，不能区分开枪过早与反向按久后重新加速。":$"这条未计入：{a.EligibilityText}。");
        inputTimeline.Show(a);
        actionDetail.Text=$"{a.TimeText}  {a.Direction}  {a.Weapon} · 持键 {Number(a.HoldMs)} ms · Demo tick {a.DemoTick?.ToString()??"—"} · 匹配残差 {Number(a.MatchErrorMs)} ms\n"+
            $"初速（反向前三 tick 中位数）{a.PriorSpeed?.ToString("F4")??"—"} u/s · 门槛 ≥34，按未四舍五入值判断\n"+
            $"反向前 {Number(a.PriorMinSpeed)}–{Number(a.PriorMaxSpeed)} u/s · 开枪附近 {Number(a.MinSpeed)}–{Number(a.MaxSpeed)} u/s · 最大蹲伏量 {a.MaxDuckAmount?.ToString("F2")??"—"}（0站立 / 1全蹲）\n"+
            $"反向轴速度 {Number(a.AxisSpeedBefore)} → {Number(a.AxisSpeedAtShot)} u/s · {a.BrakeDescription}。\n"+
            $"{a.Cohort} · {a.EligibilityText}。{a.ContextText}。{a.Detail}";
    }
    private void Job_Selected(object sender,SelectionChangedEventArgs e)
    {if(jobGrid.SelectedItem is DemoJob j)jobDetail.Text=j.Path+"\n"+j.Detail+(j.State=="失败"?$"\n下次重试：{j.NextRetryUtc.ToLocalTime():HH:mm:ss}":"");}
    private void Import_Click(object sender,RoutedEventArgs e)
    {
        if(_monitor.IsReadOnly){backgroundStatus.Text=_monitor.Status;return;}
        var picker=new OpenFileDialog{Title="导入录像（自动匹配本地对局）",Filter="Demo / 压缩包|*.dem;*.zip;*.dem.bz2",Multiselect=true};
        if(picker.ShowDialog()==true)foreach(var path in picker.FileNames)_monitor.Import(path);
        backgroundStatus.Text="已加入后台队列，等待文件稳定";
    }
    private void AddRoot_Click(object sender,RoutedEventArgs e)
    {if(_monitor.IsReadOnly)return;var picker=new OpenFolderDialog{Title="选择 Demo 下载目录",Multiselect=false};if(picker.ShowDialog()==true)_monitor.AddRoot(picker.FolderName);}
    private void RemoveRoot_Click(object sender,RoutedEventArgs e){if(!_monitor.IsReadOnly&&rootList.SelectedItem is string path)_monitor.RemoveRoot(path);}
    private void Scan_Click(object sender,RoutedEventArgs e)=>_monitor.Wake();
    private void Retry_Click(object sender,RoutedEventArgs e){if(!_monitor.IsReadOnly)_monitor.Retry();}
    private void Enabled_Changed(object sender,RoutedEventArgs e){if(!_updating)_monitor.SetEnabled(enabledBox.IsChecked==true);}
    private void Export_Click(object sender,RoutedEventArgs e)
    {
        var picker=new SaveFileDialog{Title="导出当前筛选的动作",Filter="CSV|*.csv",FileName="StrafeLab-analysis-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".csv"};
        if(picker.ShowDialog()!=true)return;
        try
        {
            var lines=new List<string>{"local_time,round,direction,weapon,gap_ms,overlap_ms,hold_ms,click_ms,demo_tick,match_error_ms,speed,min_speed,max_speed,legacy_speed_band,eligible,stance,exclusion_codes,prior_min_speed,prior_max_speed,max_duck_amount,analysis_version,context_tags,axis_speed_before,axis_speed_at_shot,axis_speed_drop,prior_speed,minimum_initial_speed"};
            foreach(var a in _visible)lines.Add(FormattableString.Invariant($"{a.LocalTime:yyyy-MM-dd HH:mm:ss.fff},{a.Round+1},{a.Direction},{a.Weapon},{a.GapMs},{a.OverlapMs},{a.HoldMs},{a.ClickMs},{a.DemoTick},{a.MatchErrorMs},{a.Speed},{a.MinSpeed},{a.MaxSpeed},{a.Verdict},{a.EligibleForStats},{a.Stance},{string.Join(';',a.ExclusionReasons)},{a.PriorMinSpeed},{a.PriorMaxSpeed},{a.MaxDuckAmount},{MatchReport.CurrentVersion},{string.Join(';',a.ContextTags)},{a.AxisSpeedBefore},{a.AxisSpeedAtShot},{a.AxisSpeedDrop},{a.PriorSpeed},{CounterStrafeCohort.MinimumInitialSpeed}"));
            File.WriteAllLines(picker.FileName,lines,new UTF8Encoding(true));backgroundStatus.Text="已导出："+picker.FileName;
        }
        catch(Exception ex){backgroundStatus.Text="导出失败："+ex.Message;}
    }
    private sealed record SessionChoice(string Id,string Label);
    public async Task CaptureExtraSmokeViewsAsync(string directory)
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")return;
        async Task Capture(string name)
        {
            await Task.Delay(150);var window=Window.GetWindow(this);window.UpdateLayout();
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window);var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file=File.Create(Path.Combine(directory,name));png.Save(file);
        }
        navOverview.IsChecked=true;await Capture("progress-preview.png");
        File.WriteAllText(Path.Combine(directory,"progress-summary.txt"),progressPage.SmokeSummary);
        navKeyboard.IsChecked=true;progressPage.ShowSettingsForSmoke();await Capture("keyboard-settings-preview.png");progressPage.ResetScrollForSmoke();
        if(progressPage.OpenFirstMatchForSmoke())
        {
            await _navigationTask;
            if(tabs.SelectedItem!=detailTab||string.IsNullOrEmpty(sessionFilter.SelectedValue as string)||_visible.Count==0)
                throw new InvalidOperationException("Progress to match navigation failed");
            await Capture("progress-action-preview.png");
            if(statsScroll.VerticalOffset<=0)throw new InvalidOperationException("Match navigation did not reach action timeline");
        }
        _updating=true;dateFilter.SelectedIndex=0;mapFilter.SelectedIndex=0;sessionFilter.SelectedIndex=0;
        weaponFilter.SelectedIndex=0;stanceFilter.SelectedIndex=0;speedFilter.SelectedIndex=0;_updating=false;Render();
        navDetails.IsChecked=true;await Capture("analysis-preview.png");
        weaponFilter.SelectedItem="ak47";
        if(weaponFilter.SelectedItem as string=="ak47"&&_visible.Any(a=>a.Weapon!="ak47"))throw new InvalidOperationException("Weapon filter failed");
        await Capture("analysis-filter-preview.png");
        var rate=lowRate.Text;var count=sampleCount.Text;
        cohortFilter.SelectedIndex=1;
        if(rate!=lowRate.Text||count!=sampleCount.Text)throw new InvalidOperationException("Cohort detail view contaminated statistics");
        await Capture("analysis-cohort-preview.png");
        weaponFilter.SelectedIndex=0;cohortFilter.SelectedIndex=2;
        if(_visible.Any(a=>!a.ContextTags.Contains("crouch")))throw new InvalidOperationException("Crouch detail filter failed");
        stanceFilter.SelectedItem="慢走";cohortFilter.SelectedIndex=0;
        if(_visible.Any(a=>a.Stance!="慢走"))throw new InvalidOperationException("Stance filter failed");
        await Capture("analysis-walk-preview.png");stanceFilter.SelectedIndex=0;cohortFilter.SelectedIndex=2;
        actionGrid.SelectedIndex=0;statsScroll.ScrollToEnd();
        await Capture("analysis-exclusions-preview.png");
        cohortFilter.SelectedIndex=4;
        var belowThreshold=_visible.FirstOrDefault(a=>a.PriorSpeed is >33 and <34);
        if(belowThreshold!=null)
        {
            actionGrid.SelectedItem=belowThreshold;actionGrid.ScrollIntoView(belowThreshold);statsScroll.ScrollToEnd();
            if(!actionDetail.Text.Contains(CounterStrafeCohort.ReasonText("initial_speed_below_threshold")))
                throw new InvalidOperationException("Initial speed exclusion detail missing");
            await Capture("analysis-speed-threshold-preview.png");
        }
        statsScroll.ScrollToTop();
        navDemos.IsChecked=true;await Capture("demo-jobs-preview.png");
        navCapture.IsChecked=true;await Capture("capture-settings-preview.png");
        navOverview.IsChecked=true;
        var originalWidth=Window.GetWindow(this).Width;Window.GetWindow(this).Width=1040;await Capture("compact-preview.png");Window.GetWindow(this).Width=originalWidth;
        progressPage.CheckProfileWorkflowForSmoke();
    }
}
