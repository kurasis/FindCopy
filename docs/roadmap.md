# Acceptance Follow-up

The remaining software work identified in the import audit has been implemented:
persistent USN directory reuse, long-path verified recycling, native EFS/cloud
fixtures, automated WPF interaction checks, and benchmark scenarios A–H.
Regression tests remain enabled in Linux and Windows CI.

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
[Recovery behavior](recovery.md) covers persistence and operational limits.

Persistent inventory replay now retains unchanged SQLite blobs and passes
UTF-16 spans without per-entry names. The five-million-file repeat run took
1.61 s against the earlier 12.35 s observation; at ten million it took 4.23 s
against the earlier 24.96 s. All six phases passed at both counts. These are single runs on
separate cloud VMs of the same reported configuration, not device calibration
or a guarantee for every workload. Full measurements remain in validation.

The following deployment evidence needs target equipment or services:

1. Run E–H against actual HDD, SATA SSD, NVMe, and network datasets, including
   documented cold and warm OS-cache conditions. Follow
   [hardware acceptance](hardware-acceptance.md). Keep conservative defaults
   until those measurements justify changing them.
2. Confirm provider-specific download behavior with a signed-in OneDrive or
   other Cloud Files provider. Native non-local Cloud Files placeholders and
   the consent dialog are covered; account/network service integration is
   separate from the default no-hydration policy.
3. Repeat scale measurement with representative customer path lengths,
   same-size candidates, cache storage, and result volume. The controlled
   five/ten-million-file NTFS gate is complete; broader workload coverage is
   separate from that result.
4. Run the desktop acceptance on actual Windows 10 and Windows 11 client
   installations, with their DPI/theme combinations. Current automated native
   and desktop evidence uses Windows Server 2025.

Complete logical hashing of two 100 GiB sparse files has passed in addition to
the fully written 50 GiB pair. Sparse reads do not establish dense-file disk
bandwidth. See the
[implementation audit](implementation-audit.md) for recorded evidence and limits.
