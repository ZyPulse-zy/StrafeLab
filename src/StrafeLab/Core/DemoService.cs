using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using StrafeLab.Platform;

namespace StrafeLab.Core;

public sealed class DemoService
{
    private static readonly string[] KnownMaps =
    [
        "de_ancient", "de_anubis", "de_dust2", "de_inferno", "de_mirage",
        "de_nuke", "de_overpass", "de_vertigo", "de_train", "de_cache"
    ];

    private readonly SteamLocator _steamLocator;

    public DemoService(SteamLocator steamLocator)
    {
        _steamLocator = steamLocator;
    }

    public IReadOnlyList<DemoCandidate> FindCandidates(SessionDocument session, int max = 20)
    {
        var roots = _steamLocator.FindDemoRoots();
        var candidates = new List<DemoCandidate>();
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.dem", new EnumerationOptions {RecurseSubdirectories=true,IgnoreInaccessible=true,AttributesToSkip=FileAttributes.ReparsePoint}).Take(5000).ToArray();
            }
            catch { continue; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length < 4096) continue;
                    var header = DemoHeaderScanner.Read(file);
                    var score = DemoScoring.Score(session, info, header);
                    if (score > 0)
                    {
                        candidates.Add(new DemoCandidate
                        {
                            FilePath = file,
                            LengthBytes = info.Length,
                            LastWriteTimeUtc = info.LastWriteTimeUtc,
                            Score = score,
                            IsSource2 = header.IsSource2,
                            MapHint = header.MapHint
                        });
                    }
                }
                catch { }
            }
        }

        return candidates.OrderByDescending(x => x.Score).ThenByDescending(x => x.LastWriteTimeUtc).Take(max).ToArray();
    }

    public async Task<DemoParseResult> ParseAsync(string path, string? steamId = null, CancellationToken cancellationToken = default)
    {
        var header = DemoHeaderScanner.Read(path);
        var helper = FindExtractor();
        if (helper is null)
        {
            return new DemoParseResult
            {
                FilePath = path,
                IsSource2 = header.IsSource2,
                MapHint = header.MapHint,
                Parser = "header-only",
                Error = "未找到本地 Demo 解析器；请使用包含 demo-python/demo-runtime 的完整发布包。"
            };
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = helper.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in helper.PrefixArguments)
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment["RAYON_NUM_THREADS"]="2";
            start.Environment["POLARS_MAX_THREADS"]="2";
            start.ArgumentList.Add("--input");
            start.ArgumentList.Add(path);
            if (!string.IsNullOrWhiteSpace(steamId))
            {
                start.ArgumentList.Add("--steamid");
                start.ArgumentList.Add(steamId);
            }
            if (helper.PythonPath is not null)
            {
                var existingPythonPath = start.Environment.TryGetValue("PYTHONPATH", out var existing)
                    ? existing
                    : Environment.GetEnvironmentVariable("PYTHONPATH");
                start.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
                    ? helper.PythonPath
                    : helper.PythonPath + Path.PathSeparator + existingPythonPath;
            }

            using var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("无法启动 Demo 提取器。");
            try {process.PriorityClass=ProcessPriorityClass.BelowNormal;} catch { }
            using var processLease=new ProcessLease(process);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            using var kill=timeout.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch{}});
            var errors=process.StandardError.ReadToEndAsync(timeout.Token);
            var observations=new List<DemoObservation>();
            while(await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                var observation=ParseObservation(line);if(observation!=null)observations.Add(observation);
                if(observations.Count>2_000_000)throw new IOException("Demo 样本超过内存保护上限");
            }
            var stderr=await errors;
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                return new DemoParseResult
                {
                    FilePath = path,
                    IsSource2 = header.IsSource2,
                    MapHint = header.MapHint,
                    Parser = helper.ParserName,
                    Error = string.IsNullOrWhiteSpace(stderr) ? $"提取器退出码 {process.ExitCode}" : stderr.Trim()
                };
            }

            return new DemoParseResult
            {
                FilePath = path,
                IsSource2 = header.IsSource2,
                MapHint = observations.FirstOrDefault(x=>x.Kind=="meta")?.Map??header.MapHint,
                PlaybackTicks = observations.FirstOrDefault(x => x.Kind == "meta_end")?.Tick,
                TickRate = observations.FirstOrDefault(x => x.Kind is "meta" or "meta_end")?.TickRate,
                Parser = helper.ParserName,
                Observations = observations
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DemoParseResult
            {
                FilePath = path,
                IsSource2 = header.IsSource2,
                MapHint = header.MapHint,
                Parser = "header-only",
                Error = ex.Message
            };
        }
    }

    public static AlignmentResult Align(SessionDocument session, DemoParseResult demo) => RobustDemoAnalysis.Align(session,demo);
    public static CalibrationResult Calibrate(SessionDocument session, DemoParseResult demo, AlignmentResult alignment) => RobustDemoAnalysis.Calibrate(session,demo,alignment);

    public static async Task UnpackBzipAsync(string source,string destination,CancellationToken ct)
    {
        var helper=FindExtractor()??throw new IOException("缺少本地 Python 解压器");
        var start=new ProcessStartInfo(helper.Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in helper.PrefixArguments)start.ArgumentList.Add(arg);
        foreach(var arg in new[]{"--input",source,"--unpack-bz2","--output",destination})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)??throw new IOException("无法启动解压器");
        using var lease=new ProcessLease(process);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var kill=timeout.Token.Register(()=>{try{process.Kill(true);}catch{}});
        var error=process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        if(process.ExitCode!=0)throw new IOException(await error);
    }

    private static DemoExtractorSpec? FindExtractor()
    {
        var configured = Environment.GetEnvironmentVariable("STRAFELAB_DEMO_EXTRACTOR");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            if (string.Equals(Path.GetExtension(configured), ".py", StringComparison.OrdinalIgnoreCase))
            {
                return BuildPythonSpec(configured);
            }

            return new DemoExtractorSpec(configured, [], null, "configured-demo-extractor");
        }

        var scripts = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "demo-extractor.py"),
            Path.Combine(AppContext.BaseDirectory, "tools", "demo-extractor.py"),
            Path.Combine(Environment.CurrentDirectory, "tools", "demo-extractor.py"),
            Path.Combine(Environment.CurrentDirectory, "demo-extractor.py")
        };
        var script = scripts.FirstOrDefault(File.Exists);
        return script is null ? null : BuildPythonSpec(script);
    }

    private static DemoExtractorSpec? BuildPythonSpec(string scriptPath)
    {
        var python = FindPythonExecutable();
        return python is null
            ? null
            : new DemoExtractorSpec(
                python,
                [scriptPath],
                FindBundledPythonPath(scriptPath),
                "demoparser2-python");
    }

    private static string? FindPythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("STRAFELAB_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && (File.Exists(configured) || !Path.IsPathRooted(configured)))
        {
            return configured;
        }

        var bundledCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "demo-python", "python.exe"),
            Path.Combine(Environment.CurrentDirectory, "demo-python", "python.exe")
        };
        var bundled = bundledCandidates.FirstOrDefault(File.Exists);
        if (bundled is not null) return bundled;

        var localPythonRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Python");
        IEnumerable<string> localCandidates = Directory.Exists(localPythonRoot)
            ? Directory.EnumerateDirectories(localPythonRoot, "Python*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => Path.Combine(x, "python.exe"))
            : Array.Empty<string>();
        foreach (var candidate in localCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        // These names are resolved by Windows using the normal PATH. Keeping
        // the fallback as a command name also works with py.exe installations.
        return "python.exe";
    }

    private static string? FindBundledPythonPath(string scriptPath)
    {
        var configured = Environment.GetEnvironmentVariable("STRAFELAB_DEMOPARSER_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "demo-runtime"),
            Path.Combine(AppContext.BaseDirectory, "demoparser_runtime"),
            Path.Combine(Path.GetDirectoryName(scriptPath) ?? string.Empty, "demo-runtime"),
            Path.Combine(Environment.CurrentDirectory, "work", "demoparser_runtime")
        };
        return candidates
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .FirstOrDefault(x => Directory.Exists(Path.Combine(x!, "demoparser2")));
    }

    private static DemoObservation? ParseObservation(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<DemoObservation>(line, ObservationJson);
        }
        catch { return null; }
    }

    private static readonly JsonSerializerOptions ObservationJson=new(){PropertyNameCaseInsensitive=true,PropertyNamingPolicy=JsonNamingPolicy.SnakeCaseLower};
    private sealed class ProcessLease(Process process):IDisposable
    {
        public void Dispose(){try{if(!process.HasExited){process.Kill(true);process.WaitForExit(2000);}}catch{}}
    }
    private readonly record struct AnchorPair(double LocalSeconds, double DemoSeconds, string Label);
    private readonly record struct AnchorSeries(string Label, double[] LocalSeconds, double[] DemoSeconds);

    private sealed record DemoExtractorSpec(
        string Executable,
        IReadOnlyList<string> PrefixArguments,
        string? PythonPath,
        string ParserName);
}

public sealed class DemoHeaderInfo
{
    public bool IsSource2 { get; init; }
    public string? MapHint { get; init; }
}

public static class DemoHeaderScanner
{
    public static DemoHeaderInfo Read(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(131_072L, Math.Max(0, stream.Length))];
        _ = stream.Read(buffer, 0, buffer.Length);
        var source2 = buffer.AsSpan().StartsWith("PBDEMS2\0"u8);
        var text = Encoding.ASCII.GetString(buffer);
        var map = KnownMapNames().FirstOrDefault(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        return new DemoHeaderInfo { IsSource2 = source2, MapHint = map };
    }

    private static IEnumerable<string> KnownMapNames()
        => new[] { "de_ancient", "de_anubis", "de_dust2", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_vertigo", "de_train", "de_cache" };
}

public static class DemoScoring
{
    public static int Score(SessionDocument session, FileInfo info, DemoHeaderInfo header)
    {
        if (!header.IsSource2) return -100;
        var score = 35;
        if (!string.IsNullOrWhiteSpace(session.Map) && string.Equals(session.Map, header.MapHint, StringComparison.OrdinalIgnoreCase)) score += 60;
        var ageHours = Math.Abs((info.LastWriteTimeUtc - session.StartedAtUtc).TotalHours);
        if (ageHours <= 2) score += 50;
        else if (ageHours <= 12) score += 25;
        else if (ageHours <= 48) score += 5;
        if(session.Map!=null && header.MapHint!=null && session.Map!=header.MapHint)return -100;
        if (info.Length > 1_000_000) score += 10;
        return score;
    }
}
