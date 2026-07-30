using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DuplicateVault.Core;

namespace DuplicateVault.Infrastructure;

public sealed class VolumeIdentityService : IVolumeIdentityService
{
    public Task<VolumeIdentity> GetVolumeIdentityAsync(string rootPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? rootPath;
        var drive = new DriveInfo(root);
        Win32.GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0);
        return Task.FromResult(new VolumeIdentity(root, serial.ToString("X8"), drive.IsReady ? drive.VolumeLabel : null, drive.DriveFormat, drive.TotalSize, drive.DriveType.ToString(), root));
    }
}

public sealed class HardLinkService : IHardLinkService
{
    public async Task<HardLinkValidationResult> ValidateAsync(string masterPath, string duplicatePath, HardLinkOptions options, CancellationToken cancellationToken)
    {
        if (!File.Exists(masterPath)) return new(false, "Master file does not exist.");
        if (!File.Exists(duplicatePath)) return new(false, "Duplicate file does not exist.");
        var masterInfo = new FileInfo(masterPath);
        var duplicateInfo = new FileInfo(duplicatePath);
        if (masterInfo.Length != duplicateInfo.Length) return new(false, "File lengths differ.");
        if ((masterInfo.Attributes & FileAttributes.ReparsePoint) != 0 || (duplicateInfo.Attributes & FileAttributes.ReparsePoint) != 0) return new(false, "Reparse points are not supported.");
        var masterRoot = Path.GetPathRoot(masterInfo.FullName);
        var duplicateRoot = Path.GetPathRoot(duplicateInfo.FullName);
        if (!string.Equals(masterRoot, duplicateRoot, StringComparison.OrdinalIgnoreCase)) return new(false, "NTFS hard links cannot cross volumes.");
        var drive = new DriveInfo(masterRoot!);
        if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return new(false, "The target volume is not NTFS.");
        var masterIdentity = await GetFileIdentityAsync(masterPath, cancellationToken);
        var duplicateIdentity = await GetFileIdentityAsync(duplicatePath, cancellationToken);
        if (masterIdentity.FileIndex == duplicateIdentity.FileIndex && masterIdentity.VolumeSerialNumber == duplicateIdentity.VolumeSerialNumber) return new(false, "The files are already hard links to the same NTFS record.", masterIdentity, duplicateIdentity);
        if (options.StrictByteVerification && !await ByteEqualsAsync(masterPath, duplicatePath, cancellationToken)) return new(false, "Strict byte verification failed.", masterIdentity, duplicateIdentity);
        return new(true, "Eligible.", masterIdentity, duplicateIdentity);
    }

    public async Task<HardLinkOperationResult> ReplaceWithHardLinkAsync(string masterPath, string duplicatePath, HardLinkOptions options, CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(masterPath, duplicatePath, options, cancellationToken);
        if (!validation.IsEligible) return new(HardLinkOperationStatus.Failed, validation.Reason, false);
        if (options.DryRun) return new(HardLinkOperationStatus.Planned, "Dry run: no file was modified.", false);

        var duplicateDirectory = Path.GetDirectoryName(Path.GetFullPath(duplicatePath))!;
        var backupPath = Path.Combine(duplicateDirectory, Path.GetFileName(duplicatePath) + $".duplicatevault-backup-{Guid.NewGuid():N}");
        var renamed = false;
        try
        {
            File.Move(duplicatePath, backupPath);
            renamed = true;
            if (options.SimulateFailureAfterRename) throw new IOException("Simulated failure after duplicate rename.");
            if (!Win32.CreateHardLink(duplicatePath, masterPath, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var resulting = await GetFileIdentityAsync(duplicatePath, cancellationToken);
            if (validation.MasterIdentity is null || resulting.FileIndex != validation.MasterIdentity.FileIndex || resulting.VolumeSerialNumber != validation.MasterIdentity.VolumeSerialNumber)
            {
                File.Delete(duplicatePath);
                throw new IOException("Hard-link verification failed.");
            }
            File.Delete(backupPath);
            return new(HardLinkOperationStatus.Succeeded, "Hard link created and verified.", false, resulting);
        }
        catch (Exception ex)
        {
            var rollback = false;
            try
            {
                if (File.Exists(duplicatePath)) File.Delete(duplicatePath);
                if (renamed && File.Exists(backupPath) && !File.Exists(duplicatePath))
                {
                    File.Move(backupPath, duplicatePath);
                    rollback = true;
                }
            }
            catch (Exception rollbackEx)
            {
                return new(HardLinkOperationStatus.RollbackFailed, $"{ex.Message} Rollback failed: {rollbackEx.Message}", false);
            }

            return new(rollback ? HardLinkOperationStatus.RolledBack : HardLinkOperationStatus.Failed, ex.Message, rollback);
        }
    }

    public Task<FileIdentity> GetFileIdentityAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var handle = Win32.CreateFile(path, Win32.GenericRead, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, Win32.FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        if (!Win32.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        var fileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        return Task.FromResult(new FileIdentity(Path.GetFullPath(path), root, fileIndex, info.VolumeSerialNumber, info.NumberOfLinks));
    }

    public Task<IReadOnlyList<string>> GetHardLinkPathsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>([Path.GetFullPath(path)]);
    }

    private static async Task<bool> ByteEqualsAsync(string first, string second, CancellationToken cancellationToken)
    {
        await using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        await using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        if (a.Length != b.Length) return false;
        var ab = new byte[128 * 1024];
        var bb = new byte[128 * 1024];
        while (true)
        {
            var ar = await a.ReadAsync(ab, cancellationToken);
            var br = await b.ReadAsync(bb, cancellationToken);
            if (ar != br) return false;
            if (ar == 0) return true;
            if (!ab.AsSpan(0, ar).SequenceEqual(bb.AsSpan(0, br))) return false;
        }
    }
}

internal static partial class Win32
{
    internal const uint GenericRead = 0x80000000;
    internal const int FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, int flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool GetVolumeInformation(string rootPathName, string? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, string? fileSystemNameBuffer, int fileSystemNameSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
