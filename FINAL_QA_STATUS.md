# SafeCopy Desktop v4.7.2 — Final QA Status

## Completed in this environment

- XML parse of all XAML files.
- `x:Name` duplicate checks.
- WPF event-handler resolution checks.
- C# lexical delimiter scan across all source files.
- Main-project exclusion of QA smoke-test C# sources.
- Operational Robocopy destructive-switch audit (`/MIR`, `/PURGE`, `/MOV`, `/MOVE`).
- Review of user-facing delete paths: Recycle Bin only; direct deletes are limited to SafeCopy-created temporary/build/QA files.
- Archive creation/read-back/recycle/extraction hardening review.
- VSS second-snapshot exact-validation review.
- SQLite transaction/cancellation/partial-index review.
- Process-tree Pause/Resume rollback review.
- Runtime-specific restore correction in the Windows QA harness.


## Final expanded static gate

The exact v4.7.2 source tree in this package passed the final expanded static/invariant gate:

```text
PASS: 138
FAIL: 0
Core SHA-256: d6e7595f6cd6db00ff7fa9023236ba4273145b82f6359a996f73a2ec052a5e1b
```

This includes XAML parsing/name/handler checks, structural C# checks, migration-switch invariants, read-only scanner boundaries, archive/manifest/index hardening checks, VSS second-snapshot exact-verification checks, retry/named-stream coverage markers, and package/version consistency.

A real Windows compile/runtime gate still cannot be executed in the Linux review environment because `dotnet`, PowerShell 7, Robocopy, VSS, Shell eject and native Windows process APIs are unavailable. An attempt to obtain the official .NET SDK in this environment was blocked by the download sandbox. This is why `Run-FinalQA.ps1` remains a mandatory Windows release gate.

## Windows automated gate included in the package

Run:

```powershell
pwsh -ExecutionPolicy Bypass -File .\Run-FinalQA.ps1
```

It requires Windows, PowerShell 7, Robocopy, and .NET 10+. It compiles the actual WPF project, runs C# service smoke tests, exercises the real SafeCopy engine on disposable trees, tests source-wins and destination-only preservation, VerifyOnly, locked-file retry recovery, and publishes the single-file EXE.

## Manual-only gates

These require real Windows/device/UI conditions and therefore are not honestly certifiable from this Linux environment:

- VSS/UAC lifecycle against a real writable source volume.
- Physical removable-drive eject and busy-drive failure behavior.
- Shutdown scheduling/cancellation.
- Native process-tree Pause/Resume with live Robocopy.
- Visual WPF interaction/layout/treemap behavior.
- Real long-running multi-million-file performance/soak testing.

A release should be treated as approved only after the automated Windows gate passes and the relevant manual checks in `QA_NOTES_v4.7.2.md` are completed.


## v4.7.2 release boundary

The source/static release gate is designed to run cross-platform, but final execution certification still requires `Run-FinalQA.ps1` on Windows because WPF, Robocopy, VSS, process suspension, Shell eject and Windows Recycle Bin semantics cannot be executed in the Linux review environment. Do not describe the release as runtime-certified until that Windows gate passes.
