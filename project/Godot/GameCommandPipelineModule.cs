using System.Collections.Generic;
using Godot;

/// <summary>
/// Small orchestration module that converts player intentions to lockstep commands.
/// </summary>
public sealed class GameCommandPipelineModule
{
    readonly Simulation sim;
    readonly LockstepManager lockstep;
    readonly int localPlayerId;

    public GameCommandPipelineModule(Simulation sim, LockstepManager lockstep, int localPlayerId)
    {
        this.sim = sim;
        this.lockstep = lockstep;
        this.localPlayerId = localPlayerId;
    }

    public void QueueBuild(IEnumerable<Vector3I> cells, string tileType, JobPriority priority)
    {
        SubmitMultiCellCommand(PlayerCommandType.DesignateBuild, cells, tileType, priority);
    }

    public void QueueMine(IEnumerable<Vector3I> cells, JobPriority priority)
    {
        SubmitMultiCellCommand(PlayerCommandType.DesignateMine, cells, "stone", priority);
    }

    public void QueueCutTree(Vector3I cell, JobPriority priority)
    {
        lockstep.SubmitLocalCommand(new PlayerCommand
        {
            Tick = sim.Tick + 1,
            Type = PlayerCommandType.DesignateCutTree,
            PlayerId = localPlayerId,
            X = cell.X,
            Y = cell.Y,
            Z = cell.Z,
            Priority = (int)priority
        });
    }

    void SubmitMultiCellCommand(string commandType, IEnumerable<Vector3I> cells, string tileType, JobPriority priority)
    {
        var payload = new List<PlayerCommandCell>();
        if (cells != null)
        {
            foreach (var c in cells)
                payload.Add(new PlayerCommandCell(c));
        }

        if (payload.Count == 0)
            return;

        lockstep.SubmitLocalCommand(new PlayerCommand
        {
            Tick = sim.Tick + 1,
            Type = commandType,
            PlayerId = localPlayerId,
            Priority = (int)priority,
            BuildTileType = tileType,
            Cells = payload
        });
    }
}
