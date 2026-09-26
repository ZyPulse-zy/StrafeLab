using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace StrafeLab.Core;

public sealed class SessionStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly object _gate = new();
    private readonly Dictionary<string,long> _revisions=[];

    public SessionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Environment.GetEnvironmentVariable("STRAFELAB_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StrafeLab");
        SessionsDirectory = Path.Combine(RootDirectory, "sessions");
        Directory.CreateDirectory(SessionsDirectory);
    }

    public string RootDirectory { get; }
    public string SessionsDirectory { get; }

    public void Save(SessionDocument session)
    {
        lock (_gate)
        {
            if(_revisions.TryGetValue(session.SessionId,out var previous)&&session.Revision<previous)return;
            Directory.CreateDirectory(SessionsDirectory);
            var path = GetPath(session.SessionId);
            var temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, session, _jsonOptions);
            File.Move(temporary, path, overwrite: true);
            _revisions[session.SessionId]=session.Revision;
        }
    }

    public SessionDocument? Load(string sessionId)
    {
        var path = GetPath(sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<SessionDocument>(stream, _jsonOptions);
        }
        catch { return null; }
    }

    public IReadOnlyList<SessionSummary> LoadRecentSummaries(int limit = 30)
    {
        var result = new List<SessionSummary>();
        foreach (var path in Directory.EnumerateFiles(SessionsDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(Math.Max(1, limit)))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var session = JsonSerializer.Deserialize<SessionDocument>(stream, _jsonOptions);
                if (session is not null) result.Add(TrendAnalyzer.Summarize(session));
            }
            catch { }
        }

        return result;
    }

    public string ExportCsv(string id)
    {
        var s=Load(id)??throw new IOException("会话不存在");
        var path=Path.Combine(RootDirectory,$"{s.SessionId}-transitions.csv");
        var lines=new List<string>{"seconds,from,to,gap_ms,overlap_ms,reverse_hold_ms,mouse1_ms,confidence"};
        foreach(var t in s.Transitions)lines.Add(FormattableString.Invariant($"{(t.TimestampUs-s.SessionStartUs)/1e6:F6},{t.From},{t.To},{t.GapUs/1000d},{t.OverlapUs/1000d},{t.ReverseHoldUs/1000d},{t.ShotDeltaUs/1000d},{t.Confidence}"));
        File.WriteAllLines(path,lines,System.Text.Encoding.UTF8);return path;
    }
    public string GetPath(string sessionId) => Guid.TryParseExact(sessionId,"N",out _) ? Path.Combine(SessionsDirectory, $"{sessionId}.json") : throw new ArgumentException("Invalid session id");
}

public static class TrendAnalyzer
{
    public static SessionSummary Summarize(SessionDocument session)
    {
        var transitions = session.Transitions.Where(x => x.Confidence >= 0.55).ToArray();
        var shots = session.Shots.Where(x => x.Confidence >= 0.75).ToArray();
        var modelShots=shots.Where(x=>x.ModelConfidence>=0.75&&CounterStrafeCohort.IsRifle(x.WeaponName)&&
            x.RelatedTransitionUs.HasValue&&x.DeltaFromTransitionUs is >=0 and <=250_000).ToArray();
        return new SessionSummary
        {
            SessionId = session.SessionId,
            StartedAtUtc = session.StartedAtUtc,
            Map = session.Map ?? session.GsiEvents.LastOrDefault()?.Snapshot.Map ?? "未知地图",
            TransitionCount = transitions.Length,
            ShotCount = shots.Length,
            AverageGapMs = transitions.Length == 0 ? 0 : transitions.Average(x => x.GapUs.GetValueOrDefault() / 1000d),
            AverageOverlapMs = transitions.Length == 0 ? 0 : transitions.Average(x => x.OverlapUs.GetValueOrDefault() / 1000d),
            FireWindowRate = modelShots.Length == 0 ? null : modelShots.Count(x => x.IsWithinFireWindow) / (double)modelShots.Length,
            ConfidenceRate = session.Transitions.Count == 0 ? 0 :
                session.Transitions.Count(x => x.Confidence >= 0.75) / (double)session.Transitions.Count
        };
    }

    public static IReadOnlyList<double> AverageGapTrend(IEnumerable<SessionSummary> summaries)
        => summaries.OrderBy(x => x.StartedAtUtc).Select(x => x.AverageGapMs).ToArray();

}
