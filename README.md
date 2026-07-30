# DuplicateVault

![DuplicateVault icon](assets/duplicatevault-icon.png)

## Overview

DuplicateVault is a Windows application that finds identical files and reclaims disk space by replacing duplicate physical files with NTFS hard links. It is designed for portable use on Windows and for large external drives that receive repeated backup copies over time.

## Features

- WPF graphical interface in Italian.
- Command-line interface for scanning, planning, deduplication, version output, and database status.
- Portable data root with configuration, database, logs, reports, backups, quarantine, and temp directories.
- SQLite database with WAL mode and persistent file/hash cache.
- Incremental quick scans, full scans, and strict scan mode.
- Multi-stage duplicate detection: size grouping, partial SHA-256 hash, full SHA-256 hash.
- NTFS file identity detection to separate physical duplicates from existing hard links.
- Safe hard-link replacement using same-directory backup rename, verification, and rollback.
- C# build tool for versioning, checksums, and package validation.

## Requirements

- Windows on x64 hardware.
- NTFS for hard-link deduplication.
- .NET 10 Desktop Runtime for framework-dependent builds, or the self-contained release package.

## Installation

Download the Windows portable ZIP from a release, extract it to a writable directory, and run `DuplicateVault.exe`. The application creates its portable folders on first startup.

## Graphical Interface

Open DuplicateVault, add one or more folders or drive roots, choose the scan mode, and start the scan. Duplicate groups are shown after the scan completes. Select a duplicate file to preview or execute hard-link replacement.

## Command Line

```text
DuplicateVault.Cli version
DuplicateVault.Cli scan --root "D:\Archive" --mode quick --min-size 1MiB
DuplicateVault.Cli plan
DuplicateVault.Cli dedupe --master "D:\A\file.bin" --duplicate "D:\B\file.bin" --dry-run
DuplicateVault.Cli dedupe --master "D:\A\file.bin" --duplicate "D:\B\file.bin" --yes
DuplicateVault.Cli db stats
```

Use `--data-root "D:\DuplicateVaultData"` to store runtime data outside the application directory.

## Configuration

Default templates are stored in `conf/appsettings.default.json` and `conf/exclusions.default.json`. On first startup, editable copies are created as `conf/appsettings.json` and `conf/exclusions.json` when they do not already exist.

## Scan Profiles

The first version includes a default profile with a 1 MiB minimum file size, zero-byte exclusion through the minimum-size rule, and built-in safety exclusions for Windows recycle bin and system volume information folders.

## Done

- [x] Portable data-root initialization
- [x] SQLite hash cache
- [x] Quick and full duplicate scans
- [x] Partial and full SHA-256 hashing
- [x] Existing hard-link detection by NTFS file identity
- [x] Dry-run hard-link planning
- [x] Confirmed hard-link replacement
- [x] Rollback after replacement failure where possible
- [x] WPF graphical interface
- [x] Command-line interface
- [x] Versioning build tool
- [x] Windows release workflows

## Todo

- [ ] Advanced scan profile editor in the GUI
- [ ] Complete hard-link path enumeration with `FindFirstFileNameW`
- [ ] ACL equivalence comparison
- [ ] Alternate data stream comparison
- [ ] Advanced report templates
- [ ] Additional localization
- [ ] GUI data virtualization for very large result sets

## Known Limitations

- Hard-link deduplication is Windows and NTFS only.
- Hard links cannot cross volumes; cross-volume duplicates are detectable but not eligible for replacement.
- The first version records NTFS file identity and link counts, but it does not yet enumerate every alternate hard-link path.
- Strict mode currently performs byte-by-byte comparison during hard-link validation; ACL and ADS strict comparison are planned.

## License

DuplicateVault is licensed under the MIT License.
