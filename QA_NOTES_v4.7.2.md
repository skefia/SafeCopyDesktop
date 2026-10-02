# SafeCopy Desktop v4.7.2 QA Notes

## Static packaging checks

- MainWindow XAML parses successfully as XML.
- No duplicate `x:Name` values in MainWindow XAML.
- Every WPF event handler referenced by MainWindow XAML exists in a MainWindow partial class.
- Migration policy remains unchanged, but the PowerShell core was intentionally hardened in v4.7.2 (verifier type versioning and unique unverified repair-temp cleanup).
- No new `/MIR`, `/PURGE`, `/MOV`, or `/MOVE` behavior was added to the migration engine.
- Disk-map/index scanning is metadata/read-only against the selected source tree; only the LocalAppData SQLite database is written.
- SQLite scan updates occur inside a transaction; cancellation/failure does not publish a partially reconciled index.
- Reparse-point traversal is excluded from the index and archive folder enumeration.
- Archive creation uses temporary-file publication and per-entry SHA-256 readback verification before replacing the requested ZIP path.
- Archive extraction validates full paths, rejects ADS-style `:` paths, rejects existing reparse-point traversal, and stages each extracted file to a temporary file before final replacement.
- Archive source recycling is explicit, uses a frozen source selection, revalidates current source bytes immediately before recycling, blocks unsupported filesystem features, and recycles only the exact regular files just revalidated against the ZIP. Folder shells are deliberately retained so newly-created/unarchived files cannot be swept up. Windows Recycle Bin APIs are the only deletion path.
- Post-run eject is not attempted when SafeCopy itself is executing from the destination volume.
- Eject+Shutdown does not schedule shutdown if eject is not confirmed successful.

## Automated Windows release gate

Run `pwsh -ExecutionPolicy Bypass -File .\Run-FinalQA.ps1`. This performs prerequisite checks, PowerShell AST parsing, WPF restore/build, C# service smoke tests, disposable real-engine Copy+Verify / VerifyOnly / locked-file RetryFailed tests, and a single-file publish unless `-SkipPublish` is supplied.

## Required manual Windows runtime tests

### Build / restore

1. Run `Build.cmd` with the .NET 10 SDK and internet access for first NuGet restore.
2. Confirm `Microsoft.Data.Sqlite` 10.0.12 restores and the self-contained single-file publish succeeds.
3. Launch `dist\SafeCopy.exe` and confirm the title badge shows v4.7.2.

### Incremental scan database

Use a disposable tree with nested folders, several large files, and one junction/symlink.

1. Full refresh; note file count and bytes.
2. Close/reopen SafeCopy and use **Load cached**; confirm no filesystem scan is performed.
3. Modify one file, add one, delete one; run Incremental scan.
4. Confirm changed/added and stale-record counters are plausible and the map reflects the new tree.
5. Start a scan and cancel midway; confirm the previous committed cached map still loads.
6. Disconnect the indexed removable drive and confirm **Load cached** still opens its last committed map by path.

### Visual disk map

1. Confirm larger folders/files receive visibly larger rectangles.
2. Drill into folders using rectangle clicks and DataGrid double-click.
3. Use Back repeatedly to return to the root.
4. Resize the window and confirm the map relayouts without changing data.

### Archive Manager

1. Archive multiple files plus at least two folders containing same-named files.
2. Confirm entry names remain unique and the archive reports Verified.
3. Corrupt a ZIP byte externally and confirm **Test archive** fails or reports invalid data when the damaged entry is read.
4. Try extraction with an existing target while overwrite is disabled; confirm it refuses overwrite.
5. Use a crafted ZIP with `..\` paths and confirm extraction is blocked.
6. Enable Recycle originals with output ZIP inside a selected source folder; confirm SafeCopy blocks the configuration.
7. With a safe external output ZIP, enable Recycle originals; confirm a second prompt appears only after verification, only the exact revalidated regular files go to Recycle Bin, and source-folder shells remain. Confirm no permanent deletion occurs.
8. Test Pause/Resume and Cancel during create, test, and extraction.

### Post-run actions

Use only disposable data and a removable test drive.

1. Run SafeCopy from `C:` and migrate to the removable drive; select Eject. Confirm Windows actually unmounts/ejects it after success.
2. Run SafeCopy itself from the destination removable drive and confirm automatic eject is refused.
3. Select Shutdown with a 60-second delay; after a successful test run confirm shutdown is scheduled, then cancel with `shutdown /a`.
4. Select Eject then Shutdown and force eject to fail (keep the drive busy); confirm shutdown is not scheduled.
5. Enable automatic integrity manifest and force manifest generation failure/cancel; confirm no post-run eject/shutdown occurs.

### Regression

Run `Run-FinalQA.ps1` first, then repeat the manual-only Windows checks below.

Repeat the v4.6 migration QA set: normal Copy + Verify, Verify Only, Retry Failed, VSS, manifest generation/verification, conflict preview, throttling, performance dashboard, pause/resume, Problem Files, Folder Compare, Storage Analyzer, Duplicate Finder, Folder Sizes, File Age, File Types, and Cleanup Suggestions.
