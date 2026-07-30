using DuplicateVault.Core;
using Microsoft.Extensions.Logging;

namespace DuplicateVault.Infrastructure;

public sealed class FileScanner(IDuplicateVaultDatabase database, IHardLinkService hardLinks, ILogger<FileScanner>? logger = null) : IFileScanner
{
    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken);
        var scanId = await database.StartScanAsync(request, cancellationToken);
        var exclusionEngine = new ExclusionEngine(request.Profile.Exclusions);
        var total = 0L;
        var included = 0L;
        var partialCalculated = 0L;
        var fullCalculated = 0L;
        var cached = 0L;
        var errors = 0L;
        var records = new List<FileRecord>();
        var withPartial = new List<FileRecord>();
        var withFull = new List<FileRecord>();

        try
        {
            foreach (var root in request.Roots.Select(Path.GetFullPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(root))
                {
                    errors++;
                    progress?.Report(Progress(root, "RootUnavailable"));
                    continue;
                }

                progress?.Report(Progress(root, "OpeningRoot"));
                foreach (var path in EnumerateFilesSafe(root, exclusionEngine, p => progress?.Report(Progress(p, "OpeningDirectory")), () => errors++))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total++;
                    progress?.Report(Progress(path, "Enumerating"));
                    var info = new FileInfo(path);
                    var excluded = IsExcluded(info, request.Profile, exclusionEngine, out var reason);
                    if (excluded)
                    {
                        records.Add(Metadata(root, info, scanId, null, null, null, 1, reason));
                        continue;
                    }
                    included++;
                    records.Add(Metadata(root, info, scanId, null, null, null, 1, null));
                }
            }

            progress?.Report(Progress(null, "Persisting"));
            await database.UpsertFilesAsync(records, cancellationToken);

            await CompleteDuplicateDetectionAsync(cancellationToken);
            var result = await PersistAndCompleteAsync(false, cancellationToken);
            progress?.Report(Progress(null, "Completed"));
            return result;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(Progress(null, "Finalizing"));
            await CompleteDuplicateDetectionAsync(CancellationToken.None);
            var result = await PersistAndCompleteAsync(true, CancellationToken.None);
            progress?.Report(Progress(null, "Cancelled"));
            return result;
        }

        async Task CompleteDuplicateDetectionAsync(CancellationToken token)
        {
            var partialPaths = withPartial.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fullPaths = withFull.Select(r => r.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var bySize = records.Where(r => !r.IsExcluded).GroupBy(r => r.Length).Where(g => g.Count() > 1).ToArray();
            var partialWork = bySize.Sum(g => g.Count(r => !partialPaths.Contains(r.FullPath)));
            var fullWorkEstimate = bySize.Sum(g => g.Count());
            var totalWork = Math.Max(1, partialWork + fullWorkEstimate);
            var completedWork = 0;
            foreach (var group in bySize)
            {
                foreach (var record in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (partialPaths.Contains(record.FullPath)) continue;
                    ReportProgress("ReusingPartialHash", record.FullPath, completedWork, totalWork);
                    var reusable = request.Mode == ScanMode.Quick ? await database.FindReusableHashAsync(record, token) : null;
                    if (reusable?.PartialHash is not null)
                    {
                        cached++;
                        withPartial.Add(record with { PartialHash = reusable.PartialHash, FullHash = reusable.FullHash });
                        partialPaths.Add(record.FullPath);
                        completedWork++;
                        ReportProgress("ReusingPartialHash", record.FullPath, completedWork, totalWork);
                        continue;
                    }
                    try
                    {
                        ReportProgress("CalculatingPartialHash", record.FullPath, completedWork, totalWork);
                        var partial = await Hashing.ComputePartialHashAsync(record.FullPath, record.Length, token);
                        partialCalculated++;
                        withPartial.Add(record with { PartialHash = partial });
                        partialPaths.Add(record.FullPath);
                        completedWork++;
                        ReportProgress("CalculatingPartialHash", record.FullPath, completedWork, totalWork);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors++;
                        logger?.LogWarning(ex, "Failed to calculate partial hash for {Path}", record.FullPath);
                        completedWork++;
                    }
                }
            }

            var fullGroups = withPartial.GroupBy(r => new { r.Length, r.PartialHash }).Where(g => g.Count() > 1).ToArray();
            var remainingFullWork = fullGroups.Sum(g => g.Count(r => !fullPaths.Contains(r.FullPath)));
            totalWork = Math.Max(completedWork + remainingFullWork, totalWork);
            foreach (var group in fullGroups)
            {
                foreach (var record in group)
                {
                    token.ThrowIfCancellationRequested();
                    if (fullPaths.Contains(record.FullPath)) continue;
                    ReportProgress("ReusingFullHash", record.FullPath, completedWork, totalWork);
                    var reusable = request.Mode == ScanMode.Quick ? await database.FindReusableHashAsync(record, token) : null;
                    if (reusable?.FullHash is not null)
                    {
                        cached++;
                        withFull.Add(record with { PartialHash = reusable.PartialHash, FullHash = reusable.FullHash });
                        fullPaths.Add(record.FullPath);
                        completedWork++;
                        ReportProgress("ReusingFullHash", record.FullPath, completedWork, totalWork);
                        continue;
                    }
                    try
                    {
                        ReportProgress("Hashing", record.FullPath, completedWork, totalWork);
                        var full = await Hashing.ComputeFullHashAsync(record.FullPath, token, (read, length) =>
                        {
                            ReportProgress("Hashing", $"{record.FullPath} ({FormatBytes(read)} / {FormatBytes(length)})", completedWork, totalWork);
                        });
                        fullCalculated++;
                        withFull.Add(record with { FullHash = full });
                        fullPaths.Add(record.FullPath);
                        completedWork++;
                        ReportProgress("Hashing", record.FullPath, completedWork, totalWork);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        errors++;
                        logger?.LogWarning(ex, "Failed to calculate full hash for {Path}", record.FullPath);
                        completedWork++;
                    }
                }
            }

            withFull = await AddIdentitiesToDuplicateCandidatesAsync(withFull, progress, token);
        }

        void ReportProgress(string message, string? path, int completedWork, int totalWork)
        {
            progress?.Report(Progress(path, message, Percent(completedWork, totalWork)));
        }

        static double Percent(int completedWork, int totalWork) => totalWork <= 0 ? 100 : Math.Clamp(completedWork * 100.0 / totalWork, 0, 100);
        long HashProgress() => partialCalculated + fullCalculated + cached;
        ScanProgress Progress(string? path, string message, double? percent = null) => new(total, included, HashProgress(), path, message, percent, partialCalculated, fullCalculated, cached);

        async Task<ScanResult> PersistAndCompleteAsync(bool wasCancelled, CancellationToken token)
        {
            progress?.Report(Progress("Scrittura metadati file", "Persisting", 0));
            if (records.Count > 0) await database.UpsertFilesAsync(records, token);
            progress?.Report(Progress("Scrittura hash parziali", "Persisting", 35));
            if (withPartial.Count > 0) await database.UpsertFilesAsync(withPartial, token);
            progress?.Report(Progress("Scrittura hash completi", "Persisting", 70));
            if (withFull.Count > 0) await database.UpsertFilesAsync(withFull, token);
            progress?.Report(Progress("Finalizzazione gruppi duplicati", "Persisting", 90));

            var duplicateGroups = BuildGroups(withFull);
            var result = new ScanResult(scanId, duplicateGroups, total, included, partialCalculated, fullCalculated, cached, duplicateGroups.Sum(g => g.Files.Count(f => f.IsExistingHardLink)), duplicateGroups.Sum(g => g.ReclaimableBytes), errors, wasCancelled);
            await database.CompleteScanAsync(scanId, result, token);
            return result;
        }
    }

    private async Task<List<FileRecord>> AddIdentitiesToDuplicateCandidatesAsync(List<FileRecord> records, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new List<FileRecord>(records.Count);
        var duplicateCandidates = records
            .Where(r => r.FullHash is not null)
            .GroupBy(r => new { r.Length, r.FullHash })
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();
        var inspected = 0;
        var total = Math.Max(1, duplicateCandidates.Count);
        foreach (var group in records.Where(r => r.FullHash is not null).GroupBy(r => new { r.Length, r.FullHash }))
        {
            var shouldInspectIdentity = group.Count() > 1;
            foreach (var record in group)
            {
                if (!shouldInspectIdentity)
                {
                    result.Add(record);
                    continue;
                }

                progress?.Report(new ScanProgress(0, 0, 0, $"{record.FullPath} ({inspected:N0}/{total:N0})", "InspectingFileIdentity", Math.Clamp(inspected * 100.0 / total, 0, 100)));
                var identity = await TryIdentityAsync(record.FullPath, cancellationToken);
                inspected++;
                result.Add(identity is null
                    ? record
                    : record with { FileId = identity.StableId, NumberOfLinks = identity.NumberOfLinks });
                progress?.Report(new ScanProgress(0, 0, 0, $"{record.FullPath} ({inspected:N0}/{total:N0})", "InspectingFileIdentity", Math.Clamp(inspected * 100.0 / total, 0, 100)));
            }
        }
        return result;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["byte", "KiB", "MiB", "GiB", "TiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private static IReadOnlyList<DuplicateGroup> BuildGroups(IEnumerable<FileRecord> records)
    {
        var groups = new List<DuplicateGroup>();
        foreach (var hashGroup in records.Where(r => r.FullHash is not null).GroupBy(r => new { r.Length, r.FullHash }))
        {
            var physical = hashGroup.GroupBy(r => r.FileId ?? r.FullPath).ToArray();
            if (physical.Length < 2 && physical.All(g => g.Count() < 2)) continue;
            var files = new List<DuplicateFile>();
            var firstPhysical = true;
            foreach (var physicalGroup in physical)
            {
                var isExistingLinkGroup = physicalGroup.Count() > 1;
                foreach (var file in physicalGroup)
                {
                    files.Add(new(file, isExistingLinkGroup, firstPhysical));
                }
                firstPhysical = false;
            }
            var reclaimable = Math.Max(0, physical.Length - 1) * hashGroup.Key.Length;
            groups.Add(new(hashGroup.Key.Length, hashGroup.Key.FullHash!, files, reclaimable));
        }
        return groups;
    }

    private async Task<FileIdentity?> TryIdentityAsync(string path, CancellationToken cancellationToken)
    {
        try { return await hardLinks.GetFileIdentityAsync(path, cancellationToken); }
        catch { return null; }
    }

    private static bool IsExcluded(FileInfo info, ScanProfile profile, ExclusionEngine engine, out string? reason)
    {
        if (profile.MinimumSizeBytes > 0 && info.Length < profile.MinimumSizeBytes)
        {
            reason = "Below minimum size";
            return true;
        }
        reason = engine.GetExclusionReason(info.FullName, false);
        return reason is not null;
    }

    private static FileRecord Metadata(string root, FileInfo info, long scanId, string? partial, string? full, FileIdentity? identity, uint links, string? exclusionReason)
    {
        var relative = Path.GetRelativePath(root, info.FullName);
        return new FileRecord(info.FullName, relative, info.Name, info.Extension, info.Length, info.CreationTimeUtc, info.LastWriteTimeUtc, info.Attributes, partial, full, Hashing.Algorithm, Hashing.PartialHashVersion, Hashing.FullHashVersion, identity?.StableId, identity?.NumberOfLinks ?? links, scanId, false, exclusionReason is not null, exclusionReason);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, ExclusionEngine exclusions, Action<string> onDirectory, Action onError)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            onDirectory(current);
            IEnumerable<string> directories = [];
            IEnumerable<string> files = [];
            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                onError();
            }
            foreach (var directory in directories)
            {
                if (exclusions.GetExclusionReason(directory, true) is null) pending.Push(directory);
            }
            foreach (var file in files) yield return file;
        }
    }
}
