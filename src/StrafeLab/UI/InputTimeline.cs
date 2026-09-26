using System.Globalization;
using System.Windows;
using System.Windows.Media;
using StrafeLab.Core;
namespace StrafeLab.UI;
public sealed class InputTimeline : FrameworkElement
{
    private ActionReview? _action;
    public void Show(ActionReview? action){_action=action;InvalidateVisual();}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(_action is not {} a)return;
        void Text(string s,double x,double y,Brush brush)=>dc.DrawText(new FormattedText(s,CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        double start=-Math.Max(20,a.GapMs??0),end=Math.Max(60,Math.Max(a.ClickMs,Math.Max(a.OverlapMs??0,a.HoldMs??0)));
        double X(double ms)=>65+(ms-start)/(end-start)*Math.Max(1,ActualWidth-215);
        var muted=new SolidColorBrush(Color.FromRgb(138,167,192));var cyan=new SolidColorBrush(Color.FromRgb(106,215,194));
        var blue=new SolidColorBrush(Color.FromRgb(113,174,250));var keys=a.Direction.Split('→');
        dc.DrawLine(new Pen(muted,1),new(X(start),52),new(X(end),52));
        void Mark(double ms,double y,string label,Brush color)
        {dc.DrawLine(new Pen(color,1),new(X(ms),52),new(X(ms),y));dc.DrawEllipse(color,null,new(X(ms),52),3,3);Text(label,Math.Clamp(X(ms)-8,0,Math.Max(0,ActualWidth-150)),y+3,color);}
        if(a.GapMs>0)Mark(-a.GapMs.Value,6,$"松 {keys[0]}（提前 {a.GapMs:0.#} ms）",muted);
        Mark(0,74,$"按 {keys.Last()} · 0 ms",cyan);
        Mark(a.ClickMs,104,$"按鼠标左键 · {a.ClickMs:0.#} ms",blue);
        if(a.HoldMs.HasValue)Mark(a.HoldMs.Value,23,$"松 {keys.Last()} · {a.HoldMs:0.#} ms",muted);
        if(a.OverlapMs>0)
        {dc.DrawLine(new Pen(cyan,6),new(X(0),52),new(X(a.OverlapMs.Value),52));Text($"两键同按 {a.OverlapMs:0.#} ms",0,140,cyan);}
        else Text(a.GapMs.HasValue?$"双键空档 {a.GapMs:0.#} ms":"按键交接时长未知",0,140,muted);
        Text("本地输入时间线；开枪标记是鼠标按下",Math.Max(170,ActualWidth-265),140,muted);
    }
}
