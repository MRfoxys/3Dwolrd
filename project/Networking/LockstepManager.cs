using System.Collections.Generic;
using System;
using Godot;

public class LockstepManager
{
    public long CurrentTick { get; private set; }
    public Dictionary<long, List<PlayerCommand>> Commands = new();
    readonly Dictionary<long, ulong> localStateHashByTick = new();
    readonly Dictionary<long, ulong> remoteStateHashByTick = new();
    int nextLocalSequence = 1;
    ILockstepTransport transport;

    public event Action<SimulationSnapshot, SimulationSnapshot> OnSnapshotDivergence;

    public LockstepManager(ILockstepTransport transport = null)
    {
        AttachTransport(transport ?? new LocalLoopbackLockstepTransport());
    }

    public void AttachTransport(ILockstepTransport newTransport)
    {
        if (transport != null)
        {
            transport.CommandReceived -= OnTransportCommandReceived;
            transport.SnapshotReceived -= OnTransportSnapshotReceived;
        }

        transport = newTransport;
        if (transport == null)
            return;

        transport.CommandReceived += OnTransportCommandReceived;
        transport.SnapshotReceived += OnTransportSnapshotReceived;
    }

    public void AddCommand(PlayerCommand cmd)
    {
        if (cmd == null)
            return;
        if (!Commands.ContainsKey(cmd.Tick))
            Commands[cmd.Tick] = new List<PlayerCommand>();

        var bucket = Commands[cmd.Tick];

        if (cmd.Type == "MOVE")
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i].Type == "MOVE" && bucket[i].EntityId == cmd.EntityId)
                {
                    bucket[i] = cmd;
                    return;
                }
            }
        }

        bucket.Add(cmd);
    }

    public void SubmitLocalCommand(PlayerCommand cmd)
    {
        if (cmd == null)
            return;

        cmd.Sequence = nextLocalSequence++;
        if (transport != null)
            transport.SendCommand(cmd);
        else
            AddCommand(cmd);
    }

    public void AdvanceToTick(long tick)
    {
        CurrentTick = tick;
        PrunePastTicks(tick - 4);
    }

    public void PublishLocalSnapshot(SimulationSnapshot snapshot)
    {
        if (snapshot == null)
            return;
        localStateHashByTick[snapshot.Tick] = snapshot.StateHash;
        transport?.SendSnapshot(snapshot);
        TryDetectDivergence(snapshot.Tick);
    }

    void OnTransportCommandReceived(PlayerCommand cmd)
    {
        if (cmd == null)
            return;
        AddCommand(cmd);
    }

    void OnTransportSnapshotReceived(SimulationSnapshot snapshot)
    {
        if (snapshot == null)
            return;
        remoteStateHashByTick[snapshot.Tick] = snapshot.StateHash;
        TryDetectDivergence(snapshot.Tick);
    }

    void TryDetectDivergence(long tick)
    {
        if (!localStateHashByTick.TryGetValue(tick, out ulong localHash))
            return;
        if (!remoteStateHashByTick.TryGetValue(tick, out ulong remoteHash))
            return;
        if (localHash == remoteHash)
            return;

        var localSnapshot = new SimulationSnapshot { Tick = tick, StateHash = localHash };
        var remoteSnapshot = new SimulationSnapshot { Tick = tick, StateHash = remoteHash };
        GD.PrintErr($"[Lockstep] Divergence tick={tick} local={localHash} remote={remoteHash}");
        OnSnapshotDivergence?.Invoke(localSnapshot, remoteSnapshot);
    }

    public List<PlayerCommand> GetCommandsForTick(long tick)
    {
        if (!Commands.TryGetValue(tick, out var cmds))
            return new List<PlayerCommand>();

        cmds.Sort(static (a, b) =>
        {
            int c = a.PlayerId.CompareTo(b.PlayerId);
            if (c != 0)
                return c;
            c = a.Sequence.CompareTo(b.Sequence);
            if (c != 0)
                return c;
            return string.CompareOrdinal(a.Type, b.Type);
        });

        return cmds;
    }

    void PrunePastTicks(long keepFromTick)
    {
        if (Commands.Count == 0)
            return;

        var toRemove = new List<long>();
        foreach (var pair in Commands)
        {
            if (pair.Key < keepFromTick)
                toRemove.Add(pair.Key);
        }

        foreach (var t in toRemove)
            Commands.Remove(t);

        toRemove.Clear();
        foreach (var pair in localStateHashByTick)
        {
            if (pair.Key < keepFromTick)
                toRemove.Add(pair.Key);
        }
        foreach (var t in toRemove)
            localStateHashByTick.Remove(t);

        toRemove.Clear();
        foreach (var pair in remoteStateHashByTick)
        {
            if (pair.Key < keepFromTick)
                toRemove.Add(pair.Key);
        }
        foreach (var t in toRemove)
            remoteStateHashByTick.Remove(t);
    }
    
}