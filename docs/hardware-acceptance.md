# Hardware Acceptance

The E–H runner is implemented and its full 72-configuration matrix has passed
on an unknown virtual storage domain. That validates the harness, not any
physical device profile. This cloud host exposes no certified HDD/SATA/NVMe
or SMB target; production tuning remains an external measurement gate.

## Device matrix

| Scenario | Required target | Configuration sweep |
| --- | --- | --- |
| E | Physical HDD / seek-penalty disk | Readers 1/2/4 |
| F | Physical SATA SSD | Readers 1/2/4 |
| G | Physical NVMe SSD | Readers 1/2/4 |
| H | Network share, record client/server/link | Readers 1/2/4 |

Each sweep also uses thresholds 256/1024/4096 KiB and streaming buffers
1024/4096 KiB, for 18 configurations per device. `--cache` adds fingerprint
fill and warm-cache runs for each configuration. `--readers` disables adaptive
reader changes; the sweep compares fixed limits. The normal scanner retains
conservative unknown/network/HDD limits of one full reader.

## Reproducible run

Build on the Windows measurement host:

```powershell
dotnet build FindCopy.sln -c Release
# Existing files are scanned read-only; benchmark cache/results go into Work.
.\tools\run-hardware-acceptance.ps1 -Scenario G -Dataset D:\DenseDataset `
  -Work C:\FindCopyResults -Label 'Drive model; firmware; NTFS; host CPU/RAM' `
  -OsCacheState warm -Cache
```

Use a representative dataset containing unique sizes, early sample rejects,
late sample rejects, and independent dense 10–100 GiB copies. For a generated
10 GiB duplicate fixture (four copies, approximately 40 GiB disk needed):

```powershell
dotnet run -c Release --no-build --project tools/FindCopy.Bench -- D:\BenchData `
  --scenario D --acceptance --generate-only
```

Supply the generated scenario subdirectory as `-Dataset`. Generated C/D data
is dense by default; do not use `--sparse` to report physical throughput.
Do not mix sparse, compressed, deduplicated, and dense runs without recording
those conditions. Validate content and expected groups before comparing speeds.
The generator refuses an unrecognized existing dataset and never deletes it.

## Cache and results

Record device model, firmware, filesystem/allocation/compression, host CPU/RAM,
volume-to-device mapping, dataset size/distribution, and network topology for H.
Separate application fingerprint cache state (`nocache`, `cache-fill`,
`cache-warm`) from OS/page cache state (`--os-cache-state`). The runner does not
evict OS caches. A reboot or a documented operator-controlled procedure may
provide a cold condition; do not label a whole multi-run sweep cold merely
because its first run followed a reboot. Record subsequent rows as warming.
For isolated cold runs, use an explicit `--path`, fixed `--readers`,
`--threshold`, and `--buffer` in separate runs instead of an automatic sweep.

Preserve `bench-results-v3.csv` with the source commit and exact commands.
Rows include elapsed time, files/s, enumeration rate, logical content bytes,
MiB/s, normalized CPU utilization, sampled scan working set, hit rate, read
amplification, tuning, observed storage domains, and completeness counters.
The `native_directories` column is Windows backend enumeration telemetry; it
is zero on the portable/virtual backend and does not mean no enumeration.
RAM is sampled every 10 ms and excludes dataset generation; it is approximate.
`cpu_budget` is the hashing budget, independent of full-reader concurrency.

Choose defaults only after repeated complete runs with no errors or changed
files show a stable improvement on the relevant device. Retain conservative
fallbacks for unknown devices and network shares. Provider-specific OneDrive
hydration tests require an authenticated provider on the target host; the CI
Cloud Files fixture tests default non-local exclusion without a network account.
