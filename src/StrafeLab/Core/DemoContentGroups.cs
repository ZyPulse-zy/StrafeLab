namespace StrafeLab.Core;

public sealed class DemoContentInfo
{
    public string? Map {get;set;}
    public string State {get;set;}="未匹配";
    public string ReasonCode {get;set;}="sync";
    public string Detail {get;set;}="";
}
public sealed record DemoContentGroup(string Key,string Name,string State,string Reason,
    IReadOnlyList<DemoJob> Files,DateTime WrittenUtc,string Detail)
{
    public int FileCount=>Files.Count;
    public string Copies=>Files.Count==1?"1 个文件":$"{Files.Count} 个副本";
    public string UpdatedLocal=>WrittenUtc.ToLocalTime().ToString("MM-dd HH:mm");
    public bool Matches(string query)=>query.Length==0||Name.Contains(query,StringComparison.OrdinalIgnoreCase)||
        Files.Any(f=>f.Path.Contains(query,StringComparison.OrdinalIgnoreCase));
}
/// <summary>One row per DEM content hash; a multi-demo archive may occur in multiple rows.</summary>
public static class DemoContentGroups
{
    public static string Reason(string code)=>code switch
    {
        "no_recording"=>"缺少本地录制","map"=>"没有同地图录制","identity"=>"录像中未找到本机玩家",
        "sync"=>"时间锚点未通过","missing_motion_fields"=>"缺少位置 / 时间字段","invalid_clock"=>"时间证据不足",
        "invalid_archive"=>"压缩包无法读取","invalid_demo"=>"不是 CS2 录像","missing_parser"=>"解析依赖缺失","parser_error"=>"解析器不兼容",
        "size_limit"=>"超过处理限制","temporary_io"=>"文件暂时不可读","ignored"=>"已忽略","matched"=>"已关联本地录制",
        "unknown"=>"内容尚待核对",_=>"等待分析"
    };
    public static IReadOnlyList<DemoContentGroup> Build(IEnumerable<DemoJob> source,IEnumerable<MatchReport> reports)
    {
        var matched=reports.Where(r=>r.Alignment?.IsReliable==true&&r.DemoHash!=null).GroupBy(r=>r.DemoHash!).ToDictionary(g=>g.Key,g=>g.ToArray());
        var rows=source.SelectMany(j=>j.ContentHashes.Count==0?new[]{(Key:"file:"+j.Path.ToUpperInvariant(),Job:j)}:
            j.ContentHashes.Distinct().Select(h=>(Key:h,Job:j))).GroupBy(x=>x.Key);
        return rows.Select(g=>
        {
            var files=g.Select(x=>x.Job).DistinctBy(j=>j.Path,StringComparer.OrdinalIgnoreCase).OrderBy(j=>j.Path,StringComparer.OrdinalIgnoreCase).ToArray();
            matched.TryGetValue(g.Key,out var matches);
            var known=files.Select(f=>f.Contents.GetValueOrDefault(g.Key)).Where(c=>c!=null).Select(c=>c!).ToArray();
            string state,code,detail;
            if(matches?.Length>0)
            {
                state="已完成";code="matched";
                detail=$"可靠配对 {matches.Length} 份本地录制；可信本地按键 {matches.Sum(r=>r.Actions.Count(InputTimingAnalysis.IsEligible))} 次，Demo 可评估动作 {matches.Sum(r=>r.EligibleCount)} 次。";
            }
            else if(files.All(f=>f.UserIgnored||f.State=="已忽略")){state="已忽略";code="ignored";detail="此内容已停止自动处理，可用“重试所选”恢复；原文件保留。";}
            else
            {
                var severe=known.FirstOrDefault(c=>c.State=="需处理")??known.FirstOrDefault(c=>c.State=="失败");
                var fallback=files.FirstOrDefault(f=>f.State is "需处理" or "失败" && f.ContentHashes.Count<=1&&!f.Contents.ContainsKey(g.Key));
                if(severe!=null){state=severe.State;code=severe.ReasonCode;detail=severe.Detail;}
                else if(fallback!=null){state=fallback.State;code=fallback.FailureCode;detail=fallback.Detail;}
                else if(files.Any(f=>f.State is "分析中" or "待分析" or "等待下载完成")){state="处理中";code="pending";detail="文件稳定后自动核对；下载中的文件不会参与分析。";}
                else
                {
                    state="未匹配";var info=known.FirstOrDefault(c=>c.ReasonCode!="unknown");
                    code=info?.ReasonCode??"unknown";detail=info?.Detail??"旧任务未记录详细原因；可手动重试核对身份、地图和时间锚点。";
                }
            }
            string? map=matches?.FirstOrDefault()?.Map??known.FirstOrDefault(c=>!string.IsNullOrEmpty(c.Map))?.Map;
            var failure=files.FirstOrDefault(f=>f.FailureCode==code);
            if(state=="需处理")detail+="\n自动重试已停止；文件内容更新或手动重试后重新检查。"+
                (code is "missing_motion_fields" or "invalid_clock" or "parser_error" or "missing_parser"?"解析器更新后也会重新检查。":"");
            if(state=="失败"&&failure!=null)detail+=$"\n连续失败 {failure.FailureCount}/{DemoFailures.MaxAutomaticFailures} 次；下次重试 {failure.NextRetryUtc.ToLocalTime():MM-dd HH:mm}。";
            detail+="\n同一内容只显示一次；文件名、路径或下载时间不用于确认比赛配对。未配对录制中的可信按键仍可参与按键习惯趋势。";
            return new DemoContentGroup(g.Key,string.IsNullOrEmpty(map)?files[0].Name:map+(matches?.Length>0?$" · {matches[0].LocalTime}":" · 待配对"),state,Reason(code),files,files.Max(f=>f.WrittenUtc),detail);
        }).OrderByDescending(g=>g.WrittenUtc).ThenBy(g=>g.Key).ToArray();
    }
}
