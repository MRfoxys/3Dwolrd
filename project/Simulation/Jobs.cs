using System.Collections.Generic;
using Godot;

public enum JobType
{
    CutTree
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

            for (int x = 0; x < Map.CHUNK_SIZE; x++)
            for (int y = 0; y < Map.CHUNK_SIZE; y++)
            for (int z = 0; z < Map.CHUNK_SIZE; z++)
            {
                var tile = chunk.Tiles[x, y, z];
                if (tile == null || tile.Type != "tree")
                    continue;

                var worldPos = new Vector3I(
                    chunkPos.X * Map.CHUNK_SIZE + x,
                    chunkPos.Y * Map.CHUNK_SIZE + y,
                    chunkPos.Z * Map.CHUNK_SIZE + z
                );

                board.AddJob(new SimJob
                {
                    Type = JobType.CutTree,
                    Priority = JobPriority.Normal,
                    Target = worldPos
                });
            }
        }
    }
}

public class JobBoard
{
    readonly Dictionary<int, SimJob> jobs = new();
    int nextId = 1;

    public void AddJob(SimJob job)
    {
        if (job.Id == 0)
            job.Id = nextId++;
        jobs[job.Id] = job;
    }

    public void Complete(SimJob job)
    {
        if (job == null)
            return;
        job.Status = JobStatus.Completed;
        jobs.Remove(job.Id);
    }

    public void Fail(SimJob job)
    {
        if (job == null)
            return;
        job.Status = JobStatus.Failed;
        jobs.Remove(job.Id);
    }

    public bool TryReserveBest(int colonistId, Vector3I colonPos, out SimJob job)
    {
        job = null;
        int bestScore = int.MinValue;

        foreach (var pair in jobs)
        {
            var j = pair.Value;
            if (j.Status != JobStatus.Available)
                continue;

            int distance = Mathf.Abs(colonPos.X - j.WorkPosition.X) +
                           Mathf.Abs(colonPos.Y - j.WorkPosition.Y) +
                           Mathf.Abs(colonPos.Z - j.WorkPosition.Z);
            int score = ((int)j.Priority * 1000) - distance;
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
}
