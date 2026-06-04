using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LanAdmin.Contracts;

namespace LanAdmin.Server.Services;

public sealed class AgentConnectionRegistry
{
    private readonly ConcurrentDictionary<string, AgentConnection> _connections = new(StringComparer.OrdinalIgnoreCase);

    public void Bind(string agentId, WebSocket socket)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        _connections.AddOrUpdate(
            agentId,
            _ => new AgentConnection(socket),
            (_, existing) =>
            {
                if (ReferenceEquals(existing.Socket, socket))
                {
                    return existing;
                }

                existing.Dispose();
                return new AgentConnection(socket);
            });
    }

    public void Unbind(string agentId, WebSocket socket)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        if (_connections.TryGetValue(agentId, out var existing) && ReferenceEquals(existing.Socket, socket))
        {
            _connections.TryRemove(agentId, out _);
            existing.Dispose();
        }
    }

    public async Task<bool> SendManualShutdownReminderAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!_connections.TryGetValue(agentId, out var connection) || connection.Socket.State != WebSocketState.Open)
        {
            _connections.TryRemove(agentId, out _);
            return false;
        }

        var payload = new ManualShutdownReminderMessage(
            agentId,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow);

        var envelope = new { type = ServerMessageTypes.ManualShutdownReminder, payload };
        var json = JsonSerializer.Serialize(envelope, JsonDefaults.Options);
        var bytes = Encoding.UTF8.GetBytes(json);

        await connection.SendLock.WaitAsync(cancellationToken);
        try
        {
            if (connection.Socket.State != WebSocketState.Open)
            {
                return false;
            }

            await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            _connections.TryRemove(agentId, out _);
            return false;
        }
        finally
        {
            connection.SendLock.Release();
        }
    }

    private sealed class AgentConnection : IDisposable
    {
        public AgentConnection(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public void Dispose()
        {
            SendLock.Dispose();
        }
    }
}
