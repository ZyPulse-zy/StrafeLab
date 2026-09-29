using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using StrafeLab.Core;

namespace StrafeLab.UI;

/// <summary>Review local timing without displaying or inferring in-game speed.</summary>
public sealed class InputTimingWindow : Window
{
    public IReadOnlyList<ActionReview> Actions {get;}
    private readonly DataGrid _grid;
    private readonly TextBlock _story;
    public InputTimingWindow(MatchReport report,string key,string? direction=null)
    {
        Title=$"按键习惯 · {report.LocalTime} · {report.Map}";
        Width=940;Height=Math.Min(740,SystemParameters.WorkArea.Height-40);MinWidth=720;MinHeight=620;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/StrafeLab;component/UI/ReviewTheme.xaml",UriKind.Relative)});
        Background=TacticalDrawing.Background;Foreground=TacticalDrawing.Ink;
        SourceInitialized+=(_,_)=>{int dark=1;_=DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,20,ref dark,sizeof(int));};
        FontFamily=(System.Windows.Media.FontFamily)FindResource("MonoFont");
        Actions=report.Actions.Where(a=>InputTimingAnalysis.IsEligible(a)&&InputTimingAnalysis.Key(a)==key&&ProgressAnalysis.InDirection(a,direction))
            .DistinctBy(a=>a.TransitionUs).OrderBy(a=>a.TransitionUs).ToArray();
        var layout=new Grid{Margin=new Thickness(22)};
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});layout.RowDefinitions.Add(new());
        layout.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var heading=new StackPanel{Margin=new Thickness(0,0,0,16)};
        heading.Children.Add(new TextBlock{Text=$"按键交接 · {Actions.Count} 个可信片段 · {direction??"全部方向"}",FontSize=23,FontWeight=FontWeights.SemiBold});
        heading.Children.Add(new TextBlock{Text=$"{report.LocalTime} · {report.Map} · "+(report.Alignment?.IsReliable==true?"已有 Demo":"尚未配对 Demo"),Margin=new Thickness(0,8,0,5),Foreground=TacticalDrawing.Muted});
        heading.Children.Add(new TextBlock{Text="只核对换向、同按、空档和按鼠标的时间。本页不筛选初速，不判断是否需要急停，也不评价制动效果。",TextWrapping=TextWrapping.Wrap,Foreground=TacticalDrawing.Muted});
        layout.Children.Add(heading);
        _grid=new DataGrid{ItemsSource=Actions,AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single};
        void Column(string title,string path,double width,string? format=null)=>_grid.Columns.Add(new DataGridTextColumn
        {Header=title,Width=width,Binding=new Binding(path){StringFormat=format}});
        Column("时间",nameof(ActionReview.TimeText),130);Column("回合",nameof(ActionReview.RoundText),65);
        Column("方向",nameof(ActionReview.Direction),75);Column("同按 ms",nameof(ActionReview.OverlapMs),110,"0.#");
        Column("空档 ms",nameof(ActionReview.GapMs),110,"0.#");Column("换向到按鼠标 ms",nameof(ActionReview.ClickMs),170,"0.#");
        System.Windows.Automation.AutomationProperties.SetName(_grid,"本局可信按键片段，不筛选游戏速度");
        Grid.SetRow(_grid,1);layout.Children.Add(_grid);
        var detail=new StackPanel{Margin=new Thickness(0,14,0,0)};
        _story=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
        var timeline=new InputTimeline{Height=200};
        detail.Children.Add(_story);detail.Children.Add(timeline);
        detail.Children.Add(new TextBlock{Text="这批片段可出现在按键习惯趋势中；是否计入实战验证与参数对比，仍由 Demo 和原有资格检查决定。",TextWrapping=TextWrapping.Wrap,Foreground=TacticalDrawing.Muted});
        var close=new Button{Content="返回趋势",HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        close.Click+=(_,_)=>Close();detail.Children.Add(close);
        Grid.SetRow(detail,2);layout.Children.Add(detail);Content=layout;
        _grid.SelectionChanged+=(_,_)=>
        {
            if(_grid.SelectedItem is not ActionReview a)return;
            _story.Text=$"{a.Direction}：两键同按 {a.OverlapMs:0.#} ms，双键空档 {a.GapMs:0.#} ms；换向后 {a.ClickMs:0.#} ms 按下 Mouse1。";
            timeline.Show(a);
        };
        _grid.SelectedIndex=Actions.Count>0?0:-1;
        if(Actions.Count==0)_story.Text="当前分组没有完整的可信按键片段；关闭此窗口后切换分组。";
    }
    public void CheckSelectionForSmoke()
    {
        if(Environment.GetEnvironmentVariable("STRAFELAB_TEST_MODE")!="1")throw new InvalidOperationException("Only isolated UI checks are allowed.");
        if(Actions.Count==0)return;
        _grid.SelectedIndex=Actions.Count-1;
        _grid.ScrollIntoView(_grid.SelectedItem);
        if(!_story.Text.Contains(Actions[^1].Direction)||!_story.Text.Contains("Mouse1"))throw new InvalidOperationException("Local timing detail did not follow selection");
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
}
