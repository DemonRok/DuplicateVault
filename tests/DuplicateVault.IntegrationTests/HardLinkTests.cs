using DuplicateVault.Core;
using DuplicateVault.Infrastructure;

namespace DuplicateVault.IntegrationTests;

public sealed class HardLinkTests
{
    [Fact]
    public async Task ReplaceWithHardLinkAsync_CreatesVerifiedHardLink()
    {
        var root = NewDirectory();
        if (!new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        var master = Path.Combine(root, "master.bin");
        var duplicate = Path.Combine(root, "duplicate.bin");
        await File.WriteAllTextAsync(master, new string('a', 2048));
        await File.WriteAllTextAsync(duplicate, new string('a', 2048));
        var service = new HardLinkService();

        var result = await service.ReplaceWithHardLinkAsync(master, duplicate, new HardLinkOptions(StrictByteVerification: true), CancellationToken.None);
        var masterId = await service.GetFileIdentityAsync(master, CancellationToken.None);
        var duplicateId = await service.GetFileIdentityAsync(duplicate, CancellationToken.None);

        Assert.Equal(HardLinkOperationStatus.Succeeded, result.Status);
        Assert.Equal(masterId.FileIndex, duplicateId.FileIndex);
    }

    [Fact]
    public async Task ReplaceWithHardLinkAsync_RollsBackAfterSimulatedFailure()
    {
        var root = NewDirectory();
        if (!new DriveInfo(Path.GetPathRoot(root)!).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return;
        var master = Path.Combine(root, "master.bin");
        var duplicate = Path.Combine(root, "duplicate.bin");
        await File.WriteAllTextAsync(master, "same");
        await File.WriteAllTextAsync(duplicate, "same");
        var service = new HardLinkService();

        var result = await service.ReplaceWithHardLinkAsync(master, duplicate, new HardLinkOptions(SimulateFailureAfterRename: true), CancellationToken.None);

        Assert.Equal(HardLinkOperationStatus.RolledBack, result.Status);
        Assert.True(File.Exists(duplicate));
        Assert.Equal("same", await File.ReadAllTextAsync(duplicate));
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DuplicateVaultHardLinks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
