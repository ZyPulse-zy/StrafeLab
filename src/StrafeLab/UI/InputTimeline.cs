using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using StrafeLab.Core;
namespace StrafeLab.UI;
public sealed class InputTimeline : FrameworkElement
{
    private ActionReview? _action;
    public void Show(ActionReview? action){_action=action;AutomationProperties.SetName(this,action==null?"未选择动作":"按键时间线，"+action.Direction+"，反向后 "+action.ClickMs.ToString("0.#")+" 毫秒按鼠标左键");InvalidateVisual();}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(_action is not {} a)return;
        var muted=B("#A0A795");var blue=B("#C6FF4A");var amber=B("#A0A795");var teal=B("#E8EBDD");
        void Text(string s,double x,double y,Brush? brush=null)=>dc.DrawText(new FormattedText(s,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,TacticalDrawing.Typeface,11,brush??muted,VisualTreeHelper.GetDpi(this).PixelsPerDip),new(x,y));
        double start=-Math.Max(35,(a.GapMs??0)+15),end=Math.Max(80,Math.Max(a.ClickMs,Math.Max(a.OverlapMs??0,a.HoldMs??0))+25);
        double X(double ms)=>74+(ms-start)/(end-start)*Math.Max(1,ActualWidth-235);
        var keys=a.Direction.Split('→');double? originalRelease=a.OverlapMs>0?a.OverlapMs:a.GapMs.HasValue?-a.GapMs.Value:null;
        for(int i=0;i<3;i++)dc.DrawLine(new Pen(B("#34392E"),1),new(74,45+i*48),new(ActualWidth-12,45+i*48));
        dc.DrawLine(new Pen(B("#747E68"),1){DashStyle=DashStyles.Dash},new(X(0),23),new(X(0),164));Text("反向按下 = 0 ms",Math.Max(74,X(0)-8),0);
        Text(keys[0],0,34,amber);Text(keys.Last(),0,82,blue);Text("Mouse1",0,130,teal);
        if(originalRelease.HasValue){dc.DrawLine(new Pen(amber,8),new(X(start),45),new(X(originalRelease.Value),45));Text($"松开 {originalRelease:0.#} ms",X(originalRelease.Value)+8,32,amber);}
        else Text("松开时刻缺失",82,32);
        dc.DrawLine(new Pen(blue,8){DashStyle=a.HoldMs.HasValue?DashStyles.Solid:DashStyles.Dash},new(X(0),93),new(X(a.HoldMs??end),93));
        Text(a.HoldMs.HasValue?$"松开 {a.HoldMs:0.#} ms":"松开时刻缺失",a.HoldMs.HasValue?X(a.HoldMs.Value)+8:Math.Max(82,ActualWidth-150),80,blue);
        dc.DrawEllipse(teal,null,new(X(a.ClickMs),141),5,5);Text($"按下 {a.ClickMs:0.#} ms",X(a.ClickMs)+10,128,teal);
        Text(a.OverlapMs>0?$"两键同按 {a.OverlapMs:0.#} ms":a.GapMs.HasValue?$"双键空档 {a.GapMs:0.#} ms":"两键交接时长未知",0,178);
        Text("本地输入记录；鼠标按下不等于子弹射出",Math.Max(200,ActualWidth-276),178);
    }
    private static SolidColorBrush B(string c)=>(SolidColorBrush)new BrushConverter().ConvertFromString(c)!;
}
