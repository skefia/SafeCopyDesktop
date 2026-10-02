using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.IO.Compression;
using System.Security.Cryptography;

static class QA
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine($"PASS  {name}");
        _passed++;
    }

    private static async Task Test(string name, Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            Failures.Add($"{name}: {ex.GetBaseException().Message}");
            Console.WriteLine($"FAIL  {name}: {ex.GetBaseException().Message}");
        }
    }

    public static async Task<int> Main()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "SafeCopy-ServiceQA-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(baseDir, "root");
        string other = Path.Combine(baseDir, "other");
        string extract = Path.Combine(baseDir, "extract");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(other);

        byte[] a = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
        byte[] c = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)((i + 17) % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(root, "a.bin"), a);
        File.WriteAllBytes(Path.Combine(root, "b.bin"), a);
        File.WriteAllBytes(Path.Combine(root, "c.bin"), c);
        string oldTmp = Path.Combine(root, "sub", "stale.tmp");
        File.WriteAllText(oldTmp, "temporary");
        File.SetLastWriteTimeUtc(oldTmp, DateTime.UtcNow.AddDays(-30));
        File.WriteAllText(Path.Combine(root, "sub", "note.txt"), "hello");

        using var pause = new PauseController();

        await Test("Largest-file analyzer", async () =>
        {
            var r = await new StorageAnalyzerService().ScanLargestAsync(root, 10, 0, null, CancellationToken.None, pause);
            Assert(r.FilesScanned >= 5, "largest scan counts files");
            Assert(r.Files.Count > 0 && r.Files[0].SizeBytes == a.Length, "largest scan returns expected size");
        });

        await Test("Duplicate finder", async () =>
        {
            var r = await new DuplicateFinderService().FindAsync(root, 1, null, CancellationToken.None, pause);
            Assert(r.Groups >= 1, "duplicate group detected");
            Assert(r.Files.Any(x => x.Name == "a.bin") && r.Files.Any(x => x.Name == "b.bin"), "confirmed duplicates contain a.bin/b.bin");
        });

        await Test("Folder-size analyzer", async () =>
        {
            long expected = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            var r = await new FolderSizeAnalyzerService().ScanAsync(root, 100, 0, null, CancellationToken.None, pause);
            Assert(r.RootSizeBytes == expected, "folder recursive byte total");
        });

        await Test("Age/type/cleanup insights", async () =>
        {
            var r = await new StorageInsightsService().ScanAsync(root, 7, 0, 100, 100, null, CancellationToken.None, pause);
            Assert(r.FileTypes.Any(x => x.Extension == ".bin" && x.FileCount == 3), "file-type grouping");
            Assert(r.OldFiles.Any(x => x.FullPath == oldTmp), "file-age result");
            Assert(r.CleanupSuggestions.Any(x => x.FullPath == oldTmp), "safe cleanup suggestion");
        });

        string manifest = Path.Combine(baseDir, "manifest.jsonl");
        await Test("Integrity manifest generate/verify/tamper", async () =>
        {
            var svc = new IntegrityManifestService();
            var g = await svc.GenerateAsync(root, manifest, false, null, CancellationToken.None);
            Assert(g.Files >= 5, "manifest generated records");
            var ok = await svc.VerifyAsync(root, manifest, null, CancellationToken.None);
            Assert(ok.Success, "manifest verifies unchanged tree");
            string cfile = Path.Combine(root, "c.bin");
            byte[] original = File.ReadAllBytes(cfile);
            File.WriteAllText(cfile, "tampered");
            var bad = await svc.VerifyAsync(root, manifest, null, CancellationToken.None);
            Assert(!bad.Success && bad.Mismatched + bad.Errors > 0, "manifest detects tamper");
            File.WriteAllBytes(cfile, original);

            string protectedSource = Path.Combine(root, "sub", "note.txt");
            string protectedBefore = File.ReadAllText(protectedSource);
            bool overwriteBlocked = false;
            try { await svc.GenerateAsync(root, protectedSource, false, null, CancellationToken.None); }
            catch (IOException) { overwriteBlocked = true; }
            Assert(overwriteBlocked && File.ReadAllText(protectedSource) == protectedBefore, "manifest cannot overwrite an existing file inside scanned tree");
        });

        await Test("Conflict viewer engine", async () =>
        {
            string src = Path.Combine(baseDir, "conf-src"), dst = Path.Combine(baseDir, "conf-dst");
            Directory.CreateDirectory(src); Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "x.txt"), "AAA");
            File.WriteAllText(Path.Combine(dst, "x.txt"), "BBB");
            DateTime t = DateTime.Now.AddDays(-1);
            File.SetLastWriteTime(Path.Combine(src, "x.txt"), t);
            File.SetLastWriteTime(Path.Combine(dst, "x.txt"), t);
            var r = await new ConflictScannerService().ScanAsync(src, dst, true, null, CancellationToken.None);
            Assert(r.Conflicts.Any(x => x.RelativePath == "x.txt"), "deep conflict detects same-metadata content change");
        });

        await Test("Exact two-folder compare", async () =>
        {
            File.WriteAllBytes(Path.Combine(other, "a.bin"), c);
            var r = await new FolderCompareService().CompareAsync(root, other, true, 1000, null, CancellationToken.None, pause);
            Assert(r.Different + r.LeftOnly + r.RightOnly + r.TypeConflicts > 0, "folder comparator reports differences");
        });

        string zipPath = Path.Combine(baseDir, "verified.zip");
        await Test("Archive create/readback/revalidate/extract", async () =>
        {
            var svc = new ArchiveManagerService();
            var sources = new List<ArchiveSourceItem> { new() { FullPath = root, Kind = ArchiveSourceKind.Folder } };
            var created = await svc.CreateVerifiedZipAsync(sources, zipPath, CompressionLevel.Fastest, null, CancellationToken.None, pause);
            Assert(created.Verified && File.Exists(zipPath), "archive published only after verification");
            await svc.TestArchiveAsync(zipPath, null, CancellationToken.None, pause);
            await svc.VerifySourcesAgainstArchiveAsync(sources, zipPath, null, CancellationToken.None, pause);
            await svc.ExtractAsync(zipPath, extract, false, null, CancellationToken.None, pause);
            Assert(Directory.EnumerateFiles(extract, "a.bin", SearchOption.AllDirectories).Any(), "archive extraction produced expected file");

            string note = Path.Combine(root, "sub", "note.txt");
            File.AppendAllText(note, " changed");
            bool blocked = false;
            try { await svc.VerifySourcesAgainstArchiveAsync(sources, zipPath, null, CancellationToken.None, pause); }
            catch { blocked = true; }
            Assert(blocked, "archive revalidation blocks recycling after source change");

            bool unsafeDestinationBlocked = false;
            try
            {
                await svc.CreateVerifiedZipAsync(
                    new[] { new ArchiveSourceItem { Kind = ArchiveSourceKind.Folder, FullPath = root } },
                    Path.Combine(root, "unsafe-inside-source.zip"),
                    System.IO.Compression.CompressionLevel.Optimal,
                    null, CancellationToken.None, pause);
            }
            catch (InvalidOperationException) { unsafeDestinationBlocked = true; }
            Assert(unsafeDestinationBlocked, "archive service blocks output inside selected source folder");

            string maliciousZip = Path.Combine(baseDir, "malicious.zip");
            using (var mfs = new FileStream(maliciousZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var mz = new ZipArchive(mfs, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(mz.CreateEntry("../escape.txt").Open());
                writer.Write("escape");
            }
            bool traversalBlocked = false;
            try { await svc.ExtractAsync(maliciousZip, Path.Combine(baseDir, "malicious-extract"), false, null, CancellationToken.None, pause); }
            catch (InvalidDataException) { traversalBlocked = true; }
            Assert(traversalBlocked && !File.Exists(Path.Combine(baseDir, "escape.txt")), "archive extraction blocks path traversal");

            string adsZip = Path.Combine(baseDir, "ads.zip");
            using (var afs = new FileStream(adsZip, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var az = new ZipArchive(afs, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(az.CreateEntry("safe.txt:evil").Open());
                writer.Write("ads");
            }
            bool adsBlocked = false;
            try { await svc.ExtractAsync(adsZip, Path.Combine(baseDir, "ads-extract"), false, null, CancellationToken.None, pause); }
            catch (InvalidDataException) { adsBlocked = true; }
            Assert(adsBlocked, "archive extraction blocks NTFS ADS-style entry paths");
        });

        await Test("Problem-file scanner basic traversal", async () =>
        {
            var r = await new ProblemFileScannerService().ScanAsync(root, true, 1000, null, CancellationToken.None, pause);
            Assert(r.FilesScanned >= 5, "problem scanner traverses regular files");
        });

        await Test("SQLite incremental index + cached map", async () =>
        {
            var svc = new IncrementalScanDatabaseService(Path.Combine(baseDir, "qa-scan-index.db"));
            var first = await svc.ScanAsync(root, true, null, CancellationToken.None, pause);
            Assert(first.FilesScanned >= 5, "full index scan");
            File.WriteAllText(Path.Combine(root, "new-index-file.txt"), "new");
            var second = await svc.ScanAsync(root, false, null, CancellationToken.None, pause);
            Assert(second.ChangedOrAddedFiles >= 1, "incremental index sees added file");
            var cached = svc.GetCachedInfo(root);
            Assert(cached is not null && cached.FileCount == second.FilesScanned, "cached root metadata available");
            Assert(svc.GetChildren(root, string.Empty).Count > 0, "disk-map cached children available");
            File.Delete(Path.Combine(root, "new-index-file.txt"));
            var third = await svc.ScanAsync(root, false, null, CancellationToken.None, pause);
            Assert(third.RemovedEntries >= 1, "incremental index detects removed entries");
        });

        try { Directory.Delete(baseDir, true); } catch { }

        Console.WriteLine();
        Console.WriteLine($"Service smoke tests passed assertions: {_passed}");
        if (Failures.Count == 0)
        {
            Console.WriteLine("ALL SERVICE SMOKE TESTS PASSED");
            return 0;
        }
        Console.WriteLine("FAILURES:");
        foreach (string failure in Failures) Console.WriteLine(" - " + failure);
        return 1;
    }
}
