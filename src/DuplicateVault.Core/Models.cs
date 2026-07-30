namespace DuplicateVault.Core;

public enum ScanMode { Quick, Full, Strict }
public enum HardLinkOperationStatus { Planned, Succeeded, Failed, RolledBack, RollbackFailed }
public enum ScanRootState { Unknown, Partial, Complete }

public sealed record AppPaths(string DataRoot)
{
    public string Conf => Path.Combine(DataRoot, "conf");
    public string Db => Path.Combine(DataRoot, "db");
    public string Logs => Path.Combine(DataRoot, "logs");
    public string Reports => Path.Combine(DataRoot, "reports");
    public string Backups => Path.Combine(DataRoot, "backups");
    public string Quarantine => Path.Combine(DataRoot, "quarantine");
    public string Temp => Path.Combine(DataRoot, "temp");
    public string DatabasePath => Path.Combine(Db, "duplicatevault.db");
}

public sealed record DuplicateVaultSettings
{
    public long MinimumSizeBytes { get; init; } = 1024 * 1024;
    public bool ExcludeZeroByteFiles { get; init; } = true;
    public bool StrictByteVerification { get; init; }
    public int HashingWorkers { get; init; } = 1;
}

public sealed record ExclusionRule(
    string Name,
    string RuleType,
    string Pattern,
    bool IsEnabled,
    bool AppliesToFiles,
    bool AppliesToDirectories,
    bool IsCaseSensitive,
    int Priority);

public sealed record ScanProfile(
    string Name,
    long MinimumSizeBytes,
    bool StrictByteVerification,
    IReadOnlyList<ExclusionRule> Exclusions);

public sealed record ScanRequest(
    IReadOnlyList<string> Roots,
    ScanMode Mode,
    ScanProfile Profile,
    string DataRoot);

public sealed record ScanProgress(
    long EnumeratedFiles,
    long IncludedFiles,
    long HashedFiles,
    string? CurrentPath,
    string Message,
    double? Percent = null,
    long PartialHashes = 0,
    long FullHashes = 0,
    long ReusedHashes = 0);

public sealed record VolumeIdentity(
    string RootPath,
    string? VolumeSerialNumber,
    string? VolumeLabel,
    string FileSystem,
    long TotalSize,
    string DriveType,
    string LastKnownMountPoint);

public sealed record FileIdentity(
    string Path,
    string VolumeRoot,
    ulong FileIndex,
    uint VolumeSerialNumber,
    uint NumberOfLinks)
{
    public string StableId => $"{VolumeSerialNumber:X8}:{FileIndex:X16}";
}

public sealed record FileRecord(
    string FullPath,
    string RelativePath,
    string FileName,
    string Extension,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes,
    string? PartialHash,
    string? FullHash,
    string HashAlgorithm,
    int PartialHashVersion,
    int FullHashVersion,
    string? FileId,
    uint NumberOfLinks,
    long LastSeenScanId,
    bool IsMissing,
    bool IsExcluded,
    string? ExclusionReason);

public sealed record DuplicateFile(FileRecord Record, bool IsExistingHardLink, bool IsMasterCandidate);

public sealed record DuplicateGroup(
    long Length,
    string FullHash,
    IReadOnlyList<DuplicateFile> Files,
    long ReclaimableBytes)
{
    public string DisplayName => $"{Files.Count} files, {Length} bytes";
}

public sealed record ScanResult(
    long ScanSessionId,
    IReadOnlyList<DuplicateGroup> DuplicateGroups,
    long TotalFilesEnumerated,
    long IncludedFiles,
    long PartialHashesCalculated,
    long FullHashesCalculated,
    long CachedHashesReused,
    long ExistingHardLinks,
    long ReclaimableBytes,
    long ErrorCount,
    bool WasCancelled);

public sealed record ScanRootStatus(string RootPath, ScanRootState State, DateTime? LastScanUtc, long IncludedFiles);

public sealed record HardLinkOptions(bool StrictByteVerification = false, bool DryRun = false, bool SimulateFailureAfterRename = false);
public sealed record HardLinkValidationResult(bool IsEligible, string Reason, FileIdentity? MasterIdentity = null, FileIdentity? DuplicateIdentity = null);
public sealed record HardLinkOperationResult(HardLinkOperationStatus Status, string Message, bool RollbackPerformed, FileIdentity? ResultingIdentity = null);
public sealed record HardLinkPlanItem(string MasterPath, string DuplicatePath, bool IsEligible, string Reason, long ReclaimableBytes);
