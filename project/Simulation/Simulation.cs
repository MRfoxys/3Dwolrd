using System.Collections.Generic;
using Godot;
using System;

public partial class Simulation :GodotObject
{
    const float TICK_DURATION_SECONDS = 0.05f;
    /// <summary>Nombre de ticks simu (~20 ticks/s) pour couper un arbre une fois le colon sur place.</summary>
    public const int CutTreeWorkTicks = 42;
    public const int MineStoneWorkTicks = 48;
    public int Tick;
    public World World;

    public PlayerVision Vision = new PlayerVision();

    /// <summary>Rayon de vision en tuiles (sphère monde, pas au pas des chunks).</summary>
    public int VisionRadiusTiles = 12;

    Pathfinder pathfinder;
    readonly PathRequestService pathRequestService = new PathRequestService();
    public JobBoard jobBoard;
    readonly List<IWorkGiver> workGivers = new() { new WorkGiverCutTree() };

    public bool EnableDebugLogs = false;
    public PathMetrics LastPathMetrics => pathRequestService.LastMetrics;

    public Queue<PlayerCommand> CommandQueue = new();

    public IReadOnlyList<Colonist> Colonists => World.CurrentMap.Colonists;

    public event System.Action<int, Vector3I> OnJobStarted;
    public event System.Action<int, Vector3I> OnJobCompleted;

    public void Init()
    {
        Vision.ResetAll();
        var map = World.CurrentMap;
        pathfinder = new Pathfinder(
            (pos) => map.GetTile(pos),
            (pos) => IsOccupied(pos) // 🔥 injection propre
        );
        pathRequestService.Bind(pathfinder);
        jobBoard = new JobBoard();
        foreach (var giver in workGivers)
            giver.Bootstrap(map, jobBoard);
    }

    public void Update()
    {
        Tick++;

        ProcessCommandQueue();


        Vision.ClearVisible();

        // déplacement des colons
        foreach (var colon in World.CurrentMap.Colonists)
        {
             if (colon.OwnerId != 0) // joueur local
                continue;

            ApplyColonistGravity(colon);
            UpdateVision(colon);
            UpdateColon(colon);
        }

    }

    void ProcessCommandQueue()
    {
        var latestMoveByEntity = new Dictionary<int, PlayerCommand>();
        var immediateCommands = new List<PlayerCommand>();

        while (CommandQueue.Count > 0)
        {
            var cmd = CommandQueue.Dequeue();
            if (cmd.Type == "MOVE")
            {
                latestMoveByEntity[cmd.EntityId] = cmd;
                continue;
            }

            immediateCommands.Add(cmd);
        }

        foreach (var cmd in immediateCommands)
            ApplyCommand(cmd);

        foreach (var cmd in latestMoveByEntity.Values)
            ApplyCommand(cmd);
    }

    public int GetColonistIndex(Colonist colon)
    {
        return World.CurrentMap.Colonists.IndexOf(colon);
    }

    void ApplyCommand(PlayerCommand cmd)
    {
        if (cmd.Type == "MOVE")
        {
            HashSet<Vector3I> reserved = new();
            var colon = World.CurrentMap.Colonists[cmd.EntityId];
            colon.HasPathFailed = false;

            var start = colon.Position;
            var rawTarget = new Vector3I(cmd.X, cmd.Y, cmd.Z);

            //trouve une case libre
            var end = FindNearestFreeWithReservation(rawTarget, reserved);

            if (end == start)
            {
                colon.Path = null;
                colon.MoveProgress = 0f;
                return;
            }

            if (!pathfinder.IsWalkable(end))
            {
                LogDebug("TARGET NON WALKABLE");
                return;
            }

            reserved.Add(end);
            colon.Target = end;
            var path = pathRequestService.GetOrCreatePath(start, end);

            if (path == null)
            {
                colon.HasPathFailed = true;
                colon.Path = null;
                colon.MoveProgress = 0f;
                return;
            }
            if ( path.Count <= 1)
            {
                colon.HasPathFailed = true;
                colon.Path = null;
                colon.MoveProgress = 0f;
                return;
            }

            path = pathfinder.SmoothPath(new List<Vector3I>(path));

            if (path == null || path.Count <= 1)
            {
                colon.HasPathFailed = true;
                colon.Path = null;
                colon.MoveProgress = 0f;
                return;
            }

            // OK
            path.RemoveAt(0);

            colon.Path = path;
            colon.HasPathFailed = false;
            colon.Target = end;
            colon.MoveProgress = 0f;
            LogDebug($"PATH OK: {path.Count} (from {start} to {end})");
        }
    }

    /// <summary>Un pas vers le bas par tick si plus de support solide sous les pieds.</summary>
    void ApplyColonistGravity(Colonist colon)
    {
        var map = World.CurrentMap;
        var p = colon.Position;
        if (p.Y <= 0)
            return;

        var below = p + new Vector3I(0, -1, 0);
        var tb = map.GetTile(below);
        if (tb != null && tb.Solid)
            return;

        colon.Position = below;
        colon.Path = null;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
        RepathColonistToActiveJobWork(colon);
    }

    /// <summary>Cache de chemins + chemins en mémoire invalidés quand un job change la marchabilité (mine, arbre, etc.).</summary>
    void NotifyMapWalkabilityChanged()
    {
        pathRequestService.InvalidateAll();
        foreach (var c in World.CurrentMap.Colonists)
        {
            if (c.OwnerId != 0)
                continue;
            if (c.ActiveJob != null)
                RepathColonistToActiveJobWork(c);
            else if (c.Path != null && c.Path.Count > 0)
                RepathColonistToMoveTarget(c);
        }
    }

    void RepathColonistToMoveTarget(Colonist colon)
    {
        if (colon.Path == null || colon.Path.Count == 0)
            return;

        if (!pathfinder.IsWalkable(colon.Target))
        {
            colon.Path = null;
            colon.HasPathFailed = true;
            colon.MoveProgress = 0f;
            return;
        }

        var path = pathRequestService.GetOrCreatePath(colon.Position, colon.Target);
        if (path == null || path.Count <= 1)
        {
            colon.Path = null;
            colon.HasPathFailed = true;
            colon.MoveProgress = 0f;
            return;
        }

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
        {
            colon.Path = null;
            colon.HasPathFailed = true;
            colon.MoveProgress = 0f;
            return;
        }

        path.RemoveAt(0);
        colon.Path = path;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
    }

    /// <summary>Après une chute, retente le chemin vers la case de travail ou annule le job.</summary>
    void RepathColonistToActiveJobWork(Colonist colon)
    {
        var job = colon.ActiveJob;
        if (job == null)
            return;

        if (colon.Position == job.WorkPosition)
            return;

        if (!pathfinder.IsWalkable(job.WorkPosition))
        {
            jobBoard.Fail(job);
            colon.ActiveJob = null;
            colon.WorkTicksRemaining = 0;
            return;
        }

        var path = pathRequestService.GetOrCreatePath(colon.Position, job.WorkPosition);
        if (path == null || path.Count <= 1)
        {
            jobBoard.Fail(job);
            colon.ActiveJob = null;
            colon.WorkTicksRemaining = 0;
            return;
        }

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
        {
            jobBoard.Fail(job);
            colon.ActiveJob = null;
            colon.WorkTicksRemaining = 0;
            return;
        }

        path.RemoveAt(0);
        colon.Path = path;
        colon.Target = job.WorkPosition;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
    }

    void UpdateColon(Colonist colon)
    {
        if (colon.HasPathFailed)
            return;

        if ((colon.Path == null || colon.Path.Count == 0) && colon.ActiveJob == null)
            TryAssignJob(colon);

        if (colon.Path == null || colon.Path.Count == 0)
        {
            colon.MoveProgress = 0f;
            TryExecuteWork(colon);
            return;
        }

        var next = colon.Path[0];

        // blocage
        if (!pathfinder.IsWalkable(next) || IsOccupied(next, colon))
        {
            var start = colon.Position;
            var newPath = pathRequestService.GetOrCreatePath(start, colon.Target);
            if (newPath != null && newPath.Count > 1)
            {
                newPath = pathfinder.SmoothPath(new List<Vector3I>(newPath));
                if (newPath != null && newPath.Count > 1)
                {
                    newPath.RemoveAt(0);
                    colon.Path = newPath;
                    colon.MoveProgress = 0f;
                    return;
                }
            }

            colon.Path = null;
            colon.HasPathFailed = true;
            colon.MoveProgress = 0f;
            return;
        }

        // Constant tile movement speed (tiles/sec) independent from render interpolation.
        colon.MoveProgress += colon.MoveSpeed * TICK_DURATION_SECONDS;

        if (colon.MoveProgress >= 1f)
        {
            colon.Position = next;

            colon.Path.RemoveAt(0);
            colon.MoveProgress = 0f;
        }
    }

    void TryAssignJob(Colonist colon)
    {
        int colonistId = GetColonistIndex(colon);
        if (colonistId < 0 || !jobBoard.TryReserveBest(colonistId, colon.Position, out var job))
            return;

        var workPos = job.Type switch
        {
            JobType.CutTree => FindNearestWalkableAdjacentToBlock(job.Target, forbidSameXzColumn: true),
            JobType.MineStone => FindNearestWalkableAdjacentToBlock(job.Target, forbidSameXzColumn: false),
            _ => FindNearestFree(job.Target)
        };

        if (!pathfinder.IsWalkable(workPos))
        {
            GD.PrintErr($"[Jobs] Aucune case marchable près de la cible {job.Target} (essayé workPos={workPos}) — job annulé.");
            jobBoard.Fail(job);
            return;
        }

        var start = colon.Position;
        var path = pathRequestService.GetOrCreatePath(start, workPos);
        if (path == null || path.Count <= 1)
        {
            GD.PrintErr($"[Jobs] Pas de chemin colon → {workPos} depuis {start} — job annulé.");
            jobBoard.Fail(job);
            return;
        }

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
        {
            jobBoard.Fail(job);
            return;
        }

        path.RemoveAt(0);
        job.WorkPosition = workPos;
        colon.ActiveJob = job;
        colon.Path = path;
        colon.Target = workPos;
        colon.MoveProgress = 0f;
        colon.WorkTicksRemaining = job.Type switch
        {
            JobType.CutTree => CutTreeWorkTicks,
            JobType.MineStone => MineStoneWorkTicks,
            _ => 10
        };
    }


    void TryExecuteWork(Colonist colon)
    {
        var job = colon.ActiveJob;
        if (job == null)
            return;

        if (colon.Position != job.WorkPosition)
            return;

        colon.WorkTicksRemaining--;

        if (job.Type == JobType.CutTree && colon.WorkTicksRemaining == CutTreeWorkTicks - 1)
        {
            GD.Print($"Colon {GetColonistIndex(colon)} a commencé à couper l'arbre à {job.Target}.");
            OnJobStarted?.Invoke(GetColonistIndex(colon), job.Target);
        }
        if (job.Type == JobType.MineStone && colon.WorkTicksRemaining == MineStoneWorkTicks - 1)
        {
            GD.Print($"Colon {GetColonistIndex(colon)} a commencé à miner à {job.Target}.");
            OnJobStarted?.Invoke(GetColonistIndex(colon), job.Target);
        }

        if (colon.WorkTicksRemaining > 0)
            return;

        if (job.Type == JobType.CutTree)
        {
            GD.Print($"Colon {GetColonistIndex(colon)} a terminé de couper l'arbre à {job.Target}. L'arbre est supprimé.");
            World.CurrentMap.SetTile(job.Target, new Tile { Solid = true, Type = "dirt" });
            OnJobCompleted?.Invoke(GetColonistIndex(colon), job.Target);
        }
        else if (job.Type == JobType.MineStone)
        {
            GD.Print($"Colon {GetColonistIndex(colon)} a terminé de miner {job.Target}.");
            World.CurrentMap.SetTile(job.Target, new Tile { Solid = false, Type = "air" });
            OnJobCompleted?.Invoke(GetColonistIndex(colon), job.Target);
        }

        NotifyMapWalkabilityChanged();

        jobBoard.Complete(job);
        colon.ActiveJob = null;
        colon.WorkTicksRemaining = 0;
    }


    /// <summary>Couche extérieure du cube de rayon r (Chebyshev) autour de l’origine.</summary>
    static bool OnChebyshevShell(int dx, int dy, int dz, int r)
    {
        return Mathf.Max(Mathf.Abs(dx), Mathf.Max(Mathf.Abs(dy), Mathf.Abs(dz))) == r;
    }

    Vector3I FindNearestFree(Vector3I target)
    {
        if (IsWalkableAndFree(target))
            return target;

        for (int r = 1; r < 10; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!OnChebyshevShell(dx, dy, dz, r))
                    continue;
                var pos = new Vector3I(target.X + dx, target.Y + dy, target.Z + dz);
                if (IsWalkableAndFree(pos))
                    return pos;
            }
        }

        return target;
    }

    /// <summary>Case où le colon se tient : touche latérale au bloc cible (pas pile au-dessus / même colonne XZ si forbidSameXzColumn).</summary>
    Vector3I FindNearestWalkableAdjacentToBlock(Vector3I blockCell, bool forbidSameXzColumn)
    {
        Vector3I[] faceNeighbors =
        {
            new(1, 0, 0), new(-1, 0, 0),
            new(0, 1, 0), new(0, -1, 0),
            new(0, 0, 1), new(0, 0, -1),
        };

        foreach (var d in faceNeighbors)
        {
            var n = blockCell + d;
            if (forbidSameXzColumn && n.X == blockCell.X && n.Z == blockCell.Z)
                continue;
            if (IsWalkableAndFree(n))
                return n;
        }

        for (int shell = 2; shell < 14; shell++)
        {
            for (int dx = -shell; dx <= shell; dx++)
            for (int dy = -shell; dy <= shell; dy++)
            for (int dz = -shell; dz <= shell; dz++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dy) + Mathf.Abs(dz) != shell)
                    continue;
                var n = blockCell + new Vector3I(dx, dy, dz);
                if (forbidSameXzColumn && n.X == blockCell.X && n.Z == blockCell.Z)
                    continue;
                if (IsWalkableAndFree(n))
                    return n;
            }
        }

        return blockCell;
    }

    bool IsWalkableAndFree(Vector3I pos)
    {
        if (!pathfinder.IsWalkable(pos))
            return false;

        return !IsOccupied(pos);
    }

    bool IsOccupied(Vector3I pos, Colonist ignore = null)
    {
        foreach (var c in World.CurrentMap.Colonists)
        {
            if (c == ignore)
                continue;

            if (c.Position == pos)
                return true;
        }
    return false;
    }


    public Vector3I FindNearestFreeWithReservation(Vector3I target, HashSet<Vector3I> reserved)
    {
        if (IsWalkableAndFree(target) && !reserved.Contains(target))
            return target;

        for (int r = 1; r < 10; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            for (int dz = -r; dz <= r; dz++)
            {
                if (!OnChebyshevShell(dx, dy, dz, r))
                    continue;
                var pos = new Vector3I(target.X + dx, target.Y + dy, target.Z + dz);
                if (IsWalkableAndFree(pos) && !reserved.Contains(pos))
                    return pos;
            }
        }

        return target;
    }


    void UpdateVision(Colonist colon)
    {
        int R = VisionRadiusTiles;
        if (R < 1)
            R = 1;
        int r2 = R * R;

        for (int x = -R; x <= R; x++)
        for (int y = -R; y <= R; y++)
        for (int z = -R; z <= R; z++)
        {
            if (x * x + y * y + z * z > r2)
                continue;

            var target = colon.Position + new Vector3I(x, y, z);

            if (HasLineOfSight(colon.Position, target))
                Vision.AddVisible(target);
        }

        // Sol de surface (arbres / herbe) : disque horizontal en tuiles, pas lié aux chunks.
        int fy = Map.WorldFloorY;
        for (int x = -R; x <= R; x++)
        for (int z = -R; z <= R; z++)
        {
            if (x * x + z * z > r2)
                continue;
            Vision.AddDiscovered(new Vector3I(colon.Position.X + x, fy, colon.Position.Z + z));
        }
    }

    bool HasLineOfSight(Vector3I from, Vector3I to)
    {
        var dir = to - from;

        int ax = Mathf.Abs(dir.X);
        int ay = Mathf.Abs(dir.Y);
        int az = Mathf.Abs(dir.Z);

        int steps = Mathf.Max(Mathf.Max(ax, ay), az);

        if (steps == 0)
            return true;

        float dx = dir.X / (float)steps;
        float dy = dir.Y / (float)steps;
        float dz = dir.Z / (float)steps;

        float x = from.X;
        float y = from.Y;
        float z = from.Z;

        for (int i = 0; i < steps; i++)
        {
            x += dx;
            y += dy;
            z += dz;

            var pos = new Vector3I(
                Mathf.RoundToInt(x),
                Mathf.RoundToInt(y),
                Mathf.RoundToInt(z)
            );

            // 🔥 ignore la case finale
            if (pos == to)
                return true;

            var tile = World.CurrentMap.GetTile(pos);

            if (tile != null && tile.Solid)
                return false; // 🚧 mur bloque vision
        }

        return true;
    }

    void LogDebug(string message)
    {
        if (!EnableDebugLogs)
            return;

        GD.Print("[Simulation] ", message);
    }
}