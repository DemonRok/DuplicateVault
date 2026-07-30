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
                foreach (var path in EnumerateFilesSafe(root, exclusionEngine, () => errors++))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total++;
                    progress?.Report(new(total, included, fullCalculated, path, "Enumerating"));
                    var info = new FileInfo(path);
                    var excluded = IsExcluded(info, request.Profile, exclusionEngine, out var reason);
                    if (excluded)
                    {
                        records.Add(Metadata(root, info, scanId, null, null, null, 1, reason));
                        continue;
                    }
                    included++;
                    records.Add(Metadata(root, info, scanId, null, null, await TryIdentityAsync(path, cancellationToken), 1, null));
                }
            }

            await database.UpsertFilesAsync(records, cancellationToken);

            var bySize = records.Where(r => !r.IsExcluded).GroupBy(r => r.Length).Where(g => g.Count() > 1);
            foreach (var group in bySize)
            {
                foreach (var record in group)
                {
                    var reusable = request.Mode == ScanMode.Quick ? await database.FindReusableHashAsync(record, cancellationToken) : null;
                    if (reusable?.PartialHash is not null)
                    {
                        cached++;
                        withPartial.Add(record with { PartialHash = reusable.PartialHash, FullHash = reusable.FullHash });
                        continue;
                    }
                    try
                    {
                        var partial = await Hashing.ComputePartialHashAsync(record.FullPath, record.Length, cancellationToken);
                        partialCalculated++;
                        withPartial.Add(record with { PartialHash = partial });
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        logger?.LogWarning(ex, "Failed to calculate partial hash for {Path}", record.FullPath);
                    }
                }
            }

            foreach (var group in withPartial.GroupBy(r => new { r.Length, r.PartialHash }).Where(g => g.Count() > 1))
            {
                foreach (var record in group)
                {
                    var reusable = request.Mode == ScanMode.Quick ? await database.FindReusableHashAsync(record, cancellationToken) : null;
                    if (reusable?.FullHash is not null)
                    {
                        cached++;
                        withFull.Add(record with { PartialHash = reusable.PartialHash, FullHash = reusable.FullHash });
                        continue;
                    }
                    try
                    {
                        var full = await Hashing.ComputeFullHashAsync(record.FullPath, cancellationToken);
                        fullCalculated++;
                        progress?.Report(new(total, included, fullCalculated, record.FullPath, "Hashing"));
                        withFull.Add(record with { FullHash = full });
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        logger?.LogWarning(ex, "Failed to calculate full hash for {Path}", record.FullPath);
                    }
                }
            }

            var result = await PersistAndCompleteAsync(false, cancellationToken);
            progress?.Report(new(total, included, fullCalculated, null, "Completed"));
            return result;
        }
        catch (OperationCanceledException)
        {
            var result = await PersistAndCompleteAsync(true, CancellationToken.None);
            progress?.Report(new(total, included, fullCalculated, null, "Cancelled"));
            return result;
        }

        async Task<ScanResult> PersistAndCompleteAsync(bool wasCancelled, CancellationToken token)
        {
            if (records.Count > 0) await database.UpsertFilesAsync(records, token);
            if (withPartial.Count > 0) await database.UpsertFilesAsync(withPartial, token);
            if (withFull.Count > 0) await database.UpsertFilesAsync(withFull, token);

            var duplicateGroups = BuildGroups(withFull);
            var result = new ScanResult(scanId, duplicateGroups, total, included, partialCalculated, fullCalculated, cached, duplicateGroups.Sum(g => g.Files.Count(f => f.IsExistingHardLink)), duplicateGroups.Sum(g => g.ReclaimableBytes), errors, wasCancelled);
            await database.CompleteScanAsync(scanId, result, token);
            return result;
        }
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

    private static IEnumerable<string> EnumerateFilesSafe(string root, ExclusionEngine exclusions, Action onError)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
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
