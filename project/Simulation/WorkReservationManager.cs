using System.Collections.Generic;
using Godot;

/// <summary>
/// Réservations explicites (job + case de travail) pour éviter les conflits
/// et rapprocher le comportement d'un colony sim type RimWorld.
/// </summary>
public sealed class WorkReservationManager
{
    readonly Dictionary<int, int> _jobToColonist = new();
    readonly Dictionary<Vector3I, int> _workCellToColonist = new();
    readonly Dictionary<int, Vector3I> _colonistToWorkCell = new();
    readonly Dictionary<int, int> _colonistToJobId = new();

    public void Reset()
    {
        _jobToColonist.Clear();
        _workCellToColonist.Clear();
        _colonistToWorkCell.Clear();
        _colonistToJobId.Clear();
    }

    public bool IsWorkCellReservedByOther(Vector3I workCell, int colonistId)
    {
        return _workCellToColonist.TryGetValue(workCell, out var owner) && owner != colonistId;
    }

    public bool IsWorkCellReserved(Vector3I workCell)
    {
        return _workCellToColonist.ContainsKey(workCell);
    }

    public bool TryReserve(int colonistId, int jobId, Vector3I workCell)
    {
        if (_jobToColonist.TryGetValue(jobId, out var existingJobOwner) && existingJobOwner != colonistId)
            return false;
        if (_workCellToColonist.TryGetValue(workCell, out var existingCellOwner) && existingCellOwner != colonistId)
            return false;

        ReleaseByColonist(colonistId);

        _jobToColonist[jobId] = colonistId;
        _workCellToColonist[workCell] = colonistId;
        _colonistToWorkCell[colonistId] = workCell;
        _colonistToJobId[colonistId] = jobId;
        return true;
    }

    public void ReleaseByColonist(int colonistId)
    {
        if (_colonistToJobId.TryGetValue(colonistId, out var jobId))
        {
            _colonistToJobId.Remove(colonistId);
            _jobToColonist.Remove(jobId);
        }

        if (_colonistToWorkCell.TryGetValue(colonistId, out var workCell))
        {
            _colonistToWorkCell.Remove(colonistId);
            _workCellToColonist.Remove(workCell);
        }
    }

    public void ReleaseJob(int jobId)
    {
        if (!_jobToColonist.TryGetValue(jobId, out var colonistId))
            return;
        ReleaseByColonist(colonistId);
    }
}
