using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

namespace StrafeLab.UI;

/// <summary>Signed, full-range histogram. Unknown edges never become zero observations.</summary>
public sealed class HandoffDistribution : FrameworkElement
{
    private double[] _values=[];
    public void Show(IReadOnlyList<double> values)
    {
        _values=values.Where(double.IsFinite).ToArray();
        AutomationProperties.SetHelpText(this,$"{_values.Length} 次完整交接。负值是双键空档，正值是两键同按；全部有限值均包含在横轴范围内。");
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(ActualWidth<80||ActualHeight<60)return;
        double left=14,width=ActualWidth-28,baseline=ActualHeight-30,top=26,center=left+width/2;
        void Text(string s,double x,double y,Brush? brush=null,TextAlignment align=TextAlignment.Left)=>TacticalDrawing.Label(this,dc,s,x,y,brush,10,alignment:align);
        Text("空档 −",left,0);Text("+ 同按",left+width,0,TacticalDrawing.Accent,TextAlignment.Right);
        dc.DrawLine(new Pen(TacticalDrawing.Border,1),new(left,baseline),new(left+width,baseline));
        dc.DrawLine(new Pen(TacticalDrawing.Ink,1){DashStyle=DashStyles.Dash},new(center,top-3),new(center,baseline+4));
        if(_values.Length==0){Text("等待完整的两键交接记录",center,40,TacticalDrawing.Muted,TextAlignment.Center);Text("0",center,baseline+8,null,TextAlignment.Center);return;}
        double limit=Math.Max(20,Math.Ceiling(_values.Max(Math.Abs)/10)*10);
        const int count=41;var bins=new int[count];
        foreach(var value in _values){int index=Math.Clamp((int)Math.Floor((value+limit)/(2*limit)*count),0,count-1);bins[index]++;}
        double max=bins.Max(),step=width/count;
        for(int i=0;i<count;i++)
        {
            if(bins[i]==0)continue;
            double h=Math.Max(2,(baseline-top)*bins[i]/max),x=left+i*step;
            dc.DrawRectangle(i<count/2?TacticalDrawing.Gray:TacticalDrawing.Accent,null,new(x+1,baseline-h,Math.Max(1,step-2),h));
        }
        for(int i=0;i<=4;i++){double v=-limit+i*limit/2,x=left+i*width/4;dc.DrawLine(new Pen(TacticalDrawing.Border,1),new(x,baseline),new(x,baseline+4));Text(v.ToString("0.#"),x,baseline+8,null,TextAlignment.Center);}
        ToolTip=$"全部 {_values.Length} 次交接；范围 {_values.Min():0.#}–{_values.Max():0.#} ms。每柱最多 {bins.Max()} 次。";
    }
}
