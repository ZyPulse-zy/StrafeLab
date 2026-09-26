using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using StrafeLab.Core;
namespace StrafeLab.UI;

/// <summary>Order distribution only; colors do not label success or failure.</summary>
public sealed class HandoffBar : FrameworkElement
{
    private int[] _counts=[];
    public void Show(ProgressSnapshot s)
    {
        _counts=[s.ReverseFirst,s.ReleaseFirst,s.SameTimestamp,s.UnknownHandoff];
        ToolTip=s.HandoffSummary;AutomationProperties.SetName(this,s.HandoffSummary);InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);double total=_counts.Sum(),x=0;
        string[] colors=["#C6FF4A","#768168","#A0A795","#34392E"];
        dc.PushClip(new RectangleGeometry(new Rect(0,0,ActualWidth,ActualHeight)));
        dc.DrawRectangle(TacticalDrawing.Surface,null,new(0,0,ActualWidth,ActualHeight));
        if(total>0)for(int i=0;i<_counts.Length;i++)
        {double w=ActualWidth*_counts[i]/total;if(w>0)dc.DrawRectangle((Brush)new BrushConverter().ConvertFromString(colors[i])!,null,new(x,0,Math.Max(0,w-2),ActualHeight));x+=w;}
        dc.Pop();
    }
}
