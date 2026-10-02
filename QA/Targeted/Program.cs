using SafeCopy.Desktop.Models;
using SafeCopy.Desktop.Services;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

static class TargetedQA
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    private static void Assert(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS  " + name);
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
        string baseDir = Path.Combine(Path.GetTempPath(), "SafeCopy-TargetedQA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDir);
        using var pause = new PauseController();

        await Test("Manifest truncation and body-integrity rejection", async () =>
        {
            string root = Path.Combine(baseDir, "manifest-root");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "one.txt"), "one");
            File.WriteAllText(Path.Combine(root, "two.txt"), "two");
            string manifest = Path.Combine(baseDir, "manifest.jsonl");
            var svc = new IntegrityManifestService();
            await svc.GenerateAsync(root, manifest, false, null, CancellationToken.None);
            string[] lines = File.ReadAllLines(manifest);

            string truncated = Path.Combine(baseDir, "manifest-truncated.jsonl");
            File.WriteAllLines(truncated, lines[..^1]);
            bool truncatedRejected = false;
            try { await svc.VerifyAsync(root, truncated, null, CancellationToken.None); }
            catch (InvalidDataException) { truncatedRejected = true; }
            Assert(truncatedRejected, "manifest missing footer is rejected");

            string corrupt = Path.Combine(baseDir, "manifest-body-corrupt.jsonl");
            string changedHeader = lines[0].Replace("SafeCopyIntegrityManifest", "SafeCopyIntegrityManifesX", StringComparison.Ordinal);
            File.WriteAllLines(corrupt, [changedHeader, .. lines.Skip(1)]);
            bool corruptRejected = false;
            try { await svc.VerifyAsync(root, corrupt, null, CancellationToken.None); }
            catch (InvalidDataException) { corruptRejected = true; }
            Assert(corruptRejected, "manifest modified body is rejected");

            string inside = Path.Combine(root, "new-manifest.jsonl");
            var generatedInside = await svc.GenerateAsync(root, inside, false, null, CancellationToken.None);
            var verifiedInside = await svc.VerifyAsync(root, inside, null, CancellationToken.None);
            Assert(generatedInside.Files == 2 && verifiedInside.Success, "new manifest inside root excludes itself and verifies");
        });

        await Test("Manifest cancellation preserves prior output", async () =>
        {
            string root = Path.Combine(baseDir, "manifest-cancel-root");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 650; i++) File.WriteAllText(Path.Combine(root, $"f-{i:D4}.txt"), i.ToString());
            string output = Path.Combine(baseDir, "manifest-existing.jsonl");
            File.WriteAllText(output, "ORIGINAL");
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress<ManifestProgress>(p => { if (p.FilesProcessed >= 500) cts.Cancel(); });
            bool cancelled = false;
            try { await new IntegrityManifestService().GenerateAsync(root, output, false, progress, cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && File.ReadAllText(output) == "ORIGINAL", "cancelled manifest generation does not replace existing output");
            Assert(!Directory.EnumerateFiles(baseDir, "manifest-existing.jsonl.tmp-*").Any(), "cancelled manifest temporary file is removed");
        });

        await Test("Duplicate finder content discrimination", async () =>
        {
            string root = Path.Combine(baseDir, "duplicates");
            Directory.CreateDirectory(root);
            byte[] same = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
            byte[] different = same.ToArray();
            different[100_000] ^= 0x5A;
            File.WriteAllBytes(Path.Combine(root, "same-a.bin"), same);
            File.WriteAllBytes(Path.Combine(root, "same-b.bin"), same);
            File.WriteAllBytes(Path.Combine(root, "different.bin"), different);
            var result = await new DuplicateFinderService().FindAsync(root, 1, null, CancellationToken.None, pause);
            Assert(result.Groups == 1, "exactly one duplicate-content group is reported");
            Assert(result.Files.Any(x => x.Name == "same-a.bin") && result.Files.Any(x => x.Name == "same-b.bin"), "identical files are grouped");
            Assert(result.Files.All(x => x.Name != "different.bin"), "same-size different-content file is not called a duplicate");
        });

        await Test("Exact folder comparison", async () =>
        {
            string left = Path.Combine(baseDir, "compare-left"), right = Path.Combine(baseDir, "compare-right");
            Directory.CreateDirectory(left); Directory.CreateDirectory(right);
            string lf = Path.Combine(left, "same-metadata.bin"), rf = Path.Combine(right, "same-metadata.bin");
            File.WriteAllBytes(lf, Encoding.UTF8.GetBytes("AAAA"));
            File.WriteAllBytes(rf, Encoding.UTF8.GetBytes("BBBB"));
            DateTime stamp = DateTime.UtcNow.AddDays(-3);
            File.SetLastWriteTimeUtc(lf, stamp); File.SetLastWriteTimeUtc(rf, stamp);
            var result = await new FolderCompareService().CompareAsync(left, right, true, 100, null, CancellationToken.None, pause);
            Assert(result.Different == 1 && result.Same == 0, "exact comparison catches same-size same-timestamp content difference");
        });

        await Test("Cooperative analyzer Pause/Resume", async () =>
        {
            string root = Path.Combine(baseDir, "pause-root");
            Directory.CreateDirectory(root);
            for (int i = 0; i < 100; i++) File.WriteAllBytes(Path.Combine(root, $"p-{i:D3}.bin"), new byte[1024]);
            pause.Pause();
            Task<StorageScanResult> scan = new StorageAnalyzerService().ScanLargestAsync(root, 10, 0, null, CancellationToken.None, pause);
            await Task.Delay(200);
            Assert(!scan.IsCompleted, "analysis remains blocked while paused");
            pause.Resume();
            StorageScanResult result = await scan;
            Assert(result.FilesScanned == 100, "analysis completes after resume");
        });

        await Test("Migration process Pause/Resume", async () =>
        {
            string heartbeat = Path.Combine(baseDir, "pause-heartbeat.txt");
            string escapedHeartbeat = heartbeat.Replace("'", "''", StringComparison.Ordinal);
            using var process = Process.Start(new ProcessStartInfo("pwsh.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoLogo", "-NoProfile", "-Command",
                    $"$p='{escapedHeartbeat}'; $i=0; while($true){{ [IO.File]::WriteAllText($p,(++$i).ToString()); Start-Sleep -Milliseconds 25 }}"
                }
            }) ?? throw new InvalidOperationException("Could not start disposable pause-test process.");

            Type pauseType = typeof(PauseController).Assembly.GetType("SafeCopy.Desktop.Services.ProcessPauseService", throwOnError: true)!;
            object processPause = Activator.CreateInstance(pauseType, nonPublic: true)!;
            MethodInfo pauseTree = pauseType.GetMethod("PauseTree")!;
            MethodInfo resumeTree = pauseType.GetMethod("ResumeTree")!;
            bool paused = false;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(heartbeat) && DateTime.UtcNow < deadline) await Task.Delay(25);
                if (!File.Exists(heartbeat)) throw new TimeoutException("Disposable pause-test process did not begin writing.");
                await Task.Delay(150);

                paused = (bool)pauseTree.Invoke(processPause, [process.Id])!;
                DateTime frozenStamp = File.GetLastWriteTimeUtc(heartbeat);
                await Task.Delay(350);
                Assert(paused && File.GetLastWriteTimeUtc(heartbeat) == frozenStamp, "migration process progress remains frozen while paused");

                bool resumed = (bool)resumeTree.Invoke(processPause, null)!;
                paused = !resumed;
                deadline = DateTime.UtcNow.AddSeconds(3);
                while (File.GetLastWriteTimeUtc(heartbeat) == frozenStamp && DateTime.UtcNow < deadline) await Task.Delay(25);
                Assert(resumed && File.GetLastWriteTimeUtc(heartbeat) != frozenStamp, "migration process progress restarts after resume");
            }
            finally
            {
                if (paused) _ = resumeTree.Invoke(processPause, null);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        });

        await Test("SQLite change detection and cancellation rollback", async () =>
        {
            string root = Path.Combine(baseDir, "index-root"), db = Path.Combine(baseDir, "target-index.db");
            Directory.CreateDirectory(root);
            string baseline = Path.Combine(root, "baseline.txt");
            File.WriteAllText(baseline, "old");
            var svc = new IncrementalScanDatabaseService(db);
            var first = await svc.ScanAsync(root, true, null, CancellationToken.None, pause);
            File.WriteAllText(baseline, "changed-and-longer");
            var changed = await svc.ScanAsync(root, false, null, CancellationToken.None, pause);
            Assert(changed.ChangedOrAddedFiles >= 1, "incremental index detects a changed file");
            long committedCount = svc.GetCachedInfo(root)!.FileCount;

            for (int i = 0; i < 5000; i++) File.WriteAllText(Path.Combine(root, $"new-{i:D5}.tmp"), "x");
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress<IndexScanProgress>(p => { if (p.FilesScanned >= 4096) cts.Cancel(); });
            bool cancelled = false;
            try { await svc.ScanAsync(root, false, progress, cts.Token, pause); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "incremental scan cancellation is observed");
            Assert(svc.GetCachedInfo(root)!.FileCount == committedCount, "cancelled incremental scan rolls back cached root metadata");
            Assert(svc.GetChildren(root, string.Empty, 6000).All(x => !x.Name.StartsWith("new-", StringComparison.Ordinal)), "cancelled incremental scan does not publish new entries");
        });

        await Test("Archive cancellation preserves prior output", async () =>
        {
            string source = Path.Combine(baseDir, "archive-cancel.bin");
            string output = Path.Combine(baseDir, "archive-existing.zip");
            File.WriteAllBytes(source, new byte[24 * 1024 * 1024]);
            File.WriteAllText(output, "ORIGINAL-ARCHIVE");
            using var cts = new CancellationTokenSource();
            var progress = new InlineProgress<ArchiveProgressInfo>(p => { if (p.BytesProcessed >= 1024 * 1024) cts.Cancel(); });
            bool cancelled = false;
            try
            {
                await new ArchiveManagerService().CreateVerifiedZipAsync(
                    [new ArchiveSourceItem { Kind = ArchiveSourceKind.File, FullPath = source }],
                    output, CompressionLevel.Fastest, progress, cts.Token, pause);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && File.ReadAllText(output) == "ORIGINAL-ARCHIVE", "cancelled archive creation does not replace prior output");
            Assert(!Directory.EnumerateFiles(baseDir, "archive-existing.zip.safecopy-*.tmp").Any(), "cancelled archive temporary file is removed");
        });

        await Test("Archive cleanup revalidation remains guarded through the exact-file action", async () =>
        {
            string source = Path.Combine(baseDir, "archive-cleanup-source.bin");
            string moved = Path.Combine(baseDir, "archive-cleanup-moved.bin");
            string output = Path.Combine(baseDir, "archive-cleanup.zip");
            File.WriteAllBytes(source, Enumerable.Range(0, 512 * 1024).Select(i => (byte)(i % 251)).ToArray());
            var sourceItems = new[] { new ArchiveSourceItem { Kind = ArchiveSourceKind.File, FullPath = source } };
            var svc = new ArchiveManagerService();
            await svc.CreateVerifiedZipAsync(sourceItems, output, CompressionLevel.Fastest, null, CancellationToken.None, pause);

            bool writerBlocked = false;
            bool actionCalled = false;
            await svc.RevalidateSourcesAndExecuteAsync(
                sourceItems,
                output,
                path =>
                {
                    try
                    {
                        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    }
                    catch (IOException) { writerBlocked = true; }
                    File.Move(path, moved);
                    actionCalled = true;
                },
                null,
                CancellationToken.None,
                pause);

            Assert(actionCalled && writerBlocked, "archive cleanup action runs while writers remain blocked");
            Assert(!File.Exists(source) && File.Exists(moved), "guard permits the verified file to be moved during cleanup");
        });

        await Test("Corrupted ZIP extraction preserves existing target", async () =>
        {
            string zipPath = Path.Combine(baseDir, "corrupt-entry.zip");
            byte[] content = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("preserve.bin", CompressionLevel.Optimal).Open()) entry.Write(content);

            byte[] raw = File.ReadAllBytes(zipPath);
            int nameLength = BitConverter.ToUInt16(raw, 26), extraLength = BitConverter.ToUInt16(raw, 28);
            int dataStart = 30 + nameLength + extraLength;
            raw[dataStart + 16] ^= 0xFF;
            File.WriteAllBytes(zipPath, raw);

            string extract = Path.Combine(baseDir, "corrupt-extract");
            Directory.CreateDirectory(extract);
            string target = Path.Combine(extract, "preserve.bin");
            File.WriteAllText(target, "KEEP-EXISTING");
            bool rejected = false;
            try { await new ArchiveManagerService().ExtractAsync(zipPath, extract, true, null, CancellationToken.None, pause); }
            catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "corrupted compressed archive entry is rejected");
            Assert(File.ReadAllText(target) == "KEEP-EXISTING", "failed extraction does not truncate or replace an existing target");
            Assert(!Directory.EnumerateFiles(extract, ".__SC_EXTRACT_*.tmp").Any(), "failed extraction temporary file is removed");
        });

        try { Directory.Delete(baseDir, true); } catch { }
        Console.WriteLine();
        Console.WriteLine($"Targeted QA passed assertions: {_passed}");
        if (Failures.Count == 0) { Console.WriteLine("ALL TARGETED QA TESTS PASSED"); return 0; }
        Console.WriteLine("FAILURES:");
        foreach (string failure in Failures) Console.WriteLine(" - " + failure);
        return 1;
    }
}
