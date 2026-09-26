using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StrafeLab.Core;

public sealed record CacheCleanupFile(string Name, long Bytes, DateTime WrittenUtc,
    string? SourcePath = null, long SourceBytes = 0, DateTime SourceWrittenUtc = default);

public sealed record CacheCleanupPlan(string Root, long CacheBytes, long RecordingBytes, long ReportBytes,
    long AnchorBytes, IReadOnlyList<CacheCleanupFile> Files, string? Error = null)
{
    public long ReclaimableBytes => Files.Sum(f => f.Bytes);
    public long RetainedBytes => CacheBytes - ReclaimableBytes;
}

public sealed record CacheCleanupResult(long FreedBytes, int DeletedFiles, int SkippedFiles, string? Error = null);

/// <summary>Delete only reproducible, generated extraction files, never user recordings or downloads.</summary>
public sealed class DemoCacheCleanup
{
    private readonly string _root;
    private readonly string _cache;
    private static readonly Regex Extraction = new(@"\A[0-9a-f]{64}\.dem\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Anchor = new(@"\A[0-9a-f]{64}-[0-9a-f]{64}-v[0-9]+\.anchors\.json\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public DemoCacheCleanup(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _cache = Path.Combine(_root, "demo-cache");
    }

    // Inspect ancestors too: resolving '..' alone is not enough for junctions/symlinks.
    private static bool PlainPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        }
        return true;
    }
    private static long DirectoryBytes(string path)
    {
        if (!Directory.Exists(path) || !PlainPath(path)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
            { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
            try { if (PlainPath(file)) total += new FileInfo(file).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return total;
    }
    private DemoLibraryState ReadState()
    {
        var path = Path.Combine(_root, "demo-library.json");
        if (!File.Exists(path)) return new();
        if (!PlainPath(path)) throw new IOException("Demo 目录记录使用了链接，已停止清理。");
        return JsonSerializer.Deserialize<DemoLibraryState>(File.ReadAllText(path), DemoLibrary.Json)
            ?? throw new IOException("Demo 目录记录无法读取，已停止清理。");
    }
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    public CacheCleanupPlan Inspect()
    {
        long total = 0, anchors = 0;
        var files = new List<CacheCleanupFile>();
        try
        {
            if (!PlainPath(_root) || !PlainPath(_cache))
                return new(_root, 0, 0, 0, 0, [], "缓存目录使用了链接，已停止清理。");
            long recordings = DirectoryBytes(Path.Combine(_root, "sessions"));
            long reports = DirectoryBytes(Path.Combine(_root, "analysis"));
            if (!Directory.Exists(_cache)) return new(_root, 0, recordings, reports, 0, []);
            total = DirectoryBytes(_cache);
            var state = ReadState();
            var references = state.ManualFiles.Concat(state.Jobs.Select(j => j.Path)).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var originals = FindOriginals(references);
            foreach (string path in Directory.EnumerateFiles(_cache))
            {
                if (!PlainPath(path)) continue;
                var info = new FileInfo(path);
                if (Anchor.IsMatch(info.Name)) anchors += info.Length;
                // A file explicitly imported/watched as a Demo is never disposable cache.
                if (references.Contains(path)) continue;
                if (Extraction.IsMatch(info.Name) && originals.TryGetValue(info.Name, out var original) &&
                    (original.ExtractedBytes == null || original.ExtractedBytes == info.Length))
                    files.Add(new(info.Name, info.Length, info.LastWriteTimeUtc, original.Path, original.Bytes, original.WrittenUtc));
                else if (info.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                    (Extraction.IsMatch(info.Name[..^4]) || Anchor.IsMatch(info.Name[..^4])) &&
                    info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    files.Add(new(info.Name, info.Length, info.LastWriteTimeUtc));
            }
            return new(_root, total, recordings, reports, anchors, files.AsReadOnly());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return new(_root, total, 0, 0, anchors, [], "无法核对缓存来源，请稍后刷新。" + ex.Message); }
    }

    private sealed record Original(string Path, long Bytes, DateTime WrittenUtc, long? ExtractedBytes);
    private Dictionary<string, Original> FindOriginals(IEnumerable<string> references)
    {
        var result = new Dictionary<string, Original>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in references)
        {
            if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".dem.bz2", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (!File.Exists(path) || !PlainPath(path)) continue;
                // Hold a read lease while deriving the exact names used by MaterializeAsync.
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var info = new FileInfo(path);
                if (path.EndsWith(".bz2", StringComparison.OrdinalIgnoreCase))
                    result[DemoLibrary.Stamp(path + source.Length + info.LastWriteTimeUtc.Ticks) + ".dem"] =
                        new(path, source.Length, info.LastWriteTimeUtc, null);
                else
                {
                    using var zip = new ZipArchive(source, ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".dem", StringComparison.OrdinalIgnoreCase)))
                        result[DemoLibrary.Stamp(path + source.Length + info.LastWriteTimeUtc.Ticks + entry.FullName) + ".dem"] =
                            new(path, source.Length, info.LastWriteTimeUtc, entry.Length);
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidDataException) { }
        }
        return result;
    }

    // Standalone callers must own the same cross-process lease as DemoMonitor.
    public CacheCleanupResult Clean(CacheCleanupPlan approved)
    {
        try
        {
            var leasePath = Path.Combine(_root, "demo-worker.lock");
            if (!PlainPath(leasePath)) return new(0, 0, 0, "任务锁使用了链接，已停止清理。");
            using var lease = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return CleanWithLease(approved);
        }
        catch (IOException) { return new(0, 0, 0, "后台正在使用缓存，请稍后重试。"); }
        catch (UnauthorizedAccessException) { return new(0, 0, 0, "没有清理此缓存目录的权限。"); }
    }

    // Called only while DemoMonitor holds both its scan semaphore and worker file lease.
    internal CacheCleanupResult CleanWithLease(CacheCleanupPlan approved, CancellationToken token = default)
    {
        if (!SamePath(approved.Root, _root)) return new(0, 0, 0, "清理预览已失效，请重新刷新。");
        var current = Inspect();
        if (current.Error != null) return new(0, 0, 0, current.Error);
        var available = current.Files.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var item in approved.Files.DistinctBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (token.IsCancellationRequested) return new(freed, deleted, skipped, "清理已中止，已处理的空间仍保留。");
            // Only the intersection of the preview and a fresh source check may be removed.
            if (!available.TryGetValue(item.Name, out var fresh) || fresh != item) { skipped++; continue; }
            string path = Path.GetFullPath(Path.Combine(_cache, fresh.Name));
            try
            {
                if (!SamePath(Path.GetDirectoryName(path)!, _cache) || !PlainPath(path)) { skipped++; continue; }
                using var source = fresh.SourcePath == null ? null :
                    new FileStream(fresh.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (source != null && (!PlainPath(fresh.SourcePath!) || source.Length != fresh.SourceBytes ||
                    File.GetLastWriteTimeUtc(fresh.SourcePath!) != fresh.SourceWrittenUtc)) { skipped++; continue; }
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != fresh.Bytes || info.LastWriteTimeUtc != fresh.WrittenUtc) { skipped++; continue; }
                File.Delete(path);
                freed += fresh.Bytes; deleted++;
            }
            catch (IOException) { skipped++; } catch (UnauthorizedAccessException) { skipped++; }
        }
        return new(freed, deleted, skipped);
    }

    public static string FormatBytes(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.##} GB" :
        bytes >= 1048576 ? $"{bytes / 1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
