namespace StrafeLab.Core;

public sealed record ProgressCohort(string Key,string Label,int Actions,int Matches);
public sealed record TimingDistribution(int Count,double? Median,double? Q25,double? Q75)
{
    public double? Spread=>Q75-Q25;
    public string Typical=>Median.HasValue?$"{Median:0.#} ms":"暂无数据";
    public string Range=>Q25.HasValue?$"常见范围 {Q25:0.#}–{Q75:0.#} ms":"记录不足";
}
public sealed record ProgressMatch(string SessionId,DateTime Started,string Map,int Count,string Scheme,
    TimingDistribution Overlap,TimingDistribution Gap,TimingDistribution Click,TimingDistribution Handoff)
{
    public string Label=>Started.ToLocalTime().ToString("MM-dd HH:mm");
    public string? SchemeId {get;init;}
}
public sealed record DirectionHandoff(string Direction,int Count,TimingDistribution Handoff)
{
    public string MedianText=>Handoff.Median?.ToString("+0.#;-0.#;0")??"—";
    public string SpreadText=>Handoff.Count>=2?Handoff.Spread?.ToString("0.#")??"—":"—";
}
public sealed record ProfileComparison(string Id,string Name,int Count,int Matches,string Parameters,
    TimingDistribution Overlap,TimingDistribution Gap,TimingDistribution Click,TimingDistribution Handoff)
{
    public bool Ready=>Count>=ProgressAnalysis.MinimumActions&&Matches>=ProgressAnalysis.MinimumMatches&&Handoff.Count>=ProgressAnalysis.MinimumActions;
    public string Coverage=>$"{Count} 次 / {Matches} 局";
    public string Status=>Ready?"可作描述性对比":"继续积累";
}
public sealed record ProgressSnapshot(ProgressCohort? Cohort,IReadOnlyList<ProgressMatch> Matches,
    TimingDistribution Overlap,TimingDistribution Gap,TimingDistribution Click,
    IReadOnlyList<ProfileComparison> Profiles,int UnknownProfileActions)
{
    public int Count=>Matches.Sum(m=>m.Count);
    public bool Ready=>Count>=ProgressAnalysis.MinimumActions&&Matches.Count>=ProgressAnalysis.MinimumMatches;
    public int ReverseFirst {get;init;}
    public int ReleaseFirst {get;init;}
    public int SameTimestamp {get;init;}
    public int UnknownHandoff {get;init;}
    public TimingDistribution Handoff {get;init;}=new(0,null,null,null);
    public IReadOnlyList<double> HandoffValues {get;init;}=[];
    public IReadOnlyList<DirectionHandoff> Directions {get;init;}=[];
    public string HandoffSummary=>Count==0?"配对完成后显示交接习惯":
        $"交接习惯：{ReverseFirst} 次先按反向键，{ReleaseFirst} 次先松原键，{SameTimestamp} 次同一记录时刻"+
        (UnknownHandoff>0?$"，{UnknownHandoff} 次边沿缺失":"")+"。这是顺序分布，不是好坏评分。";
}
public static class ProgressAnalysis
{
    // Product prompts for gathering a baseline, not statistical significance or game rules.
    public const int MinimumActions=30,MinimumMatches=3,MinimumActionsPerPoint=5;
    public static string WeaponName(string weapon)=>weapon switch
    {"ak47"=>"AK-47","m4a1"=>"M4A4","m4a1_silencer"=>"M4A1-S","awp"=>"AWP","ssg08"=>"SSG 08",
     "usp_silencer"=>"USP-S","glock"=>"Glock-18","deagle"=>"沙漠之鹰","galilar"=>"加利尔 AR",
     "famas"=>"FAMAS","mac10"=>"MAC-10","mp9"=>"MP9",_=>weapon.ToUpperInvariant()};
    public static string Key(ActionReview a)=>$"{a.Weapon}|{a.Stance}|{a.InitialSpeedBand}";
    private static IEnumerable<(MatchReport Report,ActionReview Action)> Rows(IEnumerable<MatchReport> reports)
        =>reports.Where(r=>r.Version==MatchReport.CurrentVersion&&r.Alignment?.IsReliable==true)
            .SelectMany(r=>r.Actions.Where(a=>a.EligibleForStats&&a.PriorSpeed>=CounterStrafeCohort.MinimumInitialSpeed)
                .Select(a=>(Report:r,Action:a)));
    public static IReadOnlyList<ProgressCohort> Cohorts(IEnumerable<MatchReport> reports)=>Rows(reports)
        .GroupBy(x=>Key(x.Action)).Select(g=>new ProgressCohort(g.Key,
            $"{WeaponName(g.First().Action.Weapon)} · {g.First().Action.Stance} · {g.First().Action.InitialSpeedBand.Replace("起速","初速")}",
            g.Count(),g.Select(x=>x.Report.SessionId).Distinct().Count()))
        .OrderByDescending(g=>g.Matches).ThenByDescending(g=>g.Actions).ThenBy(g=>g.Key).ToArray();
    public static TimingDistribution Distribution(IEnumerable<double?> source)
    {
        var values=source.Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).Order().ToArray();
        double? Q(double q){if(values.Length==0)return null;double i=(values.Length-1)*q;return values[(int)i]+(values[(int)Math.Ceiling(i)]-values[(int)i])*(i-(int)i);}
        return new(values.Length,Q(.5),Q(.25),Q(.75));
    }
    // Signed key handoff: overlap positive, a no-key gap negative. Unknown edges stay unknown.
    public static double? Handoff(ActionReview a)=>a.OverlapMs.HasValue&&a.GapMs.HasValue?a.OverlapMs-a.GapMs:null;
    public static ProgressSnapshot Build(IEnumerable<MatchReport> reports,string? key,KeyboardProfileHistory history)
    {
        var reportArray=reports.ToArray();var cohort=Cohorts(reportArray).FirstOrDefault(c=>c.Key==key);
        var rows=Rows(reportArray).Where(x=>Key(x.Action)==key).ToArray();
        var matches=rows.GroupBy(x=>x.Report.SessionId).OrderBy(g=>g.First().Report.StartedAtUtc).Select(g=>
        {
            var r=g.First().Report;var a=g.Select(x=>x.Action).ToArray();
            return new ProgressMatch(r.SessionId,r.StartedAtUtc,r.Map,a.Length,history.For(r)?.Name??"参数未记录",
                Distribution(a.Select(x=>x.OverlapMs)),Distribution(a.Select(x=>x.GapMs)),Distribution(a.Select(x=>(double?)x.ClickMs)),Distribution(a.Select(Handoff))){SchemeId=history.For(r)?.Id};
        }).ToArray();
        var profiles=rows.Where(x=>history.For(x.Report)!=null).GroupBy(x=>history.For(x.Report)!.Id).Select(g=>
        {
            var p=history.Profiles.Single(p=>p.Id==g.Key);var a=g.Select(x=>x.Action).ToArray();
            return new ProfileComparison(p.Id,p.Name,a.Length,g.Select(x=>x.Report.SessionId).Distinct().Count(),p.Description,
                Distribution(a.Select(x=>x.OverlapMs)),Distribution(a.Select(x=>x.GapMs)),Distribution(a.Select(x=>(double?)x.ClickMs)),Distribution(a.Select(Handoff)));
        }).OrderBy(p=>history.Profiles.Single(h=>h.Id==p.Id).AppliedAtUtc).ToArray();
        return new(cohort,matches,Distribution(rows.Select(x=>x.Action.OverlapMs)),Distribution(rows.Select(x=>x.Action.GapMs)),
            Distribution(rows.Select(x=>(double?)x.Action.ClickMs)),profiles,rows.Count(x=>history.For(x.Report)==null))
        {
            ReverseFirst=rows.Count(x=>Handoff(x.Action)>0),ReleaseFirst=rows.Count(x=>Handoff(x.Action)<0),
            SameTimestamp=rows.Count(x=>Handoff(x.Action)==0),UnknownHandoff=rows.Count(x=>!Handoff(x.Action).HasValue),
            Handoff=Distribution(rows.Select(x=>Handoff(x.Action))),
            HandoffValues=rows.Select(x=>Handoff(x.Action)).Where(x=>x.HasValue&&double.IsFinite(x.Value)).Select(x=>x!.Value).ToArray(),
            Directions=rows.GroupBy(x=>x.Action.Direction).OrderBy(g=>g.Key).Select(g=>new DirectionHandoff(g.Key,g.Count(),Distribution(g.Select(x=>Handoff(x.Action))))).ToArray()
        };
    }
    public static string Trend(ProgressSnapshot snapshot)
    {
        var usable=snapshot.Matches.Where(m=>m.Count>=MinimumActionsPerPoint&&m.Handoff.Count>=MinimumActionsPerPoint).ToArray();
        if(usable.Length<2)return $"至少需要两局同类动作、每局 {MinimumActionsPerPoint} 次，才能对比变化。当前先把图中的数字作为基线。";
        if(usable.Length>=6)
        {
            int window=Math.Min(5,usable.Length/2);
            var earlier=Distribution(usable.Take(window).Select(m=>m.Handoff.Spread)).Median!.Value;
            var recent=Distribution(usable.TakeLast(window).Select(m=>m.Handoff.Spread)).Median!.Value;
            double difference=recent-earlier;
            return $"较早 {window} 局 → 最近 {window} 局，交接常见波动 {earlier:0.#} → {recent:0.#} ms（{(Math.Abs(difference)<.1?"基本相同":difference<0?"更一致":"波动增大")}）。每局等权，取各局波动的中位数；两组不重叠。这是习惯变化，不等于命中率或参数优劣。";
        }
        var first=usable[0];var last=usable[^1];var delta=last.Handoff.Spread!.Value-first.Handoff.Spread!.Value;
        var change=Math.Abs(delta)<.1?"基本相同":$"{(delta<0?"缩小":"扩大")} {Math.Abs(delta):0.#} ms";
        return $"{first.Label} → {last.Label}，交接常见波动{change}（{first.Handoff.Spread:0.#} → {last.Handoff.Spread:0.#} ms）。目前只有 {usable.Length} 局可比较，不能说明参数优劣；满 6 局后按前后多局比较。";
    }
    public static string Compare(ProfileComparison? before,ProfileComparison? after)
    {
        if(before==null||after==null||before.Id==after.Id)return "记录两套实际使用过的方案后，选择旧方案和新方案比较；不会自动修改键盘。";
        if(!before.Ready||!after.Ready)return $"先别根据这点数据判断哪套更好：每套方案在同一组内建议积累至少 {MinimumMatches} 局、{MinimumActions} 次；当前「{before.Name}」{before.Coverage}，「{after.Name}」{after.Coverage}。";
        var delta=after.Handoff.Spread!.Value-before.Handoff.Spread!.Value;
        return $"「{after.Name}」的交接波动比「{before.Name}」{(Math.Abs(delta)<.1?"基本不变":$"{(delta<0?"小":"大")} {Math.Abs(delta):0.#} ms")}；两键同按典型值 {before.Overlap.Typical} → {after.Overlap.Typical}，开枪间隔 {before.Click.Typical} → {after.Click.Typical}。这只是同期表现差异，练习、地图和对局环境也会影响；不据此宣布最佳 RT。";
    }
}
