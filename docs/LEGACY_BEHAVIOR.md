# v1.4.0 behavior and how 2.0 replaces it

The original package is preserved unchanged in `Legacy/` (verified against its `SHA256SUMS.txt`). It is not used at runtime.

## Ported as-is

- Profile names and discovery under `Documents\Battlefield 6\settings`, including redirected and OneDrive folders
- The complete list of game keys and the values the script wrote
- GPU base scores, laptop power penalties, the resolution penalty formula and tier thresholds, now in `performance-data.json`
- Texture-by-VRAM thresholds and the 240 → 237 FPS cap behavior
- The four HKCU preferences, and never touching HKLM/HAGS
- Backup tree `Backups\Machine_<id>\OriginalBaseline` and `Snapshots\<id>`, machine key derivation
- Verification after write plus a delayed second verification

## Bugs found in the script and fixed

| v1.4.0 | Effect | 2.0 |
| --- | --- | --- |
| Tested 8/9/10/14 against `PCSystemType` | Those are `ChassisTypes` numbers, so most laptops relied on the battery check and a UPS could mark a desktop as a laptop | Uses `ChassisTypes`; battery is not evidence |
| Treated any `Arc` name as discrete | Core Ultra "Arc Graphics" iGPUs were ranked as gaming GPUs | Only `Arc A###/B###` is discrete |
| `Radeon RX Vega` matched the discrete bonus | APUs were preferred | Recognized as integrated |
| Auto always meant Competitive | Hardware was ignored | Auto weighs tier, refresh rate, form factor and objective |
| Model table guessed VRAM; `AdapterRAM` capped at 4 GB | Wrong textures on unlisted cards | DXGI 64-bit memory, NVML when present; unknown means conservative |
| Fell back to the first `nvidia-smi` row on a name mismatch | Wrong telemetry on multi-GPU | Matched by name only; no external process |
| `Encoding.Default` for non-UTF-8 files | On .NET Core that is UTF-8, which corrupts bytes | Latin-1 byte round trip, and a refusal unless re-encoding reproduces the file exactly |
| No check that the file matched the preview | Could overwrite a newer save | The preview's SHA-256 must still match at Apply |
| Restore used plain copies, with no undo | A failure mid-way left mixed state | Atomic writes plus a safety snapshot and undo |
| Restore trusted stored paths | A damaged manifest could write anywhere | Profile file names only; backup paths stay inside the restore point |
| Values outside the expected range were overwritten | Unknown enums were forced | Known-value check; unknown values stay and are reported |

## Deliberate behavior changes

- Quality mode no longer forces V-Sync, motion blur, film grain and similar taste settings, or undergrowth and post-process to the lowest level.
- Backups default to `%LOCALAPPDATA%\BF6UniversalOptimizer`; the package folder was the old default. Old backups in other folders are added from the Backup page and stay read-only.
- A pointer file is replaced by `Data\Machine_<id>\LastApply.json`. An old `LastSuccessfulApply.json` is read as a fallback and never rewritten.
- Windows preferences are optional and shown in the preview.
