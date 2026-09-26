using System.IO;
using System.Text.Json;

namespace StrafeLab.Core;

// A user-confirmed notebook, never a HID configuration writer.
public sealed record KeyboardProfile(string Id,string Name,DateTime AppliedAtUtc,
    double AdTrigger,double WsTrigger,double RtPress,double RtRelease,string Notes)
{
    public string Description=>$"A/D {AdTrigger:0.###} · W/S {WsTrigger:0.###} · RT 按下 {RtPress:0.###} / 抬起 {RtRelease:0.###} mm";
}
public sealed class KeyboardProfileHistory
{
    public int Version {get;set;}=1;
    public List<KeyboardProfile> Profiles {get;set;}=[];
    public Dictionary<string,string> SessionAssignments {get;set;}=[];
    public KeyboardProfile? For(MatchReport report)
    {
        if(SessionAssignments.TryGetValue(report.SessionId,out var id))
            return Profiles.FirstOrDefault(p=>p.Id==id); // Empty explicit assignment means unknown.
        var lastObserved=report.Actions.Select(a=>a.LocalTime.ToUniversalTime().AddMilliseconds(a.ClickMs))
            .DefaultIfEmpty(report.StartedAtUtc.ToUniversalTime()).Max();
        if(Profiles.Any(p=>p.AppliedAtUtc>report.StartedAtUtc.ToUniversalTime()&&p.AppliedAtUtc<=lastObserved))return null;
        return Profiles.Where(p=>p.AppliedAtUtc<=report.StartedAtUtc.ToUniversalTime())
            .OrderByDescending(p=>p.AppliedAtUtc).FirstOrDefault();
    }
}
public sealed class KeyboardProfileStore(string root)
{
    private readonly string _path=Path.Combine(root,"keyboard-profiles.json");
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){WriteIndented=true};
    public KeyboardProfileHistory Load()
    {
        if(!File.Exists(_path))return new();
        var history=JsonSerializer.Deserialize<KeyboardProfileHistory>(File.ReadAllText(_path),Json)
            ??throw new InvalidDataException("参数记录为空，请保留文件并检查备份。");
        if(history.Version!=1)throw new InvalidDataException("参数记录版本不支持，未覆盖文件。");
        return history;
    }
    public KeyboardProfile Add(string name,double ad,double ws,double press,double release,string notes,DateTime atUtc)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>60)throw new ArgumentException("方案名称请填写 1–60 个字符。");
        if(new[]{ad,ws,press,release}.Any(v=>!double.IsFinite(v)||v<.005||v>4))
            throw new ArgumentException("行程请填写 0.005–4 mm 内的数字，并以官方驱动允许的范围为准。");
        var history=Load();var profile=new KeyboardProfile(Guid.NewGuid().ToString("N"),name.Trim(),atUtc.ToUniversalTime(),ad,ws,press,release,notes.Trim());
        history.Profiles.Add(profile);Save(history);return profile;
    }
    public void Assign(string sessionId,string? profileId)
    {
        var history=Load();
        if(profileId!=null&&!history.Profiles.Any(p=>p.Id==profileId))throw new ArgumentException("方案不存在。");
        history.SessionAssignments[sessionId]=profileId??"";Save(history);
    }
    private void Save(KeyboardProfileHistory history)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,JsonSerializer.Serialize(history,Json));
            if(File.Exists(_path))File.Copy(_path,_path+".bak",true);
            File.Move(temp,_path,true);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
}
