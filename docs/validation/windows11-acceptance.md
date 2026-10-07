# Windows 11 acceptance

All current Windows workflows use `windows-11-arm`. Windows Server is no longer
an acceptance target. Historical Server measurements remain attributed to their
original runs; they do not establish current client behavior.

## Host and toolchain

The host check prints the requested
`Get-ComputerInfo | Select-Object WindowsProductName, OsArchitecture`, then
requires a Windows 11 client using CIM caption, build, and `ProductType == 1`.
The observed host is **Microsoft Windows 11 Enterprise**, build **26200**,
ARM64, image **20261004.176.1**, four processors and approximately 16 GiB RAM.
[Host metadata](windows11-host.json) records the volume and image details.
`Get-ComputerInfo.WindowsProductName` reports Windows 10 Enterprise because of
the image's legacy product registry name; CIM caption and build identify Windows
11. The CSV OS description `Microsoft Windows 10.0.26200` is the numeric kernel
version, not a Server OS.

`global.json` pins SDK **8.0.425**. The Windows installer downloads Microsoft's
native ARM64 SDK archive with TLS and verifies its published SHA-512 before
extraction. Installing .NET 8 without pinning allowed the preinstalled SDK 10 to
be selected; its installer also crashed under ARM64 PowerShell. Windows setup
uses Windows PowerShell and the verified archive instead. Linux uses
`actions/setup-dotnet`. Runtime-specific projects are built individually; the
.NET SDK does not support a solution-level `RuntimeIdentifier`.

## Core and desktop

First complete client run [37626957121](https://github.com/kurasis/FindCopy/actions/runs/37626957121)
passed on `3b8b771d48ec00933faa7f6513a72ffb78572fd0`: baseline **82/82**,
audit **17/17**, light and dark application settings **15 WPF cases each**,
actual SMB **4/4**, and non-admin WPF/published EXE **16/16**, for both native
ARM64 and self-contained x64 emulation. Process architecture is asserted by the
core, desktop, and benchmark runners. Full client run
[37627314903](https://github.com/kurasis/FindCopy/actions/runs/37627314903)
also passed after selection totals were changed to update once per bulk operation.

The desktop runner performs actual scanning, selection, cancellation, policy
reporting, native cloud consent, recovery, and pending cleanup. UI16 binds
**6,000 files in 2,000 groups**, retains a physical keeper per group, and checks
bulk marking/clearing. It now also verifies that a failed deletion refresh
preserves all checked candidates. Bulk selection and deletion refresh notify
individual groups but compute the global total once, avoiding repeated scans
of all result files.

The published single-file EXE runs under a standard account and performs actual
search, shell recycling, original-path restoration, keeper preservation, and a
clean exit. Artifacts contain screenshots and stdout for both architectures.

The runner launches separate processes with Windows application theme settings
light and dark, then restores the previous setting. The application keeps its
explicit light palette. These checks exercise its existing palette and native
controls; they do not implement a custom dark palette. The actual desktop DPI
is **96**. Raster exports at **100/125/150/200%** verify scaled rendering and
minimum-width control visibility; they do not change the desktop's DPI or test
a real monitor transition.

The complete final functional run
[37642268416](https://github.com/kurasis/FindCopy/actions/runs/37642268416)
on source `983ab350a6009c2ecbb451c0af4e6d2f5d174413` passed **all three matrix
entries**. Native ARM64 and x64 emulation each passed **90 baseline cases with
zero skips**, **17 audit regressions**, **16 WPF cases per theme**, **4 UNC
cases and 54 complete H sweep rows**, and **17 non-admin WPF/published cases**.
Linux passed **64 cases with 26 Windows-only skips**, plus **17 audit cases**.
[ARM64 baseline](windows11-baseline-arm64.txt),
[x64 baseline](windows11-baseline-x64.txt),
[light WPF](windows11-light-ui.txt), [dark WPF](windows11-dark-ui.txt), and
[standard account](windows11-standard-user.txt) preserve the outputs.
[Many results](ui/windows11/light/many-results.png),
[minimum width](ui/windows11/light/minimum-width.png), and
[200% raster export](ui/windows11/dark/render-200.png) preserve rendering evidence.

UI17 clicks the actual export control, completes the native Save dialog, and
parses all **6,000 rows** for **2,000 groups**, checking every Unicode/semicolon
path, group, size, verification state and hash, plus the UTF-8 BOM. Direct
character messages target only the owned edit HWND. UI Automation SetValue
changed visible text without updating IFileDialog's selected filename, while
unattended global keyboard input was not processed. A fresh standard profile
can show an initial-folder information prompt (`runneradmin\Desktop` is
inaccessible); the driver records and dismisses it, then saves into the writable
artifact directory. The production export implementation uses the actual chosen
path; its existing optional test notification hook enables diagnostic stdout.
The full CSV is retained in each UI artifact rather than committing 6,000
ephemeral fixture paths to the repository.

W19 exposed a real close-time metadata update: the downloaded payload was stable,
but Cloud Files changed `ChangeTime` after the last data handle closed. The fix
checks the closed version, discards that read, refreshes only an opt-in cloud
candidate with unchanged identity/size/creation/last-write, and reads it again
once. Sample and full-hash stages both preserve their before/after checks.
W19 repeats the real small-file hydration/exact scenario eight times per
architecture. W21 requires the large-file sample/full/exact path; W22 rejects
a write-time change during hydration. C1-C3 script late metadata over real reads
to require full rehash/exact reads, reject changed payloads with otherwise
preserved timestamps, and retain verified new fingerprints. W23 covers actual
hydrated files under the default policy: first reconcile the hydration's USN
writes (8,192 bytes reread, two invalidated entries, no new fetch), then require
zero content bytes and two fingerprint hits on the subsequent warm scan.
Journal checkpoints remain captured before content reads; advancing them past
concurrent changes would weaken invalidation. Non-cloud mutation and deletion guards remain intact.
The native consent test waits for the warning text before closing the dialog;
its earlier x64 failure came from closing a newly created HWND before its child
text was initialized.

## Actual SMB protocol

A private temporary share is created on localhost for the current account.
Tests use actual UNC paths and native SMB access, not an injected filesystem:

- Unicode and paths longer than 260 characters, with exact membership checks.
- Remote hard-link identity and conservative physical-copy savings.
- Warm fingerprints without USN inventory, and same-size mutations outside
  the initial sample that cannot retain stale groups.
- Full-block cancellation and recovery on two fully written 64 MiB files.

The H sweep uses four fully written 8 MiB files, exceeding every swept small-file
threshold, and checks readers 1/2/4, thresholds 256/1024/4096 KiB and buffers
1024/4096 KiB. Application cache off/fill/warm gives **54 rows** per architecture.
The first green run used tiny sweep files; the separate correctness cases already
used large files. The larger sweep fixture first passed in run `37631437080`/source `aa74f5d`;
the linked raw rows now come from final run `37642268416`/source `983ab35`,
including explicit completeness, parameter and cache-mode assertions.
[ARM64 UNC tests](windows11-smb-arm64.txt), [x64 UNC tests](windows11-smb-x64.txt),
[ARM64 sweep](windows11-smb-arm64.csv), and [x64 sweep](windows11-smb-x64.csv)
preserve all rows. Every row has four files, one physical group, zero errors,
changes or skips, and network classification. Off/fill rows read 34,078,720
logical bytes (full contents plus two samples per file); warm rows read zero
with 100% fingerprint hits. The OS cache is warm. Loopback does not calibrate a
physical network, external server, or specific disk type.

## Connected Cloud Files provider

W4 uses native Cloud Files registration and a connected provider, asserting
**zero fetch callbacks and zero content reads** under the default exclusion
policy. W19 opts in to online-only reads and requires actual provider transfers,
matching local payloads, an exact duplicate group and a complete result. W20
returns a provider fetch failure and requires errors, incomplete results and
no false duplicate group. No network account or OneDrive service is simulated
as authenticated; these cases use the real Windows Cloud Files API with a
local acceptance provider. The consent dialog is checked separately by WPF.

## Mixed workload

[Resource run 37626957289](https://github.com/kurasis/FindCopy/actions/runs/37626957289)
uses source `3b8b771d48ec00933faa7f6513a72ffb78572fd0`, native ARM64, and a
readable NTFS journal. The mixed job passed all six phases. It contains
100,000 unique-size sparse files, 20,000 equal-size distinct 2 KiB files,
5,000 groups of three fully written 4 KiB copies, 250 hard-link aliases, and
more than 5,000 directories including deep Unicode paths over 260 characters.
The first scan checks 135,250 paths and finds exactly 5,000 physical groups.
The approximately 98 GiB discovered logical size includes unique sparse files
which are rejected by size without reading; it is not dense physical throughput.

[All mixed rows](windows11-mixed.csv):

| Phase | Elapsed | Peak scan RAM | Content bytes | Native/reused directories |
| --- | ---: | ---: | ---: | ---: |
| Inventory fill | 12.452 s | 141.11 MiB | 102,400,000 | 5108/0 |
| Warm inventory | 2.996 s | 111.52 MiB | 0 | 0/5108 |
| Rename/add/delete | 2.784 s | 115.89 MiB | 4,096 | 2/5106 |
| Fresh oracle | 2.700 s | 117.09 MiB | 102,395,904 | 5108/0 |
| Cancel replay | 0.032 s | 134.77 MiB | 0 | 0/17 |
| Warm after cancel | 2.882 s | 141.83 MiB | 0 | 0/5108 |

The changed scan's group members must equal the fresh oracle. Cancellation
publishes no partial result; recovery must reuse complete inventory and content
fingerprints. These are single observations on a controlled cloud workload.

## Fully written 50 GiB pair

The same resource run's dense job passed all five phases. Each independent
file was fully streamed to disk, without sparse, compressed, or deduplicated
attributes. Generation precedes measurement. [All dense rows](windows11-dense.csv):

| Phase | Outcome | Elapsed | Peak scan RAM | Content bytes |
| --- | --- | ---: | ---: | ---: |
| Full hash and exact | One exact group | 520.615 s | 27.34 MiB | 214,749,020,160 |
| Cancel full hash | Cancelled; no partial result | 0.150 s | 35.75 MiB | 1,703,936 |
| After cancellation | Complete group; discarded partial hashes reread | 259.929 s | 38.89 MiB | 107,374,182,400 |
| Warm inventory/cache | One complete group | 0.061 s | 19.39 MiB | 0 |
| Middle mutation | No duplicate | 0.034 s | 20.04 MiB | 196,608 |

The initial scan reads 100 GiB for hashes and another 100 GiB for exact
comparison, plus samples. After-cancel and warm phases run without exact
comparison, as recorded by the runner. Peak scan working set is sampled every
10 ms, excluding generation. The largest dense-phase sample is **38.89 MiB**.
This is a cloud volume result; no named physical device or cold-cache bandwidth
is inferred from it.

## Real five/ten-million-file NTFS trees

Resource run `37626957289` **completed successfully**, including all three jobs.
The native job uses the same source `3b8b771` and requires a readable NTFS USN
journal. It generates actual independent sparse files of sizes 1..N bytes,
in 10,000-file buckets with both Latin and Unicode directory names. These are
not virtual metadata records. Unique sizes require zero engine content I/O.
An independent duplicate pair is introduced in the changed/oracle phases.

[Raw native CSV](windows11-native-scale.csv) retains all **18** rows: six smoke
phases at 20,000 entries and six phases each at five and ten million.

| Phase | Five million | Ten million |
| --- | --- | --- |
| Inventory fill | 20.599 s; 1738.30 MiB; 503 native directories | 61.131 s; 3430.93 MiB; 1003 native directories |
| Warm inventory | 1.523 s; 1879.71 MiB; 0 native/503 reused | 3.309 s; 3492.48 MiB; 0 native/1003 reused |
| Rename/add/delete | 1.453 s; 1915.98 MiB; 3 native/501 reused; one group | 3.348 s; 3237.30 MiB; 3 native/1001 reused; one group |
| Fresh oracle | 2.927 s; 1797.39 MiB; 504 native; one matching group | 5.905 s; 3204.16 MiB; 1004 native; one matching group |
| Cancel replay | 0.012 s; 1722.17 MiB; cancelled, no result | 0.054 s; 3107.38 MiB; cancelled, no result |
| Warm after cancel | 1.371 s; 1916.54 MiB; 0 native/504 reused | 2.744 s; 3515.64 MiB; 0 native/1004 reused |

The highest sampled scan working sets are **1916.54 MiB** and **3515.64 MiB**.
Warm and recovered scans read no content. Changed/oracle scans read
10,262,148 and 20,262,148 logical bytes respectively for the new pair, including
samples, with no errors or changed files. The changed result must agree with
a fresh scan. Cancellation cannot commit a partial inventory generation.

Generation is excluded from elapsed scan time and scan RAM sampling. It took
**9.6 s** for the 20,000-file smoke, **3076.3 s** to extend to five million and
**2848.1 s** to extend to ten million. This fixture is expensive to regenerate.
The discovered logical sizes are approximately 11.37/45.47 TiB of sparse EOF
metadata, not fully written data. The fast hot oracle and zero-I/O warm phases
do not establish physical disk bandwidth or universal latency improvement.
The ten-million initial fill is slower than the five-million fill; preserve
that observation along with the warm timings.

## External acceptance limits

Windows 11 ARM64 and x64 emulation are covered. Native x64 client hardware,
Windows 10, real desktop DPI changes/monitor transitions, authenticated
OneDrive/service behavior, external network latency and actual HDD/SATA/NVMe
calibration require those targets. The mixed fixture broadens path, candidate,
and result coverage but does not establish behavior for every customer dataset.
