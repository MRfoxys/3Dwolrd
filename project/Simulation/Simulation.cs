using System.Collections.Generic;
using Godot;
using System;
using System.Text;
using System.Text.Json;

public partial class Simulation :GodotObject
{
    const float TICK_DURATION_SECONDS = 0.05f;
    const int ManualMoveRecoveryRetryTicks = 8;
    const int ManualMoveRecoveryMaxAttempts = 5;
    const int ScaffoldRetryDelayTicks = 16;
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
    readonly DesignationBoard designationBoard = new();
    readonly WorkReservationManager reservationManager = new();
    readonly List<IWorkGiver> workGivers = new() { new WorkGiverCutTree() };

    public bool EnableDebugLogs = false;
    public PathMetrics LastPathMetrics => pathRequestService.LastMetrics;

    public Queue<PlayerCommand> CommandQueue = new();

    public IReadOnlyList<Colonist> Colonists => World.CurrentMap.Colonists;

    public event System.Action<int, Vector3I> OnJobStarted;
    public event System.Action<int, Vector3I> OnJobCompleted;
    readonly Dictionary<Vector3I, VoxelWorkProgress> voxelWork = new();
    public IReadOnlyDictionary<Vector3I, VoxelWorkProgress> ActiveVoxelWork => voxelWork;
    readonly VirtualScaffoldSystem virtualScaffolds = new();
    readonly Dictionary<Vector3I, int> scaffoldRetryAfterTickByTarget = new();
    readonly List<SimJob> _jobScratch = new();
    readonly List<WorkDesignation> _designationScratch = new();
    readonly List<Vector3I> _minePlanSortBuffer = new();
    readonly HashSet<Vector3I> _designationPendingSetScratch = new();
    readonly Queue<Vector3I> _designationBfsQueue = new();
    readonly Dictionary<int, BuildSite> _buildSites = new();
    int _nextBuildSiteId = 1;
    readonly Dictionary<Vector3I, LooseResourceStack> _looseResources = new();
    readonly Dictionary<string, int> _stockpileInventory = new(StringComparer.Ordinal);
    readonly HashSet<Vector3I> _stockpileCells = new();
    readonly List<Vector3I> _haulScratch = new();
    bool pendingWalkabilityRefresh;
    Vector3I? walkabilityChangeFocus;
    bool walkabilityChangeFullRepath;
    public IReadOnlyCollection<Vector3I> VirtualScaffoldCells => virtualScaffolds.Cells;
    public int VirtualScaffoldVersion => virtualScaffolds.Version;
    public IReadOnlyDictionary<Vector3I, LooseResourceStack> LooseResources => _looseResources;
    public IReadOnlyDictionary<string, int> StockpileInventory => _stockpileInventory;
    public IReadOnlyCollection<Vector3I> StockpileCells => _stockpileCells;

    public void Init()
    {
        Vision.ResetAll();
        var map = World.CurrentMap;
        virtualScaffolds.Reset();
        designationBoard.Clear();
        reservationManager.Reset();
        scaffoldRetryAfterTickByTarget.Clear();
        _looseResources.Clear();
        _stockpileInventory.Clear();
        _stockpileCells.Clear();
        jobBoard = new JobBoard();
        pathfinder = new Pathfinder(
            (pos) => map.GetTile(pos),
            (pos) => IsPositionBlockedByColonist(pos),
            (pos) => virtualScaffolds.HasAt(pos)
        );
        pathRequestService.Bind(pathfinder);
        foreach (var giver in workGivers)
            giver.Bootstrap(map, jobBoard);
        InitDefaultStockpileCells();
    }

    public void DesignateBuildCells(IEnumerable<Vector3I> cells, string buildTileType, JobPriority priority = JobPriority.Normal)
    {
        if (cells == null || World?.CurrentMap == null)
            return;

        var map = World.CurrentMap;
        foreach (var c in cells)
        {
            var t = map.GetTile(c);
            if (t == null)
                continue;
            if (t.Solid && !jobBoard.HasActiveJobOnTarget(c, JobType.BuildBlock))
                continue;
            designationBoard.AddOrUpdate(new WorkDesignation
            {
                Type = DesignationType.Build,
                Target = c,
                Priority = priority,
                BuildTileType = string.IsNullOrEmpty(buildTileType) ? "stone" : buildTileType,
                Planned = false
            });
        }
    }

    public void DesignateMineCells(IEnumerable<Vector3I> cells, JobPriority priority = JobPriority.Normal)
    {
        if (cells == null || World?.CurrentMap == null)
            return;

        var map = World.CurrentMap;
        foreach (var c in cells)
        {
            if (!TerrainMineRules.IsMineableBlock(map.GetTile(c)))
                continue;
            designationBoard.AddOrUpdate(new WorkDesignation
            {
                Type = DesignationType.Mine,
                Target = c,
                Priority = priority,
                Planned = false
            });
        }
    }

    public void DesignateCutTree(Vector3I cell, JobPriority priority = JobPriority.Normal)
    {
        if (World?.CurrentMap == null)
            return;
        var tile = World.CurrentMap.GetTile(cell);
        if (tile == null || tile.Type != "tree")
            return;
        designationBoard.AddOrUpdate(new WorkDesignation
        {
            Type = DesignationType.CutTree,
            Target = cell,
            Priority = priority,
            Planned = false
        });
    }

    /// <summary>Crée un chantier (≥2 blocs) : ralliement, déblocage par support, jobs liés.</summary>
    public void RegisterBuildSiteAndEnqueueJobs(List<Vector3I> sortedCells, string buildTileType)
    {
        var map = World.CurrentMap;
        if (sortedCells == null || sortedCells.Count < 2)
            return;

        var queued = new List<Vector3I>();
        foreach (var cell in sortedCells)
        {
            if (jobBoard.HasActiveJobOnTarget(cell, JobType.BuildBlock))
                continue;
            var t = map.GetTile(cell);
            if (t != null && t.Solid)
                continue;
            queued.Add(cell);
        }

        if (queued.Count < 2)
        {
            foreach (var c in queued)
            {
                jobBoard.AddJob(new SimJob
                {
                    Type = JobType.BuildBlock,
                    Priority = JobPriority.Normal,
                    Target = c,
                    BuildTileType = string.IsNullOrEmpty(buildTileType) ? "stone" : buildTileType
                });
            }
            return;
        }

        int minY = int.MaxValue;
        var footprint = new HashSet<Vector3I>();
        foreach (var c in queued)
        {
            footprint.Add(c);
            minY = Mathf.Min(minY, c.Y);
        }

        if (!TryPickRallyForFootprint(footprint, minY, out var rally))
            rally = FindRallyFallback(footprint, minY);

        // Ordre "colonne + escalier" centré sur le rally.
        VoxelSelectionOrder.SortForBuildEnqueue(queued, rally);

        int id = _nextBuildSiteId++;
        var site = new BuildSite(id, rally, minY, queued);
        _buildSites[id] = site;

        foreach (var cell in queued)
        {
            jobBoard.AddJob(new SimJob
            {
                Type = JobType.BuildBlock,
                Priority = JobPriority.Normal,
                Target = cell,
                BuildTileType = string.IsNullOrEmpty(buildTileType) ? "stone" : buildTileType,
                BuildSiteId = id
            });
        }

        GD.Print($"[BuildSite] Chantier #{id} : {queued.Count} bloc(s), rally @ {rally}");
    }

    void InitDefaultStockpileCells()
    {
        var map = World?.CurrentMap;
        if (map == null)
            return;

        var center = new Vector3I(2, Map.ColonistWalkY, 2);
        for (int x = -2; x <= 1; x++)
        for (int z = -2; z <= 1; z++)
        {
            var c = new Vector3I(center.X + x, center.Y, center.Z + z);
            if (pathfinder.IsWalkable(c))
                _stockpileCells.Add(c);
        }
    }

    void AddLooseResource(Vector3I worldCell, string resourceType, int amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(resourceType))
            return;

        if (_looseResources.TryGetValue(worldCell, out var existing))
        {
            existing.Amount += amount;
            return;
        }

        _looseResources[worldCell] = new LooseResourceStack
        {
            Position = worldCell,
            ResourceType = resourceType,
            Amount = amount
        };
    }

    void PlanDesignationsIntoJobs()
    {
        if (jobBoard == null || World?.CurrentMap == null)
            return;

        PlanBuildDesignations();
        PlanMineDesignations();
        PlanCutTreeDesignations();
        PlanHaulJobs();
    }

    void PlanBuildDesignations()
    {
        const int maxNewJobsPerTick = 24;
        designationBoard.CopyByType(DesignationType.Build, _designationScratch, onlyUnplanned: true);
        if (_designationScratch.Count == 0)
            return;

        var map = World.CurrentMap;
        var buildByCell = new Dictionary<Vector3I, WorkDesignation>();
        _designationPendingSetScratch.Clear();
        foreach (var d in _designationScratch)
        {
            var t = map.GetTile(d.Target);
            if (t == null)
            {
                designationBoard.MarkCompleted(DesignationType.Build, d.Target);
                continue;
            }
            if (t.Solid && !jobBoard.HasActiveJobOnTarget(d.Target, JobType.BuildBlock))
            {
                designationBoard.MarkCompleted(DesignationType.Build, d.Target);
                continue;
            }
            if (jobBoard.HasActiveJobOnTarget(d.Target, JobType.BuildBlock))
            {
                designationBoard.MarkPlanned(DesignationType.Build, d.Target);
                continue;
            }

            buildByCell[d.Target] = d;
            _designationPendingSetScratch.Add(d.Target);
        }

        int plannedCount = 0;
        while (_designationPendingSetScratch.Count > 0 && plannedCount < maxNewJobsPerTick)
        {
            Vector3I seed = default;
            foreach (var c in _designationPendingSetScratch)
            {
                seed = c;
                break;
            }

            var cluster = CollectBuildCluster(seed, _designationPendingSetScratch);
            if (cluster.Count == 0)
                break;

            string tileType = buildByCell.TryGetValue(seed, out var sd) ? sd.BuildTileType : "stone";
            if (cluster.Count >= 2)
            {
                RegisterBuildSiteAndEnqueueJobs(cluster, tileType);
                foreach (var c in cluster)
                {
                    if (jobBoard.HasActiveJobOnTarget(c, JobType.BuildBlock))
                    {
                        designationBoard.MarkPlanned(DesignationType.Build, c);
                        plannedCount++;
                    }
                }
                continue;
            }

            var single = cluster[0];
            if (jobBoard.HasActiveJobOnTarget(single, JobType.BuildBlock))
            {
                designationBoard.MarkPlanned(DesignationType.Build, single);
                continue;
            }

            if (!buildByCell.TryGetValue(single, out var dsg))
                continue;

            jobBoard.AddJob(new SimJob
            {
                Type = JobType.BuildBlock,
                Priority = dsg.Priority,
                Target = single,
                BuildTileType = dsg.BuildTileType
            });
            designationBoard.MarkPlanned(DesignationType.Build, single);
            plannedCount++;
        }
    }

    void PlanMineDesignations()
    {
        const int maxNewJobsPerTick = 24;
        designationBoard.CopyByType(DesignationType.Mine, _designationScratch, onlyUnplanned: true);
        if (_designationScratch.Count == 0)
            return;

        var map = World.CurrentMap;
        _minePlanSortBuffer.Clear();
        foreach (var d in _designationScratch)
        {
            if (!TerrainMineRules.IsMineableBlock(map.GetTile(d.Target)))
            {
                designationBoard.MarkCompleted(DesignationType.Mine, d.Target);
                continue;
            }
            if (jobBoard.HasActiveJobOnTarget(d.Target, JobType.MineStone))
            {
                designationBoard.MarkPlanned(DesignationType.Mine, d.Target);
                continue;
            }
            _minePlanSortBuffer.Add(d.Target);
        }

        VoxelSelectionOrder.SortForMineEnqueue(_minePlanSortBuffer);
        int n = 0;
        foreach (var c in _minePlanSortBuffer)
        {
            if (n >= maxNewJobsPerTick)
                break;
            if (jobBoard.HasActiveJobOnTarget(c, JobType.MineStone))
            {
                designationBoard.MarkPlanned(DesignationType.Mine, c);
                continue;
            }
            jobBoard.AddJob(new SimJob
            {
                Type = JobType.MineStone,
                Priority = JobPriority.Normal,
                Target = c
            });
            designationBoard.MarkPlanned(DesignationType.Mine, c);
            n++;
        }
    }

    void PlanCutTreeDesignations()
    {
        const int maxNewJobsPerTick = 12;
        designationBoard.CopyByType(DesignationType.CutTree, _designationScratch, onlyUnplanned: true);
        if (_designationScratch.Count == 0)
            return;

        var map = World.CurrentMap;
        int n = 0;
        foreach (var d in _designationScratch)
        {
            var t = map.GetTile(d.Target);
            if (t == null || t.Type != "tree")
            {
                designationBoard.MarkCompleted(DesignationType.CutTree, d.Target);
                continue;
            }
            if (jobBoard.HasActiveJobOnTarget(d.Target, JobType.CutTree))
            {
                designationBoard.MarkPlanned(DesignationType.CutTree, d.Target);
                continue;
            }
            if (n >= maxNewJobsPerTick)
                break;
            jobBoard.AddJob(new SimJob
            {
                Type = JobType.CutTree,
                Priority = d.Priority,
                Target = d.Target
            });
            designationBoard.MarkPlanned(DesignationType.CutTree, d.Target);
            n++;
        }
    }

    void PlanHaulJobs()
    {
        if (_looseResources.Count == 0 || _stockpileCells.Count == 0)
            return;

        const int maxHaulJobsPerTick = 6;
        int planned = 0;
        foreach (var pair in _looseResources)
        {
            if (planned >= maxHaulJobsPerTick)
                break;

            var stack = pair.Value;
            if (stack == null || stack.Amount <= 0)
                continue;

            if (jobBoard.HasActiveJobOnTarget(stack.Position, JobType.HaulResource))
                continue;

            var dropoff = FindNearestStockpileCell(stack.Position);
            if (!dropoff.HasValue)
                continue;

            jobBoard.AddJob(new SimJob
            {
                Type = JobType.HaulResource,
                Priority = JobPriority.Normal,
                Target = stack.Position,
                ResourceType = stack.ResourceType,
                ResourceAmount = Mathf.Min(5, stack.Amount),
                DropoffTarget = dropoff.Value
            });
            planned++;
        }
    }

    Vector3I? FindNearestStockpileCell(Vector3I from)
    {
        if (_stockpileCells.Count == 0)
            return null;

        bool found = false;
        Vector3I best = default;
        int bestDist = int.MaxValue;
        foreach (var c in _stockpileCells)
        {
            int d = Mathf.Abs(c.X - from.X) + Mathf.Abs(c.Y - from.Y) + Mathf.Abs(c.Z - from.Z);
            if (d >= bestDist)
                continue;
            best = c;
            bestDist = d;
            found = true;
        }

        return found ? best : null;
    }

    List<Vector3I> CollectBuildCluster(Vector3I seed, HashSet<Vector3I> pending)
    {
        var cluster = new List<Vector3I>();
        if (!pending.Remove(seed))
            return cluster;

        _designationBfsQueue.Clear();
        _designationBfsQueue.Enqueue(seed);
        cluster.Add(seed);

        ReadOnlySpan<Vector3I> neighbors = stackalloc Vector3I[]
        {
            new(1,0,0), new(-1,0,0), new(0,0,1), new(0,0,-1), new(0,1,0), new(0,-1,0),
        };

        const int maxClusterSize = 48;
        while (_designationBfsQueue.Count > 0 && cluster.Count < maxClusterSize)
        {
            var c = _designationBfsQueue.Dequeue();
            foreach (var d in neighbors)
            {
                var n = c + d;
                if (!pending.Remove(n))
                    continue;
                cluster.Add(n);
                _designationBfsQueue.Enqueue(n);
                if (cluster.Count >= maxClusterSize)
                    break;
            }
        }

        return cluster;
    }

    bool TryPickRallyForFootprint(HashSet<Vector3I> footprint, int minY, out Vector3I rally)
    {
        rally = default;
        ReadOnlySpan<Vector3I> offs = stackalloc Vector3I[]
        {
            new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
            new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
        };

        Vector3I colonAvg = Vector3I.Zero;
        int nc = 0;
        foreach (var c in World.CurrentMap.Colonists)
        {
            if (c.OwnerId != 0)
                continue;
            colonAvg += c.Position;
            nc++;
        }

        if (nc > 0)
            colonAvg = new Vector3I(colonAvg.X / nc, colonAvg.Y / nc, colonAvg.Z / nc);

        int best = int.MaxValue;
        bool any = false;
        foreach (var b in footprint)
        {
            if (b.Y != minY)
                continue;
            foreach (var d in offs)
            {
                var n = b + d;
                if (footprint.Contains(n))
                    continue;
                if (!pathfinder.IsWalkable(n))
                    continue;
                if (IsOccupied(n))
                    continue;

                int dist = Mathf.Abs(n.X - colonAvg.X) + Mathf.Abs(n.Y - colonAvg.Y) + Mathf.Abs(n.Z - colonAvg.Z);
                if (dist >= best)
                    continue;
                best = dist;
                rally = n;
                any = true;
            }
        }

        return any;
    }

    Vector3I FindRallyFallback(HashSet<Vector3I> footprint, int minY)
    {
        int sx = 0, sz = 0, n = 0;
        foreach (var p in footprint)
        {
            if (p.Y != minY)
                continue;
            sx += p.X;
            sz += p.Z;
            n++;
        }

        Vector3I anchor;
        if (n > 0)
            anchor = new Vector3I(sx / n, minY, sz / n);
        else
        {
            anchor = default;
            foreach (var p in footprint)
            {
                anchor = p;
                break;
            }
        }

        for (int y = minY; y <= minY + 2; y++)
        {
            for (int r = 0; r < 18; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dy = -r; dy <= r; dy++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (!OnChebyshevShell(dx, dy, dz, r))
                        continue;
                    var pos = anchor + new Vector3I(dx, dy, dz);
                    if (footprint.Contains(pos))
                        continue;
                    if (!pathfinder.IsWalkable(pos))
                        continue;
                    if (IsOccupied(pos))
                        continue;
                    return pos;
                }
            }
        }

        return anchor;
    }

    public void Update()
    {
        Tick++;

        ProcessCommandQueue();
        if (Tick % 3 == 0)
            PlanDesignationsIntoJobs();
        if (Tick % 8 == 0)
            jobBoard.WakeWaitingAccessJobsDue(Tick);
        if (Tick % 12 == 0)
            CleanupVirtualScaffoldsIfBuildQueueEmpty();


        Vision.ClearVisible();

        // déplacement des colons
        foreach (var colon in World.CurrentMap.Colonists)
        {
             if (colon.OwnerId != 0) // joueur local
                continue;

            ApplyColonistGravity(colon);
            UpdateVision(colon);
            UpdateColon(colon);
            if (Tick % 12 == 0)
                TryRecoverIdlePerchedColonist(colon);
        }

        if (pendingWalkabilityRefresh)
        {
            pendingWalkabilityRefresh = false;
            NotifyMapWalkabilityChanged();
        }

    }

    void ProcessCommandQueue()
    {
        var latestMoveByEntity = new Dictionary<int, PlayerCommand>();
        var immediateCommands = new List<PlayerCommand>();

        while (CommandQueue.Count > 0)
        {
            var cmd = CommandQueue.Dequeue();
            if (cmd.Type == PlayerCommandType.Move)
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
        if (cmd == null || string.IsNullOrEmpty(cmd.Type))
            return;

        if (cmd.Type == PlayerCommandType.Move)
        {
            HashSet<Vector3I> reserved = new();
            var colon = World.CurrentMap.Colonists[cmd.EntityId];
            CancelColonCurrentJobForManualMove(colon);
            colon.HasPathFailed = false;
            colon.RepathTimer = 0;
            colon.WaitTimer = 0;

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
            if (TryAssignMovePath(colon, end))
                return;

            int generatedCount;
            if (TryQueueMoveLiftScaffold(colon, end, out generatedCount))
            {
                RequestWalkabilityRefresh(colon.Position);
                pathRequestService.InvalidateAll();
                if (TryAssignMovePath(colon, end))
                {
                    LogDebug($"[Scaffold] move lift {generatedCount} -> path récupéré vers {end}");
                    return;
                }
            }

            colon.HasPathFailed = true;
            colon.Path = null;
            colon.MoveProgress = 0f;
        }
        else if (cmd.Type == PlayerCommandType.DesignateBuild)
        {
            if (cmd.Cells == null || cmd.Cells.Count == 0)
                return;
            var buildCells = new List<Vector3I>(cmd.Cells.Count);
            foreach (var cell in cmd.Cells)
                buildCells.Add(cell.ToVector3I());
            var tileType = string.IsNullOrEmpty(cmd.BuildTileType) ? "stone" : cmd.BuildTileType;
            DesignateBuildCells(buildCells, tileType, (JobPriority)cmd.Priority);
        }
        else if (cmd.Type == PlayerCommandType.DesignateMine)
        {
            if (cmd.Cells == null || cmd.Cells.Count == 0)
                return;
            var mineCells = new List<Vector3I>(cmd.Cells.Count);
            foreach (var cell in cmd.Cells)
                mineCells.Add(cell.ToVector3I());
            DesignateMineCells(mineCells, (JobPriority)cmd.Priority);
        }
        else if (cmd.Type == PlayerCommandType.DesignateCutTree)
        {
            DesignateCutTree(new Vector3I(cmd.X, cmd.Y, cmd.Z), (JobPriority)cmd.Priority);
        }
    }

    void CancelColonCurrentJobForManualMove(Colonist colon)
    {
        if (colon == null)
            return;

        // Un ordre joueur de déplacement doit toujours casser la contrainte
        // de job actif / rally chantier.
        if (colon.ActiveJob != null)
            ReleaseColonistJob(colon, waitForAccess: false);
        colon.PostBuildRallyCell = null;
        colon.WorkTicksRemaining = 0;
    }

    bool TryAssignMovePath(Colonist colon, Vector3I end)
    {
        if (colon == null || !pathfinder.IsWalkable(end))
            return false;

        var path = pathRequestService.GetOrCreatePath(colon.Position, end);
        if (path == null || path.Count <= 1)
            return false;

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
            return false;

        path.RemoveAt(0);
        colon.Path = path;
        colon.HasPathFailed = false;
        colon.WaitTimer = 0;
        colon.Target = end;
        colon.MoveProgress = 0f;
        LogDebug($"PATH OK: {path.Count} (from {colon.Position} to {end})");
        return true;
    }

    /// <summary>Un pas vers le bas par tick si plus de support solide sous les pieds.</summary>
    void ApplyColonistGravity(Colonist colon)
    {
        var map = World.CurrentMap;
        var p = colon.Position;
        if (p.Y <= 0)
            return;

        // Un colon sur/au-dessus d'un scaffold virtuel reste "accroché" :
        // pas de gravité tant que l'échelle d'accès le supporte.
        if (virtualScaffolds.HasAt(p))
            return;

        var below = p + new Vector3I(0, -1, 0);
        if (virtualScaffolds.HasAt(below))
            return;
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
        jobBoard.WakeAllWaitingAccessJobs();
        pathRequestService.InvalidateAll();

        bool localOnly = !walkabilityChangeFullRepath && walkabilityChangeFocus.HasValue;
        var focus = walkabilityChangeFocus;
        walkabilityChangeFocus = null;
        walkabilityChangeFullRepath = false;

        const int localManhattanRadius = 36;
        foreach (var c in World.CurrentMap.Colonists)
        {
            if (c.OwnerId != 0)
                continue;
            if (localOnly && focus.HasValue)
            {
                var p = c.Position;
                int d = Mathf.Abs(p.X - focus.Value.X) + Mathf.Abs(p.Y - focus.Value.Y) + Mathf.Abs(p.Z - focus.Value.Z);
                if (d > localManhattanRadius)
                    continue;
            }
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
            ReleaseColonistJob(colon, waitForAccess: true, retryDelayTicks: 10);
            colon.HasPathFailed = false;
            return;
        }

        var path = pathRequestService.GetOrCreatePath(colon.Position, job.WorkPosition);
        if (path == null || path.Count <= 1)
        {
            if (TryQueueAccessScaffoldForBlockedPath(colon, job, job.WorkPosition))
                ReleaseColonistJob(colon, waitForAccess: true, retryDelayTicks: 8);
            else
                ReleaseColonistJob(colon, waitForAccess: false);
            colon.HasPathFailed = false;
            return;
        }

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
        {
            if (TryQueueAccessScaffoldForBlockedPath(colon, job, job.WorkPosition))
                ReleaseColonistJob(colon, waitForAccess: true, retryDelayTicks: 8);
            else
                ReleaseColonistJob(colon, waitForAccess: false);
            colon.HasPathFailed = false;
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
        if (colon.HasPathFailed && colon.ActiveJob != null)
        {
            colon.HasPathFailed = false;
            RepathColonistToActiveJobWork(colon);
        }

        if (colon.HasPathFailed && colon.ActiveJob == null)
        {
            colon.RepathTimer++;
            if (colon.RepathTimer >= ManualMoveRecoveryRetryTicks)
            {
                colon.RepathTimer = 0;
                TryRecoverManualMovePath(colon);
            }
        }

        if (colon.HasPathFailed)
            return;

        if ((colon.Path == null || colon.Path.Count == 0) && colon.ActiveJob == null)
        {
            if (colon.PostBuildRallyCell.HasValue && colon.Position != colon.PostBuildRallyCell.Value)
            {
                TryStartPostBuildRallyPath(colon);
                if (colon.PostBuildRallyCell.HasValue && colon.Position != colon.PostBuildRallyCell.Value
                    && (colon.Path == null || colon.Path.Count == 0))
                    return;
            }

            TryAssignJob(colon);
        }

        if (colon.Path == null || colon.Path.Count == 0)
        {
            colon.MoveProgress = 0f;
            TryExecuteWork(colon);
            return;
        }

        var next = colon.Path[0];

        // blocage
        if (!pathfinder.IsWalkable(next) || IsPositionBlockedByColonist(next, colon))
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
            if (colon.ActiveJob != null)
            {
                var blockedJob = colon.ActiveJob;
                if (TryQueueAccessScaffoldForBlockedPath(colon, blockedJob, blockedJob.WorkPosition))
                    ReleaseColonistJob(colon, waitForAccess: true, retryDelayTicks: 8);
                else
                    ReleaseColonistJob(colon, waitForAccess: false);
                colon.HasPathFailed = false;
                return;
            }

            int generatedCount;
            if (TryQueueMoveLiftScaffold(colon, colon.Target, out generatedCount))
            {
                RequestWalkabilityRefresh(colon.Position);
                pathRequestService.InvalidateAll();
                if (TryAssignMovePath(colon, colon.Target))
                {
                    LogDebug($"[Scaffold] move lift {generatedCount} pendant déplacement vers {colon.Target}");
                    return;
                }
            }

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
        if (colonistId < 0)
            return;
        HashSet<int> rejectedJobIds = new();
        const int maxAttempts = 8;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!jobBoard.TryReserveBest(colonistId, colon.Position, Tick, rejectedJobIds, CanOfferBuildJob, out var job))
                return;

            if (job.Type == JobType.BuildBlock && job.BuildSiteId != 0
                && _buildSites.TryGetValue(job.BuildSiteId, out var siteForRoofAssign)
                && siteForRoofAssign.RoofTargets.Count > 0
                && siteForRoofAssign.RoofTargets.Contains(job.Target))
            {
                var r = siteForRoofAssign.RallyCell;
                int rcx = Mathf.Abs(colon.Position.X - r.X);
                int rcy = Mathf.Abs(colon.Position.Y - r.Y);
                int rcz = Mathf.Abs(colon.Position.Z - r.Z);
                if (Mathf.Max(rcx, Mathf.Max(rcy, rcz)) > 1)
                {
                    // Amène le colon au point de ralliement pour pouvoir prendre
                    // ensuite les jobs "sommet de colonne" sans bloquer le chantier.
                    colon.PostBuildRallyCell = r;
                    rejectedJobIds.Add(job.Id);
                    jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 4);
                    continue;
                }
            }

            var workPos = job.Type switch
            {
                JobType.CutTree => FindNearestWalkableAdjacentToBlock(job.Target, forbidSameXzColumn: true, preferDiagonalXz: true, avoidAboveTarget: false, buildContextJob: null),
                JobType.MineStone => FindNearestWalkableAdjacentToBlock(job.Target, forbidSameXzColumn: true, preferDiagonalXz: true, avoidAboveTarget: true, buildContextJob: null),
                JobType.BuildBlock => FindNearestWalkableAdjacentToBlock(job.Target, forbidSameXzColumn: true, preferDiagonalXz: true, avoidAboveTarget: true, buildContextJob: job),
                JobType.HaulResource => FindNearestFree(job.Target),
                _ => FindNearestFree(job.Target)
            };

            if ((job.Type == JobType.MineStone || job.Type == JobType.BuildBlock) && workPos == job.Target)
            {
                TryQueueAccessScaffoldForBlockedPath(colon, job, workPos);
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 12);
                continue;
            }

            if (!pathfinder.IsWalkable(workPos))
            {
                TryQueueAccessScaffoldForBlockedPath(colon, job, workPos);
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 8);
                continue;
            }

            if (!virtualScaffolds.HasAt(workPos) && reservationManager.IsWorkCellReservedByOther(workPos, colonistId))
            {
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobAvailable(job);
                continue;
            }

            if (job.Type == JobType.BuildBlock && !HasSafeEscapeNeighbor(workPos, job.Target))
            {
                TryQueueAccessScaffoldForBlockedPath(colon, job, workPos);
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 8);
                continue;
            }

            var start = colon.Position;
            var path = pathRequestService.GetOrCreatePath(start, workPos);
            if (path == null || path.Count <= 1)
            {
                TryQueueAccessScaffoldForBlockedPath(colon, job, workPos);
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 8);
                continue;
            }

            path = pathfinder.SmoothPath(new List<Vector3I>(path));
            if (path == null || path.Count <= 1)
            {
                TryQueueAccessScaffoldForBlockedPath(colon, job, workPos);
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks: 8);
                continue;
            }

            path.RemoveAt(0);
            job.WorkPosition = workPos;
            if (!virtualScaffolds.HasAt(workPos)
                && !reservationManager.TryReserve(colonistId, job.Id, workPos))
            {
                rejectedJobIds.Add(job.Id);
                jobBoard.ReleaseJobAvailable(job);
                continue;
            }
            colon.ActiveJob = job;
            colon.Path = path;
            colon.Target = workPos;
            colon.MoveProgress = 0f;
            colon.WorkTicksRemaining = 0;
            if (job.BuildSiteId != 0 && _buildSites.TryGetValue(job.BuildSiteId, out var siteCommitted))
                siteCommitted.WorkersCommitted.Add(colonistId);
            return;
        }
    }

    bool CanOfferBuildJob(SimJob j)
    {
        if (j.Type != JobType.BuildBlock || j.BuildSiteId == 0)
            return true;
        if (!_buildSites.TryGetValue(j.BuildSiteId, out var site))
            return true;
        return site.IsJobUnlocked(j, World.CurrentMap, World.CurrentMap.Colonists);
    }

    bool CanUseAutoScaffoldForJob(SimJob job)
    {
        if (job == null)
            return false;
        return job.Type == JobType.BuildBlock || job.Type == JobType.MineStone || job.Type == JobType.CutTree;
    }

    bool TryQueueAccessScaffoldForBlockedPath(Colonist colon, SimJob blockedJob, Vector3I blockedWorkPos)
    {
        if (!CanUseAutoScaffoldForJob(blockedJob))
            return false;
        if (scaffoldRetryAfterTickByTarget.TryGetValue(blockedJob.Target, out int retryAfter) && Tick < retryAfter)
            return false;
        if (blockedJob.Type == JobType.BuildBlock && virtualScaffolds.IsBuildSiteSeeded(blockedJob.BuildSiteId))
            return false;
        if (virtualScaffolds.IsTargetSeeded(blockedJob.Target))
            return false;

        int generatedCount = 0;
        bool generated = false;
        if (colon != null)
        {
            generated = TryQueueColonStairScaffold(colon, blockedWorkPos, out generatedCount);
            if (generated)
                LogDebug($"[Scaffold] stair {generatedCount} cellule(s) vers {blockedJob.Target}");
        }

        if (!generated && !virtualScaffolds.HasNearby(blockedJob.Target, radius: 3))
        {
            generated = virtualScaffolds.TryGenerateColumn(
                World.CurrentMap,
                jobBoard,
                _buildSites,
                blockedJob.Target,
                blockedJob.BuildSiteId,
                (probeFrom) =>
                {
                    if (colon == null)
                        return true;
                    if (!pathfinder.IsWalkable(probeFrom))
                        return false;
                    var p = pathfinder.FindPath(colon.Position, blockedWorkPos);
                    return p != null && p.Count > 1;
                },
                out generatedCount);
        }

        // Fallback anti-mur : si la colonne près de la cible n'aide pas,
        // on tente une mini-échelle verticale sur la colonne du colon.
        if (!generated && colon != null)
        {
            generated = TryQueueColonLiftScaffold(colon, blockedWorkPos, out generatedCount);
            if (generated)
                LogDebug($"[Scaffold] lift {generatedCount} cellule(s) pour débloquer path @ {blockedJob.Target}");
        }

        if (!generated)
        {
            scaffoldRetryAfterTickByTarget[blockedJob.Target] = Tick + ScaffoldRetryDelayTicks;
            return false;
        }

        virtualScaffolds.MarkTargetSeeded(blockedJob.Target);
        scaffoldRetryAfterTickByTarget.Remove(blockedJob.Target);
        if (blockedJob.Type == JobType.BuildBlock)
            virtualScaffolds.MarkBuildSiteSeeded(blockedJob.BuildSiteId);
        RequestWalkabilityRefresh(colon?.Position ?? blockedJob.Target);
        LogDebug($"[Scaffold] {generatedCount} cellule(s) générée(s) pour accès job @ {blockedJob.Target}");
        return true;
    }

    bool TryQueueColonStairScaffold(Colonist colon, Vector3I blockedWorkPos, out int generatedCount)
    {
        generatedCount = 0;
        if (colon == null)
            return false;

        const int maxStairSteps = 10;
        return virtualScaffolds.TryGenerateStairWithPathProbe(
            World.CurrentMap,
            colon.Position,
            blockedWorkPos,
            maxStairSteps,
            (top) =>
            {
                if (!pathfinder.IsWalkable(top))
                    return false;
                var p = pathfinder.FindPath(top, blockedWorkPos);
                return p != null && p.Count > 1;
            },
            out generatedCount
        );
    }

    bool TryQueueColonLiftScaffold(Colonist colon, Vector3I blockedWorkPos, out int generatedCount)
    {
        generatedCount = 0;
        if (colon == null)
            return false;

        const int maxLiftHeight = 6;
        var map = World.CurrentMap;
        return virtualScaffolds.TryGenerateLiftWithPathProbe(
            map,
            colon.Position,
            maxLiftHeight,
            (top) =>
            {
                if (!pathfinder.IsWalkable(top))
                    return false;
                var p = pathfinder.FindPath(top, blockedWorkPos);
                return p != null && p.Count > 1;
            },
            out generatedCount
        );
    }

    bool TryQueueMoveLiftScaffold(Colonist colon, Vector3I moveTarget, out int generatedCount)
    {
        generatedCount = 0;
        if (colon == null)
            return false;
        if (!pathfinder.IsWalkable(moveTarget))
            return false;

        const int maxLiftHeight = 6;
        return virtualScaffolds.TryGenerateLiftWithPathProbe(
            World.CurrentMap,
            colon.Position,
            maxLiftHeight,
            (top) =>
            {
                if (!pathfinder.IsWalkable(top))
                    return false;
                var p = pathfinder.FindPath(top, moveTarget);
                return p != null && p.Count > 1;
            },
            out generatedCount
        );
    }

    void CleanupVirtualScaffoldsIfBuildQueueEmpty()
    {
        if (jobBoard == null)
            return;

        _jobScratch.Clear();
        jobBoard.CopyActiveJobs(_jobScratch);
        foreach (var j in _jobScratch)
        {
            if (j.Type == JobType.BuildBlock)
                return;
        }

        foreach (var c in World.CurrentMap.Colonists)
        {
            if (c?.ActiveJob?.Type == JobType.BuildBlock)
                return;
        }

        designationBoard.CopyByType(DesignationType.Build, _designationScratch, onlyUnplanned: false);
        if (_designationScratch.Count > 0)
            return;

        if (virtualScaffolds.ClearAllScaffolds())
        {
            scaffoldRetryAfterTickByTarget.Clear();
            RequestWalkabilityRefresh(Vector3I.Zero);
            LogDebug("[Scaffold] cleanup auto: plus aucun job build actif.");
        }
    }

    void RequestWalkabilityRefresh(Vector3I focus)
    {
        if (pendingWalkabilityRefresh)
            walkabilityChangeFullRepath = true;
        else
        {
            walkabilityChangeFocus = focus;
            walkabilityChangeFullRepath = false;
        }

        pendingWalkabilityRefresh = true;
    }

    void TryStartPostBuildRallyPath(Colonist colon)
    {
        if (!colon.PostBuildRallyCell.HasValue || colon.ActiveJob != null)
            return;
        if (colon.Path != null && colon.Path.Count > 0)
            return;

        var rally = colon.PostBuildRallyCell.Value;
        if (colon.Position == rally)
        {
            colon.PostBuildRallyCell = null;
            return;
        }

        if (!pathfinder.IsWalkable(rally) || IsPositionBlockedByColonist(rally))
        {
            colon.PostBuildRallyCell = null;
            return;
        }

        var path = pathRequestService.GetOrCreatePath(colon.Position, rally);
        if (path == null || path.Count <= 1)
        {
            colon.PostBuildRallyCell = null;
            return;
        }

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
        {
            colon.PostBuildRallyCell = null;
            return;
        }

        path.RemoveAt(0);
        colon.Path = path;
        colon.Target = rally;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
    }

    void TryRecoverManualMovePath(Colonist colon)
    {
        if (colon == null)
            return;
        if (colon.ActiveJob != null)
            return;
        if (colon.Position == colon.Target)
        {
            ClearFailedManualMoveOrder(colon);
            return;
        }

        if (TryAssignMovePath(colon, colon.Target))
            return;

        int generatedCount;
        if (TryQueueMoveLiftScaffold(colon, colon.Target, out generatedCount))
        {
            RequestWalkabilityRefresh(colon.Position);
            pathRequestService.InvalidateAll();
            if (TryAssignMovePath(colon, colon.Target))
            {
                LogDebug($"[Scaffold] recover move {generatedCount} vers {colon.Target}");
                return;
            }
        }

        // L'ordre manuel n'est plus faisable (ou déjà obsolète) :
        // on évite de bloquer le colon indéfiniment.
        colon.WaitTimer++;
        if (colon.WaitTimer >= ManualMoveRecoveryMaxAttempts)
        {
            LogDebug($"[Move] abandon ordre manuel impossible vers {colon.Target}");
            ClearFailedManualMoveOrder(colon);
        }
    }

    void ClearFailedManualMoveOrder(Colonist colon)
    {
        if (colon == null)
            return;
        colon.HasPathFailed = false;
        colon.Path = null;
        colon.MoveProgress = 0f;
        colon.Target = colon.Position;
        colon.RepathTimer = 0;
        colon.WaitTimer = 0;
    }


    void TryExecuteWork(Colonist colon)
    {
        var job = colon.ActiveJob;
        if (job == null)
            return;

        if (colon.Position != job.WorkPosition)
            return;

        colon.WorkTicksRemaining++;
        int colonId = GetColonistIndex(colon);

        if (colon.WorkTicksRemaining == 1)
        {
            GD.Print($"Colon {colonId} commence {job.Type} sur {job.Target}.");
            OnJobStarted?.Invoke(colonId, job.Target);
        }

        bool mapChanged = false;
        bool finished = ApplyVoxelJobProgress(job, out mapChanged);
        if (!finished)
            return;

        GD.Print($"Colon {colonId} a terminé {job.Type} sur {job.Target}.");
        OnJobCompleted?.Invoke(colonId, job.Target);

        if (mapChanged)
        {
            if (pendingWalkabilityRefresh)
                walkabilityChangeFullRepath = true;
            else
            {
                walkabilityChangeFocus = job.Target;
                walkabilityChangeFullRepath = false;
            }
            pendingWalkabilityRefresh = true;
        }

        Vector3I? rallyAfter = null;
        if (job.Type == JobType.BuildBlock && job.BuildSiteId != 0
            && _buildSites.TryGetValue(job.BuildSiteId, out var siteOnComplete))
        {
            siteOnComplete.WorkersCommitted.Remove(colonId);
            siteOnComplete.OnBuildJobCompleted(job.Target);
            rallyAfter = siteOnComplete.RallyCell;
            if (siteOnComplete.IsFinished)
                _buildSites.Remove(job.BuildSiteId);
        }

        TryMoveColonAwayFromHighPerchAfterVoxelJob(colon, job);
        reservationManager.ReleaseByColonist(colonId);
        jobBoard.Complete(job);
        switch (job.Type)
        {
            case JobType.BuildBlock:
                designationBoard.MarkCompleted(DesignationType.Build, job.Target);
                break;
            case JobType.MineStone:
                designationBoard.MarkCompleted(DesignationType.Mine, job.Target);
                break;
            case JobType.CutTree:
                designationBoard.MarkCompleted(DesignationType.CutTree, job.Target);
                break;
        }
        colon.ActiveJob = null;
        colon.WorkTicksRemaining = 0;

        if (rallyAfter.HasValue && colon.Position != rallyAfter.Value)
            colon.PostBuildRallyCell = rallyAfter.Value;
    }

    void TryMoveColonAwayFromHighPerchAfterVoxelJob(Colonist colon, SimJob finishedJob)
    {
        if (finishedJob == null)
            return;
        if (finishedJob.Type != JobType.BuildBlock && finishedJob.Type != JobType.MineStone)
            return;
        if (colon.Position.Y <= finishedJob.Target.Y)
            return;

        Vector3I? best = null;
        int bestDist = int.MaxValue;
        for (int r = 1; r <= 6; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
            {
                int man = Mathf.Abs(dx) + Mathf.Abs(dz);
                if (man == 0 || man > r)
                    continue;

                var c = new Vector3I(colon.Position.X + dx, finishedJob.Target.Y, colon.Position.Z + dz);
                if (!IsWalkableAndFree(c))
                    continue;

                int dist = Mathf.Abs(c.X - colon.Position.X) + Mathf.Abs(c.Z - colon.Position.Z);
                if (dist >= bestDist)
                    continue;
                bestDist = dist;
                best = c;
            }
            if (best.HasValue)
                break;
        }

        if (!best.HasValue)
            return;

        var path = pathRequestService.GetOrCreatePath(colon.Position, best.Value);
        if (path == null || path.Count <= 1)
            return;

        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
            return;

        path.RemoveAt(0);
        colon.Path = path;
        colon.Target = best.Value;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
    }

    void TryRecoverIdlePerchedColonist(Colonist colon)
    {
        if (colon == null || colon.ActiveJob != null)
            return;
        if (colon.Path != null && colon.Path.Count > 0)
            return;

        // Si le colon a déjà une sortie latérale au même niveau, ne rien forcer.
        ReadOnlySpan<Vector3I> lateral = stackalloc Vector3I[]
        {
            new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
            new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
        };
        foreach (var d in lateral)
        {
            var n = colon.Position + d;
            if (IsWalkableAndFree(n))
                return;
        }

        Vector3I? best = null;
        int bestDist = int.MaxValue;
        int minY = Mathf.Max(0, colon.Position.Y - 8);
        for (int y = colon.Position.Y - 1; y >= minY; y--)
        {
            for (int r = 1; r <= 5; r++)
            {
                for (int dx = -r; dx <= r; dx++)
                for (int dz = -r; dz <= r; dz++)
                {
                    if (Mathf.Abs(dx) + Mathf.Abs(dz) > r)
                        continue;
                    var c = new Vector3I(colon.Position.X + dx, y, colon.Position.Z + dz);
                    if (!IsWalkableAndFree(c))
                        continue;
                    int dist = Mathf.Abs(dx) + Mathf.Abs(dz) + (colon.Position.Y - y) * 2;
                    if (dist >= bestDist)
                        continue;
                    bestDist = dist;
                    best = c;
                }
            }
            if (best.HasValue)
                break;
        }

        if (!best.HasValue)
            return;

        var path = pathRequestService.GetOrCreatePath(colon.Position, best.Value);
        if (path == null || path.Count <= 1)
            return;
        path = pathfinder.SmoothPath(new List<Vector3I>(path));
        if (path == null || path.Count <= 1)
            return;

        path.RemoveAt(0);
        colon.Path = path;
        colon.Target = best.Value;
        colon.MoveProgress = 0f;
        colon.HasPathFailed = false;
    }

    bool ApplyVoxelJobProgress(SimJob job, out bool mapChanged)
    {
        mapChanged = false;
        var map = World.CurrentMap;

        switch (job.Type)
        {
            case JobType.CutTree:
            case JobType.MineStone:
                {
                    var tile = map.GetTile(job.Target);
                    if (tile == null || !tile.Solid)
                    {
                        voxelWork.Remove(job.Target);
                        return true;
                    }

                    if (!voxelWork.TryGetValue(job.Target, out var progress)
                        || progress.IsConstruction
                        || progress.TargetTileType != tile.Type)
                    {
                        progress = new VoxelWorkProgress
                        {
                            Position = job.Target,
                            State = VoxelWorkState.InProgress,
                            Current = VoxelWorkCatalog.GetMaxHp(tile.Type),
                            Max = VoxelWorkCatalog.GetMaxHp(tile.Type),
                            IsConstruction = false,
                            TargetTileType = tile.Type
                        };
                        voxelWork[job.Target] = progress;
                    }

                    progress.State = VoxelWorkState.InProgress;
                    progress.Current -= VoxelWorkCatalog.GetMinePowerPerTick(tile.Type);
                    if (progress.Current > 0f)
                        return false;

                    map.SetTile(job.Target, VoxelWorkCatalog.GetDestroyedResult(tile.Type));
                    if (job.Type == JobType.CutTree)
                        AddLooseResource(job.Target, "wood", amount: 4);
                    else if (job.Type == JobType.MineStone)
                        AddLooseResource(job.Target, "stone", amount: 3);
                    progress.Current = 0f;
                    progress.State = VoxelWorkState.Destroyed;
                    voxelWork.Remove(job.Target);
                    mapChanged = true;
                    return true;
                }

            case JobType.BuildBlock:
                {
                    var current = map.GetTile(job.Target);
                    if (current != null && current.Solid)
                    {
                        voxelWork.Remove(job.Target);
                        return true;
                    }

                    string targetType = string.IsNullOrEmpty(job.BuildTileType) ? "stone" : job.BuildTileType;
                    float maxHp = VoxelWorkCatalog.GetMaxHp(targetType);
                    if (!voxelWork.TryGetValue(job.Target, out var progress)
                        || !progress.IsConstruction
                        || progress.TargetTileType != targetType)
                    {
                        progress = new VoxelWorkProgress
                        {
                            Position = job.Target,
                            State = VoxelWorkState.Ghost,
                            Current = 0f,
                            Max = maxHp,
                            IsConstruction = true,
                            TargetTileType = targetType
                        };
                        voxelWork[job.Target] = progress;
                    }

                    progress.State = VoxelWorkState.InProgress;
                    progress.Current += VoxelWorkCatalog.GetBuildPowerPerTick(targetType);
                    if (progress.Current < progress.Max)
                        return false;

                    map.SetTile(job.Target, VoxelWorkCatalog.BuildTile(targetType));
                    progress.Current = progress.Max;
                    progress.State = VoxelWorkState.Built;
                    voxelWork.Remove(job.Target);
                    mapChanged = true;
                    return true;
                }

            case JobType.HaulResource:
                {
                    if (!_looseResources.TryGetValue(job.Target, out var stack) || stack.Amount <= 0)
                        return true;

                    int movedAmount = job.ResourceAmount > 0 ? Mathf.Min(job.ResourceAmount, stack.Amount) : 1;
                    stack.Amount -= movedAmount;
                    if (stack.Amount <= 0)
                        _looseResources.Remove(job.Target);
                    else
                        _looseResources[job.Target] = stack;

                    string key = string.IsNullOrEmpty(job.ResourceType) ? stack.ResourceType : job.ResourceType;
                    if (!_stockpileInventory.TryAdd(key, movedAmount))
                        _stockpileInventory[key] += movedAmount;
                    return true;
                }
        }

        return true;
    }

    bool HasSafeEscapeNeighbor(Vector3I workSpot, Vector3I targetBlock)
    {
        ReadOnlySpan<Vector3I> neighbors = stackalloc Vector3I[]
        {
            new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
            new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
            new(0, 1, 0), new(0, -1, 0),
        };

        foreach (var d in neighbors)
        {
            var n = workSpot + d;
            if (n == targetBlock)
                continue;
            if (!pathfinder.IsWalkable(n))
                continue;
            if (IsOccupied(n))
                continue;
            if (reservationManager.IsWorkCellReserved(n))
                continue;
            return true;
        }

        return false;
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

    /// <summary>
    /// Case où le colon travaille : voisin du bloc cible.
    /// forbidSameXzColumn évite d’être pile au-dessus / dessous (même colonne) pour mine & build.
    /// preferDiagonalXz teste d’abord les coins XZ au même Y, puis les côtés, puis l’anneau Manhattan.
    /// </summary>
    Vector3I FindNearestWalkableAdjacentToBlock(Vector3I blockCell, bool forbidSameXzColumn, bool preferDiagonalXz, bool avoidAboveTarget, SimJob buildContextJob = null)
    {
        bool TryOffset(Vector3I d, out Vector3I found)
        {
            found = default;
            var n = blockCell + d;
            if (avoidAboveTarget && n.Y > blockCell.Y)
                return false;
            if (forbidSameXzColumn && n.X == blockCell.X && n.Z == blockCell.Z)
                return false;
            if (!IsWalkableAndFree(n))
                return false;
            if (buildContextJob != null && buildContextJob.Type == JobType.BuildBlock
                && jobBoard.HasActiveJobOnTarget(n, JobType.BuildBlock))
                return false;
            found = n;
            return true;
        }

        if (preferDiagonalXz)
        {
            ReadOnlySpan<Vector3I> xzDiagonals = stackalloc Vector3I[]
            {
                new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
            };
            ReadOnlySpan<Vector3I> xzAxes = stackalloc Vector3I[]
            {
                new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
            };

            foreach (var d in xzDiagonals)
            {
                if (TryOffset(d, out var w))
                    return w;
            }

            foreach (var d in xzAxes)
            {
                if (TryOffset(d, out var w))
                    return w;
            }

            if (!forbidSameXzColumn)
            {
                ReadOnlySpan<Vector3I> verticals = stackalloc Vector3I[] { new(0, 1, 0), new(0, -1, 0), };
                foreach (var d in verticals)
                {
                    if (TryOffset(d, out var w))
                        return w;
                }
            }
        }
        else
        {
            Vector3I[] faceNeighbors =
            {
                new(1, 0, 0), new(-1, 0, 0),
                new(0, 1, 0), new(0, -1, 0),
                new(0, 0, 1), new(0, 0, -1),
            };

            foreach (var d in faceNeighbors)
            {
                if (TryOffset(d, out var w))
                    return w;
            }
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
                if (avoidAboveTarget && n.Y > blockCell.Y)
                    continue;
                if (forbidSameXzColumn && n.X == blockCell.X && n.Z == blockCell.Z)
                    continue;
                if (!IsWalkableAndFree(n))
                    continue;
                if (buildContextJob != null && buildContextJob.Type == JobType.BuildBlock
                    && jobBoard.HasActiveJobOnTarget(n, JobType.BuildBlock))
                    continue;
                return n;
            }
        }

        return blockCell;
    }

    bool IsWalkableAndFree(Vector3I pos)
    {
        if (!pathfinder.IsWalkable(pos))
            return false;

        if (!virtualScaffolds.HasAt(pos) && reservationManager.IsWorkCellReserved(pos))
            return false;

        return !IsPositionBlockedByColonist(pos);
    }

    void ReleaseColonistJob(Colonist colon, bool waitForAccess, int retryDelayTicks = 8)
    {
        var job = colon.ActiveJob;
        if (job == null)
            return;

        if (job.Type == JobType.BuildBlock && job.BuildSiteId != 0
            && _buildSites.TryGetValue(job.BuildSiteId, out var siteRelease))
        {
            int cid = GetColonistIndex(colon);
            if (cid >= 0)
                siteRelease.WorkersCommitted.Remove(cid);
        }

        int colonistId = GetColonistIndex(colon);
        if (colonistId >= 0)
            reservationManager.ReleaseByColonist(colonistId);
        else
            reservationManager.ReleaseJob(job.Id);
        colon.ActiveJob = null;
        colon.WorkTicksRemaining = 0;
        colon.Path = null;
        colon.MoveProgress = 0f;

        if (waitForAccess)
            jobBoard.ReleaseJobWaitingForAccess(job, Tick, retryDelayTicks);
        else
            jobBoard.ReleaseJobAvailable(job);
    }

    bool IsPositionBlockedByColonist(Vector3I pos, Colonist ignore = null)
    {
        if (virtualScaffolds.HasAt(pos))
            return false;
        return IsOccupied(pos, ignore);
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

    public SimulationSnapshot CaptureSnapshot()
    {
        return new SimulationSnapshot
        {
            Tick = Tick,
            StateHash = ComputeDeterministicHash()
        };
    }

    public ulong ComputeDeterministicHash()
    {
        unchecked
        {
            ulong hash = 1469598103934665603UL;
            void Mix(ulong v)
            {
                hash ^= v;
                hash *= 1099511628211UL;
            }

            Mix((ulong)Tick);
            var map = World?.CurrentMap;
            if (map != null)
            {
                foreach (var colon in map.Colonists)
                {
                    Mix((ulong)(colon.OwnerId + 1));
                    Mix((ulong)(colon.Position.X * 73856093));
                    Mix((ulong)(colon.Position.Y * 19349663));
                    Mix((ulong)(colon.Position.Z * 83492791));
                }
            }

            _jobScratch.Clear();
            jobBoard?.CopyActiveJobs(_jobScratch);
            foreach (var job in _jobScratch)
            {
                Mix((ulong)((int)job.Type + 1));
                Mix((ulong)(job.Target.X * 73856093));
                Mix((ulong)(job.Target.Y * 19349663));
                Mix((ulong)(job.Target.Z * 83492791));
                Mix((ulong)job.EnqueueOrder);
            }

            foreach (var pair in _looseResources)
            {
                Mix((ulong)(pair.Key.X * 73856093));
                Mix((ulong)(pair.Key.Y * 19349663));
                Mix((ulong)(pair.Key.Z * 83492791));
                Mix((ulong)pair.Value.Amount);
            }

            foreach (var pair in _stockpileInventory)
            {
                Mix((ulong)pair.Key.GetHashCode());
                Mix((ulong)pair.Value);
            }

            return hash;
        }
    }

    public string GetLogisticsStatusText()
    {
        var sb = new StringBuilder();
        int looseStacks = _looseResources.Count;
        int looseTotal = 0;
        foreach (var stack in _looseResources.Values)
            looseTotal += stack.Amount;
        sb.Append("Stock : ");
        if (_stockpileInventory.Count == 0)
            sb.Append("vide");
        else
        {
            bool first = true;
            foreach (var pair in _stockpileInventory)
            {
                if (!first)
                    sb.Append(" · ");
                first = false;
                sb.Append(pair.Key).Append('=').Append(pair.Value);
            }
        }
        sb.Append(" | Sol : ").Append(looseTotal).Append(" (").Append(looseStacks).Append(" pile(s))");
        return sb.ToString();
    }

    public string ExportSaveJson()
    {
        var data = new SimulationSaveData
        {
            Tick = Tick,
            Colonists = new List<ColonistSaveData>(),
            Tiles = new List<TileSaveData>(),
            Jobs = new List<JobSaveData>(),
            Designations = new List<DesignationSaveData>(),
            LooseResources = new List<ResourceSaveData>(),
            StockpileInventory = new Dictionary<string, int>(_stockpileInventory, StringComparer.Ordinal),
            StockpileCells = new List<Int3SaveData>()
        };

        var map = World.CurrentMap;
        foreach (var colon in map.Colonists)
        {
            data.Colonists.Add(new ColonistSaveData
            {
                OwnerId = colon.OwnerId,
                Position = new Int3SaveData(colon.Position),
                Target = new Int3SaveData(colon.Target)
            });
        }

        var modifiedTiles = new List<Vector3I>(map.GetModifiedTiles());
        modifiedTiles.Sort(static (a, b) =>
        {
            int c = a.X.CompareTo(b.X);
            if (c != 0)
                return c;
            c = a.Y.CompareTo(b.Y);
            if (c != 0)
                return c;
            return a.Z.CompareTo(b.Z);
        });

        foreach (var worldPos in modifiedTiles)
        {
            var tile = map.GetTile(worldPos);
            if (tile == null)
                continue;

            data.Tiles.Add(new TileSaveData
            {
                Position = new Int3SaveData(worldPos),
                Type = tile.Type,
                Solid = tile.Solid
            });
        }

        _jobScratch.Clear();
        jobBoard.CopyActiveJobs(_jobScratch);
        foreach (var job in _jobScratch)
        {
            data.Jobs.Add(new JobSaveData
            {
                Type = (int)job.Type,
                Priority = (int)job.Priority,
                Status = (int)job.Status,
                Target = new Int3SaveData(job.Target),
                WorkPosition = new Int3SaveData(job.WorkPosition),
                RetryAfterTick = job.RetryAfterTick,
                EnqueueOrder = job.EnqueueOrder,
                BuildTileType = job.BuildTileType,
                BuildSiteId = job.BuildSiteId,
                ResourceType = job.ResourceType,
                ResourceAmount = job.ResourceAmount,
                DropoffTarget = new Int3SaveData(job.DropoffTarget)
            });
        }

        designationBoard.CopyByType(DesignationType.Build, _designationScratch, onlyUnplanned: false);
        foreach (var d in _designationScratch)
        {
            data.Designations.Add(new DesignationSaveData
            {
                Type = (int)d.Type,
                Target = new Int3SaveData(d.Target),
                Priority = (int)d.Priority,
                BuildTileType = d.BuildTileType,
                Planned = d.Planned
            });
        }
        designationBoard.CopyByType(DesignationType.Mine, _designationScratch, onlyUnplanned: false);
        foreach (var d in _designationScratch)
        {
            data.Designations.Add(new DesignationSaveData
            {
                Type = (int)d.Type,
                Target = new Int3SaveData(d.Target),
                Priority = (int)d.Priority,
                BuildTileType = d.BuildTileType,
                Planned = d.Planned
            });
        }
        designationBoard.CopyByType(DesignationType.CutTree, _designationScratch, onlyUnplanned: false);
        foreach (var d in _designationScratch)
        {
            data.Designations.Add(new DesignationSaveData
            {
                Type = (int)d.Type,
                Target = new Int3SaveData(d.Target),
                Priority = (int)d.Priority,
                BuildTileType = d.BuildTileType,
                Planned = d.Planned
            });
        }

        foreach (var pair in _looseResources)
        {
            data.LooseResources.Add(new ResourceSaveData
            {
                Position = new Int3SaveData(pair.Key),
                ResourceType = pair.Value.ResourceType,
                Amount = pair.Value.Amount
            });
        }

        foreach (var c in _stockpileCells)
            data.StockpileCells.Add(new Int3SaveData(c));

        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
    }

    public void ImportSaveJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;
        var data = JsonSerializer.Deserialize<SimulationSaveData>(json);
        if (data == null)
            return;

        Tick = data.Tick;
        var map = World.CurrentMap;
        map.ResetGeneratedWorldState();
        map.Colonists.Clear();
        foreach (var c in data.Colonists)
        {
            map.Colonists.Add(new Colonist
            {
                OwnerId = c.OwnerId,
                Position = c.Position.ToVector3I(),
                Target = c.Target.ToVector3I()
            });
        }

        // Rebuild chunks from generated world then apply saved overrides.
        foreach (var t in data.Tiles)
        {
            map.SetTile(t.Position.ToVector3I(), new Tile
            {
                Type = t.Type,
                Solid = t.Solid
            });
        }

        jobBoard = new JobBoard();
        foreach (var j in data.Jobs)
        {
            jobBoard.AddJob(new SimJob
            {
                Type = (JobType)j.Type,
                Priority = (JobPriority)j.Priority,
                Status = (JobStatus)j.Status,
                Target = j.Target.ToVector3I(),
                WorkPosition = j.WorkPosition.ToVector3I(),
                RetryAfterTick = j.RetryAfterTick,
                EnqueueOrder = j.EnqueueOrder,
                BuildTileType = j.BuildTileType,
                BuildSiteId = j.BuildSiteId,
                ResourceType = j.ResourceType,
                ResourceAmount = j.ResourceAmount,
                DropoffTarget = j.DropoffTarget.ToVector3I()
            });
        }

        designationBoard.Clear();
        foreach (var d in data.Designations)
        {
            designationBoard.AddOrUpdate(new WorkDesignation
            {
                Type = (DesignationType)d.Type,
                Target = d.Target.ToVector3I(),
                Priority = (JobPriority)d.Priority,
                BuildTileType = d.BuildTileType,
                Planned = d.Planned
            });
        }

        _looseResources.Clear();
        foreach (var r in data.LooseResources)
        {
            var pos = r.Position.ToVector3I();
            _looseResources[pos] = new LooseResourceStack
            {
                Position = pos,
                ResourceType = r.ResourceType,
                Amount = r.Amount
            };
        }

        _stockpileInventory.Clear();
        if (data.StockpileInventory != null)
        {
            foreach (var pair in data.StockpileInventory)
                _stockpileInventory[pair.Key] = pair.Value;
        }

        _stockpileCells.Clear();
        if (data.StockpileCells != null)
        {
            foreach (var c in data.StockpileCells)
                _stockpileCells.Add(c.ToVector3I());
        }
    }

    void LogDebug(string message)
    {
        if (!EnableDebugLogs)
            return;

        GD.Print("[Simulation] ", message);
    }
}