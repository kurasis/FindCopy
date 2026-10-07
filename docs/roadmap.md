# Remaining Implementation Work

The first correction pass resolves the reproduced import defects and adds
missing validation and scanner functionality. Continue in this order:

1. Extend the passing Windows console-suite CI with interactive WPF validation.
   Check staged recovery and UI reporting, and add actual OneDrive and EFS
   acceptance fixtures on a suitable Windows host. Record unsupported
   capabilities explicitly and keep native regression CI enabled.
2. Implement a persistent USN inventory keyed by volume and file/directory IDs.
   Record parent/name relationships and root membership. Apply journal deltas
   before traversal; enumerate affected directories while reusing unchanged
   inventory. Journal reset, gaps, unsupported records, inaccessible volumes,
   and uncertain rename pairs must trigger full enumeration. Acceptance must
   measure avoided enumeration and compare results against a fresh full scan.
3. Replace the limited shell recycling API with a long-path-capable operation
   that retains the verified staging contract and recovery reporting. Test
   original-name reuse, unavailable bins, cancellation, and failed shell moves.
4. Run specification-scale workloads on actual devices: 5–10 million-file
   metadata RAM, complete 10–100 GiB streaming hashes, and scenarios E–H for
   HDD/SATA/NVMe/network. Report content I/O and elapsed time with cache state,
   hardware, concurrency, and buffer settings; use those results to tune defaults.

Both console suites must remain passing. Keep Linux compilation, injected
filesystem behavior, native Windows execution, and device measurements distinct
when reporting progress.
