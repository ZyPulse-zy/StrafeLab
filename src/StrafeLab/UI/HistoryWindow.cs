using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using StrafeLab.Core;

namespace StrafeLab.UI;
public sealed class HistoryWindow : Window
{
    private readonly RuntimeService _runtime;
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,IsReadOnly=true,SelectionMode=DataGridSelectionMode.Single,
        Background=new SolidColorBrush(Color.FromRgb(17,28,46)),Foreground=Brushes.LightGray,RowBackground=new SolidColorBrush(Color.FromRgb(17,28,46)),
        AlternatingRowBackground=new SolidColorBrush(Color.FromRgb(22,36,58)),GridLinesVisibility=DataGridGridLinesVisibility.None,HeadersVisibility=DataGridHeadersVisibility.Column};
    private readonly TextBox _details=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Background=Brushes.Transparent,Foreground=Brushes.LightGray,
        BorderThickness=new Thickness(0),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Height=135};
    private readonly Sparkline _chart=new(){Height=100};
    private readonly TextBlock _trend=new(){Margin=new Thickness(0,8,0,12),Foreground=Brushes.LightGray};
    public HistoryWindow(RuntimeService runtime)
    {
        _runtime=runtime;Title="StrafeLab · 历史与趋势";Width=1060;Height=730;MinWidth=880;MinHeight=600;
        Background=new SolidColorBrush(Color.FromRgb(11,18,32));Foreground=Brushes.LightGray;
        var root=new DockPanel{Margin=new Thickness(24)};Content=root;
        var title=new TextBlock{Text="历史对局与趋势",FontSize=25,Margin=new Thickness(0,0,0,14)};DockPanel.SetDock(title,Dock.Top);root.Children.Add(title);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        var refresh=Button("刷新",()=>Refresh());var export=Button("导出所选 CSV",Export);var demo=Button("为所选会话匹配 Demo",MatchDemo);
        actions.Children.Add(refresh);actions.Children.Add(export);actions.Children.Add(demo);
        DockPanel.SetDock(_details,Dock.Bottom);root.Children.Add(_details);
        DockPanel.SetDock(_chart,Dock.Top);root.Children.Add(_chart);DockPanel.SetDock(_trend,Dock.Top);root.Children.Add(_trend);
        foreach(var (header,path,format) in new[]{("开始时间 (UTC)","StartedAtUtc","yyyy-MM-dd HH:mm"),("地图","Map",""),("反向次数","TransitionCount",""),
            ("Mouse1","ShotCount",""),("gap ms","AverageGapMs","F1"),("overlap ms","AverageOverlapMs","F1"),("低速占比","FireWindowRate","P0"),("输入可信率","ConfidenceRate","P0")})
            _grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(path){StringFormat=format},Width=new DataGridLength(1,DataGridLengthUnitType.Star)});
        _grid.SelectionChanged+=(_,_)=>Describe();root.Children.Add(_grid);Refresh();
    }
    private static Button Button(string text,Action action)
    {var b=new Button{Content=text,Margin=new Thickness(0,10,12,0),Padding=new Thickness(14,8,14,8)};b.Click+=(_,_)=>action();return b;}
    private SessionDocument? Selected()=>_grid.SelectedItem is SessionSummary s?_runtime.LoadSession(s.SessionId):null;
    private void Refresh()
    {
        var rows=_runtime.GetRecentSummaries();_grid.ItemsSource=rows;_chart.SetValues(TrendAnalyzer.AverageGapTrend(rows));
        var valid=rows.Where(s=>s.TransitionCount>0).ToArray();
        _trend.Text=$"平均 gap 趋势 · 旧 → 新 · {rows.Count} 个会话";
        if(valid.Length>=2)_trend.Text+=$" · 最近 {Math.Min(5,valid.Length)} 局 {valid.Take(5).Average(s=>s.AverageGapMs):F1} ms";
    }
    private void Describe()
    {
        var s=Selected();if(s==null)return;
        var lines=new List<string>{$"{(s.DiagnosticMode?"桌面诊断":"CS2 会话")} · {s.InputEvents.Count:N0} 个输入边沿 · {s.HallSamples.Count:N0} 个 Hall 样本"};
        foreach(var g in s.Transitions.Where(t=>t.Confidence>=.75).GroupBy(t=>$"{t.From}→{t.To}"))
        {
            var gaps=g.Select(t=>t.GapMs).Order().ToArray();var holds=g.Where(t=>t.ReverseHoldUs.HasValue).ToArray();
            lines.Add($"{g.Key}   n={g.Count()}   gap 中位 {gaps[gaps.Length/2]:F1} / P95 {gaps[(int)((gaps.Length-1)*.95)]:F1} ms   平均反向持键 {(holds.Length>0?holds.Average(t=>t.ReverseHoldUs!.Value)/1000:0):F1} ms");
        }
        lines.Add(s.DemoAlignment?.Explanation??"尚无可靠 Demo 同步");if(s.Calibration!=null)lines.Add(s.Calibration.Explanation);
        _details.Text=string.Join(Environment.NewLine,lines);
    }
    private void Export()
    {
        try{if(_grid.SelectedItem is SessionSummary s){var path=_runtime.ExportCsv(s.SessionId);Process.Start(new ProcessStartInfo("explorer.exe"){Arguments=$"/select,\"{path}\"",UseShellExecute=true});}}
        catch(Exception ex){_details.Text=ex.Message;}
    }
    private async void MatchDemo()
    {
        var s=Selected();if(s==null)return;
        var picker=new OpenFileDialog{Title="选择本局 CS2 Demo",Filter="CS2 Demo|*.dem"};if(picker.ShowDialog(this)!=true)return;
        IsEnabled=false;_details.Text="正在本地解析 Demo…";
        try{await _runtime.AnalyzeDemoAsync(s,picker.FileName);_details.Text=_runtime.GetView().Metrics.DemoStatus;Refresh();}
        finally{IsEnabled=true;}
    }
}
