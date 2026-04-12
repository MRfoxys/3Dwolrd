using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;

public partial class Game : Node3D
{
    const int CHUNK_VISIBILITY_INTERVAL_TICKS = 1;

    [Export] public Simulation sim = new Simulation();
    LockstepManager lockstep = new LockstepManager();
    SimulationFacade simFacade;

    CameraController cameraController;
    SelectionManager selectionManager;
    UnitController unitController;

    public Dictionary<Colonist, Node3D> colonVisuals = new();

    PackedScene colonScene = GD.Load<PackedScene>("res://project/Godot/Colon.tscn");

    Node3D cameraPivot;
    Camera3D camera;

    StandardMaterial3D selectedMat;

    StandardMaterial3D defaultMat;

    ColorRect selectionRect;

    int localPlayerId = 0;

    double accumulator = 0;
    const double TICK_RATE = 0.05;
    Dictionary<Vector3I, Node3D> spawnedTiles = new();

    [Export] public int ChunkLoadRadius = 2;
    /// <summary>Rayon supplémentaire (en chunks) autour des colons + pivot caméra pour éviter les trous quand la vue dépasse le colon.</summary>
    [Export] public int ChunkPreloadMargin = 1;
    bool debugChunks = false;

    [Export] public float TerrainOcclusionMaxCameraDistance = 50f;
    [Export] public float TerrainOcclusionCameraMinY = 5f;
    [Export] public float TerrainOcclusionCameraMaxY = 100f;
    [Export] public float TerrainOcclusionRayMax = 220f;
    /// <summary>Distance max caméra → bloc pour valider une sélection (le raycast physique est borné par la même longueur de rayon).</summary>
    [Export] public float TerrainPickMaxDistance = 800f;
    /// <summary>Rayon depuis le centre du pixel (ProjectRay).</summary>
    [Export] public bool TerrainPickUsePixelCenterRay = true;
    /// <summary>False = repli grille DDA uniquement (sans collision chunk). True = raycast calque terrain + repli DDA si échec ou Q/Alt (percée).</summary>
    [Export] public bool TerrainPickUsePhysicsRaycast = true;
    [ExportGroup("Coupe terrain (touche V)")]
    /// <summary>Si true : inverse seulement la coupe verticale (gris ↔ marron). Le pelage sol n&apos;est pas inversé.</summary>
    [Export] public bool VerticalSliceInvertVisibility = true;
    [Export] public bool VerticalSliceUseVerticalCut = true;
    /// <summary>True = masque derrière le regard (s&lt;0). False = masque devant (s&gt;0).</summary>
    [Export] public bool VerticalSliceHideBehindCameraSide = false;
    /// <summary>≤0 (ex. -1) = pas de limite devant, seulement masquage derrière (s&lt;0). &gt;0 = au plus N tuiles visibles devant, au-delà masqué.</summary>
    [Export] public float VerticalSliceForwardCutDepth = -1f;
    [Export] public bool VerticalSlicePeelGroundWhenCameraLow = true;
    /// <summary>Sous cette hauteur Y (monde), retire le sol / le plafond de voxels au-dessus de la caméra pour éviter de voir l&apos;intérieur des blocs en descendant.</summary>
    [Export] public float VerticalSliceGroundPeelBelowY = 14f;
    /// <summary>Quand le pelage est actif : true = masque au-dessus (comportement d&apos;origine) ; false = masque en dessous.</summary>
    [Export] public bool VerticalSliceGroundPeelHideAbove = true;
    [Export] public float VerticalSliceGroundPeelMargin = 1.25f;
    /// <summary>0 = désactivé. Sinon (0–1) : masque les voxels dont le centre projeté tombe dans la bande **basse** de l&apos;écran (repère viewport, Y vers le bas).</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float VerticalSliceScreenBottomHideFraction = 0f;
    /// <summary>True = approximation espace caméra (léger). False = Unproject par voxel (lourd si fraction &gt; 0).</summary>
    [Export] public bool VerticalSliceScreenBottomFastCameraApprox = true;

    [ExportGroup("Sélection blocs")]
    [Export] public bool TerrainSelectionHighlightEnabled = true;
    [Export] public Color TerrainSelectionHighlightMix = new Color(1f, 0.15f, 0.12f, 1f);
    [Export] public float TerrainSelectionHighlightAmount = 0.55f;
    [Export] public bool TerrainHoverHighlightEnabled = true;
    [Export] public Color TerrainHoverHighlightMix = new Color(1f, 0.92f, 0.2f, 1f);
    [Export] public float TerrainHoverHighlightAmount = 0.38f;
    [Export] public Color TerrainMineHoverHighlightMix = new Color(0.35f, 1f, 0.45f, 1f);
    [Export] public float TerrainMineHoverHighlightAmount = 0.48f;

    HashSet<Vector3I> currentNeededChunks = new();

    Dictionary<Vector3I, ChunkVisual> _chunkVisuals = new();
    BoxMesh _tileCubeMesh;
    StandardMaterial3D _chunkSolidMaterial;
    int lastChunkRefreshTick = -1;
    int lastChunkVisibilityTick = -1;
    long lastChunkRefreshMicroseconds = 0;
    int lastNeededChunkCount = 0;
    float lastFrameDeltaSeconds = 0f;

    WorldSelectionTargetKind _selectionTargetKind = WorldSelectionTargetKind.Colonists;
    Vector3I? _selectedTreeTile;
    readonly HashSet<Vector3I> _selectedTerrainTiles = new();
    bool _verticalSliceTerrainActive;
    bool _verticalSliceTerrainWasActive;
    /// <summary>Invalide le cache peel quand chunks ajoutés/retirés ou options coupe changent.</summary>
    int _terrainMeshLayoutVersion;
    ulong _multimeshPeelStateToken = ulong.MaxValue;
    bool _slicePeelSkippedThisFrame = true;
    int _lastTerrainHighlightSelectionHash = int.MinValue;
    int _lastTerrainHighlightHoverHash = int.MinValue;
    int _lastTerrainHighlightLayoutVersion = int.MinValue;
    bool _lastTerrainHighlightEnabled = true;
    bool _lastTerrainHoverHighlightEnabled = true;
    Vector3I? _terrainHoverCell;
    readonly Dictionary<Node3D, Vector3I> _treeTileByRoot = new();
    readonly HashSet<Vector3I> _terrainOcclusionHidden = new();
    readonly HashSet<Vector3I> _terrainOcclusionHiddenPrev = new();
    readonly List<Vector3I> _raySolidBuffer = new();
    readonly List<Vector3I> _raySolidPickBuffer = new();

    Label _selectionModeLabel;
    Label _treeTargetLabel;
    Button _btnModeColonists;
    Button _btnModeTrees;
    Button _btnCutTree;
    Button _btnMineStone;
    Button _btnTerrainTiles;
    Label _jobsQueueLabel;
    Label _terrainTileLabel;
    readonly List<SimJob> _jobsUiBuffer = new();

    private Simulation _simulation;
    private PlayerController _playerController;
    private PlayerCommands _playerCommands;
    private MapRenderer _mapRenderer;


    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = GetNode<Camera3D>("CameraPivot/Camera3D");

        // Sinon le StaticBody3D « Ground » (calque 1 par défaut) mange le raycast avant les chunks voxel.
        var debugGround = GetNodeOrNull<StaticBody3D>("Ground");
        if (debugGround != null)
            debugGround.CollisionLayer = 0u;

        selectedMat = new StandardMaterial3D();
        selectedMat.AlbedoColor = new Color(0, 1, 0);

        defaultMat = new StandardMaterial3D();
        defaultMat.AlbedoColor = new Color(1, 0, 0);

        

        selectionRect = GetNode<ColorRect>("UI/SelectionRect");
        _selectionModeLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/ModeLabel");
        _treeTargetLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/TreeTargetLabel");
        _btnModeColonists = GetNodeOrNull<Button>("UI/SelectionPanel/VBox/ModeRow/BtnColonists");
        _btnModeTrees = GetNodeOrNull<Button>("UI/SelectionPanel/VBox/ModeRow/BtnTrees");
        _btnCutTree = GetNodeOrNull<Button>("UI/SelectionPanel/VBox/BtnCutTree");
        _btnMineStone = GetNodeOrNull<Button>("UI/SelectionPanel/VBox/BtnMineStone");
        _btnTerrainTiles = GetNodeOrNull<Button>("UI/SelectionPanel/VBox/ModeRow/BtnTerrainTiles");
        _jobsQueueLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/JobsQueueLabel");
        _terrainTileLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/TerrainTileLabel");
        if (_btnModeColonists != null)
            _btnModeColonists.Pressed += () => SetSelectionTargetKind(WorldSelectionTargetKind.Colonists);
        if (_btnModeTrees != null)
            _btnModeTrees.Pressed += () => SetSelectionTargetKind(WorldSelectionTargetKind.Trees);
        if (_btnCutTree != null)
            _btnCutTree.Pressed += OnCutSelectedTreePressed;
        if (_btnMineStone != null)
            _btnMineStone.Pressed += OnMineStonePressed;
        if (_btnTerrainTiles != null)
            _btnTerrainTiles.Pressed += () => SetSelectionTargetKind(WorldSelectionTargetKind.TerrainTiles);

        InitSimulation();

        if (sim == null)
        {
            GD.PrintErr("Erreur : Simulation non assignée à Game.");
            return;
        }

        _mapRenderer = new MapRenderer();
        AddChild(_mapRenderer);


        simFacade = new SimulationFacade(sim, lockstep);
        SpawnVisuals();
        spawnedTiles.Clear();

        _tileCubeMesh = new BoxMesh();
        _chunkSolidMaterial = new StandardMaterial3D();
        _chunkSolidMaterial.VertexColorUseAsAlbedo = true;
        _chunkSolidMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _tileCubeMesh.SurfaceSetMaterial(0, _chunkSolidMaterial);

        cameraController = new CameraController(cameraPivot, camera);
        selectionManager = new SelectionManager(camera, colonVisuals, localPlayerId);
        selectionManager.TargetKind = _selectionTargetKind;
        unitController = new UnitController(sim, lockstep, camera, GetNode<Node>("UI"));
        UpdateSelectionTargetUi();

        sim.OnJobStarted   += OnJobStarted;
        sim.OnJobCompleted   += OnJobCompleted;
    }

    public override void _Process(double delta)
    {
        if (sim.World == null)
            return;

        ComputeTerrainOcclusionHiddenSet();
        UpdateTerrainHoverCell();

        lastFrameDeltaSeconds = (float)delta;
        accumulator += delta;

        bool tickAdvanced = false;
        while (accumulator >= TICK_RATE)
        {
            Step();
            accumulator -= TICK_RATE;
            tickAdvanced = true;
        }

        UpdateSelectionRect();


        if (ShouldRefreshChunks(tickAdvanced))
        {
            var sw = Stopwatch.StartNew();
            UpdateChunksAroundColonists();
            UnloadFarChunks();
            lastChunkRefreshTick = sim.Tick;
            sw.Stop();
            lastChunkRefreshMicroseconds = (long)(sw.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency));
            lastNeededChunkCount = currentNeededChunks.Count;
            // Évite une frame où les nouveaux chunks sont à taille 1 avant le pelage (artefacts au zoom).
            if (_verticalSliceTerrainActive)
                ApplyMultimeshOcclusionPeels();
        }
        if (tickAdvanced && sim.Tick % CHUNK_VISIBILITY_INTERVAL_TICKS == 0 && sim.Tick != lastChunkVisibilityTick)
        {
            UpdateChunkVisibility();
            lastChunkVisibilityTick = sim.Tick;
        }
        

        Render();
        cameraController.Update(delta);
        RefreshJobsQueueUi();
        RefreshTreeJobOverlays();
        ApplyMultimeshOcclusionPeels();
        ApplyTerrainSelectionHighlights();
        SyncTreeResourcesVisibilityWithOcclusion();
        RefreshMineStoneButtonState();
    }

    void SyncTreeResourcesVisibilityWithOcclusion()
    {
        if (sim?.Vision == null)
            return;
        if (_verticalSliceTerrainActive && _slicePeelSkippedThisFrame)
            return;
        foreach (var kv in _treeTileByRoot)
        {
            if (!GodotObject.IsInstanceValid(kv.Key))
                continue;
            if (!sim.Vision.IsDiscovered(kv.Value))
            {
                kv.Key.Visible = false;
                continue;
            }
            bool hide = _terrainOcclusionHidden.Contains(kv.Value) || IsTerrainCellHiddenByVerticalSlice(kv.Value);
            kv.Key.Visible = !hide;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // Raccourcis globaux : avec le focus sur les boutons UI, _Input du Game ne reçoit souvent pas les touches.
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if (key.PhysicalKeycode == Key.V || key.Keycode == Key.V)
            {
                _verticalSliceTerrainActive = !_verticalSliceTerrainActive;
                _multimeshPeelStateToken = ulong.MaxValue;
                GD.Print(_verticalSliceTerrainActive
                    ? "[Vue] Coupe terrain : ON (vertical qui suit la caméra + pelage sol si bas)"
                    : "[Vue] Coupe terrain : OFF");
                GetViewport().SetInputAsHandled();
                return;
            }

            if (key.PhysicalKeycode == Key.T || key.Keycode == Key.T)
            {
                if (cameraController != null && selectionManager != null && unitController != null)
                {
                    var next = _selectionTargetKind switch
                    {
                        WorldSelectionTargetKind.Colonists => WorldSelectionTargetKind.Trees,
                        WorldSelectionTargetKind.Trees => WorldSelectionTargetKind.TerrainTiles,
                        _ => WorldSelectionTargetKind.Colonists
                    };
                    SetSelectionTargetKind(next);
                }
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (sim?.World == null || camera == null)
            return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left && !mb.Pressed)
        {
            if (_selectionTargetKind == WorldSelectionTargetKind.Trees)
            {
                TryPickTreeAtScreen();
                GetViewport().SetInputAsHandled();
            }
            else if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
            {
                TryPickTerrainTileAtScreen();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Godot can dispatch input before _Ready has fully wired controllers.
        if (cameraController == null || selectionManager == null || unitController == null)
            return;

        cameraController.HandleInput(@event);
        selectionManager.HandleInput(@event);
        unitController.HandleInput(@event, selectionManager.SelectedColonists);


        // debug tools
        if (@event is InputEventKey key && key.Pressed)
        {
            if (key.Keycode == Key.F9)
            {
                debugChunks = !debugChunks;

                GD.Print("DEBUG CHUNKS: ", debugChunks);

                // 🔥 refresh visuel
                foreach (var pair in spawnedTiles)
                {
                    var pos = pair.Key;
                    var node = pair.Value;

                    var mesh = node.GetNodeOrNull<MeshInstance3D>("MeshInstance3D");

                    if (mesh == null)
                        continue;

                    var mat = mesh.MaterialOverride as StandardMaterial3D;

                    var chunkPos = WorldToChunk(new Vector3(pos.X, pos.Y, pos.Z));

                    var baseColor = GetTileColor(pos);

                    if (debugChunks && chunkPos != Vector3I.Zero)
                        mat.AlbedoColor = baseColor * 0.8f;
                    else
                        mat.AlbedoColor = baseColor;
                }
            }

            if (key.Keycode == Key.F10)
            {
                var m = sim.LastPathMetrics;
                GD.Print($"PATH METRICS req={m.Requests} hit={m.CacheHits} fail={m.Failures} us={m.TotalMicroseconds}");
            }

            if (key.Keycode == Key.F11)
            {
                GD.Print($"CHUNK METRICS active={_chunkVisuals.Count} needed={lastNeededChunkCount} last_us={lastChunkRefreshMicroseconds}");
            }
        }
    }

    void Step()
    {
        simFacade.Step();
    }

    void Render()
    {
        foreach (var pair in colonVisuals)
        {
            var colon = pair.Key;
            var node = pair.Value;

            var currentPos = new Vector3(colon.Position.X, colon.Position.Y, colon.Position.Z);
            var renderPos = currentPos;

            if (colon.Path != null && colon.Path.Count > 0)
            {
                var next = colon.Path[0];
                var nextPos = new Vector3(next.X, next.Y, next.Z);
                renderPos = currentPos.Lerp(nextPos, Mathf.Clamp(colon.MoveProgress, 0f, 1f));
            }

            var mesh = node.GetNode<MeshInstance3D>("MeshInstance3D");

            // 🎯 sélection
            mesh.MaterialOverride =
                selectionManager.SelectedColonists.Contains(colon)
                ? selectedMat
                : defaultMat;

            var tilePos = colon.Position;

            // 👁️ visibilité
            if (colon.OwnerId != 0) // ennemi
            {
                if (!sim.Vision.IsVisible(tilePos))
                {
                    node.Visible = false;
                    continue;
                }
            }

            node.Visible = true;

            // Smooth visual update to avoid jitter when many move commands arrive quickly.
            float maxStep = colon.MoveSpeed * lastFrameDeltaSeconds;
            node.Position = node.Position.MoveToward(renderPos, maxStep);
        }
    }

    void SpawnVisuals()
    {
        foreach (var colon in sim.World.CurrentMap.Colonists)
        {
            var instance = colonScene.Instantiate<Node3D>();
            AddChild(instance);
            
            instance.Position = colon.Position;

            colonVisuals[colon] = instance;
        }
    }

    void InitSimulation()
    {
        var bootstrap = new WorldBootstrap();
        sim.World = bootstrap.CreateDefaultWorld(ChunkLoadRadius, localPlayerId);
        sim.Init();
    }

    bool ShouldRefreshChunks(bool tickAdvanced)
    {
        return tickAdvanced;
    }

    void SpawnTiles()
    {
        var map = sim.World.CurrentMap;

        foreach (var chunkPair in map.Chunks)
        {
            var chunkPos = chunkPair.Key;
            var chunk = chunkPair.Value;

            for (int x = 0; x < Map.CHUNK_SIZE; x++)
            for (int y = 0; y < Map.CHUNK_SIZE; y++)
            for (int z = 0; z < Map.CHUNK_SIZE; z++)
            {
                var tile = chunk.Tiles[x, y, z];

                if (tile == null || !tile.Solid)
                    continue;

                var worldPos = new Vector3I(
                    chunkPos.X * Map.CHUNK_SIZE + x,
                    chunkPos.Y * Map.CHUNK_SIZE + y,
                    chunkPos.Z * Map.CHUNK_SIZE + z
                );

                // 🔥 évite double spawn
                if (spawnedTiles.ContainsKey(worldPos))
                    continue;

                var node = SpawnTile(worldPos, tile);
                spawnedTiles[worldPos] = node;
                
            }
        }
    }

    Node3D SpawnTile(Vector3I pos, Tile tile)
    {
        var body = new StaticBody3D();

        var mesh = new MeshInstance3D();
        mesh.Mesh = new BoxMesh();

        var collision = new CollisionShape3D();
        collision.Shape = new BoxShape3D();

        body.AddChild(mesh);
        body.AddChild(collision);

        body.Position = new Vector3(pos.X, pos.Y, pos.Z);

        var mat = new StandardMaterial3D();

        switch (tile.Type)
        {
            case "ground":
                mat.AlbedoColor = new Color(0.4f, 0.25f, 0.1f);
                break;

            case "platform":
                mat.AlbedoColor = new Color(0.4f, 0.24f, 0.11f);
                break;

            case "stairs":
                mat.AlbedoColor = new Color(0.38f, 0.22f, 0.1f);
                break;

            default:
                mat.AlbedoColor = new Color(1, 1, 1);
                break;
        }

        mesh.MaterialOverride = mat;

        AddChild(body);

        return body;
    }

    void UpdateSelectionRect()
    {
        if (selectionManager.IsDragging)
        {
            selectionRect.Visible = true;

            var rect = selectionManager.GetScreenRect();
            selectionRect.Position = rect.Position;
            selectionRect.Size = rect.Size;
        }
        else
        {
            selectionRect.Visible = false;
        }
    }

    Vector3I WorldToChunk(Vector3 pos)
    {
        int size = Map.CHUNK_SIZE;

        return new Vector3I(
            Mathf.FloorToInt(pos.X / size),
            Mathf.FloorToInt(pos.Y / size),
            Mathf.FloorToInt(pos.Z / size)
        );
    }

    static bool IsWorldTileSolidForPick(Map map, int wx, int wy, int wz)
    {
        var t = map.GetTile(new Vector3I(wx, wy, wz));
        return t != null && t.Solid;
    }

    static void AddCollisionTri(List<Vector3> v, Vector3 a, Vector3 b, Vector3 c)
    {
        v.Add(a);
        v.Add(b);
        v.Add(c);
    }

    void BuildAndAttachChunkTerrainPickBody(Chunk chunk, Vector3I chunkPos, Map map, ChunkVisual cv)
    {
        int cs = Map.CHUNK_SIZE;
        int ox = chunkPos.X * cs;
        int oy = chunkPos.Y * cs;
        int oz = chunkPos.Z * cs;
        var verts = new List<Vector3>(16384);
        // Même convention que le MultiMesh : cube 1×1×1 centré sur (wx, wy, wz), pas sur le coin bas.
        const float h = 0.5f;

        for (int lx = 0; lx < cs; lx++)
        for (int ly = 0; ly < cs; ly++)
        for (int lz = 0; lz < cs; lz++)
        {
            var tile = chunk.Tiles[lx, ly, lz];
            if (tile == null || !tile.Solid)
                continue;
            int wx = ox + lx, wy = oy + ly, wz = oz + lz;
            float cx = wx, cy = wy, cz = wz;
            float xmin = cx - h, xmax = cx + h;
            float ymin = cy - h, ymax = cy + h;
            float zmin = cz - h, zmax = cz + h;

            if (!IsWorldTileSolidForPick(map, wx + 1, wy, wz))
            {
                float x = xmax;
                AddCollisionTri(verts, new Vector3(x, ymin, zmin), new Vector3(x, ymin, zmax), new Vector3(x, ymax, zmax));
                AddCollisionTri(verts, new Vector3(x, ymin, zmin), new Vector3(x, ymax, zmax), new Vector3(x, ymax, zmin));
            }
            if (!IsWorldTileSolidForPick(map, wx - 1, wy, wz))
            {
                float x = xmin;
                AddCollisionTri(verts, new Vector3(x, ymin, zmax), new Vector3(x, ymin, zmin), new Vector3(x, ymax, zmin));
                AddCollisionTri(verts, new Vector3(x, ymin, zmax), new Vector3(x, ymax, zmin), new Vector3(x, ymax, zmax));
            }
            if (!IsWorldTileSolidForPick(map, wx, wy + 1, wz))
            {
                float y = ymax;
                AddCollisionTri(verts, new Vector3(xmin, y, zmin), new Vector3(xmax, y, zmin), new Vector3(xmax, y, zmax));
                AddCollisionTri(verts, new Vector3(xmin, y, zmin), new Vector3(xmax, y, zmax), new Vector3(xmin, y, zmax));
            }
            if (!IsWorldTileSolidForPick(map, wx, wy - 1, wz))
            {
                float y = ymin;
                AddCollisionTri(verts, new Vector3(xmin, y, zmax), new Vector3(xmax, y, zmax), new Vector3(xmax, y, zmin));
                AddCollisionTri(verts, new Vector3(xmin, y, zmax), new Vector3(xmax, y, zmin), new Vector3(xmin, y, zmin));
            }
            if (!IsWorldTileSolidForPick(map, wx, wy, wz + 1))
            {
                float z = zmax;
                AddCollisionTri(verts, new Vector3(xmin, ymin, z), new Vector3(xmax, ymin, z), new Vector3(xmax, ymax, z));
                AddCollisionTri(verts, new Vector3(xmin, ymin, z), new Vector3(xmax, ymax, z), new Vector3(xmin, ymax, z));
            }
            if (!IsWorldTileSolidForPick(map, wx, wy, wz - 1))
            {
                float z = zmin;
                AddCollisionTri(verts, new Vector3(xmin, ymax, z), new Vector3(xmax, ymax, z), new Vector3(xmax, ymin, z));
                AddCollisionTri(verts, new Vector3(xmin, ymax, z), new Vector3(xmax, ymin, z), new Vector3(xmin, ymin, z));
            }
        }

        if (verts.Count < 9)
            return;

        var shape = new ConcavePolygonShape3D { Data = verts.ToArray() };

        var body = new StaticBody3D
        {
            CollisionLayer = TerrainPhysicsPicker.TerrainPickCollisionMask,
            CollisionMask = 0
        };
        var col = new CollisionShape3D { Shape = shape };
        body.AddChild(col);
        AddChild(body);
        cv.TerrainPickBody = body;
    }

    void UpdateChunksAround(Vector3I playerChunk)
    {
        var map = sim.World.CurrentMap;

        for (int x = -ChunkLoadRadius; x <= ChunkLoadRadius; x++)
        for (int z = -ChunkLoadRadius; z <= ChunkLoadRadius; z++)
        {
            var chunkPos = new Vector3I(
                playerChunk.X + x,
                0,
                playerChunk.Z + z
            );

            var chunk = map.GetOrCreateChunk(chunkPos);

            SpawnChunkMesh(chunkPos, chunk);

            // 🔥 spawn debug (optionnel)
            if (debugChunks)
                SpawnChunkDebug(chunkPos, chunk);
        }
    }

    void SpawnChunkMesh(Vector3I chunkPos, Chunk chunk)
    {
        if (_chunkVisuals.ContainsKey(chunkPos))
            return;

        var cv = new ChunkVisual();

        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            InstanceCount = 0,
            Mesh = _tileCubeMesh
        };

        var solidInstance = new MultiMeshInstance3D();
        solidInstance.Multimesh = mm;
        AddChild(solidInstance);
        cv.Solid = solidInstance;

        List<Transform3D> solidTransforms = new();
        List<Color> solidColors = new();
        cv.SolidInstanceByWorld.Clear();

        for (int x = 0; x < Map.CHUNK_SIZE; x++)
        for (int y = 0; y < Map.CHUNK_SIZE; y++)
        for (int z = 0; z < Map.CHUNK_SIZE; z++)
        {
            var tile = chunk.Tiles[x, y, z];
            if (tile == null || !tile.Solid)
                continue;

            var worldPos = new Vector3(
                chunkPos.X * Map.CHUNK_SIZE + x,
                chunkPos.Y * Map.CHUNK_SIZE + y,
                chunkPos.Z * Map.CHUNK_SIZE + z
            );

            if (tile.Type == "ground" || tile.Type == "platform" || tile.Type == "stairs" || tile.Type == "stone" || tile.Type == "dirt")
            {
                var wi = new Vector3I((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
                cv.SolidInstanceByWorld[wi] = solidTransforms.Count;
                solidTransforms.Add(new Transform3D(Basis.Identity, worldPos));
                solidColors.Add(GetTileColor(tile.Type));
            }
            else if (tile.Type == "tree")
            {
                var wp = new Vector3I((int)worldPos.X, (int)worldPos.Y, (int)worldPos.Z);
                cv.SolidInstanceByWorld[wp] = solidTransforms.Count;
                solidTransforms.Add(new Transform3D(Basis.Identity, worldPos));
                solidColors.Add(GetTileColor("dirt"));
                var resourceInstance = _mapRenderer.InstantiateResource(tile.Type, wp);
                if (resourceInstance != null)
                {
                    AddChild(resourceInstance);
                    cv.Resources.Add(resourceInstance);
                    _treeTileByRoot[resourceInstance] = wp;
                }
            }
        }

        if (solidTransforms.Count > 0)
        {
            mm.InstanceCount = solidTransforms.Count;
            for (int i = 0; i < solidTransforms.Count; i++)
            {
                mm.SetInstanceTransform(i, solidTransforms[i]);
                mm.SetInstanceColor(i, solidColors[i]);
            }
            solidInstance.Visible = true;
        }
        else
            solidInstance.Visible = false;

        BuildAndAttachChunkTerrainPickBody(chunk, chunkPos, sim.World.CurrentMap, cv);

        _chunkVisuals[chunkPos] = cv;
        _terrainMeshLayoutVersion++;
        _multimeshPeelStateToken = ulong.MaxValue;
        ApplyChunkFog(cv, chunkPos);
    }

    void UnloadFarChunks()
    {
        List<Vector3I> toRemove = new();

        foreach (var pair in _chunkVisuals)
        {
            if (!currentNeededChunks.Contains(pair.Key))
            {
                UnregisterTreeRoots(pair.Value);
                pair.Value.FreeVisual();
                toRemove.Add(pair.Key);
            }
        }

        if (toRemove.Count > 0)
        {
            _terrainMeshLayoutVersion++;
            _multimeshPeelStateToken = ulong.MaxValue;
        }

        foreach (var pos in toRemove)
            _chunkVisuals.Remove(pos);
    }

    void SpawnChunkDebug(Vector3I chunkPos, Chunk chunk)
    {
        for (int x = 0; x < Map.CHUNK_SIZE; x++)
        for (int y = 0; y < Map.CHUNK_SIZE; y++)
        for (int z = 0; z < Map.CHUNK_SIZE; z++)
        {
            var tile = chunk.Tiles[x, y, z];

            if (tile == null || !tile.Solid)
                continue;

            var worldPos = new Vector3I(
                chunkPos.X * Map.CHUNK_SIZE + x,
                chunkPos.Y * Map.CHUNK_SIZE + y,
                chunkPos.Z * Map.CHUNK_SIZE + z
            );

            if (spawnedTiles.ContainsKey(worldPos))
                continue;

            var node = SpawnTile(worldPos, tile);
            spawnedTiles[worldPos] = node;
        }
    }

    void UpdateChunksAroundColonists()
    {
        var map = sim.World.CurrentMap;
        currentNeededChunks.Clear();

        int r = ChunkLoadRadius + Mathf.Max(0, ChunkPreloadMargin);

        void AddColumnNeighborhoodXZ(int chunkX, int chunkZ)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dz = -r; dz <= r; dz++)
                currentNeededChunks.Add(new Vector3I(chunkX + dx, 0, chunkZ + dz));
        }

        foreach (var colon in map.Colonists)
        {
            if (colon.OwnerId != localPlayerId)
                continue;

            int ox = Mathf.FloorToInt((float)colon.Position.X / Map.CHUNK_SIZE);
            int oz = Mathf.FloorToInt((float)colon.Position.Z / Map.CHUNK_SIZE);
            AddColumnNeighborhoodXZ(ox, oz);
        }

        // Même colonne Y=0 que la génération : le monde jouable tient dans un chunk vertical ;
        // sans ça, avancer la caméra (WASD) vers le bord laisse un vide si le colon est plus loin.
        if (cameraPivot != null)
        {
            Vector3 gp = cameraPivot.GlobalPosition;
            int cx = Mathf.FloorToInt(gp.X / Map.CHUNK_SIZE);
            int cz = Mathf.FloorToInt(gp.Z / Map.CHUNK_SIZE);
            AddColumnNeighborhoodXZ(cx, cz);
        }

        foreach (var chunkPos in currentNeededChunks)
            SpawnChunkMesh(chunkPos, map.GetOrCreateChunk(chunkPos));
    }

    bool IsInCameraView(Vector3I pos)
    {
        var worldPos = new Vector3(pos.X, pos.Y, pos.Z);

        var screenPos = camera.UnprojectPosition(worldPos);

        var rect = GetViewport().GetVisibleRect();

        return screenPos.X >= 0 && screenPos.X <= rect.Size.X &&
            screenPos.Y >= 0 && screenPos.Y <= rect.Size.Y;
    }


    bool IsChunkVisible(Vector3I chunkPos)
    {
        var center = new Vector3I(
            chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
            Map.ColonistWalkY,
            chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
        );

        bool discovered = sim.Vision.IsDiscovered(center);
        bool visible = sim.Vision.IsVisible(center);
        bool inCamera = IsInCameraView(center);

        if (!discovered)
            return false;

        if (!visible || !inCamera)
            return true; // visible sombre

        return true;
    }

    void UpdateChunkVisibility()
    {
        foreach (var pair in _chunkVisuals)
            ApplyChunkFog(pair.Value, pair.Key);
    }

    void ApplyChunkFog(ChunkVisual cv, Vector3I chunkPos)
    {
        EvaluateChunkFog(chunkPos, out bool discovered, out bool visible);

        if (cv.Solid != null)
        {
            if (!discovered)
                cv.Solid.Visible = false;
            else
            {
                cv.Solid.Visible = cv.Solid.Multimesh.InstanceCount > 0;
                var mat = cv.Solid.MaterialOverride as StandardMaterial3D;
                if (mat == null)
                {
                    // Même pipeline que le mesh : sinon les vertex colors du MultiMesh sont ignorées → tout gris.
                    mat = new StandardMaterial3D
                    {
                        VertexColorUseAsAlbedo = true,
                        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
                    };
                    cv.Solid.MaterialOverride = mat;
                }
                // Teinte « hors ligne de vue » : assombrir sans tuer les marrons (éviter le gris boue).
                mat.AlbedoColor = visible ? Colors.White : new Color(0.5f, 0.42f, 0.36f);
            }
        }

        foreach (var r in cv.Resources)
            r.Visible = discovered;
    }

    void EvaluateChunkFog(Vector3I chunkPos, out bool discovered, out bool visible)
    {
        discovered = false;
        visible = false;

        int startX = chunkPos.X * Map.CHUNK_SIZE;
        int startZ = chunkPos.Z * Map.CHUNK_SIZE;
        int endX = startX + Map.CHUNK_SIZE - 1;
        int endZ = startZ + Map.CHUNK_SIZE - 1;
        int midX = startX + Map.CHUNK_SIZE / 2;
        int midZ = startZ + Map.CHUNK_SIZE / 2;

        int sy = Map.ColonistWalkY;
        Vector3I[] samples = new Vector3I[]
        {
            new(startX, sy, startZ),
            new(endX, sy, startZ),
            new(startX, sy, endZ),
            new(endX, sy, endZ),
            new(midX, sy, midZ),
        };

        foreach (var sample in samples)
        {
            if (sim.Vision.IsDiscovered(sample))
                discovered = true;
            if (sim.Vision.IsVisible(sample))
                visible = true;

            if (discovered && visible)
                return;
        }
    }

    private void OnJobStarted(int colonistId, Vector3I target)
    {
        GD.Print($"[Godot] Colon {colonistId} commence un travail sur {target}.");
    }

    private void OnJobCompleted(int colonistId, Vector3I target)
    {
        GD.Print($"[Godot] Colon {colonistId} a terminé le travail sur {target}.");

        RefreshChunkAtPosition(target);

        // // Rafraîchir le tile dans Godot
        // if (spawnedTiles.TryGetValue(target, out var tileNode))
        // {
        //     tileNode.QueueFree();
        //     spawnedTiles.Remove(target);
        // }
    }

    private void RefreshChunkAtPosition(Vector3I worldPosition)
    {
        var chunkPos = WorldToChunk(new Vector3(worldPosition.X, worldPosition.Y, worldPosition.Z));

        // Si le chunk existe, le rafraîchir
        if (_chunkVisuals.TryGetValue(chunkPos, out var cv))
        {
            UnregisterTreeRoots(cv);
            cv.FreeVisual();
            _chunkVisuals.Remove(chunkPos);
            var chunk = sim.World.CurrentMap.GetOrCreateChunk(chunkPos);
            SpawnChunkMesh(chunkPos, chunk);
            if (_verticalSliceTerrainActive)
                ApplyMultimeshOcclusionPeels();
        }
    }

    void SetSelectionTargetKind(WorldSelectionTargetKind kind)
    {
        _selectionTargetKind = kind;
        if (selectionManager != null)
            selectionManager.TargetKind = kind;
        if (kind != WorldSelectionTargetKind.Trees)
            _selectedTreeTile = null;
        if (kind != WorldSelectionTargetKind.TerrainTiles)
            _selectedTerrainTiles.Clear();
        UpdateSelectionTargetUi();
    }

    void UpdateSelectionTargetUi()
    {
        if (selectionManager != null)
            selectionManager.TargetKind = _selectionTargetKind;

        if (_selectionModeLabel != null)
        {
            _selectionModeLabel.Text = _selectionTargetKind switch
            {
                WorldSelectionTargetKind.Colonists => "Cible : Colons (clic / cadre)",
                WorldSelectionTargetKind.Trees => "Cible : Arbres — clic sur un arbre",
                WorldSelectionTargetKind.TerrainTiles => "Cible : Blocs — clic = 1 · Shift+clic = + · Q/Alt+clic = fond · V = coupe terrain",
                _ => "Cible : —"
            };
        }

        if (_treeTargetLabel != null)
        {
            _treeTargetLabel.Text = _selectedTreeTile.HasValue
                ? $"Arbre sélectionné : {_selectedTreeTile.Value}"
                : "Arbre : (aucun)";
        }

        if (_terrainTileLabel != null)
        {
            if (_selectedTerrainTiles.Count == 0)
                _terrainTileLabel.Text = "Blocs : (aucun)";
            else if (_selectedTerrainTiles.Count == 1)
            {
                Vector3I only = default;
                foreach (var v in _selectedTerrainTiles)
                {
                    only = v;
                    break;
                }
                _terrainTileLabel.Text = $"Blocs : 1 — {only}";
            }
            else
                _terrainTileLabel.Text = $"Blocs : {_selectedTerrainTiles.Count} sélectionnés";
        }

        if (_btnCutTree != null)
            _btnCutTree.Disabled = !_selectedTreeTile.HasValue;

        RefreshMineStoneButtonState();
    }

    void RefreshMineStoneButtonState()
    {
        if (_btnMineStone == null || sim?.World?.CurrentMap == null)
            return;

        bool canMine = false;
        if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles && _selectedTerrainTiles.Count > 0)
        {
            foreach (var p in _selectedTerrainTiles)
            {
                if (TerrainMineRules.IsMineableBlock(sim.World.CurrentMap.GetTile(p)))
                {
                    canMine = true;
                    break;
                }
            }
        }
        _btnMineStone.Disabled = !canMine;
    }

    void ComputeTerrainOcclusionHiddenSet()
    {
        _terrainOcclusionHidden.Clear();
        if (camera == null || sim?.World?.CurrentMap == null)
            return;

        // Alt est souvent capturé par Windows : Q = percée fiable. Alt physique en complément.
        bool wantPeel = Input.IsPhysicalKeyPressed(Key.Q) || Input.IsPhysicalKeyPressed(Key.Alt);
        if (!wantPeel)
            return;

        var from = camera.GlobalPosition;
        var mouse = camera.GetViewport().GetMousePosition();
        var dir = camera.ProjectRayNormal(mouse).Normalized();

        TerrainRayDda.CollectOrderedSolidCellsAlongRay(sim.World.CurrentMap, TerrainOcclusionRayMax, from, dir, _raySolidBuffer, 0.35f);
        if (_raySolidBuffer.Count < 2)
            return;

        var deepest = _raySolidBuffer[^1];
        var focusCenter = new Vector3(deepest.X + 0.5f, deepest.Y + 0.5f, deepest.Z + 0.5f);
        if (from.DistanceTo(focusCenter) > TerrainOcclusionMaxCameraDistance)
            return;

        float cy = from.Y;
        if (cy < TerrainOcclusionCameraMinY || cy > TerrainOcclusionCameraMaxY)
            return;

        for (int i = 0; i < _raySolidBuffer.Count - 1; i++)
            _terrainOcclusionHidden.Add(_raySolidBuffer[i]);
    }

    /// <summary>Repli sans physique : solides rencontrés le long du rayon (ordre DDA). Filtré par la coupe V si active.</summary>
    List<Vector3I> BuildTerrainFallbackPickList(Vector3 from, Vector3 dir, float rayLen)
    {
        TerrainRayDda.CollectOrderedSolidCellsAlongRay(sim.World.CurrentMap, TerrainOcclusionRayMax, from, dir, _raySolidBuffer, 0f, rayLen);
        if (!_verticalSliceTerrainActive)
            return _raySolidBuffer;
        _raySolidPickBuffer.Clear();
        foreach (var c in _raySolidBuffer)
        {
            if (!IsTerrainCellHiddenByVerticalSlice(c))
                _raySolidPickBuffer.Add(c);
        }
        return _raySolidPickBuffer;
    }

    bool IsTerrainCellHiddenByVerticalSlice(Vector3I cell)
    {
        if (!_verticalSliceTerrainActive || camera == null)
            return false;

        Vector3 c = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        Vector3 camPos = camera.GlobalPosition;

        bool hideVertical = false;
        if (VerticalSliceUseVerticalCut)
        {
            Vector3 lookWorld = -camera.GlobalTransform.Basis.Z;
            Vector3 n = new Vector3(lookWorld.X, 0f, lookWorld.Z);
            if (n.LengthSquared() < 1e-8f)
                n = Vector3.Right;
            else
                n = n.Normalized();
            float s = (c - camPos).Dot(n);

            if (VerticalSliceHideBehindCameraSide)
            {
                hideVertical = s < 0f;
                if (VerticalSliceForwardCutDepth > 0f && s > VerticalSliceForwardCutDepth)
                    hideVertical = true;
            }
            else
            {
                hideVertical = s > 0f;
                if (VerticalSliceForwardCutDepth > 0f && s < -VerticalSliceForwardCutDepth)
                    hideVertical = true;
            }
        }

        bool hidePeel = false;
        if (VerticalSlicePeelGroundWhenCameraLow && camPos.Y < VerticalSliceGroundPeelBelowY)
        {
            float peelY = camPos.Y - VerticalSliceGroundPeelMargin;
            hidePeel = VerticalSliceGroundPeelHideAbove ? c.Y > peelY : c.Y < peelY;
        }

        bool hideScreenBottom = false;
        if (VerticalSliceScreenBottomHideFraction > 1e-4f)
        {
            Vector3 forward = -camera.GlobalTransform.Basis.Z;
            if ((c - camPos).Dot(forward) > 0.02f)
            {
                if (VerticalSliceScreenBottomFastCameraApprox)
                {
                    Transform3D inv = camera.GlobalTransform.AffineInverse();
                    Vector3 lp = inv * c;
                    float depth = -lp.Z;
                    if (depth > 0.02f)
                    {
                        float tanHalf = Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f));
                        float yNorm = lp.Y / (depth * tanHalf + 1e-6f);
                        hideScreenBottom = yNorm <= 2f * VerticalSliceScreenBottomHideFraction - 1f;
                    }
                }
                else
                {
                    Vector2 sp = camera.UnprojectPosition(c);
                    float vh = GetViewport().GetVisibleRect().Size.Y;
                    if (vh > 1e-4f && sp.Y >= vh * (1f - VerticalSliceScreenBottomHideFraction))
                        hideScreenBottom = true;
                }
            }
        }

        bool verticalForDisplay = VerticalSliceInvertVisibility ? !hideVertical : hideVertical;
        return verticalForDisplay || hidePeel || hideScreenBottom;
    }

    static int QuantizeForSliceToken(float v, float unitsPerBucket)
    {
        return Mathf.RoundToInt(v * unitsPerBucket);
    }

    ulong ComputeMultimeshPeelStateToken()
    {
        unchecked
        {
            ulong t = _verticalSliceTerrainActive ? 0x9E3779B185EBCA87UL : 0xC6A4A7935BD1E995UL;
            t ^= (ulong)(uint)_terrainMeshLayoutVersion * 0x85EBCA77C2B2AE63UL;

            t ^= VerticalSliceInvertVisibility ? 3UL : 1UL;
            t ^= VerticalSliceUseVerticalCut ? 7UL : 2UL;
            t ^= VerticalSliceHideBehindCameraSide ? 11UL : 4UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceForwardCutDepth);
            t ^= VerticalSlicePeelGroundWhenCameraLow ? 13UL : 8UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceGroundPeelBelowY);
            t ^= VerticalSliceGroundPeelHideAbove ? 17UL : 9UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceGroundPeelMargin);
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceScreenBottomHideFraction);
            t ^= VerticalSliceScreenBottomFastCameraApprox ? 19UL : 14UL;

            foreach (var cell in _terrainOcclusionHidden)
                t = t * 0xD1B54A32D192ED03UL + (ulong)cell.GetHashCode();

            if (_verticalSliceTerrainActive && camera != null)
            {
                Vector3 p = camera.GlobalPosition;
                // Quantification plus grossière = moins de recalculs peel par frame (moins de lag en coupe V).
                t ^= (ulong)(uint)QuantizeForSliceToken(p.X, 14f) << 8;
                t ^= (ulong)(uint)QuantizeForSliceToken(p.Y, 14f) << 16;
                t ^= (ulong)(uint)QuantizeForSliceToken(p.Z, 14f) << 24;
                Vector3 lz = -camera.GlobalTransform.Basis.Z;
                Vector3 horiz = new Vector3(lz.X, 0f, lz.Z);
                if (horiz.LengthSquared() > 1e-10f)
                {
                    horiz = horiz.Normalized();
                    t ^= (ulong)(uint)QuantizeForSliceToken(horiz.X, 180f) << 4;
                    t ^= (ulong)(uint)QuantizeForSliceToken(horiz.Z, 180f) << 12;
                }
            }

            return t;
        }
    }

    void ApplyMultimeshOcclusionPeels()
    {
        _slicePeelSkippedThisFrame = true;
        var affectedChunks = new HashSet<Vector3I>();
        bool sliceOn = _verticalSliceTerrainActive;
        bool needFullPass = sliceOn || _verticalSliceTerrainWasActive;
        _verticalSliceTerrainWasActive = sliceOn;

        ulong peelToken = ComputeMultimeshPeelStateToken();
        if (peelToken == _multimeshPeelStateToken)
        {
            _terrainOcclusionHiddenPrev.Clear();
            foreach (var x in _terrainOcclusionHidden)
                _terrainOcclusionHiddenPrev.Add(x);
            return;
        }

        _slicePeelSkippedThisFrame = false;
        _multimeshPeelStateToken = peelToken;

        if (needFullPass)
        {
            foreach (var cp in _chunkVisuals.Keys)
                affectedChunks.Add(cp);
        }
        else
        {
            foreach (var c in _terrainOcclusionHidden)
                affectedChunks.Add(WorldToChunk(new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f)));
            foreach (var c in _terrainOcclusionHiddenPrev)
                affectedChunks.Add(WorldToChunk(new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f)));
        }

        var tiny = new Vector3(1e-4f, 1e-4f, 1e-4f);

        foreach (var cp in affectedChunks)
        {
            if (!_chunkVisuals.TryGetValue(cp, out var cv) || cv.Solid?.Multimesh == null)
                continue;
            var mm = cv.Solid.Multimesh;
            if (mm.InstanceCount == 0 || cv.SolidInstanceByWorld.Count == 0)
                continue;

            foreach (var kv in cv.SolidInstanceByWorld)
            {
                bool hide = _terrainOcclusionHidden.Contains(kv.Key) || IsTerrainCellHiddenByVerticalSlice(kv.Key);
                var pos = new Vector3(kv.Key.X, kv.Key.Y, kv.Key.Z);
                var basis = hide ? Basis.Identity.Scaled(tiny) : Basis.Identity;
                mm.SetInstanceTransform(kv.Value, new Transform3D(basis, pos));
            }
        }

        _terrainOcclusionHiddenPrev.Clear();
        foreach (var x in _terrainOcclusionHidden)
            _terrainOcclusionHiddenPrev.Add(x);
    }

    int ComputeTerrainSelectionSetHash()
    {
        int h = _selectedTerrainTiles.Count;
        foreach (var v in _selectedTerrainTiles)
            h = unchecked(h * -1521134295 + v.GetHashCode());
        return h;
    }

    int ComputeTerrainHoverHighlightHash()
    {
        if (_selectionTargetKind != WorldSelectionTargetKind.TerrainTiles || !TerrainHoverHighlightEnabled)
            return -173_173;
        return _terrainHoverCell?.GetHashCode() ?? -1;
    }

    void UpdateTerrainHoverCell()
    {
        if (_selectionTargetKind != WorldSelectionTargetKind.TerrainTiles
            || !TerrainHoverHighlightEnabled
            || camera == null
            || sim?.World?.CurrentMap == null)
        {
            _terrainHoverCell = null;
            return;
        }

        if (TryGetTerrainPickedCellFromScreen(out Vector3I cell, respectPeelModifierKeys: true))
            _terrainHoverCell = cell;
        else
            _terrainHoverCell = null;
    }

    /// <summary>Même résolution que le clic blocs (physique + repli DDA, Q/Alt = fond).</summary>
    bool TryGetTerrainPickedCellFromScreen(out Vector3I pick, bool respectPeelModifierKeys)
    {
        pick = default;
        if (camera == null || sim?.World?.CurrentMap == null)
            return false;
        Vector2 vpMouse = camera.GetViewport().GetMousePosition();
        if (TerrainPickUsePixelCenterRay)
            vpMouse += new Vector2(0.5f, 0.5f);
        return TryGetTerrainPickedCellViewport(vpMouse, out pick, respectPeelModifierKeys);
    }

    bool TryGetTerrainPickedCellViewport(Vector2 vpMouse, out Vector3I pick, bool respectPeelModifierKeys)
    {
        pick = default;
        var from = camera.ProjectRayOrigin(vpMouse);
        var dir = camera.ProjectRayNormal(vpMouse).Normalized();
        float pickRayLen = Mathf.Max(TerrainOcclusionRayMax, TerrainPickMaxDistance + 120f);
        bool wantPeel = respectPeelModifierKeys
            && (Input.IsPhysicalKeyPressed(Key.Q) || Input.IsPhysicalKeyPressed(Key.Alt));

        bool pickedPhysics = TerrainPickUsePhysicsRaycast && !wantPeel
            && TerrainPhysicsPicker.TryPickCell(
                camera.GetWorld3D().DirectSpaceState,
                sim.World.CurrentMap,
                from,
                dir,
                pickRayLen,
                TerrainPhysicsPicker.TerrainPickCollisionMask,
                c => !_verticalSliceTerrainActive || !IsTerrainCellHiddenByVerticalSlice(c),
                out pick);

        if (!pickedPhysics)
        {
            List<Vector3I> ordered = BuildTerrainFallbackPickList(from, dir, pickRayLen);
            if (ordered.Count == 0)
                return false;
            pick = wantPeel && ordered.Count >= 2 ? ordered[^1] : ordered[0];
        }

        var focusCenter = new Vector3(pick.X + 0.5f, pick.Y + 0.5f, pick.Z + 0.5f);
        float maxPick = Mathf.Max(TerrainPickMaxDistance, pickRayLen + 64f);
        return from.DistanceTo(focusCenter) <= maxPick;
    }

    void ApplyTerrainSelectionHighlights()
    {
        if (sim?.World?.CurrentMap == null)
            return;
        int selHash = ComputeTerrainSelectionSetHash();
        int hoverHash = ComputeTerrainHoverHighlightHash();
        bool hl = TerrainSelectionHighlightEnabled;
        bool hoverOn = TerrainHoverHighlightEnabled;
        if (selHash == _lastTerrainHighlightSelectionHash
            && hoverHash == _lastTerrainHighlightHoverHash
            && _terrainMeshLayoutVersion == _lastTerrainHighlightLayoutVersion
            && hl == _lastTerrainHighlightEnabled
            && hoverOn == _lastTerrainHoverHighlightEnabled)
            return;

        _lastTerrainHighlightSelectionHash = selHash;
        _lastTerrainHighlightHoverHash = hoverHash;
        _lastTerrainHighlightLayoutVersion = _terrainMeshLayoutVersion;
        _lastTerrainHighlightEnabled = hl;
        _lastTerrainHoverHighlightEnabled = hoverOn;

        var map = sim.World.CurrentMap;
        float mix = Mathf.Clamp(TerrainSelectionHighlightAmount, 0f, 1f);
        float hoverMixAmt = Mathf.Clamp(TerrainHoverHighlightAmount, 0f, 1f);
        float mineHoverMixAmt = Mathf.Clamp(TerrainMineHoverHighlightAmount, 0f, 1f);
        foreach (var pair in _chunkVisuals)
        {
            var cv = pair.Value;
            if (cv.Solid?.Multimesh == null || cv.SolidInstanceByWorld.Count == 0)
                continue;
            var mm = cv.Solid.Multimesh;
            foreach (var inst in cv.SolidInstanceByWorld)
            {
                Vector3I w = inst.Key;
                int idx = inst.Value;
                var tile = map.GetTile(w);
                Color baseC = tile != null ? GetTileColor(tile.Type) : Colors.White;
                Color c = baseC;

                if (TerrainHoverHighlightEnabled
                    && _selectionTargetKind == WorldSelectionTargetKind.TerrainTiles
                    && _terrainHoverCell.HasValue
                    && w == _terrainHoverCell.Value)
                {
                    bool mine = tile != null && TerrainMineRules.IsMineableBlock(tile);
                    if (mine)
                        c = c.Lerp(TerrainMineHoverHighlightMix, mineHoverMixAmt);
                    else
                        c = c.Lerp(TerrainHoverHighlightMix, hoverMixAmt);
                }

                if (hl && _selectedTerrainTiles.Contains(w))
                    c = c.Lerp(TerrainSelectionHighlightMix, mix);

                mm.SetInstanceColor(idx, c);
            }
        }
    }

    void TryPickTerrainTileAtScreen()
    {
        bool shift = Input.IsPhysicalKeyPressed(Key.Shift);
        if (!TryGetTerrainPickedCellFromScreen(out Vector3I pick, respectPeelModifierKeys: true))
        {
            if (!shift)
                _selectedTerrainTiles.Clear();
            UpdateSelectionTargetUi();
            return;
        }

        if (shift)
        {
            if (_selectedTerrainTiles.Contains(pick))
                _selectedTerrainTiles.Remove(pick);
            else
                _selectedTerrainTiles.Add(pick);
        }
        else
        {
            _selectedTerrainTiles.Clear();
            _selectedTerrainTiles.Add(pick);
        }

        UpdateSelectionTargetUi();
    }

    void TryPickTreeAtScreen()
    {
        Vector2 vpMouse = camera.GetViewport().GetMousePosition();
        var from = camera.ProjectRayOrigin(vpMouse);
        var to = from + camera.ProjectRayNormal(vpMouse) * 500f;
        var query = PhysicsRayQueryParameters3D.Create(from, to);
        query.CollisionMask = 1 << 1;
        var hit = camera.GetWorld3D().DirectSpaceState.IntersectRay(query);
        if (hit.Count == 0)
        {
            _selectedTreeTile = null;
            UpdateSelectionTargetUi();
            return;
        }

        var obj = hit["collider"].AsGodotObject() as Node;
        while (obj != null)
        {
            if (obj is Node3D n3 && _treeTileByRoot.TryGetValue(n3, out var tile))
            {
                var t = sim.World.CurrentMap.GetTile(tile);
                if (t != null && t.Type == "tree")
                {
                    _selectedTreeTile = tile;
                    UpdateSelectionTargetUi();
                    return;
                }
                break;
            }
            obj = obj.GetParent();
        }

        _selectedTreeTile = null;
        UpdateSelectionTargetUi();
    }

    void UnregisterTreeRoots(ChunkVisual cv)
    {
        foreach (var r in cv.Resources)
            _treeTileByRoot.Remove(r);
    }

    void OnCutSelectedTreePressed()
    {
        if (sim?.World == null || !_selectedTreeTile.HasValue)
            return;

        var tilePos = _selectedTreeTile.Value;
        var t = sim.World.CurrentMap.GetTile(tilePos);
        if (t == null || t.Type != "tree")
        {
            _selectedTreeTile = null;
            UpdateSelectionTargetUi();
            return;
        }

        if (sim.jobBoard.HasActiveJobOnTarget(tilePos, JobType.CutTree))
            return;

        sim.jobBoard.AddJob(new SimJob
        {
            Type = JobType.CutTree,
            Priority = JobPriority.Normal,
            Target = tilePos
        });

        GD.Print($"[Jobs] Coupe d'arbre ajoutée à la file : {tilePos} (total actifs : {sim.jobBoard.ActiveJobCount})");

        _selectedTreeTile = null;
        UpdateSelectionTargetUi();
    }

    void OnMineStonePressed()
    {
        if (sim?.World == null || _selectedTerrainTiles.Count == 0)
            return;

        int added = 0;
        foreach (var tilePos in _selectedTerrainTiles)
        {
            var t = sim.World.CurrentMap.GetTile(tilePos);
            if (!TerrainMineRules.IsMineableBlock(t))
                continue;
            if (sim.jobBoard.HasActiveJobOnTarget(tilePos, JobType.MineStone))
                continue;

            sim.jobBoard.AddJob(new SimJob
            {
                Type = JobType.MineStone,
                Priority = JobPriority.Normal,
                Target = tilePos
            });
            added++;
        }

        if (added > 0)
            GD.Print($"[Jobs] {added} job(s) minage ajouté(s) (file : {sim.jobBoard.ActiveJobCount})");

        _selectedTerrainTiles.Clear();
        UpdateSelectionTargetUi();
    }

    void RefreshJobsQueueUi()
    {
        if (_jobsQueueLabel == null || sim?.jobBoard == null)
            return;

        sim.jobBoard.CopyActiveJobs(_jobsUiBuffer);
        if (_jobsUiBuffer.Count == 0)
        {
            _jobsQueueLabel.Text = "Travail en attente : aucun";
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("Travail en attente (").Append(_jobsUiBuffer.Count).Append(") :\n");
        const int maxLines = 6;
        int shown = 0;
        foreach (var j in _jobsUiBuffer)
        {
            if (shown >= maxLines)
            {
                sb.Append("…");
                break;
            }
            string state = j.Status == JobStatus.Reserved ? "en cours" : "à faire";
            string line = j.Type switch
            {
                JobType.CutTree => $"• Coupe arbre @ {j.Target} ({state})",
                JobType.MineStone => $"• Mine bloc @ {j.Target} ({state})",
                _ => $"• {j.Type} @ {j.Target} ({state})"
            };
            sb.AppendLine(line);
            shown++;
        }
        _jobsQueueLabel.Text = sb.ToString().TrimEnd();
    }

    void RefreshTreeJobOverlays()
    {
        if (sim?.jobBoard == null)
            return;

        foreach (var kv in _treeTileByRoot)
        {
            var root = kv.Key;
            if (!GodotObject.IsInstanceValid(root))
                continue;

            bool pending = sim.jobBoard.HasActiveJobOnTarget(kv.Value, JobType.CutTree);
            var trunk = root.GetNodeOrNull<MeshInstance3D>("Trunk");
            var foliage = root.GetNodeOrNull<MeshInstance3D>("Foliage");
            if (trunk == null)
                continue;

            if (pending)
            {
                if (trunk.MaterialOverride == null)
                {
                    var mt = trunk.GetActiveMaterial(0) as StandardMaterial3D;
                    if (mt != null)
                    {
                        var dup = (StandardMaterial3D)mt.Duplicate();
                        dup.EmissionEnabled = true;
                        dup.Emission = new Color(1f, 0.42f, 0.08f);
                        dup.EmissionEnergyMultiplier = 0.55f;
                        trunk.MaterialOverride = dup;
                    }
                }
                if (foliage != null && foliage.MaterialOverride == null)
                {
                    var mf = foliage.GetActiveMaterial(0) as StandardMaterial3D;
                    if (mf != null)
                    {
                        var dupf = (StandardMaterial3D)mf.Duplicate();
                        dupf.EmissionEnabled = true;
                        dupf.Emission = new Color(0.4f, 0.9f, 0.2f);
                        dupf.EmissionEnergyMultiplier = 0.35f;
                        foliage.MaterialOverride = dupf;
                    }
                }
            }
            else
            {
                trunk.MaterialOverride = null;
                if (foliage != null)
                    foliage.MaterialOverride = null;
            }
        }
    }

    Color GetTileColor(Vector3I pos)
    {
        var tile = sim.World.CurrentMap.GetTile(pos);
        return tile == null ? Colors.White : GetTileColor(tile.Type);
    }

    Color GetTileColor(string tileType)
    {
        return tileType switch
        {
            "ground" => new Color(0.4f, 0.25f, 0.1f),
            "dirt" => new Color(0.35f, 0.22f, 0.12f),
            "stone" => new Color(0.45f, 0.45f, 0.48f),
            "tree" => new Color(0.15f, 0.45f, 0.12f),
            "platform" => new Color(0.4f, 0.24f, 0.11f),
            "stairs" => new Color(0.38f, 0.22f, 0.1f),
            "air" => new Color(0.1f, 0.1f, 0.8f),
            _ => new Color(1, 1, 1)
        };
    }

    sealed class ChunkVisual
    {
        public MultiMeshInstance3D Solid;
        /// <summary>Corps statique calque 1 (terrain) pour IntersectRay précis.</summary>
        public StaticBody3D TerrainPickBody;
        public readonly List<Node3D> Resources = new();
        public readonly Dictionary<Vector3I, int> SolidInstanceByWorld = new();

        public void FreeVisual()
        {
            Solid?.QueueFree();
            TerrainPickBody?.QueueFree();
            TerrainPickBody = null;
            foreach (var r in Resources)
                r.QueueFree();
            Resources.Clear();
        }
    }
}