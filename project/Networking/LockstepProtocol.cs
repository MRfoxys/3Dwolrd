using System;
using System.Text.Json;

public sealed class SimulationSnapshot
{
    public long Tick;
    public ulong StateHash;
}

public interface ILockstepTransport
{
    event Action<PlayerCommand> CommandReceived;
    event Action<SimulationSnapshot> SnapshotReceived;

    void SendCommand(PlayerCommand command);
    void SendSnapshot(SimulationSnapshot snapshot);
}

/// <summary>
/// In-process transport used as default when no multiplayer session is attached.
/// It keeps the same codepath for local and network lockstep.
/// </summary>
public sealed class LocalLoopbackLockstepTransport : ILockstepTransport
{
    public event Action<PlayerCommand> CommandReceived;
    public event Action<SimulationSnapshot> SnapshotReceived;

    public void SendCommand(PlayerCommand command)
    {
        CommandReceived?.Invoke(command);
    }

    public void SendSnapshot(SimulationSnapshot snapshot)
    {
        SnapshotReceived?.Invoke(snapshot);
    }
}

public static class LockstepProtocolCodec
{
    public static string SerializeCommand(PlayerCommand command)
    {
        return JsonSerializer.Serialize(command);
    }

    public static PlayerCommand DeserializeCommand(string json)
    {
        return JsonSerializer.Deserialize<PlayerCommand>(json);
    }

    public static string SerializeSnapshot(SimulationSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot);
    }

    public static SimulationSnapshot DeserializeSnapshot(string json)
    {
        return JsonSerializer.Deserialize<SimulationSnapshot>(json);
    }
}
