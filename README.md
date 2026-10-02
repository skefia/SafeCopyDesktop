# SafeCopy Desktop v4.7.2

SafeCopy Desktop is a Windows WPF front end for the QA-hardened SafeCopy migration engine plus storage analysis, diagnostics, cleanup review, pause/resume, conflict preview, VSS snapshot, integrity manifests, native copy throttling, live telemetry, a persistent SQLite scan index, visual disk map, post-run device/power actions, and a verification-first ZIP archive manager for very large directory trees.

## Migration safety model

- Copy + Verify, Verify Only, and Retry Failed modes.
- Source wins exact relative-path file conflicts.
- Destination-only content is retained.
- Source files are never deleted by the migration engine.
- No `/MIR`, `/PURGE`, `/MOV`, or `/MOVE` migration behavior.
- Byte-for-byte verification and optional NTFS/ReFS named-stream verification.
- Efficient retry manifests for locked/transient files.
- Final read-only Robocopy convergence audit.
- Live phase/status/log UI.




## v4.7.2 — final QA corrections

This QA revision adds no new product feature. It hardens existing behavior found during the final release audit:

- missing source files during Move are reported as failures rather than successful moves;
- migration Resume reports native resume failures instead of silently claiming success;
- service smoke tests use an isolated temporary SQLite database;
- SQLite scan-generation IDs are process-monotonic rather than clock-resolution dependent;
- Archive Manager enforces safe output placement inside the service, rejects reparse-point source roots, and recycles only the exact files revalidated against the ZIP (source-folder shells are left intact);
- integrity-manifest generation will not overwrite an existing file inside the tree being manifested;
- the Windows release harness now covers Unicode paths, empty directories, destination-only directories, named streams when available, manifest overwrite protection, archive traversal/ADS blocking, and incremental-index removal.

## New in v4.7.2 — final QA hardening

This maintenance release preserves the v4.7 feature set while hardening release-critical edge cases found during final QA:

- Archive operations freeze the exact source selection for the duration of a run.
- Archive output is always rejected when it equals a selected source file or sits inside a selected source folder.
- Recycle-originals performs a fresh source-vs-archive SHA-256 verification immediately before recycling and is blocked for NTFS named streams, reparse/symlink objects, and empty directories that this ZIP format does not fully represent.
- ZIP extraction rejects traversal/ADS paths and pre-existing reparse-point traversal, and writes to a verified temporary file before replacing an existing output.
- Largest Files / Duplicate Finder / Cleanup ZIP actions now perform SHA-256 read-back verification before publication.
- SQLite scans roll back on cancellation/database errors and persist whether a committed index is partial because filesystem entries were inaccessible.
- VSS mode performs a second fresh VSS snapshot with a complete VerifyOnly byte pass before the final live-source metadata audit.
- Process-tree Pause/Resume was hardened to roll back partial suspension if the whole SafeCopy process tree cannot be paused.
- SafeCopy repair cleanup removes only its own unique unverified temporary file; existing destinations remain untouched.
- The embedded verifier type is versioned to prevent stale Add-Type reuse in a reused PowerShell process.
- Eject confirmation now polls for up to 15 seconds; shutdown/eject failures are reported separately from migration success.
- The project excludes the release-QA smoke-test sources from the main WPF compile.

`Run-FinalQA.ps1` is included. On Windows it compiles the WPF project, runs C# service smoke tests, executes the real SafeCopy PowerShell engine against disposable trees, tests locked-file retry recovery, and optionally publishes the single-file executable. VSS/UAC, physical device eject, shutdown scheduling, and visual interaction remain manual-only validation gates.

## New in v4.7

### Visual Disk Usage Map

The **Disk Map & Index** tab renders a size-proportional treemap from a persistent metadata index. Click a folder rectangle (or double-click its row) to drill into that folder, and use **Back** to move up. Up to 250 direct child items are shown in the grid and the largest 100 non-zero items are drawn on the map. The map is read-only.

### Incremental Scan Database

SafeCopy now maintains `%LOCALAPPDATA%\SafeCopy\Desktop\scan-index-v1.db` using SQLite. The index stores file metadata and recursive folder aggregates per scanned root.

- **Incremental scan** reconciles the index transactionally: current entries are upserted, changed/added files are marked, and stale records are removed after a successful traversal.
- **Full refresh** rebuilds the selected root's index from scratch.
- **Load cached** opens the last committed map instantly without traversing the filesystem, including when the indexed drive is currently disconnected.
- Reparse-point directories/files are not followed implicitly.
- Cancellation rolls back the in-progress SQLite transaction, leaving the previous committed index available.

For correctness, an incremental scan still enumerates filesystem metadata; SafeCopy intentionally does not trust directory timestamps to skip subtrees because that can miss file-content changes. The database primarily avoids rebuilding analysis state and enables instant cached visualization.

The project uses `Microsoft.Data.Sqlite` 10.0.12.

### Shutdown / Eject When Finished

Copy + Verify can now request one of these post-run actions:

- Do nothing
- Safely eject destination
- Shutdown Windows after a configurable delay
- Eject destination, then shutdown

Post-run actions execute **only after successful Copy + Verify and successful completion of any requested automatic integrity-manifest pass**. SafeCopy uses the Windows Shell canonical `Eject` verb and verifies that the volume is no longer ready before treating eject as successful. If SafeCopy itself is running from the destination volume, automatic eject is refused. If eject fails, an Eject+Shutdown request will not proceed to shutdown. Scheduled shutdown can be cancelled with `shutdown /a`.

### Archive Manager

The **Archive Manager** tab provides verification-first ZIP workflows:

- Add individual files and/or folders.
- Compression: None, Fastest, Optimal, or Smallest Size.
- Archive creation is written to a unique temporary file first.
- Source bytes are SHA-256 hashed while being written into ZIP entries.
- The temporary ZIP is reopened and every file entry is SHA-256 verified before the requested output path is published/replaced.
- Reparse-point source files are rejected and reparse-point directories are not traversed.
- Optional post-verification recycling is deliberately file-exact: after a fresh source-vs-archive SHA-256 revalidation, SafeCopy may send **only the exact regular files represented and revalidated in the ZIP** to the Windows Recycle Bin. Source-folder shells are intentionally left in place so a newly-created, unarchived file cannot be swept up by a folder-level recycle. There is no permanent-delete fallback. Archive output is rejected if it equals a selected source file or is located inside a selected source folder.
- Existing ZIPs can be listed, fully read-tested, and extracted with zip-slip path protection. Overwrite during extraction is opt-in.
- Archive create/test/extract supports Pause/Resume and Cancel.



## New in v4.6

### I/O Speed Control

Copy + Verify now exposes four copy-phase profiles:

- **Maximum** — no Robocopy bandwidth cap; preserves the previous behavior.
- **Balanced** — 100 MiB/s requested maximum copy I/O rate.
- **Low impact** — 25 MiB/s requested maximum copy I/O rate.
- **Custom** — user-selected 1–102400 MiB/s.

The limit is passed to Robocopy through its native `/IORATE` and `/IOMAXSIZE` throttling options. No size threshold is set, so small files are not exempted from the requested rate limit. It applies only to the copy transport phase. SafeCopy intentionally does not throttle the independent byte-verification phase because verification assurance should not be altered by a transport setting.

### Performance Dashboard

The Migration status card now shows live process-tree telemetry while SafeCopy is running:

- current total I/O rate,
- current read rate,
- current write rate,
- approximate average I/O rate,
- elapsed run time,
- configured copy I/O limit,
- existing verified-file and verified-data counters.

Telemetry is derived from Windows process I/O counters for SafeCopy's PowerShell/Robocopy process tree. It is operational telemetry rather than filesystem accounting, so values are intentionally labelled/treated as approximate and never affect success/failure decisions.

### Find Broken / Problematic Files

The new **Diagnostics → Problem Files** view is read-only and can run in Fast or Deep mode.

It identifies or reports:

- directories/files that cannot be enumerated or queried,
- access-denied paths,
- file read/I/O errors,
- files locked in a way that conflicts with SafeCopy-style read sharing,
- broken/unresolvable symbolic links or reparse points,
- offline/cloud-placeholder files (reported without forcing recall),
- paths above 260 characters as a legacy-tool compatibility warning.

Deep mode opens regular files read-only and reads their first and last blocks. It is intended to detect filesystem/readability problems; it does not claim to validate every application file format semantically. Reparse points are inspected but never traversed.

### Compare Two Folders

The new **Diagnostics → Compare Two Folders** view performs a memory-bounded relative-path comparison with one tree traversal per side.

Two modes are available:

- **Quick metadata** — compares relative path, type, size, and last-write timestamp.
- **Exact byte-for-byte** — for same-size same-relative-path files, reads both files and requires identical primary file content. It also rejects a comparison if either file changes while it is being read.

The comparison reports:

- identical files,
- left-only files/folders,
- right-only files/folders,
- different content/metadata,
- file-vs-folder type conflicts,
- read/access errors.

Results are read-only, exportable to CSV, and can open either side in Explorer. Reparse points are deliberately skipped rather than followed. Exact comparison covers the primary data stream; it is not an ACL/ADS/hard-link-topology comparison.

## New in v4.5

### File Age Analyzer

- Finds files not modified for a configurable age threshold (90 days through 5 years).
- Optional minimum-size threshold and bounded Top-N results keep memory predictable on very large trees.
- Results show logical size, age, modified date, name, and location.
- Open location, copy paths, and CSV export are available; age analysis itself is read-only.

### File-Type Storage Analysis

- Aggregates storage by extension in the same traversal used by the Age and Cleanup views.
- Shows file count, total logical bytes, percentage of the scanned root, largest file size, and largest file path for each extension.
- Files without an extension are grouped as `[no extension]`.
- Results can be exported to CSV.

### Safe Cleanup Suggestions

Cleanup is deliberately advisory-first rather than a generic one-click cleaner.

- Conservative suggestion classes include stale `.tmp`/`.temp` files, old crash dumps, stale Office lock-style files, old large logs/traces, old backup copies, and old large archives/disk images.
- Every suggestion includes a category, confidence level, reason, age, size, and exact path.
- Files with the System attribute and files under Windows, Program Files, ProgramData, `$Recycle.Bin`, and `System Volume Information` are excluded from cleanup suggestions.
- Nothing is automatically removed.
- Explicitly selected candidates can be opened, copied as paths, moved without overwrite, archived to ZIP without deleting originals, or sent to the Windows Recycle Bin.
- Recycle Bin remains the only delete behavior; SafeCopy does not silently fall back to permanent deletion.
- The displayed grid is bounded while total candidate count/bytes are still tracked.

### One-pass Storage Insights

File Age, File-Type Storage, and Cleanup Suggestions are produced by one filesystem traversal. A single scan therefore refreshes all three views instead of rereading a million-file tree three times.

### Pause / Resume

- Migration has a dedicated **Pause / Resume** control.
- SafeCopy pauses only its own PowerShell/Robocopy process tree. A pause is accepted only if the complete currently observed process tree can be suspended; partial suspension is rolled back.
- Largest Files, Duplicate Finder, Folder Sizes, and the new Storage Insights scans use cooperative pause checkpoints and resume the same scan without restarting it.
- Pause is distinct from Cancel. Cancel retains the existing safe cancellation behavior.
- Pause does not make a completed copy more or less verified; final success still depends on the normal verification/audit stages.
- Short file actions such as Move/ZIP/Recycle are cancellable where applicable but are not presented as pausable scans.

## New in v4.4

### VSS Snapshot Mode

Optional VSS mode creates a temporary point-in-time shadow copy of the source volume and runs SafeCopy against that stable view instead of the live source tree.

- Intended to avoid ordinary locked-file/read-consistency problems while applications continue using the live volume.
- Requires Administrator rights. SafeCopy can restart itself elevated when VSS is selected.
- Uses the Windows system VSS provider through `Win32_ShadowCopy` with the `ClientAccessible` context.
- The snapshot is mapped to a temporary free drive letter (M: through Z:) only for the duration of the operation.
- SafeCopy removes only the temporary drive mapping and VSS snapshot it created when the operation ends.
- After a successful snapshot copy/verification, SafeCopy performs an additional **read-only audit of the live source**. If files were added or normally changed after the snapshot was taken, the run is not certified complete and must be repeated.
- Retry Failed is intentionally disabled for VSS runs because a later retry cannot reuse the destroyed point-in-time snapshot; rerun a fresh VSS operation instead.
- The current implementation is a stable filesystem point-in-time snapshot; `ClientAccessible` does **not** coordinate VSS writers. Do not treat it as an application-consistent database backup mechanism. For strongest migration assurance, stop or settle applications that continue writing to the source before the final pass.

### Backup Integrity Manifest

The new **Integrity** tab can generate or verify a streamed SafeCopy manifest.

Manifest records include:
- relative file path,
- logical size,
- last-write timestamp,
- SHA-256 of the primary file data,
- optional named NTFS/ReFS data streams and their SHA-256 values.

Design details:
- newline-delimited JSON (`.jsonl`) so millions of entries do not need to be held in RAM,
- temporary-file staging plus durable flush before a completed manifest replaces its destination,
- footer required during verification so incomplete/cancelled manifests are rejected,
- a SHA-256 digest of the manifest body is stored in the footer so truncation or modification of the manifest itself is detected,
- files that change while being hashed are rejected rather than recorded/accepted as stable,
- the manifest file itself is excluded if the user saves it under the directory being manifested,
- relative paths are validated so manifest entries cannot escape the selected verification root,
- destination-only extra files are allowed; verification checks the recorded items and does not require the directory to contain only manifest entries,
- issue output is bounded in the UI while total missing/mismatch/error counts remain accurate.

An optional **Create SHA-256 integrity manifest after successful copy + verification** setting creates a manifest of the final destination directory under `%LOCALAPPDATA%\SafeCopy\Manifests`.

Manifest identity does not include ACLs, owner/SACL metadata, hard-link topology, sparse allocation, compression allocation, or other filesystem structures.

### Smart Conflict Viewer

Read-only conflict preview for the current Migration source/destination pair.

- Detects exact relative-path file conflicts.
- Different file size is immediately a confirmed conflict.
- Same-size pairs with differing timestamps are SHA-256 compared before being displayed.
- Optional **Deep exact scan** hashes same-size/same-timestamp pairs too, catching intentionally or accidentally metadata-identical content differences.
- Detects file-vs-folder and folder-vs-file type conflicts and labels them **Manual resolution required**.
- Shows source/destination size, modified time, conflict type, hashes when calculated, and the active SafeCopy decision.
- Can open either side in Explorer and export results to CSV.
- The viewer never modifies source, destination, or the SafeCopy policy.

## Storage Analyzer — Largest Files

- Recursively scans a selected directory and finds the largest files.
- Memory-bounded Top-N algorithm for very large trees.
- Configurable Top 100 / 500 / 1,000 / 5,000 / 10,000 results.
- Optional minimum-size filter.
- Sortable/filterable results with multi-select.
- Open containing folder, copy paths, Move, ZIP, Recycle Bin delete, and CSV export.

## Storage Analyzer — Duplicate Finder

- Conservative three-stage duplicate detection:
  1. exact file-size grouping,
  2. first/last-block SHA-256 fingerprinting,
  3. full SHA-256 confirmation.
- Same names or sizes alone are never reported as duplicates.
- Configurable minimum duplicate size.
- Shows duplicate groups and estimated reclaimable space.
- Multi-select actions: Open Location, Copy Paths, Move, ZIP, and Delete to Recycle Bin.
- “Select all but newest” only selects; it never automatically performs a file action.
- Files that change while hashing or cannot be read are skipped rather than reported as confirmed duplicates.

Duplicate identity is based on the primary file data stream; ADS/ACL/ownership/hard-link topology are not part of duplicate identity.

## Storage Analyzer — Folder Sizes

- Calculates recursive folder totals in a single traversal.
- Stores folder aggregates rather than every file path.
- Configurable Top 100 / 500 / 1,000 / 5,000 / 10,000 folders and minimum size.
- Shows recursive logical size, percentage of root, file count, subfolder count, and full path.
- Double-click or **Analyze selected folder** to drill down.
- Open folder, copy path, and CSV export.

## Analyzer/file-action safety

- Analyzer scans are read-only.
- Reparse points/junctions are not traversed.
- Move never overwrites an existing target.
- ZIP stages to a temporary archive and does not delete originals.
- Delete means Windows Recycle Bin only; there is no silent permanent-delete fallback.
- Migration, analyzer, and manifest I/O jobs are mutually exclusive in the UI.

## Build

Requirements:
- Windows 10/11 x64
- .NET 10 SDK for building
- PowerShell 7 on the target PC for the embedded SafeCopy migration engine and VSS orchestration

Build the self-contained single-file executable:

```powershell
.\Build.cmd
```

Output:

```text
dist\SafeCopy.exe
```

If needed:

```powershell
winget install Microsoft.DotNet.SDK.10
```

## Signing

```powershell
.\Build.ps1 -CertificateThumbprint "YOUR_CERTIFICATE_THUMBPRINT"
```

The build script uses SignTool when available and otherwise attempts Authenticode signing with the selected CurrentUser code-signing certificate.

## Architecture

The WPF GUI embeds `Core\SafeCopy_Core.ps1` and executes it invisibly through PowerShell 7. VSS is an optional wrapper around the source. v4.7.2 retains the v4.7 feature set and the bounded v4.6 transport I/O-rate feature. Final QA also hardened the embedded verifier's temporary-repair cleanup and versioned its compiled verifier type; the core migration policy remains source-wins for exact-path conflicts, destination-only retention, no source deletion, no mirror/purge/move, independent byte verification, targeted retry, and final read-only audit. Storage analysis, duplicate scanning, folder analysis, diagnostics, one-pass age/type/cleanup insights, conflict preview, performance telemetry, and integrity manifests are separate C# services.

## QA

See `QA_NOTES_v4.7.2.md` for static checks and the required Windows runtime validation checklist for I/O throttling, performance telemetry, diagnostics, Pause/Resume, and cleanup actions.

## License

SafeCopy Desktop is available under the [MIT License](LICENSE).
