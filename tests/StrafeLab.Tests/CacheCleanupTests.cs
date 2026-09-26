using System.IO.Compression;
using System.Text;
using StrafeLab.Core;
using Xunit;

namespace StrafeLab.Tests;

public sealed class CacheCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StrafeLabCleanup-" + Guid.NewGuid().ToString("N"));
    private readonly DemoLibrary _library;
    public CacheCleanupTests() => _library = new(_root, []);
    private DemoCacheCleanup Cleaner => new(_root);

    private async Task<(string Source, string Cache)> Extract(string name = "match.zip", string entryName = "match.dem")
    {
        var source = Path.Combine(_root, "downloads", name);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        using (var zip = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var stream = zip.CreateEntry(entryName).Open()) stream.Write(Encoding.ASCII.GetBytes("PBDEMS2\0fixture-demo"));
        _library.State.ManualFiles.Add(source); _library.Save();
        var result = await _library.MaterializeAsync(source, default);
        return (source, Assert.Single(result));
    }
    private string CacheFile(string name, string content = "keep")
    {
        string path = Path.Combine(_library.CachePath, name);
        File.WriteAllText(path, content); return path;
    }
    private static string Hash(char c) => new(c, 64);

    [Fact] public async Task Cleanup_releases_extracted_copies_but_preserves_originals_reports_records_and_indexes()
    {
        var (source, cached) = await Extract();
        var store = new SessionStore(_root); var session = new SessionDocument { EndedAtUtc = DateTime.UtcNow };
        store.Save(session); _library.SaveReport(new() { SessionId = session.SessionId });
        var anchor = CacheFile(Hash('a') + "-" + Hash('b') + "-v4.anchors.json");
        var unknown = CacheFile("user-demo.dem");
        var orphan = CacheFile(Hash('c') + ".dem");
        var recent = CacheFile(Hash('d') + ".dem.tmp");
        var old = CacheFile(Hash('e') + ".dem.tmp"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        string[] protectedFiles = [source, anchor, unknown, orphan, recent, store.GetPath(session.SessionId),
            Path.Combine(_library.ReportsPath, session.SessionId + ".json"), Path.Combine(_root, "demo-library.json")];
        var original = protectedFiles.ToDictionary(p => p, File.ReadAllBytes);
        var preview = Cleaner.Inspect(); Assert.Null(preview.Error); Assert.Equal(2, preview.Files.Count);
        Assert.True(File.Exists(cached)); Assert.True(File.Exists(old));
        var result = Cleaner.Clean(preview);
        Assert.Null(result.Error); Assert.Equal(2, result.DeletedFiles); Assert.Equal(preview.ReclaimableBytes, result.FreedBytes);
        Assert.False(File.Exists(cached)); Assert.False(File.Exists(old));
        foreach (var p in protectedFiles) Assert.Equal(original[p], File.ReadAllBytes(p));
        Assert.Empty(Cleaner.Inspect().Files);
        // Exercise the real extractor, not just our cache-name calculation.
        var regenerated = Assert.Single(await _library.MaterializeAsync(source, default));
        Assert.Equal(cached, regenerated); Assert.Equal("PBDEMS2\0fixture-demo", File.ReadAllText(regenerated));
    }
    [Fact] public async Task Missing_original_keeps_the_last_extracted_copy()
    {
        var (source, cached) = await Extract(); var preview = Cleaner.Inspect();
        File.Move(source, source + ".moved");
        var result = Cleaner.Clean(preview);
        Assert.Equal(0, result.DeletedFiles); Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task A_changed_cache_file_is_not_deleted_using_an_old_preview()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        File.AppendAllText(cached, "changed");
        var result = Cleaner.Clean(preview);
        Assert.Equal(1, result.SkippedFiles); Assert.Equal(0, result.FreedBytes); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task New_files_created_after_preview_are_not_included_in_cleanup()
    {
        var first = await Extract(); var preview = Cleaner.Inspect();
        var second = await Extract("second.zip");
        var result = Cleaner.Clean(preview);
        Assert.Equal(1, result.DeletedFiles); Assert.False(File.Exists(first.Cache)); Assert.True(File.Exists(second.Cache));
    }
    [Fact] public async Task Foreign_or_forged_paths_cannot_escape_the_cache()
    {
        var (source, cached) = await Extract(); var plan = Cleaner.Inspect();
        var forged = plan with { Files = [new("../downloads/match.zip", new FileInfo(source).Length, File.GetLastWriteTimeUtc(source))] };
        Assert.Equal(0, Cleaner.Clean(forged).DeletedFiles);
        Assert.NotNull(Cleaner.Clean(plan with { Root = Path.GetTempPath() }).Error);
        Assert.True(File.Exists(source)); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task Explicitly_imported_cache_files_are_protected()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        _library.State.ManualFiles.Add(cached); _library.Save();
        Assert.Empty(Cleaner.Inspect().Files); Assert.Equal(1, Cleaner.Clean(preview).SkippedFiles); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task Changed_originals_are_rechecked_and_unrecognized_subdirectories_are_retained()
    {
        var (source, cached) = await Extract();
        var nested = Path.Combine(_library.CachePath, "personal"); Directory.CreateDirectory(nested);
        var userFile = Path.Combine(nested, Hash('f') + ".dem"); File.WriteAllText(userFile, "only user copy");
        var preview = Cleaner.Inspect();
        Assert.Equal(new FileInfo(cached).Length + new FileInfo(userFile).Length, preview.CacheBytes);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(1));
        var result = Cleaner.Clean(preview);
        Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(cached)); Assert.True(File.Exists(userFile));
    }
    [Fact] public async Task Replacing_a_previewed_cache_file_with_a_link_never_deletes_its_target()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        var target = Path.Combine(_root, "user-original.dem"); File.Move(cached, target);
        File.CreateSymbolicLink(cached, target);
        try
        {
            Assert.Equal(1, Cleaner.Clean(preview).SkippedFiles);
            Assert.Equal("PBDEMS2\0fixture-demo", File.ReadAllText(target));
        }
        finally { File.Delete(cached); }
    }
    [Fact] public async Task Files_in_use_are_skipped_without_changing_the_reported_freed_space()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        using var reader = new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = Cleaner.Clean(preview);
        Assert.Equal(0, result.FreedBytes); Assert.Equal(1, result.SkippedFiles); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task Active_worker_lease_prevents_standalone_cleanup()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        using var lease = new FileStream(Path.Combine(_root, "demo-worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = Cleaner.Clean(preview);
        Assert.NotNull(result.Error); Assert.Equal(0, result.DeletedFiles); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task Owning_monitor_can_clean_between_scans_and_other_monitors_cannot()
    {
        var (_, cached) = await Extract(); bool playing = false;
        await using var owner = new DemoMonitor(new SessionStore(_root), [], defer: () => playing);
        await owner.ScanOnceAsync(); var preview = Cleaner.Inspect();
        await using var other = new DemoMonitor(new SessionStore(_root), [], defer: () => false);
        await other.ScanOnceAsync(); Assert.True(other.IsReadOnly);
        Assert.NotNull((await other.CleanCacheAsync(preview)).Error); Assert.True(File.Exists(cached));
        playing = true; Assert.NotNull((await owner.CleanCacheAsync(preview)).Error); Assert.True(File.Exists(cached));
        playing = false; var result = await owner.CleanCacheAsync(preview);
        Assert.Null(result.Error); Assert.Equal(1, result.DeletedFiles); Assert.False(File.Exists(cached));
    }
    [Fact] public async Task Malformed_library_fails_closed_without_touching_cache()
    {
        var (_, cached) = await Extract(); var preview = Cleaner.Inspect();
        File.WriteAllText(Path.Combine(_root, "demo-library.json"), "not json");
        Assert.NotNull(Cleaner.Inspect().Error); Assert.NotNull(Cleaner.Clean(preview).Error); Assert.True(File.Exists(cached));
    }
    [Fact] public async Task Linked_cache_directory_is_rejected_and_target_is_preserved()
    {
        var (_, cached) = await Extract(); var target = Path.Combine(_root, "outside-cache");
        Directory.Move(_library.CachePath, target);
        Directory.CreateSymbolicLink(_library.CachePath, target);
        try
        {
            var preview = Cleaner.Inspect(); Assert.NotNull(preview.Error);
            Assert.Equal(0, Cleaner.Clean(preview).DeletedFiles);
            Assert.True(File.Exists(Path.Combine(target, Path.GetFileName(cached))));
        }
        finally { Directory.Delete(_library.CachePath); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
