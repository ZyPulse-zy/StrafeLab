using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StrafeLab.Core;

namespace StrafeLab.UI;
public partial class ProgressPage : UserControl
{
    private readonly KeyboardProfileStore _store=new(new SessionStore().RootDirectory);
    private KeyboardProfileHistory _history=new();
    private IReadOnlyList<MatchReport> _reports=[];
    private bool _updating=true,_profileError;
    private bool _parametersShown;
    private double _overviewOffset,_parametersOffset;
    private ProgressSnapshot? _snapshot;
    public bool ChartKeyboardChecked {get;private set;}
    public event Action<string,string?>? OpenMatch;
    public event Action<int>? Navigate;
    public ProgressPage()
    {
        InitializeComponent();period.ItemsSource=new[]{"全部时间","最近 7 天","最近 30 天"};period.SelectedIndex=0;
        metric.ItemsSource=new[]{"两键交接时间","两键同时按住","两键都松开的空档","换向到开枪","交接波动"};metric.SelectedIndex=0;
        chart.MatchClicked+=id=>OpenMatch?.Invoke(id,cohort.SelectedValue as string);
        LoadProfiles();FillEditor();_updating=false;
    }
    public void SetReports(IReadOnlyList<MatchReport> reports){_reports=reports;LoadProfiles();RefreshGroups();}
    public void ShowParameters(bool show)
    {
        if(_parametersShown==show)return;
        if(_parametersShown)_parametersOffset=progressScroll.VerticalOffset;else _overviewOffset=progressScroll.VerticalOffset;
        _parametersShown=show;overviewPanel.Visibility=show?Visibility.Collapsed:Visibility.Visible;parametersPanel.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        progressScroll.ScrollToVerticalOffset(show?_parametersOffset:_overviewOffset);
    }
    private void Metric_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(chart==null)return;chart.Metric=Math.Max(0,metric.SelectedIndex);
        chartDescription.Text=metric.SelectedIndex switch{1=>"最近 12 局 · 反向按下后，两键同时按住多久。",2=>"最近 12 局 · 松开原键，到按下反向键的空档。",3=>"最近 12 局 · 反向按下，到第一次按鼠标左键。",4=>"最近 12 局 · 中间 50% 动作的跨度；更窄表示更一致。",_=>"最近 12 局 · 负值是空档，正值是两键同按。"};
        if(chartLegend!=null)chartLegend.Text=metric.SelectedIndex==4?"折线：每局交接波动 · 空心点：少于 5 次 · 方向键选局 / Enter 复盘":"折线：中位数 · 色带：中间 50% · 空心点：少于 5 次";
    }
    private void TrendLayout_Changed(object sender,SizeChangedEventArgs e)
    {
        bool compact=e.NewSize.Width<1100;
        readoutColumn.Width=new(compact?238:272);
        readoutSurface.Padding=new(0,18,compact?16:24,12);
        trendSurface.Padding=new(compact?18:24,18,0,12);
        FitReadout();
    }
    private void FitReadout()=>handoffSpread.FontSize=Math.Clamp((readoutColumn.Width.Value-readoutSurface.Padding.Right-38)/Math.Max(1,handoffSpread.Text.Length*.62),24,76);
    private void Parameters_Click(object sender,RoutedEventArgs e)=>Navigate?.Invoke(1);
    private void Overview_Click(object sender,RoutedEventArgs e)=>Navigate?.Invoke(0);
    private void Demo_Click(object sender,RoutedEventArgs e)=>Navigate?.Invoke(3);
    private void Capture_Click(object sender,RoutedEventArgs e)=>Navigate?.Invoke(4);
    private void Latest_Click(object sender,RoutedEventArgs e)
    {if(_snapshot?.Matches.LastOrDefault() is {} m)OpenMatch?.Invoke(m.SessionId,cohort.SelectedValue as string);}
    private MatchReport[] InPeriod()
    {
        var since=period.SelectedIndex switch{1=>DateTime.Now.Date.AddDays(-6),2=>DateTime.Now.Date.AddDays(-29),_=>DateTime.MinValue};
        return _reports.Where(r=>r.StartedAtUtc.ToLocalTime()>=since).ToArray();
    }
    private void RefreshGroups()
    {
        _updating=true;var key=cohort.SelectedValue as string;var groups=ProgressAnalysis.Cohorts(InPeriod());
        cohort.ItemsSource=groups;cohort.SelectedValue=key;
        if(cohort.SelectedIndex<0&&groups.Count>0)cohort.SelectedIndex=0;
        cohort.IsEnabled=groups.Count>0;cohortPlaceholder.Visibility=groups.Count==0?Visibility.Visible:Visibility.Collapsed;
        _updating=false;Render();
    }
    private void Render()
    {
        if(_updating)return;
        var reports=InPeriod();var s=ProgressAnalysis.Build(reports,cohort.SelectedValue as string,_history);
        _snapshot=s;openLatest.IsEnabled=s.Matches.Count>0;
        emptyPanel.Visibility=s.Count==0?Visibility.Visible:Visibility.Collapsed;
        emptyTitle.Text=reports.Length==0?"还没有这个时间范围内的记录":"当前没有可比较的同类动作";
        emptyMessage.Text=reports.Length==0?"先开启后台采集，完成一局后下载对应 Demo；分析完成会自动出现在这里。也可以切换到全部时间。":"导入这几局对应的 Demo，或切换时间范围。有记录但未通过同步、初速或输入核对的动作不会进入趋势。";
        int reliable=reports.Count(r=>r.Alignment?.IsReliable==true);
        headline.Text=s.Count==0?"等待可靠配对的同类动作":s.Ready?"已积累一组可对照的记录":"样本还少，先建立基线";
        coverage.Text=$"边沿 {s.Handoff.Count}/{s.Count} · Demo {reliable}/{reports.Length} 局";
        coverage.ToolTip=$"当前分组 {s.Handoff.Count} 次边沿完整，{s.UnknownHandoff} 次边沿缺失；当前时间范围 {reports.Length} 局中，{reliable} 局已可靠配对 Demo。";
        handoffSpread.Text=s.Handoff.Count>=2?s.Handoff.Spread?.ToString("0.#")??"—":"—";
        FitReadout();
        handoffMedian.Text=s.Handoff.Median?.ToString("+0.#;-0.#;0")??"—";
        sampleTotal.Text=s.Count.ToString();matchTotal.Text=s.Matches.Count.ToString();
        handoffDistribution.Show(s.HandoffValues);handoffDirections.ItemsSource=s.Directions;
        handoffDirections.Height=Math.Max(70,34+Math.Min(4,s.Directions.Count)*30);
        distributionNote.Text=$"{s.Handoff.Count} 次完整交接 · 横轴 ms / 柱高为次数"+(s.UnknownHandoff>0?$" · {s.UnknownHandoff} 次边沿缺失未绘制":"");
        nextStep.Text=s.Count==0?"先确认采集已开启，再导入对应 Demo。完成配对后，趋势会自动更新。":s.Ready?"先记下参数，每次只改一项，再比较同组动作的变化。":
            $"接下来：保持同一套参数，继续积累。距 3 局 / 30 次的基线提示还差 {Math.Max(0,3-s.Matches.Count)} 局、{Math.Max(0,30-s.Count)} 次。";
        selectionNote.Text="初速 ≥34 u/s · 同武器 / 姿态 / 初速组\n慢走与蹲伏各自保留";
        overlapValue.Text=s.Overlap.Typical;overlapRange.Text=s.Overlap.Range;
        gapValue.Text=s.Gap.Typical;gapRange.Text=s.Gap.Range;
        clickValue.Text=s.Click.Typical;clickRange.Text=s.Click.Range;
        handoffSummary.Text=s.HandoffSummary;
        handoffBar.Show(s);
        chart.SetPoints(s.Matches);trendSummary.Text=ProgressAnalysis.Trend(s);
        if(_profileError){adviceTitle.Text="参数记录读取失败";advice.Text="先检查下方提示，现有文件不会被空记录覆盖。";}
        else if(_history.Profiles.Count==0)
        {adviceTitle.Text="先记下当前方案，暂时保持参数";advice.Text="还没有确认过的参数记录，无法知道每局用了哪套触发 / RT。下方已按你之前的截图预填；核对后保存，之后的新对局会自动关联。";}
        else if(!s.Ready)
        {adviceTitle.Text="当前样本还少，先保持这套参数";advice.Text=$"当前同组只有 {s.Count} 次 / {s.Matches.Count} 局。先看交接是否越来越一致，再决定是否试另一套设置；一次好坏不足以建议具体 RT 数字。";}
        else
        {
            adviceTitle.Text="可以安排一轮单项对照，尚不能指定最佳数值";
            advice.Text=$"当前两键同按典型 {s.Overlap.Typical}，双键空档 {s.Gap.Typical}。先查看跨度大的动作，区分手指交接习惯与触发设置；若要试调，先只改 RT 抬起或触发行程中的一项，再记录为新方案。下方用同组数据对比，不凭一次成绩决定。";
        }
        var current=_history.Profiles.OrderByDescending(p=>p.AppliedAtUtc).FirstOrDefault();
        currentProfile.Text=current==null?"尚未记录当前方案；截图预填值不会自动写入历史。":$"最近确认：{current.Name} · {current.Description}\n当前同组有 {s.UnknownProfileActions} 次未记录参数，只参与表现趋势。";
        _updating=true;
        var oldBefore=(beforeProfile.SelectedItem as ProfileComparison)?.Id;var oldAfter=(afterProfile.SelectedItem as ProfileComparison)?.Id;
        profileGrid.ItemsSource=s.Profiles;beforeProfile.ItemsSource=s.Profiles;afterProfile.ItemsSource=s.Profiles;
        beforeProfile.SelectedItem=s.Profiles.FirstOrDefault(p=>p.Id==oldBefore)??s.Profiles.FirstOrDefault();
        afterProfile.SelectedItem=s.Profiles.FirstOrDefault(p=>p.Id==oldAfter)??s.Profiles.LastOrDefault();
        var selectedMatch=(historyGrid.SelectedItem as ProgressMatch)?.SessionId;
        historyGrid.ItemsSource=s.Matches.Reverse().ToArray();historyGrid.SelectedItem=s.Matches.FirstOrDefault(m=>m.SessionId==selectedMatch);
        var selected=(assignmentMatch.SelectedItem as MatchChoice)?.Id;
        assignmentMatch.ItemsSource=reports.OrderByDescending(r=>r.StartedAtUtc).Select(r=>new MatchChoice(r.SessionId,$"{r.LocalTime} · {r.Map}")).ToArray();
        assignmentMatch.SelectedItem=assignmentMatch.Items.Cast<MatchChoice>().FirstOrDefault(x=>x.Id==selected)??assignmentMatch.Items.Cast<MatchChoice>().FirstOrDefault();
        var assigned=(assignmentProfile.SelectedItem as ProfileChoice)?.Id;
        assignmentProfile.ItemsSource=new[]{new ProfileChoice(null,"未知 / 中途变更")}.Concat(_history.Profiles.Select(p=>new ProfileChoice(p.Id,p.Name))).ToArray();
        assignmentProfile.SelectedItem=assignmentProfile.Items.Cast<ProfileChoice>().FirstOrDefault(p=>p.Id==assigned);
        _updating=false;RenderComparison();
    }
    private void LoadProfiles()
    {
        try{_history=_store.Load();_profileError=false;}
        catch(Exception ex){_profileError=true;profileStatus.Text="参数记录读取失败："+ex.Message;}
    }
    private void FillEditor()
    {
        var p=_history.Profiles.OrderByDescending(p=>p.AppliedAtUtc).FirstOrDefault();
        profileName.Text=p==null?"我的基线方案":$"方案 {_history.Profiles.Count+1}";
        adTrigger.Text=(p?.AdTrigger??.50).ToString("0.###",CultureInfo.InvariantCulture);
        wsTrigger.Text=(p?.WsTrigger??.85).ToString("0.###",CultureInfo.InvariantCulture);
        rtPress.Text=(p?.RtPress??.11).ToString("0.###",CultureInfo.InvariantCulture);
        rtRelease.Text=(p?.RtRelease??.11).ToString("0.###",CultureInfo.InvariantCulture);
        profileNotes.Text=p?.Notes??"";
        prefillNote.Text=p==null?"按你之前的驱动截图预填：A/D 0.50、W/S 0.85、RT 按下/抬起 0.11 mm。请核对当前实际值后保存；这不是程序给出的推荐数值。":"从最近方案复制，改成你已经在驱动实际应用的值再保存。旧方案会保留。";
    }
    private void Period_Changed(object sender,SelectionChangedEventArgs e){if(!_updating)RefreshGroups();}
    private void Cohort_Changed(object sender,SelectionChangedEventArgs e){if(!_updating)Render();}
    private void Comparison_Changed(object sender,SelectionChangedEventArgs e){if(!_updating)RenderComparison();}
    private void RenderComparison()=>comparisonText.Text=ProgressAnalysis.Compare(beforeProfile.SelectedItem as ProfileComparison,afterProfile.SelectedItem as ProfileComparison);
    private void History_Selected(object sender,SelectionChangedEventArgs e)
    {if(openSelected!=null)openSelected.IsEnabled=historyGrid.SelectedItem is ProgressMatch;}
    private void OpenSelected_Click(object sender,RoutedEventArgs e)
    {if(historyGrid.SelectedItem is ProgressMatch m)OpenMatch?.Invoke(m.SessionId,cohort.SelectedValue as string);}
    private void History_DoubleClick(object sender,MouseButtonEventArgs e)
    {if(ItemsControl.ContainerFromElement(historyGrid,e.OriginalSource as DependencyObject) is DataGridRow)OpenSelected_Click(sender,e);}
    private void SaveProfile_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            if(string.IsNullOrWhiteSpace(profileName.Text)){profileName.Focus();throw new ArgumentException("请填写方案名称。");}
            double Value(TextBox box,string label)
            {
                if(double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)&&double.IsFinite(v)&&v>=.005&&v<=4)return v;
                box.Focus();box.SelectAll();throw new ArgumentException($"{label}请填写 0.005–4 mm 内的数字，例如 0.11，并以驱动允许的范围为准。");
            }
            var p=_store.Add(profileName.Text,Value(adTrigger,"A / D 触发"),Value(wsTrigger,"W / S 触发"),Value(rtPress,"RT 按下"),Value(rtRelease,"RT 抬起"),profileNotes.Text,DateTime.UtcNow);
            LoadProfiles();FillEditor();Render();profileStatus.Text=$"已记录「{p.Name}」。今后开始的对局自动关联；过去的对局可在下方手动补记。键盘没有被程序修改。";
        }
        catch(Exception ex){profileStatus.Text="未保存："+ex.Message;}
    }
    private void AssignProfile_Click(object sender,RoutedEventArgs e)
    {
        if(assignmentMatch.SelectedItem is not MatchChoice match||assignmentProfile.SelectedItem is not ProfileChoice profile)
        {assignmentStatus.Text="请选择要标记的对局和当时使用的方案。";return;}
        try{_store.Assign(match.Id,profile.Id);LoadProfiles();Render();assignmentStatus.Text=$"已标记 {match.Label}：{profile.Name}。原始对局文件未修改。";}
        catch(Exception ex){assignmentStatus.Text="未保存："+ex.Message;}
    }
    private sealed record MatchChoice(string Id,string Label);
    private sealed record ProfileChoice(string? Id,string Name);
    public void ShowSettingsForSmoke()
    {ShowParameters(true);profileEditor.IsExpanded=true;progressScroll.ScrollToTop();}
    public void ResetScrollForSmoke(){profileEditor.IsExpanded=false;progressScroll.ScrollToTop();}
    public bool OpenFirstMatchForSmoke()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1"||historyGrid.Items.Count==0)return false;
        historyGrid.SelectedIndex=0;OpenSelected_Click(this,new());return true;
    }
    public void CheckProfileWorkflowForSmoke()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1"||assignmentMatch.Items.Count==0)return;
        var data=Environment.GetEnvironmentVariable("STRAFELAB_DATA_DIR");
        var normal=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"StrafeLab");
        if(string.IsNullOrEmpty(data)||System.IO.Path.GetFullPath(data).TrimEnd('\\','/').Equals(normal,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile smoke requires an isolated data directory");
        var name="界面验证-"+Guid.NewGuid().ToString("N")[..6];profileName.Text=name;
        SaveProfile_Click(this,new());
        var profile=_store.Load().Profiles.Single(p=>p.Name==name);
        assignmentProfile.SelectedItem=assignmentProfile.Items.Cast<ProfileChoice>().Single(p=>p.Id==profile.Id);
        var match=(MatchChoice)assignmentMatch.SelectedItem;
        AssignProfile_Click(this,new());
        if(_store.Load().SessionAssignments[match.Id]!=profile.Id)throw new InvalidOperationException("Profile attribution UI failed");
        assignmentProfile.SelectedIndex=0;AssignProfile_Click(this,new());
        if(_store.Load().SessionAssignments[match.Id]!="")throw new InvalidOperationException("Unknown scheme UI failed");
    }
    public string SmokeSummary=>headline.Text+"\n"+coverage.Text+"\n"+trendSummary.Text+"\n"+adviceTitle.Text;
    public object TacticalSmokeState=>new{spread=handoffSpread.Text,median=handoffMedian.Text,samples=sampleTotal.Text,matches=matchTotal.Text,
        completeEdges=_snapshot?.Handoff.Count,histogramSamples=_snapshot?.HandoffValues.Count,directions=_snapshot?.Directions,
        layoutWidth=ActualWidth,chartWidth=chart.ActualWidth,readoutWidth=readoutSurface.ActualWidth};
    public void SetMetricForSmoke(int index)
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")throw new InvalidOperationException("Only isolated UI checks are allowed.");
        metric.SelectedIndex=index;
    }
    public void CheckReviewInteractionsForSmoke()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")throw new InvalidOperationException("Only isolated UI checks are allowed.");
        var key=cohort.SelectedValue as string;var draft=profileNotes.Text;profileNotes.Text="未保存的界面测试草稿";
        ShowParameters(true);ShowParameters(false);ShowParameters(true);
        if(profileNotes.Text!="未保存的界面测试草稿"||cohort.SelectedValue as string!=key)throw new InvalidOperationException("Navigation lost draft or cohort");
        int profiles=_store.Load().Profiles.Count;var input=adTrigger.Text;adTrigger.Text="not-a-number";
        SaveProfile_Click(this,new());
        if(_store.Load().Profiles.Count!=profiles||adTrigger.Text!="not-a-number"||!profileStatus.Text.Contains("A / D"))throw new InvalidOperationException("Invalid form did not preserve input or explain field");
        adTrigger.Text=input;profileNotes.Text=draft;profileStatus.Text="";
        ShowParameters(false);
        if(_snapshot?.Matches.Count>0)
        {
            string? target=null;void Remember(string id)=>target=id;
            chart.MatchClicked+=Remember;
            var source=PresentationSource.FromVisual(Window.GetWindow(this))??throw new InvalidOperationException("No WPF input surface");
            chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Right){RoutedEvent=Keyboard.KeyDownEvent});
            chart.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent});
            chart.MatchClicked-=Remember;
            if(target==null||!_snapshot.Matches.Any(m=>m.SessionId==target))throw new InvalidOperationException("Chart keyboard navigation failed");
            var peer=System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(chart);
            if(peer==null||string.IsNullOrWhiteSpace(peer.GetName()))throw new InvalidOperationException("Chart automation name missing");
            ChartKeyboardChecked=true;
        }
        metric.SelectedIndex=2;if(chart.Metric!=2||!chartDescription.Text.Contains("空档"))throw new InvalidOperationException("Metric explanation not updated");metric.SelectedIndex=0;
        ShowParameters(false);progressScroll.ScrollToTop();
    }
}
