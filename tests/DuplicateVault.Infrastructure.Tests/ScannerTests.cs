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
    public async Task GetSavedDuplicateGroupsAsync_ReturnsGroupsFromPreviousScan()
    {
        var root = NewDirectory();
        var dataRoot = NewDirectory();
        var content = new byte[1024 * 1024 + 16];
        new Random(13).NextBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(root, "saved-a.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(root, "saved-b.bin"), content);

        var paths = new PortableDataRoot().Initialize(dataRoot);
        var database = new SqliteDuplicateVaultDatabase(paths.DatabasePath);
        var scanner = new FileScanner(database, new HardLinkService(), NullLogger<FileScanner>.Instance);

        await scanner.ScanAsync(new ScanRequest([root], ScanMode.Quick, ServiceCollectionExtensions.DefaultProfile(), dataRoot), null, CancellationToken.None);
        var saved = await database.GetSavedDuplicateGroupsAsync([root], CancellationToken.None);

        Assert.Single(saved);
        Assert.Equal(2, saved[0].Files.Count);
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
        var status = await database.GetScanRootStatusAsync(root, CancellationToken.None);
        Assert.Equal(ScanRootState.Partial, status.State);
    }

    [Fact]
    public async Task ScanAsync_WhenCancelled_FinalizesDuplicatesFromEnumeratedFiles()
    {
        var root = NewDirectory();
        var dataRoot = NewDirectory();
        var content = new byte[1024 * 1024 + 32];
        new Random(11).NextBytes(content);
        await File.WriteAllBytesAsync(Path.Combine(root, "duplicate-a.bin"), content);
        await File.WriteAllBytesAsync(Path.Combine(root, "duplicate-b.bin"), content);
        await File.WriteAllTextAsync(Path.Combine(root, "tail.txt"), "tail");

        var paths = new PortableDataRoot().Initialize(dataRoot);
        var database = new SqliteDuplicateVaultDatabase(paths.DatabasePath);
        var scanner = new FileScanner(database, new HardLinkService(), NullLogger<FileScanner>.Instance);
        using var cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Message == "Enumerating" && p.EnumeratedFiles >= 2) cts.Cancel();
        });

        var result = await scanner.ScanAsync(new ScanRequest([root], ScanMode.Quick, ServiceCollectionExtensions.DefaultProfile(1), dataRoot), progress, cts.Token);

        Assert.True(result.WasCancelled);
        Assert.NotEmpty(result.DuplicateGroups);
        Assert.True(result.ReclaimableBytes > 0);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DuplicateVaultTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
