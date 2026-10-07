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

The expanded run [37631437080](https://github.com/kurasis/FindCopy/actions/runs/37631437080)
on source `aa74f5d` **completed successfully on all three matrix entries**,
including **86 baseline cases without skips**, the larger SMB sweep and all
desktop/published checks on both architectures.
[ARM64 baseline](windows11-baseline-arm64.txt),
[x64 baseline](windows11-baseline-x64.txt),
[light WPF](windows11-light-ui.txt), [dark WPF](windows11-dark-ui.txt), and
[standard account](windows11-standard-user.txt) preserve the outputs.
[Many results](ui/windows11/light/many-results.png),
[minimum width](ui/windows11/light/minimum-width.png), and
[200% raster export](ui/windows11/dark/render-200.png) preserve rendering evidence.
W19 exposed a real close-time metadata update: the downloaded payload was stable,
but Cloud Files changed `ChangeTime` after the last data handle closed. The fix
checks the closed version, discards that read, refreshes only an opt-in cloud
candidate with unchanged identity/size/creation/last-write, and reads it again
once. Sample and full-hash stages both preserve their before/after checks.
W21 requires the large-file sample/full/exact path; W22 rejects a write-time
change during hydration. Non-cloud mutation and deletion guards remain intact.
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
used large files. The larger sweep fixture passed in run `37631437080`/source `aa74f5d`.
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

## External acceptance limits

Windows 11 ARM64 and x64 emulation are covered. Native x64 client hardware,
Windows 10, real desktop DPI changes/monitor transitions, authenticated
OneDrive/service behavior, external network latency and actual HDD/SATA/NVMe
calibration require those targets. The mixed fixture broadens path, candidate,
and result coverage but does not establish behavior for every customer dataset.
