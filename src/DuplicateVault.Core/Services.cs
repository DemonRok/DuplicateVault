using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DuplicateVault.Core;

public interface IPortableDataRoot
{
    AppPaths Initialize(string? dataRoot);
}

public interface IFileScanner
{
    Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}

public interface IHardLinkService
{
    Task<HardLinkValidationResult> ValidateAsync(string masterPath, string duplicatePath, HardLinkOptions options, CancellationToken cancellationToken);
    Task<HardLinkOperationResult> ReplaceWithHardLinkAsync(string masterPath, string duplicatePath, HardLinkOptions options, CancellationToken cancellationToken);
    Task<FileIdentity> GetFileIdentityAsync(string path, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetHardLinkPathsAsync(string path, CancellationToken cancellationToken);
}

public interface IVolumeIdentityService
{
    Task<VolumeIdentity> GetVolumeIdentityAsync(string rootPath, CancellationToken cancellationToken);
}

public interface IDuplicateVaultDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<long> StartScanAsync(ScanRequest request, CancellationToken cancellationToken);
    Task UpsertFilesAsync(IReadOnlyList<FileRecord> files, CancellationToken cancellationToken);
    Task<bool> HasReusableHashesAsync(CancellationToken cancellationToken);
    Task<FileRecord?> FindReusableHashAsync(FileRecord metadata, CancellationToken cancellationToken);
    Task<IReadOnlyList<FileRecord>> GetDuplicateCandidatesAsync(long scanId, CancellationToken cancellationToken);
    Task CompleteScanAsync(long scanId, ScanResult result, CancellationToken cancellationToken);
    Task<ScanRootStatus> GetScanRootStatusAsync(string rootPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScanRootStatus>> GetScanRootStatusesAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken);
    Task<IReadOnlyList<DuplicateGroup>> GetSavedDuplicateGroupsAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken);
    Task<int> ClearScanDataForRootsAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken);
    Task RecordHardLinkOperationAsync(long scanId, string masterPath, string duplicatePath, HardLinkOperationResult result, CancellationToken cancellationToken);
    Task<IReadOnlyList<HardLinkPlanItem>> GetLatestPlanAsync(CancellationToken cancellationToken);
    string DatabasePath { get; }
}

public static class SizeParser
{
    private static readonly Regex SizePattern = new(@"^\s*(\d+)(?:\s*(b|kb|mb|gb|tb|kib|mib|gib|tib))?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static long Parse(string value)
    {
        var match = SizePattern.Match(value);
        if (!match.Success) throw new FormatException($"Invalid size: {value}");
        var number = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var unit = match.Groups[2].Value.ToLowerInvariant();
        return unit switch
        {
            "" or "b" => number,
            "kb" or "kib" => checked(number * 1024L),
            "mb" or "mib" => checked(number * 1024L * 1024L),
            "gb" or "gib" => checked(number * 1024L * 1024L * 1024L),
            "tb" or "tib" => checked(number * 1024L * 1024L * 1024L * 1024L),
            _ => throw new FormatException($"Invalid size unit: {unit}")
        };
    }
}

public sealed class ExclusionEngine
{
    private readonly IReadOnlyList<ExclusionRule> _rules;

    public ExclusionEngine(IEnumerable<ExclusionRule> rules)
    {
        _rules = rules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToArray();
    }

    public string? GetExclusionReason(string path, bool isDirectory)
    {
        foreach (var rule in _rules)
        {
            if (isDirectory && !rule.AppliesToDirectories) continue;
            if (!isDirectory && !rule.AppliesToFiles) continue;
            var comparison = rule.IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var matched = rule.RuleType.Equals("contains", StringComparison.OrdinalIgnoreCase)
                ? path.Contains(rule.Pattern, comparison)
                : WildcardMatch(name, rule.Pattern, comparison);
            if (matched) return rule.Name;
        }

        return null;
    }

    private static bool WildcardMatch(string value, string pattern, StringComparison comparison)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        var options = comparison == StringComparison.Ordinal ? RegexOptions.None : RegexOptions.IgnoreCase;
        return Regex.IsMatch(value, regex, options | RegexOptions.CultureInvariant);
    }
}

public static class Hashing
{
    public const int PartialHashVersion = 1;
    public const int FullHashVersion = 1;
    public const string Algorithm = "SHA-256";
    private const int BlockSize = 64 * 1024;

    public static async Task<string> ComputeFullHashAsync(string path, CancellationToken cancellationToken, Action<long, long>? progress = null)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        var buffer = new byte[1024 * 1024];
        var total = stream.Length;
        var readTotal = 0L;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            readTotal += read;
            progress?.Invoke(readTotal, total);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public static async Task<string> ComputePartialHashAsync(string path, long length, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        AppendInt64(sha, length);
        if (length <= BlockSize * 3)
        {
            await HashBlockAsync(stream, sha, 0, length, cancellationToken);
        }
        else
        {
            await HashBlockAsync(stream, sha, 0, BlockSize, cancellationToken);
            await HashBlockAsync(stream, sha, Math.Max(0, length / 2 - BlockSize / 2), BlockSize, cancellationToken);
            await HashBlockAsync(stream, sha, length - BlockSize, BlockSize, cancellationToken);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static async Task HashBlockAsync(FileStream stream, HashAlgorithm hash, long offset, long count, CancellationToken cancellationToken)
    {
        AppendInt64(hash, offset);
        AppendInt64(hash, count);
        stream.Position = offset;
        var buffer = new byte[Math.Min(BlockSize, count)];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) break;
            hash.TransformBlock(buffer, 0, read, null, 0);
            remaining -= read;
        }
    }

    private static void AppendInt64(HashAlgorithm hash, long value)
    {
        var bytes = BitConverter.GetBytes(value);
        hash.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }
}
