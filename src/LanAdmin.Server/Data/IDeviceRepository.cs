using LanAdmin.Contracts;

namespace LanAdmin.Server.Data;

public interface IDeviceRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpsertRegistrationAsync(AgentRegisterMessage message, CancellationToken cancellationToken);
    Task RecordHeartbeatAsync(AgentHeartbeatMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceDto>> GetDevicesAsync(string? search, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceEventDto>> GetDeviceEventsAsync(string? agentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DeviceGroupDto>> GetGroupsAsync(CancellationToken cancellationToken);
    Task<DeviceGroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken);
    Task<DeviceGroupDto?> RenameGroupAsync(long groupId, string name, CancellationToken cancellationToken);
    Task<bool> DeleteGroupAsync(long groupId, CancellationToken cancellationToken);
    Task<bool> AssignGroupAsync(string agentId, long? groupId, CancellationToken cancellationToken);
    Task<bool> DeleteDeviceAsync(string agentId, CancellationToken cancellationToken);
    Task<int> MarkOfflineDevicesAsync(DateTimeOffset threshold, CancellationToken cancellationToken);
}
