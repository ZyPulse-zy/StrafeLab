using StrafeLab.Core;
using StrafeLab.Platform;
using System.Text.Json;
var id=File.ReadAllText(args[1]).Trim();
var result=await new DemoService(new SteamLocator()).ParseAsync(Path.GetFullPath(args[0]),id);
var summary=new {result.Parser,result.Error,result.MapHint,result.TickRate,
    observations=result.Observations.Count,players=result.Observations.Where(o=>o.Kind=="player_sample").Select(o=>o.SteamId).Distinct().Count(),
    ground=result.Observations.Count(o=>o.OnGround==true&&o.IsAlive==true),
    typed=result.Observations.Count(o=>o.MoveType==2),fire=result.Observations.Count(o=>o.EventName=="weapon_fire")};
Console.WriteLine(JsonSerializer.Serialize(summary));
if(args.Length>2)File.WriteAllText(args[2],JsonSerializer.Serialize(summary));
return result.Error!=null||summary.ground==0||summary.players!=1?1:0;
