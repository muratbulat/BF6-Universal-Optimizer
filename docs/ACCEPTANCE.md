# Acceptance results

Tested against `Release\BF6Optimizer.exe` on the development PC (Windows 11, NVIDIA GPU) on 2026-11-01. The .NET SDK exists there per-user but was removed from `PATH` and `DOTNET_ROOT` for these runs.

## Verified on this PC

| Check | Result |
| --- | --- |
| Publish output is one file | Yes, 61.8 MB; `build-release.ps1` fails on anything extra |
| Starts with no .NET on PATH or DOTNET_ROOT | Yes, window in about 0.8 s |
| No administrator rights | Yes, `asInvoker` manifest |
| Folder with spaces and non-ASCII characters | Yes |
| Read-only folder | Yes |
| Portable data mode in a writable folder | Yes, all six data folders created beside the EXE |
| Portable marker in a read-only folder | Falls back to LocalAppData; nothing written beside the EXE |
| Settings persist across restarts | Yes |
| Network use at startup | 0 TCP connections |
| Clean exit | Exit code 0, no child processes, no leftover process, Run key or service |
| All pages open with real hardware | Yes, no errors logged |
| Full audit, live monitor, BF6 preview on real profiles | Yes; real profiles were read only and not changed |

## Covered by automated tests only

- Apply, rollback, restore and status use real file and backup code against temporary folders, with a simulated registry. **Apply was not run against the real BF6 profiles or the real registry.**
- Unknown GPUs, missing GPU telemetry, partial audit failure, a missing BF6 folder and v1.4.0 backup formats use simulated inputs. The v1.4.0 samples were written to match the script's output; no real v1.4.0 backup existed here.

## Not verified

- A clean Windows 10 or 11 install with no .NET or Visual C++ runtime, including Windows 10 generally
- Launching with the network disabled (no network use was observed, but it was not disconnected)
- AMD or Intel GPUs, laptops, hybrid graphics, multi-monitor setups, on real hardware
- Light theme and English at every page; Turkish dark theme was reviewed on the pages captured
- Upgrading from one release to a later one
- Reads while the game is running; `bf6` as the process name comes from the v1.4.0 script
- Performance Sessions and the latency test on real runs; their calculations are tested

## Clean machine procedure

1. In a Windows 10 22H2 and a Windows 11 VM with no .NET installed, copy `BF6Optimizer.exe` to `C:\Users\Public\Test Klasör`.
2. Disconnect the network and double-click it. Expect the welcome screen with no missing-DLL dialog.
3. Run a Full System Audit. With no BF6 folder, the dashboard should say the profile was not found, and the app should not error.
4. Copy a test `PROFSAVE_profile` into `Documents\Battlefield 6\settings`, create a preview, apply, confirm the Status page says verified, then use "Restore original baseline" and compare SHA-256 with the original.
5. Close the app and confirm in Task Manager that no `BF6Optimizer` process remains.

## UI automation status (latest build)

The FlaUI suite in `tests/BF6Optimizer.UiTests` has 44 tests. Its last complete run was on the previous build: 29 passed, 15 failed. Fixes for those failures are in, but the final rerun was stopped before finishing, so the current build has no complete UI result.
Run `scripts\run-full-qa.ps1` in a VM: it opens the app about 45 times. Not executed at all: 3840x2160, Windows scaling above 100 %, moving between monitors of different DPI.
