using DuplicateVault.Core;
using DuplicateVault.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace DuplicateVault.Infrastructure.Tests;

public sealed class ScannerTests
{
    [Fact]
    public async Task ScanAsync_FindsDuplicateGroupAndReusesHashes()
    {
        var root = NewDirectory();
        var dataRoot = NewDirectory();
        var content = new byte[1024 * 1024 + 16];
        new Random(7).NextBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(root, "a.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(root, "b.bin"), content);

        var paths = new PortableDataRoot().Initialize(dataRoot);
        var database = new SqliteDuplicateVaultDatabase(paths.DatabasePath);
        var scanner = new FileScanner(database, new HardLinkService(), NullLogger<FileScanner>.Instance);
        var request = new ScanRequest([root], ScanMode.Quick, ServiceCollectionExtensions.DefaultProfile(), dataRoot);

        var first = await scanner.ScanAsync(request, null, CancellationToken.None);
        var second = await scanner.ScanAsync(request, null, CancellationToken.None);

        Assert.Single(first.DuplicateGroups);
        Assert.True(first.ReclaimableBytes > 0);
        Assert.True(second.CachedHashesReused > 0);
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_ReturnsPartialResultAndKeepsDatabase()
    {
        var root = NewDirectory();
        var dataRoot = NewDirectory();
        for (var index = 0; index < 50; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(root, $"file-{index}.txt"), "content");
        }

        var paths = new PortableDataRoot().Initialize(dataRoot);
        var database = new SqliteDuplicateVaultDatabase(paths.DatabasePath);
        var scanner = new FileScanner(database, new HardLinkService(), NullLogger<FileScanner>.Instance);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(_ => cts.Cancel());

        var result = await scanner.ScanAsync(new ScanRequest([root], ScanMode.Quick, ServiceCollectionExtensions.DefaultProfile(1), dataRoot), progress, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.True(File.Exists(paths.DatabasePath));
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DuplicateVaultTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
