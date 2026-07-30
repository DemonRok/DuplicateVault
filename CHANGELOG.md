# Changelog

All notable changes to DuplicateVault are documented in this file.

## [0.1.0.1] - Unreleased

### Added

- Initial Windows WPF application and command-line interface.
- Portable data-root initialization with default configuration files.
- SQLite database with WAL mode, scan sessions, file records, hard-link operations, and indexes.
- Incremental duplicate scanning with size grouping, partial hashes, full SHA-256 hashes, and cache reuse.
- NTFS file identity inspection and existing hard-link distinction.
- Safe hard-link replacement using same-directory backup rename, `CreateHardLinkW`, verification, and rollback.
- C# build tool for version reading, validation, incrementing, checksums, and package validation.
- Windows-only GitHub Actions workflows for version bumping and release packaging.
