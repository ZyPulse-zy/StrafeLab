using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using StrafeLab.Core;

namespace StrafeLab.UI;
public sealed class ProgressChart : FrameworkElement
{
    private IReadOnlyList<ProgressMatch> _points=[];
    private int _metric;
    public int Metric {get=>_metric;set{_metric=value;InvalidateVisual();}}
    public event Action<string>? MatchClicked;
    public ProgressChart(){Cursor=Cursors.Hand;MouseMove+=Hover;MouseLeftButtonUp+=(_,e)=>{int i=Index(e.GetPosition(this).X);if(i>=0)MatchClicked?.Invoke(_points[i].SessionId);};}
    public void SetPoints(IReadOnlyList<ProgressMatch> points){_points=points.TakeLast(12).ToArray();InvalidateVisual();}
    private int Index(double x)
    {
        if(_points.Count==0)return -1;
        var fraction=(x-64)/Math.Max(1,ActualWidth-112);
        if(fraction<-.08||fraction>1.08)return -1;
        return Math.Clamp((int)Math.Round(fraction*(_points.Count-1)),0,_points.Count-1);
    }
    private void Hover(object sender,MouseEventArgs e)
    {
        int i=Index(e.GetPosition(this).X);ToolTip=i<0?null:$"{_points[i].Label} · {_points[i].Count} 次\n{_points[i].Scheme}\n交接常见波动 {_points[i].Handoff.Spread:0.#} ms\n两键同按 {_points[i].Overlap.Typical}\n双键空档 {_points[i].Gap.Typical}\n换向到开枪 {_points[i].Click.Typical}\n点击查看本局动作";
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);double left=64,width=Math.Max(1,ActualWidth-left-48);
        void Text(string value,double x,double y,Brush? color=null,double size=11)=>dc.DrawText(new FormattedText(value,CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,color??new SolidColorBrush(Color.FromRgb(158,181,203)),VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        if(_points.Count==0){Text("还没有同类动作。下载对应 Demo 后，趋势会自动出现。",12,70);return;}
        TimingDistribution MetricValue(ProgressMatch p)=>_metric switch{1=>p.Overlap,2=>p.Gap,3=>p.Click,_=>new(p.Handoff.Count,p.Handoff.Spread,null,null)};
        var brush=new SolidColorBrush(Color.FromRgb(116,214,186));
        double max=Math.Max(10,_points.Max(p=>MetricValue(p).Q75??MetricValue(p).Median??0)*1.2);
        double top=26,bottom=ActualHeight-45,plotHeight=bottom-top;
        for(int tick=0;tick<=3;tick++)
        {
            double y=bottom-plotHeight*tick/3;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(48,57,63)),1),new(left,y),new(left+width,y));
            Text((max*tick/3).ToString("0.#"),5,y-7,size:10);
        }
        Text("ms",5,0,size:10);
        Point? last=null;
        for(int i=0;i<_points.Count;i++)
        {
            double x=left+(_points.Count==1?width/2:i*width/(_points.Count-1));
            var value=MetricValue(_points[i]);
            if(value.Median.HasValue)
            {
                double Y(double v)=>bottom-v/max*plotHeight;
                var point=new Point(x,Y(value.Median.Value));bool enough=value.Count>=ProgressAnalysis.MinimumActionsPerPoint;
                if(last.HasValue&&enough)dc.DrawLine(new Pen(brush,2),last.Value,point);
                if(value.Q25.HasValue&&value.Q75.HasValue)
                {
                    var range=new Pen(new SolidColorBrush(Color.FromArgb(95,116,214,186)),7);
                    dc.DrawLine(range,new(x,Y(value.Q25.Value)),new(x,Y(value.Q75.Value)));
                }
                dc.DrawEllipse(enough?brush:new SolidColorBrush(Color.FromRgb(27,33,37)),new Pen(brush,2),point,4,4);
                if(_points.Count<=6)Text(value.Median.Value.ToString("0.#"),x-10,point.Y-20,brush,12);
                last=enough?point:null;
            }
            else{last=null;Text("未知",x-12,bottom-20);}
            if(_points.Count<=6||i==0||i==_points.Count-1){Text(_points[i].Label,x-35,bottom+10,size:10);Text($"{_points[i].Count} 次",x-13,bottom+25,size:10);}
        }
    }
}
