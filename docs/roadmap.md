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

Optional software follow-ups can be implemented in this repository:

- Profile and reduce persistent inventory replay overhead. On the recorded
  host it avoids native enumeration but takes longer than a fresh scan of the
  OS-cached tree; keep correctness and cancellation guarantees intact.
- Add application-assisted original-path restoration for staged recycling.
  Current recovery manifests record original and staging paths; the shell's
  Restore action targets the staging path, so original-path recovery is manual.
  This is a new recovery feature, not a claim that original-path undo exists.

Complete logical hashing of two 100 GiB sparse files has passed in addition to
the fully written 50 GiB pair. Sparse reads do not establish dense-file disk
bandwidth. See the
[implementation audit](implementation-audit.md) for recorded evidence and limits.
