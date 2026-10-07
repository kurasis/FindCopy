# Acceptance Follow-up

The remaining software work identified in the import audit has been implemented:
persistent USN directory reuse, long-path verified recycling, native EFS/cloud
fixtures, automated WPF interaction checks, and benchmark scenarios A–H.
Regression tests remain enabled in Linux and Windows 11 CI. All Windows jobs
use `windows-11-arm` and require a Windows 11 client, with separate native
ARM64 and x64 emulation coverage. Windows Server checks have been retired.

Controlled real NTFS trees of five and ten million files now pass inventory,
change handling, fresh-scan comparison, and cancellation/recovery checks.
Two fully written 50 GiB Windows files pass complete hashing, exact comparison,
cancellation, and cache reuse. Commands, raw CSV, RAM, and limits are in
[extended acceptance](validation/extended-acceptance.md).
The published single-file desktop executable also passes actual search,
recycling, keeper preservation, and clean shutdown under a non-admin account.
Original-path restoration is now implemented through the deletion-history
window, with conflict/identity/metadata checks and native no-replace renaming.
The published executable's non-admin acceptance includes actual restoration.
Pending restore intent now survives interruption after the data rename; the
history command verifies the returned object and finishes guarded cleanup.
Semantic inventory corruption is replaced atomically, enabling the following
warm scan to reuse the repaired snapshot.
[Recovery behavior](recovery.md) covers persistence and operational limits.

Persistent inventory replay now retains unchanged SQLite blobs and passes
UTF-16 spans without per-entry names. The five-million-file repeat run took
1.61 s against the earlier 12.35 s observation; at ten million it took 4.23 s
against the earlier 24.96 s. All six phases passed at both counts. These are single runs on
separate cloud VMs of the same reported configuration, not device calibration
or a guarantee for every workload. Full measurements remain in validation.

The Windows 11 mixed workload additionally covers 135,250 paths, deep Unicode
directories, equal-size candidates, 5,000 duplicate groups and 250 hard-link
aliases. All six fill/reuse/change/oracle/cancel/recovery phases passed. The
desktop binds 2,000 groups and verifies bulk selection and refresh preservation;
selection totals are computed once per bulk operation. Actual localhost SMB
checks cover long paths, aliases, cache mutation, full-hash cancellation and H
parameter sweeps. Connected native Cloud Files tests exercise default
non-hydration, explicit transfer, and provider failures. The hydration retry
discards reads whose metadata changes on close and rechecks a new version.
[Windows 11 evidence](validation/windows11-acceptance.md) records source/run
attribution and the remaining limits.

The following deployment evidence needs target equipment or services:

1. Run E–H against actual HDD, SATA SSD, NVMe, and network datasets, including
   documented cold and warm OS-cache conditions. Follow
   [hardware acceptance](hardware-acceptance.md). Keep conservative defaults
   until those measurements justify changing them.
2. Confirm provider-specific download behavior with a signed-in OneDrive or
   other Cloud Files provider. Real local provider callbacks, payload transfer,
   failures, non-local exclusion and the consent dialog are covered;
   authenticated service and network integration require the actual provider.
3. Repeat scale measurement with representative customer path lengths,
   same-size candidates, cache storage, and result volume. The controlled
   five/ten-million-file NTFS gate and the controlled mixed workload have
   dedicated checks; customer distributions and slower cache media vary.
4. Exercise native x64 hardware, Windows 10 and real DPI/monitor transitions.
   Windows 11 native ARM64 and x64 emulation already run in CI under both
   application theme settings. The actual CI desktop uses 96 DPI; screenshots
   rendered at 100/125/150/200% do not establish desktop DPI transitions.

Complete logical hashing of two 100 GiB sparse files has passed in addition to
the fully written 50 GiB pair. Sparse reads do not establish dense-file disk
bandwidth. See the
[implementation audit](implementation-audit.md) for recorded evidence and limits.
