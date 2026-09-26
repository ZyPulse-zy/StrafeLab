using System.Text.Json;
using StrafeLab.Core;
// Offline queue acceptance: no RuntimeService, HID, Raw Input or GSI startup.
if(args.Length<2)throw new ArgumentException("Usage: AnalysisCheck <isolated data directory> <watch directory> [watch directory ...]");
await using var monitor=new DemoMonitor(new SessionStore(args[0]),args.Skip(1));
await monitor.ScanOnceAsync();
await Task.Delay(TimeSpan.FromSeconds(11));
for(int i=0;i<3;i++)
{
    await monitor.ScanOnceAsync();
    Console.WriteLine(JsonSerializer.Serialize(new{monitor.Status,jobs=monitor.Snapshot().Jobs.Select(j=>new{j.Name,j.State,j.Detail}),
        reports=monitor.Reports().Select(r=>new{r.LocalTime,r.Map,r.Count,r.Matched,r.EligibleCount,r.UnassociatedShotCount,groups=CounterStrafeCohort.Groups(r.Actions),
            exclusions=r.Actions.SelectMany(a=>a.ExclusionReasons).GroupBy(a=>a).ToDictionary(g=>g.Key,g=>g.Count()),alignment=r.Alignment?.Explanation,
            verdicts=r.Actions.GroupBy(a=>a.Verdict).ToDictionary(g=>g.Key,g=>g.Count())})}));
}
