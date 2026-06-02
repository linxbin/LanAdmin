using LanAdmin.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LanAdmin.Server.Data;

public sealed class SqliteDeviceRepository : IDeviceRepository
{
    private readonly string _connectionString;

    public SqliteDeviceRepository(IOptions<DatabaseOptions> databaseOptions)
    {
        var dbPath = Path.GetFullPath(databaseOptions.Value.Path, AppContext.BaseDirectory);
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS DeviceGroups (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Devices (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AgentId TEXT NOT NULL UNIQUE,
                HostName TEXT NOT NULL,
                IpAddress TEXT NOT NULL,
                MacAddress TEXT NOT NULL,
                CurrentUser TEXT NOT NULL,
                OsVersion TEXT NOT NULL,
                AgentVersion TEXT NOT NULL,
                Status INTEGER NOT NULL,
                LastSeenAt TEXT NOT NULL,
                GroupId INTEGER NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY(GroupId) REFERENCES DeviceGroups(Id)
            );

            CREATE TABLE IF NOT EXISTS DeviceEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                AgentId TEXT NOT NULL,
                EventType INTEGER NOT NULL,
                Message TEXT NOT NULL,
                OccurredAt TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertRegistrationAsync(AgentRegisterMessage message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var existing = await GetDeviceRecordAsync(connection, transaction, message.AgentId, cancellationToken);
        var now = message.ReportedAt.ToUniversalTime();

        if (existing is null)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO Devices (
                    AgentId, HostName, IpAddress, MacAddress, CurrentUser, OsVersion, AgentVersion,
                    Status, LastSeenAt, GroupId, CreatedAt, UpdatedAt
                ) VALUES (
                    $agentId, $hostName, $ipAddress, $macAddress, $currentUser, $osVersion, $agentVersion,
                    $status, $lastSeenAt, NULL, $createdAt, $updatedAt
                );
                """;
            BindCommon(insert, message.AgentId, message.HostName, message.IpAddress, message.MacAddress, message.CurrentUser, message.OsVersion, message.AgentVersion, now);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await InsertEventAsync(connection, transaction, message.AgentId, DeviceEventType.Registered, $"Device {message.HostName} registered.", now, cancellationToken);
            await InsertEventAsync(connection, transaction, message.AgentId, DeviceEventType.Online, $"Device {message.HostName} is online.", now, cancellationToken);
        }
        else
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Devices
                SET HostName = $hostName,
                    IpAddress = $ipAddress,
                    MacAddress = $macAddress,
                    CurrentUser = $currentUser,
                    OsVersion = $osVersion,
                    AgentVersion = $agentVersion,
                    Status = $status,
                    LastSeenAt = $lastSeenAt,
                    UpdatedAt = $updatedAt
                WHERE AgentId = $agentId;
                """;
            BindCommon(update, message.AgentId, message.HostName, message.IpAddress, message.MacAddress, message.CurrentUser, message.OsVersion, message.AgentVersion, now);
            await update.ExecuteNonQueryAsync(cancellationToken);

            if (existing.Status == DeviceStatus.Offline)
            {
                await InsertEventAsync(connection, transaction, message.AgentId, DeviceEventType.Online, $"Device {message.HostName} is online.", now, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordHeartbeatAsync(AgentHeartbeatMessage message, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await GetDeviceRecordAsync(connection, transaction, message.AgentId, cancellationToken);
        if (existing is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var now = message.ReportedAt.ToUniversalTime();

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE Devices
            SET IpAddress = $ipAddress,
                CurrentUser = $currentUser,
                Status = $status,
                LastSeenAt = $lastSeenAt,
                UpdatedAt = $updatedAt
            WHERE AgentId = $agentId;
            """;
        update.Parameters.AddWithValue("$agentId", message.AgentId);
        update.Parameters.AddWithValue("$ipAddress", message.IpAddress);
        update.Parameters.AddWithValue("$currentUser", message.CurrentUser);
        update.Parameters.AddWithValue("$status", (int)DeviceStatus.Online);
        update.Parameters.AddWithValue("$lastSeenAt", now.ToString("O"));
        update.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await update.ExecuteNonQueryAsync(cancellationToken);

        if (existing.Status == DeviceStatus.Offline)
        {
            await InsertEventAsync(connection, transaction, message.AgentId, DeviceEventType.Online, $"Device {existing.HostName} is online.", now, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(string? search, CancellationToken cancellationToken)
    {
        var results = new List<DeviceDto>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.AgentId, d.HostName, d.IpAddress, d.MacAddress, d.CurrentUser, d.OsVersion, d.AgentVersion,
                   d.Status, d.LastSeenAt, g.Name
            FROM Devices d
            LEFT JOIN DeviceGroups g ON g.Id = d.GroupId
            WHERE $search IS NULL OR d.HostName LIKE $pattern OR d.AgentId LIKE $pattern
            ORDER BY d.HostName ASC;
            """;
        command.Parameters.AddWithValue("$search", string.IsNullOrWhiteSpace(search) ? DBNull.Value : search);
        command.Parameters.AddWithValue("$pattern", $"%{search?.Trim()}%");

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DeviceDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                (DeviceStatus)reader.GetInt32(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<DeviceEventDto>> GetDeviceEventsAsync(string? agentId, CancellationToken cancellationToken)
    {
        var results = new List<DeviceEventDto>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT Id, AgentId, EventType, Message, OccurredAt
            FROM DeviceEvents
            WHERE $agentId IS NULL OR AgentId = $agentId
            ORDER BY OccurredAt DESC
            LIMIT 200;
            """;
        command.Parameters.AddWithValue("$agentId", string.IsNullOrWhiteSpace(agentId) ? DBNull.Value : agentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DeviceEventDto(
                reader.GetInt64(0),
                reader.GetString(1),
                (DeviceEventType)reader.GetInt32(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }

        return results;
    }

    public async Task<IReadOnlyList<DeviceGroupDto>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        var results = new List<DeviceGroupDto>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt, UpdatedAt FROM DeviceGroups ORDER BY Name ASC;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new DeviceGroupDto(
                reader.GetInt64(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)),
                DateTimeOffset.Parse(reader.GetString(3))));
        }

        return results;
    }

    public async Task<DeviceGroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO DeviceGroups (Name, CreatedAt, UpdatedAt)
            VALUES ($name, $createdAt, $updatedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));

        var id = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        return new DeviceGroupDto(id, name, now, now);
    }

    public async Task<DeviceGroupDto?> RenameGroupAsync(long groupId, string name, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE DeviceGroups
            SET Name = $name,
                UpdatedAt = $updatedAt
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", groupId);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));

        var updatedRows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (updatedRows == 0)
        {
            return null;
        }

        return new DeviceGroupDto(groupId, name, now, now);
    }

    public async Task<bool> AssignGroupAsync(string agentId, long? groupId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = await GetDeviceRecordAsync(connection, transaction, agentId, cancellationToken);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        string? groupName = null;
        if (groupId.HasValue)
        {
            await using var groupCommand = connection.CreateCommand();
            groupCommand.Transaction = transaction;
            groupCommand.CommandText = "SELECT Name FROM DeviceGroups WHERE Id = $id;";
            groupCommand.Parameters.AddWithValue("$id", groupId.Value);
            groupName = (string?)await groupCommand.ExecuteScalarAsync(cancellationToken);
            if (groupName is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText =
            """
            UPDATE Devices
            SET GroupId = $groupId,
                UpdatedAt = $updatedAt
            WHERE AgentId = $agentId;
            """;
        update.Parameters.AddWithValue("$groupId", groupId.HasValue ? groupId.Value : DBNull.Value);
        update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$agentId", agentId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        var eventMessage = groupName is null
            ? $"Device {existing.HostName} removed from group."
            : $"Device {existing.HostName} assigned to group {groupName}.";
        await InsertEventAsync(connection, transaction, agentId, DeviceEventType.GroupChanged, eventMessage, DateTimeOffset.UtcNow, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> MarkOfflineDevicesAsync(DateTimeOffset threshold, CancellationToken cancellationToken)
    {
        var marked = 0;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText =
            """
            SELECT AgentId, HostName
            FROM Devices
            WHERE Status = $status AND LastSeenAt < $threshold;
            """;
        query.Parameters.AddWithValue("$status", (int)DeviceStatus.Online);
        query.Parameters.AddWithValue("$threshold", threshold.ToString("O"));

        var offlineCandidates = new List<(string AgentId, string HostName)>();
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                offlineCandidates.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var candidate in offlineCandidates)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE Devices
                SET Status = $status,
                    UpdatedAt = $updatedAt
                WHERE AgentId = $agentId;
                """;
            update.Parameters.AddWithValue("$status", (int)DeviceStatus.Offline);
            update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$agentId", candidate.AgentId);
            marked += await update.ExecuteNonQueryAsync(cancellationToken);

            await InsertEventAsync(connection, transaction, candidate.AgentId, DeviceEventType.Offline, $"Device {candidate.HostName} is offline.", DateTimeOffset.UtcNow, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return marked;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void BindCommon(SqliteCommand command, string agentId, string hostName, string ipAddress, string macAddress, string currentUser, string osVersion, string agentVersion, DateTimeOffset timestamp)
    {
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$hostName", hostName);
        command.Parameters.AddWithValue("$ipAddress", ipAddress);
        command.Parameters.AddWithValue("$macAddress", macAddress);
        command.Parameters.AddWithValue("$currentUser", currentUser);
        command.Parameters.AddWithValue("$osVersion", osVersion);
        command.Parameters.AddWithValue("$agentVersion", agentVersion);
        command.Parameters.AddWithValue("$status", (int)DeviceStatus.Online);
        command.Parameters.AddWithValue("$lastSeenAt", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$createdAt", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", timestamp.ToString("O"));
    }

    private static async Task InsertEventAsync(SqliteConnection connection, SqliteTransaction transaction, string agentId, DeviceEventType eventType, string message, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO DeviceEvents (AgentId, EventType, Message, OccurredAt)
            VALUES ($agentId, $eventType, $message, $occurredAt);
            """;
        command.Parameters.AddWithValue("$agentId", agentId);
        command.Parameters.AddWithValue("$eventType", (int)eventType);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$occurredAt", occurredAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DeviceRecord?> GetDeviceRecordAsync(SqliteConnection connection, SqliteTransaction transaction, string agentId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT HostName, Status FROM Devices WHERE AgentId = $agentId;";
        command.Parameters.AddWithValue("$agentId", agentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new DeviceRecord(reader.GetString(0), (DeviceStatus)reader.GetInt32(1));
    }

    private sealed record DeviceRecord(string HostName, DeviceStatus Status);
}
