using Godot;
using System.Collections.Generic;
using System.Diagnostics;

public partial class Game : Node3D
{
    const int CHUNK_REFRESH_INTERVAL_TICKS = 1;
    const int CHUNK_VISIBILITY_INTERVAL_TICKS = 1;

    Simulation sim = new Simulation();
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

    int chunkRadius = 2;
    int chunkPreloadMargin = 1;
    bool debugChunks = false;

    HashSet<Vector3I> currentNeededChunks = new();

    Vector3I lastPlayerChunk = new Vector3I(int.MinValue, int.MinValue, int.MinValue);
    Dictionary<Vector3I, MultiMeshInstance3D> chunkMeshes = new();
    int lastChunkRefreshTick = -1;
    int lastChunkVisibilityTick = -1;
    long lastChunkRefreshMicroseconds = 0;
    int lastNeededChunkCount = 0;
    float lastFrameDeltaSeconds = 0f;

    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = GetNode<Camera3D>("CameraPivot/Camera3D");

        selectedMat = new StandardMaterial3D();
        selectedMat.AlbedoColor = new Color(0, 1, 0);

        defaultMat = new StandardMaterial3D();
        defaultMat.AlbedoColor = new Color(1, 0, 0);

        selectionRect = GetNode<ColorRect>("UI/SelectionRect");

        InitSimulation();
        simFacade = new SimulationFacade(sim, lockstep);
        SpawnVisuals();
        spawnedTiles.Clear();
        //SpawnTiles();replace by updatechunks
        UpdateChunksAroundColonists();

        cameraController = new CameraController(cameraPivot, camera);
        selectionManager = new SelectionManager(camera, colonVisuals, localPlayerId);
        unitController = new UnitController(sim, lockstep, camera, GetNode<Node>("UI"));
    }

    public override void _Process(double delta)
    {
        if (sim.World == null)
            return;

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
        }
        if (tickAdvanced && sim.Tick % CHUNK_VISIBILITY_INTERVAL_TICKS == 0 && sim.Tick != lastChunkVisibilityTick)
        {
            UpdateChunkVisibility();
            lastChunkVisibilityTick = sim.Tick;
        }
        

        Render();
        cameraController.Update(delta);
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
                GD.Print($"CHUNK METRICS active={chunkMeshes.Count} needed={lastNeededChunkCount} last_us={lastChunkRefreshMicroseconds}");
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

            var currentPos = new Vector3(colon.X, colon.Y, colon.Z);
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

            var tilePos = new Vector3I(colon.X, colon.Y, colon.Z);

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
            
            instance.Position = new Vector3(colon.X, colon.Y, colon.Z);

            colonVisuals[colon] = instance;
        }
    }

    void InitSimulation()
    {
        var bootstrap = new WorldBootstrap();
        sim.World = bootstrap.CreateDefaultWorld(chunkRadius, localPlayerId);
        sim.Init();
    }

    bool ShouldRefreshChunks(bool tickAdvanced)
    {
        if (!tickAdvanced)
            return false;

        if (sim.Tick == lastChunkRefreshTick)
            return false;

        var map = sim.World.CurrentMap;
        foreach (var colon in map.Colonists)
        {
            if (colon.OwnerId != localPlayerId)
                continue;

            var chunk = WorldToChunk(new Vector3(colon.X, colon.Y, colon.Z));
            if (chunk != lastPlayerChunk)
            {
                lastPlayerChunk = chunk;
                return true;
            }
        }

        return sim.Tick % CHUNK_REFRESH_INTERVAL_TICKS == 0;
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
                mat.AlbedoColor = new Color(1.0f, 0.5f, 0.0f);
                break;

            case "stairs":
                mat.AlbedoColor = new Color(0.8f, 0.8f, 0.2f);
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

    void UpdateChunksAround(Vector3I playerChunk)
    {
        var map = sim.World.CurrentMap;

        for (int x = -chunkRadius; x <= chunkRadius; x++)
        for (int z = -chunkRadius; z <= chunkRadius; z++)
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
        if (chunkMeshes.ContainsKey(chunkPos))
            return;

        // 🧱 MultiMesh setup
        var mm = new MultiMesh();

        mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
        mm.UseColors = true;
        mm.InstanceCount = 0; // ⚠️ important (reset clean)

        // 🧱 Mesh + material
        var mesh = new BoxMesh();

        var material = new StandardMaterial3D();
        material.VertexColorUseAsAlbedo = true; // 🔥 utilise SetInstanceColor
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded; // perf + lisibilité RTS

        mesh.SurfaceSetMaterial(0, material);

        mm.Mesh = mesh;

        // 🧱 Instance
        var instance = new MultiMeshInstance3D();
        instance.Multimesh = mm;

        List<Transform3D> transforms = new();
        List<Color> colors = new();

        // 📦 Build instances
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

            transforms.Add(new Transform3D(Basis.Identity, worldPos));

            // 🎨 couleur de base
            var color = GetTileColor(new Vector3I(
                (int)worldPos.X,
                (int)worldPos.Y,
                (int)worldPos.Z
            ));

            // 🧪 debug chunk
            if (debugChunks && chunkPos != Vector3I.Zero)
                color *= 0.8f;

            colors.Add(color);
        }

        // ⚠️ si chunk vide → skip
        if (transforms.Count == 0)
            return;

        // 📦 Apply instances
        mm.InstanceCount = transforms.Count;

        for (int i = 0; i < transforms.Count; i++)
        {
            mm.SetInstanceTransform(i, transforms[i]);
            mm.SetInstanceColor(i, colors[i]);
        }

        // 🧠 FOG OF WAR INIT (évite le "tout gris au start")
        var center = new Vector3I(
            chunkPos.X * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2,
            1,
            chunkPos.Z * Map.CHUNK_SIZE + Map.CHUNK_SIZE / 2
        );

        if (!sim.Vision.IsDiscovered(center))
        {
            instance.Visible = false;
        }
        else
        {
            instance.Visible = true;
        }

        AddChild(instance);
        chunkMeshes[chunkPos] = instance;
    }

    Color GetTileColor(Vector3I pos)
    {
        var tile = sim.World.CurrentMap.GetTile(pos);

        return tile.Type switch
        {
            "ground" => new Color(0.4f, 0.25f, 0.1f),
            "platform" => new Color(1.0f, 0.5f, 0.0f),
            "stairs" => new Color(0.8f, 0.8f, 0.2f),
            "tree" => new Color(0.05f, 0.35f, 0.08f),
            _ => new Color(1, 1, 1)
        };
    }

    void UnloadFarChunks()
    {
        List<Vector3I> toRemove = new();

        foreach (var pair in chunkMeshes)
        {
            var chunkPos = pair.Key;

            if (!currentNeededChunks.Contains(chunkPos))
            {
                pair.Value.QueueFree();
                toRemove.Add(chunkPos);
            }
        }

        foreach (var pos in toRemove)
            chunkMeshes.Remove(pos);
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

        HashSet<Vector3I> neededChunks = new();

        foreach (var colon in map.Colonists)
        {

            if (colon.OwnerId != localPlayerId)
                continue;

            var colonChunk = WorldToChunk(new Vector3(colon.X, colon.Y, colon.Z));

            int effectiveRadius = chunkRadius + chunkPreloadMargin;
            int radiusSq = effectiveRadius * effectiveRadius;
            for (int x = -effectiveRadius; x <= effectiveRadius; x++)
            for (int z = -effectiveRadius; z <= effectiveRadius; z++)
            {
                if (x * x + z * z > radiusSq)
                    continue;

                var chunkPos = new Vector3I
                (
                    colonChunk.X + x,
                    0,
                    colonChunk.Z + z
                );

                neededChunks.Add(chunkPos);

                var chunk = map.GetOrCreateChunk(chunkPos);

                SpawnChunkMesh(chunkPos, chunk);
            }
        }

        // stock pour unload
        currentNeededChunks = neededChunks;
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
            1,
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
        foreach (var pair in chunkMeshes)
        {
            var chunkPos = pair.Key;
            var instance = pair.Value;
            EvaluateChunkFog(chunkPos, out bool discovered, out bool visible);
            if (!discovered)
            {
                instance.Visible = false;
                continue;
            }

            instance.Visible = true;

            var mat = instance.MaterialOverride as StandardMaterial3D;

            if (mat == null)
                continue;

            if (!visible)
                mat.AlbedoColor = new Color(0.2f, 0.2f, 0.2f);
            else
                mat.AlbedoColor = new Color(1, 1, 1);
        }
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

        Vector3I[] samples = new Vector3I[]
        {
            new(startX, 1, startZ),
            new(endX, 1, startZ),
            new(startX, 1, endZ),
            new(endX, 1, endZ),
            new(midX, 1, midZ),
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

}