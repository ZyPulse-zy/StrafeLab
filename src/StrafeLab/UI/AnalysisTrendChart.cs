using System.Globalization;
using System.Windows;
using System.Windows.Media;
namespace StrafeLab.UI;
public sealed record TrendPoint(string Label,double? EvaluatedCount,double? Overlap);
public sealed class AnalysisTrendChart : FrameworkElement
{
    private IReadOnlyList<TrendPoint> _points=[];
    public void SetPoints(IReadOnlyList<TrendPoint> points){_points=points;InvalidateVisual();}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);double width=Math.Max(1,ActualWidth-112),left=88;
        void Label(string text,double x,double y,Brush? brush=null)=>dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,new Typeface("Segoe UI"),10,brush??Brushes.SlateGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        Label("统计动作数",0,12);Label("两键同按 ms",0,77);
        if(_points.Count==0){Label("暂无符合筛选条件的动作",left,50);return;}
        void Plot(Func<TrendPoint,double?> value,double top,double maximum,Brush color)
        {
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(34,53,83)),1),new(left,top+42),new(left+width,top+42));
            Point? last=null;
            for(int i=0;i<_points.Count;i++)
            {
                double x=left+(_points.Count==1?width/2:i*width/(_points.Count-1));var v=value(_points[i]);
                if(!v.HasValue){last=null;Label("—",x-4,top+23);continue;}
                var p=new Point(x,top+42-Math.Clamp(v.Value/maximum,0,1)*34);
                if(last.HasValue)dc.DrawLine(new Pen(color,2),last.Value,p);
                dc.DrawEllipse(color,null,p,3,3);Label(v.Value.ToString("0.#"),x-9,p.Y-16,color);last=p;
            }
        }
        Plot(p=>p.EvaluatedCount,16,Math.Max(1,_points.Max(p=>p.EvaluatedCount??0)),new SolidColorBrush(Color.FromRgb(99,230,168)));
        Plot(p=>p.Overlap,79,Math.Max(10,_points.Max(p=>p.Overlap??0)),new SolidColorBrush(Color.FromRgb(71,199,255)));
        for(int i=0;i<_points.Count;i++)if(_points.Count<=8||i==0||i==_points.Count-1)
            Label(_points[i].Label,left+(_points.Count==1?width/2:i*width/(_points.Count-1))-24,126);
    }
}
