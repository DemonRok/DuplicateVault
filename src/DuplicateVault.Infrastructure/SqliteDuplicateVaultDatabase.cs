using DuplicateVault.Core;
using Microsoft.Data.Sqlite;

namespace DuplicateVault.Infrastructure;

public sealed class SqliteDuplicateVaultDatabase(string databasePath) : IDuplicateVaultDatabase
{
    public string DatabasePath { get; } = databasePath;
    private readonly Dictionary<long, ScanRequest> _scanRequests = [];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        await using var connection = Open();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, Schema, cancellationToken);
    }

    public async Task<long> StartScanAsync(ScanRequest request, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ScanSession(ProfileName,StartedUtc,Status,ScanMode,ApplicationVersion) VALUES($profile,$started,'Running',$mode,$version); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$profile", request.Profile.Name);
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$mode", request.Mode.ToString());
        command.Parameters.AddWithValue("$version", ThisAssembly.Version);
        var scanId = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        _scanRequests[scanId] = request;
        return scanId;
    }

    public async Task UpsertFilesAsync(IReadOnlyList<FileRecord> files, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var file in files)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)tx;
            command.CommandText = """
                INSERT INTO FileRecord(FullPath,RelativePath,FileName,Extension,Length,CreationTimeUtc,LastWriteTimeUtc,FileAttributes,PartialHash,FullHash,HashAlgorithm,PartialHashVersion,FullHashVersion,FileId,NumberOfLinks,LastSeenScanId,FirstSeenUtc,LastSeenUtc,IsMissing,IsExcluded,ExclusionReason)
                VALUES($full,$rel,$name,$ext,$len,$created,$written,$attrs,$partial,$fullhash,$alg,$phv,$fhv,$fileid,$links,$scan,$first,$last,0,$excluded,$reason)
                ON CONFLICT(FullPath) DO UPDATE SET RelativePath=$rel,FileName=$name,Extension=$ext,Length=$len,CreationTimeUtc=$created,LastWriteTimeUtc=$written,FileAttributes=$attrs,PartialHash=COALESCE($partial,PartialHash),FullHash=COALESCE($fullhash,FullHash),HashAlgorithm=$alg,PartialHashVersion=$phv,FullHashVersion=$fhv,FileId=$fileid,NumberOfLinks=$links,LastSeenScanId=$scan,LastSeenUtc=$last,IsMissing=0,IsExcluded=$excluded,ExclusionReason=$reason;
                """;
            Add(command, file);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<FileRecord?> FindReusableHashAsync(FileRecord metadata, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM FileRecord
            WHERE FullPath=$full AND Length=$len AND LastWriteTimeUtc=$written AND FileAttributes=$attrs
              AND ($fileid IS NULL OR IFNULL(FileId,'')=IFNULL($fileid,''))
              AND PartialHashVersion=$phv AND FullHashVersion=$fhv
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$full", metadata.FullPath);
        command.Parameters.AddWithValue("$len", metadata.Length);
        command.Parameters.AddWithValue("$written", metadata.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$attrs", (long)metadata.Attributes);
        command.Parameters.AddWithValue("$fileid", (object?)metadata.FileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$phv", metadata.PartialHashVersion);
        command.Parameters.AddWithValue("$fhv", metadata.FullHashVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<FileRecord>> GetDuplicateCandidatesAsync(long scanId, CancellationToken cancellationToken)
    {
        var result = new List<FileRecord>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM FileRecord
            WHERE LastSeenScanId=$scan AND IsExcluded=0 AND IsMissing=0 AND FullHash IS NOT NULL
              AND Length IN (SELECT Length FROM FileRecord WHERE LastSeenScanId=$scan AND IsExcluded=0 GROUP BY Length,FullHash HAVING COUNT(*) > 1)
            ORDER BY Length DESC, FullHash, FullPath;
            """;
        command.Parameters.AddWithValue("$scan", scanId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task CompleteScanAsync(long scanId, ScanResult result, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ScanSession SET CompletedUtc=$completed,Status=$status,TotalFilesEnumerated=$total,IncludedFiles=$included,
            PartialHashesCalculated=$partial,FullHashesCalculated=$full,CachedHashesReused=$cached,DuplicateGroups=$groups,
            ExistingHardLinks=$links,ReclaimableBytes=$bytes,ErrorCount=$errors,WasCancelled=$cancelled WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$completed", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", result.WasCancelled ? "Cancelled" : "Completed");
        command.Parameters.AddWithValue("$total", result.TotalFilesEnumerated);
        command.Parameters.AddWithValue("$included", result.IncludedFiles);
        command.Parameters.AddWithValue("$partial", result.PartialHashesCalculated);
        command.Parameters.AddWithValue("$full", result.FullHashesCalculated);
        command.Parameters.AddWithValue("$cached", result.CachedHashesReused);
        command.Parameters.AddWithValue("$groups", result.DuplicateGroups.Count);
        command.Parameters.AddWithValue("$links", result.ExistingHardLinks);
        command.Parameters.AddWithValue("$bytes", result.ReclaimableBytes);
        command.Parameters.AddWithValue("$errors", result.ErrorCount);
        command.Parameters.AddWithValue("$cancelled", result.WasCancelled ? 1 : 0);
        command.Parameters.AddWithValue("$id", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        if (_scanRequests.TryGetValue(scanId, out var request))
        {
            foreach (var root in request.Roots)
            {
                await UpsertRootStatusAsync(connection, Path.GetFullPath(root), result, cancellationToken);
            }
            _scanRequests.Remove(scanId);
        }
    }

    public async Task<ScanRootStatus> GetScanRootStatusAsync(string rootPath, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT RootPath,State,LastScanUtc,IncludedFiles FROM ScanRootStatus WHERE RootPath=$root LIMIT 1;";
        command.Parameters.AddWithValue("$root", Path.GetFullPath(rootPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(Path.GetFullPath(rootPath), ScanRootState.Unknown, null, 0);
        }

        return ReadRootStatus(reader);
    }

    public async Task<IReadOnlyList<ScanRootStatus>> GetScanRootStatusesAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken)
    {
        var statuses = new List<ScanRootStatus>();
        foreach (var root in rootPaths)
        {
            statuses.Add(await GetScanRootStatusAsync(root, cancellationToken));
        }
        return statuses;
    }

    public async Task<IReadOnlyList<DuplicateGroup>> GetSavedDuplicateGroupsAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken)
    {
        var roots = rootPaths
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return [];

        var records = new List<FileRecord>();
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM FileRecord
            WHERE IsExcluded=0 AND IsMissing=0 AND FullHash IS NOT NULL
            ORDER BY Length DESC, FullHash, FullPath;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = Read(reader);
            if (roots.Any(root => record.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            {
                records.Add(record);
            }
        }

        return BuildGroups(records);
    }

    public async Task<int> ClearScanDataForRootsAsync(IEnumerable<string> rootPaths, CancellationToken cancellationToken)
    {
        var roots = rootPaths
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roots.Length == 0) return 0;

        var affected = 0;
        await using var connection = Open();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var root in roots)
        {
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            affected += await ExecuteDeleteAsync(connection, (SqliteTransaction)tx,
                "DELETE FROM HardLinkOperation WHERE MasterPath=$root OR MasterPath LIKE $prefix OR DuplicatePath=$root OR DuplicatePath LIKE $prefix;",
                root, prefix, cancellationToken);
            affected += await ExecuteDeleteAsync(connection, (SqliteTransaction)tx,
                "DELETE FROM FileRecord WHERE FullPath=$root OR FullPath LIKE $prefix;",
                root, prefix, cancellationToken);
            affected += await ExecuteDeleteAsync(connection, (SqliteTransaction)tx,
                "DELETE FROM ScanRootStatus WHERE RootPath=$root;",
                root, prefix, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return affected;
    }

    public async Task RecordHardLinkOperationAsync(long scanId, string masterPath, string duplicatePath, HardLinkOperationResult result, CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO HardLinkOperation(ScanSessionId,MasterPath,DuplicatePath,StartedUtc,CompletedUtc,Status,RollbackPerformed,ErrorMessage,ApplicationVersion) VALUES($scan,$master,$dup,$start,$end,$status,$rollback,$error,$version);";
        command.Parameters.AddWithValue("$scan", scanId);
        command.Parameters.AddWithValue("$master", masterPath);
        command.Parameters.AddWithValue("$dup", duplicatePath);
        command.Parameters.AddWithValue("$start", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$end", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$rollback", result.RollbackPerformed ? 1 : 0);
        command.Parameters.AddWithValue("$error", result.Message);
        command.Parameters.AddWithValue("$version", ThisAssembly.Version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HardLinkPlanItem>> GetLatestPlanAsync(CancellationToken cancellationToken)
    {
        var latest = new List<HardLinkPlanItem>();
        var candidates = await GetDuplicateCandidatesAsync(await LatestScanIdAsync(cancellationToken), cancellationToken);
        foreach (var group in candidates.GroupBy(f => new { f.Length, f.FullHash }))
        {
            var master = group.OrderBy(f => f.FullPath.Length).First();
            foreach (var duplicate in group.Where(f => f.FullPath != master.FullPath))
            {
                var sameRecord = master.FileId is not null && master.FileId == duplicate.FileId;
                latest.Add(new(master.FullPath, duplicate.FullPath, !sameRecord, sameRecord ? "Already a hard link." : "Eligible pending final validation.", sameRecord ? 0 : duplicate.Length));
            }
        }
        return latest;
    }

    private async Task<long> LatestScanIdAsync(CancellationToken cancellationToken)
    {
        await using var connection = Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IFNULL(MAX(Id),0) FROM ScanSession;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        return connection;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ExecuteDeleteAsync(SqliteConnection connection, SqliteTransaction tx, string sql, string root, string prefix, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$prefix", prefix + "%");
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertRootStatusAsync(SqliteConnection connection, string rootPath, ScanResult result, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanRootStatus(RootPath,State,LastScanUtc,IncludedFiles)
            VALUES($root,$state,$last,$included)
            ON CONFLICT(RootPath) DO UPDATE SET State=$state,LastScanUtc=$last,IncludedFiles=$included;
            """;
        command.Parameters.AddWithValue("$root", rootPath);
        command.Parameters.AddWithValue("$state", result.WasCancelled ? ScanRootState.Partial.ToString() : ScanRootState.Complete.ToString());
        command.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$included", result.IncludedFiles);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ScanRootStatus ReadRootStatus(SqliteDataReader reader)
    {
        var stateText = reader.GetString(reader.GetOrdinal("State"));
        var state = Enum.TryParse<ScanRootState>(stateText, out var parsed) ? parsed : ScanRootState.Unknown;
        DateTime? lastScan = reader.IsDBNull(reader.GetOrdinal("LastScanUtc")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("LastScanUtc"))).ToUniversalTime();
        return new(
            reader.GetString(reader.GetOrdinal("RootPath")),
            state,
            lastScan,
            reader.GetInt64(reader.GetOrdinal("IncludedFiles")));
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

    private static void Add(SqliteCommand command, FileRecord file)
    {
        command.Parameters.AddWithValue("$full", file.FullPath);
        command.Parameters.AddWithValue("$rel", file.RelativePath);
        command.Parameters.AddWithValue("$name", file.FileName);
        command.Parameters.AddWithValue("$ext", file.Extension);
        command.Parameters.AddWithValue("$len", file.Length);
        command.Parameters.AddWithValue("$created", file.CreationTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$written", file.LastWriteTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$attrs", (long)file.Attributes);
        command.Parameters.AddWithValue("$partial", (object?)file.PartialHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$fullhash", (object?)file.FullHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$alg", file.HashAlgorithm);
        command.Parameters.AddWithValue("$phv", file.PartialHashVersion);
        command.Parameters.AddWithValue("$fhv", file.FullHashVersion);
        command.Parameters.AddWithValue("$fileid", (object?)file.FileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$links", file.NumberOfLinks);
        command.Parameters.AddWithValue("$scan", file.LastSeenScanId);
        command.Parameters.AddWithValue("$first", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$last", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$excluded", file.IsExcluded ? 1 : 0);
        command.Parameters.AddWithValue("$reason", (object?)file.ExclusionReason ?? DBNull.Value);
    }

    private static FileRecord Read(SqliteDataReader reader) => new(
        reader.GetString(reader.GetOrdinal("FullPath")),
        reader.GetString(reader.GetOrdinal("RelativePath")),
        reader.GetString(reader.GetOrdinal("FileName")),
        reader.GetString(reader.GetOrdinal("Extension")),
        reader.GetInt64(reader.GetOrdinal("Length")),
        DateTime.Parse(reader.GetString(reader.GetOrdinal("CreationTimeUtc"))).ToUniversalTime(),
        DateTime.Parse(reader.GetString(reader.GetOrdinal("LastWriteTimeUtc"))).ToUniversalTime(),
        (FileAttributes)reader.GetInt64(reader.GetOrdinal("FileAttributes")),
        reader.IsDBNull(reader.GetOrdinal("PartialHash")) ? null : reader.GetString(reader.GetOrdinal("PartialHash")),
        reader.IsDBNull(reader.GetOrdinal("FullHash")) ? null : reader.GetString(reader.GetOrdinal("FullHash")),
        reader.GetString(reader.GetOrdinal("HashAlgorithm")),
        reader.GetInt32(reader.GetOrdinal("PartialHashVersion")),
        reader.GetInt32(reader.GetOrdinal("FullHashVersion")),
        reader.IsDBNull(reader.GetOrdinal("FileId")) ? null : reader.GetString(reader.GetOrdinal("FileId")),
        (uint)reader.GetInt64(reader.GetOrdinal("NumberOfLinks")),
        reader.GetInt64(reader.GetOrdinal("LastSeenScanId")),
        reader.GetInt64(reader.GetOrdinal("IsMissing")) == 1,
        reader.GetInt64(reader.GetOrdinal("IsExcluded")) == 1,
        reader.IsDBNull(reader.GetOrdinal("ExclusionReason")) ? null : reader.GetString(reader.GetOrdinal("ExclusionReason")));

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS ApplicationMetadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        INSERT OR IGNORE INTO ApplicationMetadata(Key,Value) VALUES('DatabaseSchemaVersion','1'),('HashAlgorithmVersion','1'),('ConfigurationVersion','1');
        CREATE TABLE IF NOT EXISTS ScanSession(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, ProfileName TEXT, StartedUtc TEXT NOT NULL, CompletedUtc TEXT, Status TEXT NOT NULL, ScanMode TEXT NOT NULL,
          TotalFilesEnumerated INTEGER DEFAULT 0, IncludedFiles INTEGER DEFAULT 0, PartialHashesCalculated INTEGER DEFAULT 0, FullHashesCalculated INTEGER DEFAULT 0,
          CachedHashesReused INTEGER DEFAULT 0, DuplicateGroups INTEGER DEFAULT 0, ExistingHardLinks INTEGER DEFAULT 0, ReclaimableBytes INTEGER DEFAULT 0,
          ErrorCount INTEGER DEFAULT 0, WasCancelled INTEGER DEFAULT 0, ApplicationVersion TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS FileRecord(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, FullPath TEXT NOT NULL UNIQUE, RelativePath TEXT NOT NULL, FileName TEXT NOT NULL, Extension TEXT NOT NULL,
          Length INTEGER NOT NULL, CreationTimeUtc TEXT NOT NULL, LastWriteTimeUtc TEXT NOT NULL, FileAttributes INTEGER NOT NULL,
          PartialHash TEXT, FullHash TEXT, HashAlgorithm TEXT NOT NULL, PartialHashVersion INTEGER NOT NULL, FullHashVersion INTEGER NOT NULL,
          FileId TEXT, NumberOfLinks INTEGER NOT NULL, LastSeenScanId INTEGER NOT NULL, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL,
          IsMissing INTEGER NOT NULL DEFAULT 0, IsExcluded INTEGER NOT NULL DEFAULT 0, ExclusionReason TEXT, LastHashError TEXT, LastFileSystemError TEXT);
        CREATE TABLE IF NOT EXISTS HardLinkOperation(
          Id INTEGER PRIMARY KEY AUTOINCREMENT, ScanSessionId INTEGER NOT NULL, GroupId INTEGER, MasterPath TEXT NOT NULL, DuplicatePath TEXT NOT NULL,
          MasterFileIdBefore TEXT, DuplicateFileIdBefore TEXT, ResultingFileId TEXT, StartedUtc TEXT NOT NULL, CompletedUtc TEXT, Status TEXT NOT NULL,
          RollbackPerformed INTEGER NOT NULL, ErrorCode INTEGER, ErrorMessage TEXT, ApplicationVersion TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS ScanRootStatus(
          RootPath TEXT PRIMARY KEY, State TEXT NOT NULL, LastScanUtc TEXT, IncludedFiles INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_FullPath ON FileRecord(FullPath);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_RelativePath ON FileRecord(RelativePath);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_Length ON FileRecord(Length);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_Partial ON FileRecord(Length,PartialHash);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_Full ON FileRecord(Length,FullHash);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_FileId ON FileRecord(FileId);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_LastWrite ON FileRecord(LastWriteTimeUtc);
        CREATE INDEX IF NOT EXISTS IX_FileRecord_LastSeen ON FileRecord(LastSeenScanId);
        """;
}

internal static class ThisAssembly
{
    public static string Version => typeof(ThisAssembly).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion
        ?? typeof(ThisAssembly).Assembly.GetName().Version?.ToString()
        ?? "0.0.0.0";
}
