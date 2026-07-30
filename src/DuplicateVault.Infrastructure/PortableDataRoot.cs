using DuplicateVault.Core;

namespace DuplicateVault.Infrastructure;

public sealed class PortableDataRoot : IPortableDataRoot
{
    public AppPaths Initialize(string? dataRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(dataRoot) ? AppContext.BaseDirectory : dataRoot);
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.Conf);
        Directory.CreateDirectory(paths.Db);
        Directory.CreateDirectory(paths.Logs);
        Directory.CreateDirectory(paths.Reports);
        Directory.CreateDirectory(paths.Backups);
        Directory.CreateDirectory(paths.Quarantine);
        Directory.CreateDirectory(paths.Temp);
        EnsureWritable(root);
        CopyDefault(Path.Combine(paths.Conf, "appsettings.default.json"), Path.Combine(paths.Conf, "appsettings.json"));
        CopyDefault(Path.Combine(paths.Conf, "exclusions.default.json"), Path.Combine(paths.Conf, "exclusions.json"));
        return paths;
    }

    private static void EnsureWritable(string root)
    {
        var probe = Path.Combine(root, $".duplicatevault-write-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
    }

    private static void CopyDefault(string source, string target)
    {
        if (File.Exists(source) && !File.Exists(target)) File.Copy(source, target);
    }
}
