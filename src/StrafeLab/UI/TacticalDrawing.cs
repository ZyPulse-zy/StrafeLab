using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace StrafeLab.UI;

internal static class TacticalDrawing
{
    public static readonly FontFamily Font=new("pack://application:,,,/StrafeLab;component/Assets/Fonts/#IBM Plex Mono, pack://application:,,,/StrafeLab;component/Assets/Fonts/#Sarasa Mono SC");
    public static readonly Typeface Typeface=new(Font,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
    public static readonly Brush Background=Color("#0B0C0A"), Surface=Color("#181A17"), Border=Color("#34392E"),
        Ink=Color("#E8EBDD"), Muted=Color("#A0A795"), Accent=Color("#C6FF4A"), Band=Color("#28351B"), Gray=Color("#768168");
    private static Brush Color(string hex){var brush=(SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;brush.Freeze();return brush;}
    public static void Label(FrameworkElement owner,DrawingContext dc,string value,double x,double y,Brush? brush=null,double size=11,double maxWidth=double.PositiveInfinity,TextAlignment alignment=TextAlignment.Left)
    {
        var text=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,Typeface,size,brush??Muted,VisualTreeHelper.GetDpi(owner).PixelsPerDip){TextAlignment=alignment};
        if(double.IsFinite(maxWidth)){text.MaxTextWidth=Math.Max(1,maxWidth);text.MaxLineCount=1;text.Trimming=TextTrimming.CharacterEllipsis;}
        dc.DrawText(text,new(x,y));
    }
}
