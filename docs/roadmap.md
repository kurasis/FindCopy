# Acceptance Follow-up

The remaining software work identified in the import audit has been implemented:
persistent USN directory reuse, long-path verified recycling, native EFS/cloud
fixtures, automated WPF interaction checks, and benchmark scenarios A–H.
Regression tests remain enabled in Linux and Windows CI.

The following deployment evidence needs equipment or services outside this
cloud workspace:

1. Run E–H against actual HDD, SATA SSD, NVMe, and network datasets, including
   documented cold and warm OS-cache conditions. Follow
   [hardware acceptance](hardware-acceptance.md). Keep conservative defaults
   until those measurements justify changing them.
2. Confirm provider-specific download behavior with a signed-in OneDrive or
   other Cloud Files provider. Native non-local Cloud Files placeholders and
   the consent dialog are covered; account/network service integration is
   separate from the default no-hydration policy.
3. Measure a representative 5–10 million-file physical tree, including real
   path distributions and cache storage. The virtual 10-million-record fixture
   proves engine memory behavior, while the physical million-file fixture
   proves enumeration and zero-content-I/O behavior at that scale.

Complete logical hashing of two 100 GiB sparse files has passed. Sparse reads
do not establish dense-file disk bandwidth. See the
[implementation audit](implementation-audit.md) for recorded evidence and limits.
