using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using StrafeLab.Core;

namespace StrafeLab.UI;

public sealed class ProgressChart : FrameworkElement
{
    private IReadOnlyList<ProgressMatch> _points=[];
    private int _metric,_selected=-1;
    private const double Left=52,Right=30,Top=30,Foot=49;
    public int Metric {get=>_metric;set{_metric=value;InvalidateVisual();}}
    public event Action<string>? MatchClicked;
    protected override AutomationPeer OnCreateAutomationPeer()=>new ChartPeer(this);
    private sealed class ChartPeer(ProgressChart owner):FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore()=>nameof(ProgressChart);
        protected override AutomationControlType GetAutomationControlTypeCore()=>AutomationControlType.Custom;
    }
    public ProgressChart()
    {
        Focusable=true;Cursor=Cursors.Hand;
        MouseMove+=(_,e)=>Select(Index(e.GetPosition(this).X));
        MouseLeave+=(_,_)=>{if(!IsKeyboardFocused)Select(-1);};
        MouseLeftButtonUp+=(_,e)=>{Select(Index(e.GetPosition(this).X));Focus();OpenSelected();};
        GotKeyboardFocus+=(_,_)=>Select(_selected<0?_points.Count-1:_selected);
        LostKeyboardFocus+=(_,_)=>InvalidateVisual();
        KeyDown+=(_,e)=>
        {
            if(e.Key is Key.Left or Key.Right){Select(Math.Clamp((_selected<0?0:_selected)+(e.Key==Key.Left?-1:1),0,Math.Max(0,_points.Count-1)));e.Handled=true;}
            else if(e.Key is Key.Enter or Key.Space){OpenSelected();e.Handled=true;}
        };
    }
    public void SetPoints(IReadOnlyList<ProgressMatch> points)
    {
        var selected=_selected>=0&&_selected<_points.Count?_points[_selected].SessionId:null;
        _points=points.TakeLast(12).ToArray();Select(selected==null?-1:Array.FindIndex(_points.ToArray(),p=>p.SessionId==selected));
    }
    private TimingDistribution Value(ProgressMatch p)=>_metric switch
    {1=>p.Overlap,2=>p.Gap,3=>p.Click,4=>new(p.Handoff.Count,p.Handoff.Count<2?null:p.Handoff.Spread,null,null),_=>p.Handoff};
    private void OpenSelected(){if(_selected>=0&&_selected<_points.Count)MatchClicked?.Invoke(_points[_selected].SessionId);}
    private double X(int i)=>Left+(_points.Count==1?(ActualWidth-Left-Right)/2:i*Math.Max(1,ActualWidth-Left-Right)/Math.Max(1,_points.Count-1));
    private int Index(double x)
    {
        if(_points.Count==0||x<Left-12||x>ActualWidth-Right+12)return -1;
        return _points.Count==1?0:Math.Clamp((int)Math.Round((x-Left)/Math.Max(1,ActualWidth-Left-Right)*(_points.Count-1)),0,_points.Count-1);
    }
    private void Select(int index)
    {
        _selected=index;
        string? summary=index<0||index>=_points.Count?null:
            $"{_points[index].Label} · {_points[index].Map} · {_points[index].Count} 次动作\n{_points[index].Scheme}\n交接 {_points[index].Handoff.Typical}（−空档 / +同按）\n两键同按 {_points[index].Overlap.Typical}\n双键空档 {_points[index].Gap.Typical}\n换向到开枪 {_points[index].Click.Typical}\n点击或按 Enter 查看本局动作";
        ToolTip=summary;AutomationProperties.SetItemStatus(this,summary??"未选择对局");InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(ActualWidth<120||ActualHeight<100)return;
        dc.DrawRectangle(TacticalDrawing.Background,null,new(0,0,ActualWidth,ActualHeight));
        double width=ActualWidth-Left-Right,bottom=ActualHeight-Foot,height=bottom-Top;
        void Text(string s,double x,double y,Brush? brush=null,double size=10,TextAlignment align=TextAlignment.Left,double maxWidth=double.PositiveInfinity)
            =>TacticalDrawing.Label(this,dc,s,x,y,brush,size,maxWidth,align);
        var distributions=_points.Select(Value).ToArray();
        var values=distributions.SelectMany(v=>new[]{v.Median,v.Q25,v.Q75}).Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).ToArray();
        double min=_metric==0?Math.Min(-10,values.DefaultIfEmpty(0).Min()):0;
        double max=Math.Max(10,values.DefaultIfEmpty(0).Max());
        double step=NiceStep((max-min)/4);
        min=Math.Floor(min/step)*step;max=Math.Ceiling(max/step)*step;
        if(max==min)max=min+step;
        double Y(double v)=>bottom-(v-min)/(max-min)*height;
        var gridPen=new Pen(TacticalDrawing.Border,1);
        Text("ms",0,0);
        Text(_points.Count==0?"暂无对局":$"最近 {_points.Count} 局",ActualWidth-Right,0,align:TextAlignment.Right);
        for(double tick=min;tick<=max+step*.1;tick+=step)
        {
            double y=Y(tick);dc.DrawLine(new Pen(tick==0?TacticalDrawing.Gray:TacticalDrawing.Border,1),new(Left,y),new(Left+width,y));
            Text(tick.ToString("0.#"),Left-10,y-7,align:TextAlignment.Right);
            dc.DrawLine(gridPen,new(Left-4,y),new(Left,y));
        }
        for(int i=0;i<_points.Count;i++)dc.DrawLine(gridPen,new(X(i),Top),new(X(i),bottom));
        if(_points.Count==0)
        {
            Text("配对 Demo 后显示逐局变化",Left+width/2,Top+height/2-10,TacticalDrawing.Muted,12,TextAlignment.Center);
            return;
        }
        // The band joins only consecutive sufficiently sampled matches; missing edges break it.
        for(int i=1;i<_points.Count;i++)
        {
            var a=distributions[i-1];var b=distributions[i];
            if(!Enough(a)||!Enough(b)||!a.Q25.HasValue||!a.Q75.HasValue||!b.Q25.HasValue||!b.Q75.HasValue)continue;
            var band=new StreamGeometry();using(var ctx=band.Open())
            {
                ctx.BeginFigure(new(X(i-1),Y(a.Q25.Value)),true,true);
                ctx.LineTo(new(X(i),Y(b.Q25.Value)),true,false);
                ctx.LineTo(new(X(i),Y(b.Q75.Value)),true,false);
                ctx.LineTo(new(X(i-1),Y(a.Q75.Value)),true,false);
            }
            band.Freeze();dc.DrawGeometry(TacticalDrawing.Band,null,band);
        }
        for(int i=1;i<_points.Count;i++)
        {
            var old=_points[i-1];var current=_points[i];
            if(old.SchemeId==null||current.SchemeId==null||old.SchemeId==current.SchemeId)continue;
            double x=(X(i-1)+X(i))/2;
            dc.DrawLine(new Pen(TacticalDrawing.Gray,1){DashStyle=DashStyles.Dash},new(x,Top),new(x,bottom));
            Text("方案",x+4,13,TacticalDrawing.Muted,maxWidth:Math.Max(20,ActualWidth-x-8));
        }
        Point? last=null;int labelStep=Math.Max(1,(int)Math.Ceiling(_points.Count/Math.Max(2,width/112)));
        for(int i=0;i<_points.Count;i++)
        {
            double x=X(i);var value=distributions[i];bool enough=Enough(value);
            if(i==_selected)dc.DrawLine(new Pen(TacticalDrawing.Ink,1){DashStyle=DashStyles.Dash},new(x,Top),new(x,bottom));
            if(value.Median is {} median&&double.IsFinite(median))
            {
                var p=new Point(x,Y(median));
                if(last.HasValue&&enough)dc.DrawLine(new Pen(TacticalDrawing.Accent,2),last.Value,p);
                if(value.Q25.HasValue&&value.Q75.HasValue)
                {
                    dc.DrawLine(new Pen(TacticalDrawing.Gray,1),new(x,Y(value.Q25.Value)),new(x,Y(value.Q75.Value)));
                    dc.DrawLine(new Pen(TacticalDrawing.Gray,1),new(x-3,Y(value.Q25.Value)),new(x+3,Y(value.Q25.Value)));
                    dc.DrawLine(new Pen(TacticalDrawing.Gray,1),new(x-3,Y(value.Q75.Value)),new(x+3,Y(value.Q75.Value)));
                }
                if(i==_selected)dc.DrawEllipse(TacticalDrawing.Background,new Pen(TacticalDrawing.Ink,1),p,8,8);
                dc.DrawEllipse(enough?TacticalDrawing.Accent:TacticalDrawing.Background,new Pen(TacticalDrawing.Accent,1.5),p,3.5,3.5);
                if(_points.Count<=4||i==_selected)Text(median.ToString("0.#"),Math.Clamp(x,Left+12,Left+width-12),Math.Max(Top,p.Y-23),TacticalDrawing.Accent,12,TextAlignment.Center);
                last=enough?p:null;
            }
            else{last=null;Text("缺失",x,bottom-20,align:TextAlignment.Center);}
            if(i%labelStep==0||i==_points.Count-1)
            {
                // Avoid overlapping the last two labels in a narrow window.
                if(i!=_points.Count-1&&i+labelStep>=_points.Count-1&&i>0)continue;
                Text(_points[i].Started.ToLocalTime().ToString("MM-dd"),x,bottom+10,align:TextAlignment.Center);
                Text($"{value.Count} 次",x,bottom+26,align:TextAlignment.Center);
            }
        }
        if(IsKeyboardFocused)dc.DrawRectangle(null,new Pen(TacticalDrawing.Accent,1){DashStyle=DashStyles.Dash},new(1,1,ActualWidth-2,ActualHeight-2));
    }
    private static bool Enough(TimingDistribution value)=>value.Count>=ProgressAnalysis.MinimumActionsPerPoint&&value.Median.HasValue;
    private static double NiceStep(double raw)
    {
        var power=Math.Pow(10,Math.Floor(Math.Log10(Math.Max(.01,raw))));var normalized=raw/power;
        return (normalized<=1?1:normalized<=2?2:normalized<=5?5:10)*power;
    }
}
