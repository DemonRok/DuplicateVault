using DuplicateVault.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DuplicateVault.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDuplicateVault(this IServiceCollection services, string dataRoot)
    {
        var paths = new PortableDataRoot().Initialize(dataRoot);
        services.AddSingleton(paths);
        services.AddSingleton<IPortableDataRoot, PortableDataRoot>();
        services.AddSingleton<IDuplicateVaultDatabase>(_ => new SqliteDuplicateVaultDatabase(paths.DatabasePath));
        services.AddSingleton<IHardLinkService, HardLinkService>();
        services.AddSingleton<IVolumeIdentityService, VolumeIdentityService>();
        services.AddSingleton<IFileScanner, FileScanner>();
        return services;
    }

    public static ScanProfile DefaultProfile(long? minimumSize = null, bool strict = false) => new(
        "Default",
        minimumSize ?? 1024 * 1024,
        strict,
        [
            new("Windows recycle bin", "wildcard", "$RECYCLE.BIN", true, false, true, false, 10),
            new("System volume information", "wildcard", "System Volume Information", true, false, true, false, 20),
            new("Temporary files", "wildcard", "*.tmp", true, true, false, false, 100)
        ]);
}
