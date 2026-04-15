using System;
using System.Collections.Generic;
using Godot;

public enum JobType
{
    CutTree,
    MineStone,
    BuildBlock,
    HaulResource,
}

public enum JobPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Forced = 3
}

public enum JobStatus
{
    Available,
    /// <summary>En file mais aucun chemin depuis un colon pour l’instant — réveillé quand le terrain change.</summary>
    WaitingAccess,
    Reserved,
    Completed,
    Failed
}

public class SimJob
{
    public int Id;
    public JobType Type;
    public JobPriority Priority;
    public JobStatus Status = JobStatus.Available;
    public Vector3I Target;
    public Vector3I WorkPosition;
    public int? ReservedByColonistId;
    /// <summary>Tick minimal avant nouvelle tentative d’assignation si le job était bloqué.</summary>
    public int RetryAfterTick;
    /// <summary>Ordre d’ajout à la file (plus petit = plus tôt) pour départager mine/construction par couche.</summary>
    public int EnqueueOrder;
    /// <summary>Type final pour les jobs de construction voxel.</summary>
    public string BuildTileType = "stone";
    /// <summary>0 = hors chantier ; sinon id de BuildSite.</summary>
    public int BuildSiteId;
    public string ResourceType = string.Empty;
    public int ResourceAmount;
    public Vector3I DropoffTarget;
}

public interface IWorkGiver
{
    void Bootstrap(Map map, JobBoard board);
}

public class WorkGiverCutTree : IWorkGiver
{
    public void Bootstrap(Map map, JobBoard board)
    {
        foreach (var pair in map.Chunks)
        {
            var chunkPos = pair.Key;
            var chunk = pair.Value;

            // for (int x = 0; x < Map.CHUNK_SIZE; x++)
            // for (int y = 0; y < Map.CHUNK_SIZE; y++)
            // for (int z = 0; z < Map.CHUNK_SIZE; z++)
            // {
            //     var tile = chunk.Tiles[x, y, z];
            //     if (tile == null || tile.Type != "tree")
            //         continue;

            //     var worldPos = new Vector3I(
            //         chunkPos.X * Map.CHUNK_SIZE + x,
            //         chunkPos.Y * Map.CHUNK_SIZE + y,
            //         chunkPos.Z * Map.CHUNK_SIZE + z
            //     );

            //     board.AddJob(new SimJob
            //     {
            //         Type = JobType.CutTree,
            //         Priority = JobPriority.Normal,
            //         Target = worldPos
            //     });
            // }
        }
    }
}

public class JobBoard
{
    readonly Dictionary<int, SimJob> jobs = new();
    readonly Dictionary<JobType, HashSet<Vector3I>> activeTargetsByType = new()
    {
        { JobType.CutTree, new HashSet<Vector3I>() },
        { JobType.MineStone, new HashSet<Vector3I>() },
        { JobType.BuildBlock, new HashSet<Vector3I>() },
        { JobType.HaulResource, new HashSet<Vector3I>() },
    };
    int nextId = 1;
    int nextEnqueueOrder;

    public void AddJob(SimJob job)
    {
        if (jobs.TryGetValue(job.Id, out var existing))
            RemoveFromTargetIndex(existing);
        if (job.Id == 0)
            job.Id = nextId++;
        job.EnqueueOrder = nextEnqueueOrder++;
        jobs[job.Id] = job;
        AddToTargetIndex(job);
    }

    public void Complete(SimJob job)
    {
        if (job == null)
            return;
        RemoveFromTargetIndex(job);
        job.Status = JobStatus.Completed;
        jobs.Remove(job.Id);
    }

    public void Fail(SimJob job)
    {
        if (job == null)
            return;
        RemoveFromTargetIndex(job);
        job.Status = JobStatus.Failed;
        jobs.Remove(job.Id);
    }

    public bool HasActiveJobOnTarget(Vector3I worldTile, JobType type)
    {
        return activeTargetsByType.TryGetValue(type, out var set) && set.Contains(worldTile);
    }

    public void CopyActiveTargets(JobType type, List<Vector3I> buffer)
    {
        buffer.Clear();
        if (!activeTargetsByType.TryGetValue(type, out var set) || set.Count == 0)
            return;
        foreach (var p in set)
            buffer.Add(p);
    }

    /// <summary>Jobs encore dans la file (disponible ou réservé à un colon).</summary>
    public void CopyActiveJobs(List<SimJob> buffer)
    {
        buffer.Clear();
        foreach (var j in jobs.Values)
        {
            if (j.Status != JobStatus.Available
                && j.Status != JobStatus.WaitingAccess
                && j.Status != JobStatus.Reserved)
                continue;
            buffer.Add(j);
        }

        buffer.Sort(static (a, b) => a.EnqueueOrder.CompareTo(b.EnqueueOrder));
    }

    public int ActiveJobCount
    {
        get
        {
            int n = 0;
            foreach (var j in jobs.Values)
            {
                if (j.Status == JobStatus.Available
                    || j.Status == JobStatus.WaitingAccess
                    || j.Status == JobStatus.Reserved)
                    n++;
            }
            return n;
        }
    }

    public bool TryReserveBest(int colonistId, Vector3I colonPos, int currentTick, HashSet<int> excludedJobIds, Func<SimJob, bool> canOfferJob, out SimJob job)
    {
        job = null;
        int bestScore = int.MinValue;

        foreach (var pair in jobs)
        {
            var j = pair.Value;
            if (excludedJobIds != null && excludedJobIds.Contains(j.Id))
                continue;
            if (j.Status == JobStatus.WaitingAccess && currentTick >= j.RetryAfterTick)
                j.Status = JobStatus.Available;
            if (j.Status != JobStatus.Available)
                continue;
            if (canOfferJob != null && !canOfferJob(j))
                continue;

            // WorkPosition n'est rempli qu'à l'assignation ; le tri se fait sur la cible du job
            var anchor = j.Target;
            int distance = Mathf.Abs(colonPos.X - anchor.X) +
                            Mathf.Abs(colonPos.Y - anchor.Y) +
                            Mathf.Abs(colonPos.Z - anchor.Z);
            int score = j.Type switch
            {
                // Sur les chantiers voxel, l'ordre d'enqueue (couches/lignes) prime sur la distance.
                JobType.BuildBlock or JobType.MineStone => (int)j.Priority * 1_000_000 - j.EnqueueOrder * 10 - distance,
                _ => (int)j.Priority * 1_000_000 - distance * 1000 - j.EnqueueOrder
            };
            if (score <= bestScore)
                continue;

            bestScore = score;
            job = j;
        }

        if (job == null)
            return false;

        job.Status = JobStatus.Reserved;
        job.ReservedByColonistId = colonistId;
        return true;
    }

    public void WakeAllWaitingAccessJobs()
    {
        foreach (var j in jobs.Values)
        {
            if (j.Status == JobStatus.WaitingAccess)
                j.Status = JobStatus.Available;
        }
    }

    public void WakeWaitingAccessJobsDue(int currentTick)
    {
        foreach (var j in jobs.Values)
        {
            if (j.Status == JobStatus.WaitingAccess && currentTick >= j.RetryAfterTick)
                j.Status = JobStatus.Available;
        }
    }

    /// <summary>Échec d’assignation ou repath (pas de case travail / pas de chemin) : ne pas supprimer le job.</summary>
    public void ReleaseJobWaitingForAccess(SimJob job, int currentTick, int retryDelayTicks = 12)
    {
        if (job == null)
            return;
        job.Status = JobStatus.WaitingAccess;
        int delay = Mathf.Max(1, retryDelayTicks);
        job.RetryAfterTick = currentTick + delay;
        job.WorkPosition = default;
        job.ReservedByColonistId = null;
    }

    /// <summary>Libère immédiatement le job (contention temporaire : autre colon / case occupée).</summary>
    public void ReleaseJobAvailable(SimJob job)
    {
        if (job == null)
            return;
        job.Status = JobStatus.Available;
        job.WorkPosition = default;
        job.ReservedByColonistId = null;
    }

    static bool IsIndexedStatus(JobStatus status)
    {
        return status == JobStatus.Available
            || status == JobStatus.WaitingAccess
            || status == JobStatus.Reserved;
    }

    void AddToTargetIndex(SimJob job)
    {
        if (job == null || !IsIndexedStatus(job.Status))
            return;
        if (!activeTargetsByType.TryGetValue(job.Type, out var set))
            return;
        set.Add(job.Target);
    }

    void RemoveFromTargetIndex(SimJob job)
    {
        if (job == null)
            return;
        if (!activeTargetsByType.TryGetValue(job.Type, out var set))
            return;
        set.Remove(job.Target);
    }
}

