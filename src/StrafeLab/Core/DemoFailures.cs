using System.IO;
using System.Text.Json;

namespace StrafeLab.Core;

public sealed record DemoFailure(string Code,string Explanation,bool Retryable);
public static class DemoFailures
{
    public const string ParserRevision="1.9";
    public const int MaxAutomaticFailures=3;
    public static DemoFailure Classify(Exception error)
    {
        var text=error.Message;
        foreach(var line in text.Split('\n'))
            if(line.StartsWith("STRAFELAB_ERROR ",StringComparison.Ordinal))
                try
                {
                    using var json=JsonDocument.Parse(line[16..]);
                    return ForCode(json.RootElement.GetProperty("code").GetString()??"parser_error");
                }
                catch(JsonException){}catch(KeyNotFoundException){}catch(InvalidOperationException){}
        if(text.Contains("Column not found",StringComparison.OrdinalIgnoreCase)||text.Contains("game_time unavailable",StringComparison.OrdinalIgnoreCase))return ForCode("missing_motion_fields");
        if(text.Contains("clock is irregular",StringComparison.OrdinalIgnoreCase)||text.Contains("Not enough samples to verify",StringComparison.OrdinalIgnoreCase))return ForCode("invalid_clock");
        if(text.Contains("不是 Source 2 Demo",StringComparison.Ordinal))return ForCode("invalid_demo");
        if(error is InvalidDataException||text.Contains("Central Directory",StringComparison.OrdinalIgnoreCase)||text.Contains("解压长度不符")||text.Contains("尚未下载完整"))return ForCode("invalid_archive");
        if(text.Contains("未找到本地 Demo 解析器")||text.Contains("not installed")||text.Contains("缺少本地 Python"))return ForCode("missing_parser");
        if(text.Contains("超出限制")||text.Contains("保护上限"))return ForCode("size_limit");
        if(text.Contains("demo parse failed:",StringComparison.OrdinalIgnoreCase))return ForCode("parser_error");
        return ForCode("temporary_io");
    }
    public static DemoFailure ForCode(string code)=>code switch
    {
        "missing_motion_fields"=>new(code,"解析结果缺少位置或时间字段，无法核对速度。可重新下载 Demo 或在解析器更新后重试；本地按键统计保留。",false),
        "invalid_clock"=>new(code,"Demo 的时间或连续采样不足，无法可靠同步。可重新下载或换用完整录像；不会放宽置信度。",false),
        "invalid_archive"=>new(code,"压缩包无法完整读取。请确认下载完成、文件确为比赛录像，或重新下载。",false),
        "invalid_demo"=>new(code,"文件不是可识别的 CS2 Source 2 Demo。请检查是否下载了正确的比赛录像。",false),
        "missing_parser"=>new(code,"本地解析依赖缺失。请恢复完整程序目录后手动重试。",false),
        "size_limit"=>new(code,"录像或压缩包超过本地处理限制。请导入单独的 Demo 文件。",false),
        "parser_error"=>new(code,"当前解析器无法读取这份 Demo。可重新下载，或在解析器更新后重试。",false),
        _=>new("temporary_io","文件暂时无法读取，稍后自动重试。",true)
    };
    public static bool IsBlocked(DemoJob job)=>job.State=="需处理"||job.UserIgnored;
    public static bool IsPending(DemoJob job,DateTime now)=>!IsBlocked(job)&&
        (job.State is "待分析" or "等待下载完成"||job.State=="失败"&&job.NextRetryUtc<=now);
    public static void Reset(DemoJob job)
    {
        job.FailureCode="";job.FailureDiagnostic="";job.FailureCount=0;job.FailureRevision="";
        job.UserIgnored=false;job.NextRetryUtc=default;job.Attempts=0;job.SessionStamp="";
        job.AttemptedSessions.Clear();job.Contents.Clear();job.State="待分析";job.Detail="等待重新分析";
    }
    public static void Apply(DemoJob job,Exception error,DateTime now)
    {
        var failure=Classify(error);job.FailureCount++;job.FailureCode=failure.Code;job.FailureRevision=ParserRevision;
        job.FailureDiagnostic=error.Message.Length>1600?error.Message[..1600]:error.Message;
        bool blocked=!failure.Retryable||job.FailureCount>=MaxAutomaticFailures;
        job.State=blocked?"需处理":"失败";
        job.NextRetryUtc=blocked?default:now.AddMinutes(Math.Pow(2,job.FailureCount));
        job.Detail=(blocked&&failure.Retryable?"连续三次读取失败，已停止自动重试。请检查文件或目录后手动重试。":failure.Explanation)+
            (blocked?" 文件更新或点击“重试所选”后重新检查。":"");
    }
    public static void Migrate(DemoJob job)
    {
        if(job.State=="失败"&&job.FailureCode.Length==0)
        {
            job.FailureCount=Math.Max(0,Math.Min(job.Attempts,MaxAutomaticFailures)-1);
            Apply(job,new IOException(job.Detail),DateTime.UtcNow);
        }
        else if(job.State=="需处理"&&job.FailureRevision!=ParserRevision&&job.FailureCode is "missing_motion_fields" or "invalid_clock" or "parser_error" or "missing_parser")
            Reset(job);
    }
}
