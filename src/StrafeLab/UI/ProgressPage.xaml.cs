using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StrafeLab.Core;

namespace StrafeLab.UI;
public partial class ProgressPage : UserControl
{
    private readonly KeyboardProfileStore _store=new(new SessionStore().RootDirectory);
    private KeyboardProfileHistory _history=new();
    private IReadOnlyList<MatchReport> _reports=[];
    private bool _updating=true,_profileError;
    public event Action<string,string?>? OpenMatch;
    public ProgressPage()
    {
        InitializeComponent();period.ItemsSource=new[]{"全部时间","最近 7 天","最近 30 天"};period.SelectedIndex=0;
        metric.ItemsSource=new[]{"交接稳定性","两键同时按住","两键都松开的空档","换向到开枪"};metric.SelectedIndex=0;
        chart.MatchClicked+=id=>OpenMatch?.Invoke(id,cohort.SelectedValue as string);
        LoadProfiles();FillEditor();_updating=false;
    }
    public void SetReports(IReadOnlyList<MatchReport> reports){_reports=reports;LoadProfiles();RefreshGroups();}
    public void ShowParameters(bool show)
    {overviewPanel.Visibility=show?Visibility.Collapsed:Visibility.Visible;parametersPanel.Visibility=show?Visibility.Visible:Visibility.Collapsed;progressScroll.ScrollToTop();}
    private void Metric_Changed(object sender,SelectionChangedEventArgs e){if(chart!=null)chart.Metric=Math.Max(0,metric.SelectedIndex);}
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
        _updating=false;Render();
    }
    private void Render()
    {
        if(_updating)return;
        var reports=InPeriod();var s=ProgressAnalysis.Build(reports,cohort.SelectedValue as string,_history);
        int reliable=reports.Count(r=>r.Alignment?.IsReliable==true);
        headline.Text=s.Count==0?"等对局配好录像，就能开始看变化":s.Ready?"基线已积累，可以开始单项对照":"先建立基线，再调整键盘";
        coverage.Text=$"已解析 {reliable} / {reports.Length} 局；当前同组 {s.Count} 次动作、{s.Matches.Count} 局。"+
            (s.Cohort==null?" 先到「Demo 资料库」导入对应录像。":"");
        nextStep.Text=s.Ready?"接下来：先记下参数，每次只改一项，再比较同组动作的变化。":
            $"接下来：保持同一套参数，继续积累。距 3 局 / 30 次的基线提示还差 {Math.Max(0,3-s.Matches.Count)} 局、{Math.Max(0,30-s.Count)} 次。";
        selectionNote.Text="同武器 × 同姿态 × 同初速组 · 初速 ≥34 u/s · 不同组不混成一个分数";
        overlapValue.Text=s.Overlap.Typical;overlapRange.Text=s.Overlap.Range;
        gapValue.Text=s.Gap.Typical;gapRange.Text=s.Gap.Range;
        clickValue.Text=s.Click.Typical;clickRange.Text=s.Click.Range;
        handoffSummary.Text=s.HandoffSummary;
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
        historyGrid.ItemsSource=s.Matches.Reverse().ToArray();
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
    {if(!_updating&&historyGrid.SelectedItem is ProgressMatch m)OpenMatch?.Invoke(m.SessionId,cohort.SelectedValue as string);}
    private void SaveProfile_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            double Value(TextBox box)=>double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var v)?v:throw new ArgumentException("请使用数字填写行程，例如 0.11。");
            var p=_store.Add(profileName.Text,Value(adTrigger),Value(wsTrigger),Value(rtPress),Value(rtRelease),profileNotes.Text,DateTime.UtcNow);
            LoadProfiles();FillEditor();Render();profileStatus.Text=$"已记录「{p.Name}」。今后开始的对局自动关联；过去的对局可在下方手动补记。键盘没有被程序修改。";
        }
        catch(Exception ex){profileStatus.Text="未保存："+ex.Message;}
    }
    private void AssignProfile_Click(object sender,RoutedEventArgs e)
    {
        if(assignmentMatch.SelectedItem is not MatchChoice match||assignmentProfile.SelectedItem is not ProfileChoice profile)return;
        try{_store.Assign(match.Id,profile.Id);LoadProfiles();Render();profileStatus.Text=$"已标记 {match.Label}：{profile.Name}。原始对局文件未修改。";}
        catch(Exception ex){profileStatus.Text="未保存："+ex.Message;}
    }
    private sealed record MatchChoice(string Id,string Label);
    private sealed record ProfileChoice(string? Id,string Name);
    public void ShowSettingsForSmoke()
    {ShowParameters(true);profileEditor.IsExpanded=true;progressScroll.ScrollToTop();}
    public void ResetScrollForSmoke(){profileEditor.IsExpanded=false;progressScroll.ScrollToTop();}
    public bool OpenFirstMatchForSmoke()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1"||historyGrid.Items.Count==0)return false;
        historyGrid.SelectedIndex=0;return true;
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
}
