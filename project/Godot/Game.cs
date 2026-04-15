using Godot;
using System;
using System.Collections.Generic;
using System.Diagnostics;

public partial class Game : Node3D
{
    public enum ChunkQualityProfile
    {
        Auto,
        Low,
        Medium,
        High
    }

    [ExportGroup("Chunk Visibility Update")]
    /// <summary>Fréquence de mise à jour de la visibilité des chunks (en ticks simulation).</summary>
    [Export(PropertyHint.Range, "1,12,1")]
    public int ChunkVisibilityIntervalTicks = 2;
    /// <summary>Nombre max de chunks évalués par passe de visibilité.</summary>
    [Export(PropertyHint.Range, "16,2048,16")]
    public int ChunkVisibilityBatchSize = 192;
    /// <summary>Tuile posée par le mode Build : même identifiant pour le job, le mesh terrain et la couleur du fantôme.</summary>
    const string BuildPlacementTileType = "build_black";

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
    [ExportGroup("Chunk Quality Profiles")]
    /// <summary>Active l'adaptation automatique de la qualité chunk selon la machine au lancement.</summary>
    [Export] public bool AdaptiveChunkQualityEnabled = true;
    /// <summary>Permet de forcer un profil. Auto = détection machine.</summary>
    [Export] public ChunkQualityProfile ChunkQualityProfileOverride = ChunkQualityProfile.Auto;
    /// <summary>Écrit le profil choisi dans user://video_settings.cfg pour garder le même réglage au prochain lancement.</summary>
    [Export] public bool SaveDetectedChunkQualityProfile = true;
    /// <summary>Recharge un profil déjà sauvegardé si aucun override manuel n'est défini.</summary>
    [Export] public bool LoadSavedChunkQualityProfile = true;
    [ExportGroup("Chunk Runtime Tuning")]
    /// <summary>Ajuste automatiquement le budget de spawn chunk selon le FPS mesuré.</summary>
    [Export] public bool AdaptiveChunkSpawnBudgetEnabled = true;
    [Export(PropertyHint.Range, "20,120,1")]
    public int AdaptiveChunkSpawnTargetFps = 60;
    [Export(PropertyHint.Range, "8,512,1")]
    public int AdaptiveChunkSpawnMinBudget = 32;
    [Export(PropertyHint.Range, "8,512,1")]
    public int AdaptiveChunkSpawnMaxBudget = 196;
    [Export(PropertyHint.Range, "1,64,1")]
    public int AdaptiveChunkSpawnStep = 8;
    [Export(PropertyHint.Range, "0.1,2,0.1")]
    public float AdaptiveChunkSpawnAdjustIntervalSeconds = 0.5f;
    [ExportGroup("Chunk Camera Trajectory Prefetch")]
    /// <summary>Précharge des chunks dans la trajectoire de la caméra pour éviter les trous lors des déplacements rapides.</summary>
    [Export] public bool ChunkCameraTrajectoryPrefetchEnabled = true;
    /// <summary>Horizon de prédiction (secondes) utilisé pour le lookahead caméra.</summary>
    [Export(PropertyHint.Range, "0.05,1.5,0.05")]
    public float ChunkCameraLookaheadSeconds = 0.35f;
    /// <summary>Distance max de lookahead exprimée en nombre de chunks.</summary>
    [Export(PropertyHint.Range, "1,6,1")]
    public int ChunkCameraLookaheadMaxChunks = 2;
    [ExportGroup("Perf Benchmark")]
    [Export] public bool PerformanceBenchmarkEnabled = true;
    [Export(PropertyHint.Range, "10,180,1")]
    public int PerformanceBenchmarkDurationSeconds = 60;
    [Export(PropertyHint.Range, "0.1,2,0.1")]
    public float PerformanceBenchmarkSampleIntervalSeconds = 0.5f;
    [Export] public bool PerformanceBenchmarkSuiteEnabled = true;
    [Export] public bool PerformanceBenchmarkSuiteAutoCameraPath = true;
    [Export(PropertyHint.Range, "2,60,1")]
    public float PerformanceBenchmarkCameraPathRadiusX = 22f;
    [Export(PropertyHint.Range, "2,60,1")]
    public float PerformanceBenchmarkCameraPathRadiusZ = 16f;
    [Export(PropertyHint.Range, "0.05,2,0.05")]
    public float PerformanceBenchmarkCameraPathSpeed = 0.35f;
    [Export(PropertyHint.Range, "0,20,0.5")]
    public float PerformanceBenchmarkCameraPathVerticalAmplitude = 3f;
    /// <summary>Rayon supplémentaire (en chunks) autour des colons + pivot caméra pour éviter les trous quand la vue dépasse le colon.</summary>
    [Export] public int ChunkPreloadMargin = 1;
    /// <summary>Couches de chunks au-dessus / en-dessous du colon et de la caméra (grottes, hauteur Y).</summary>
    [Export] public int ChunkVerticalLoadMargin = 1;
    /// <summary>À moins de N tuiles d’une face de chunk, précharge aussi la colonne de chunks voisine (vue au bord).</summary>
    [Export] public int ChunkEdgePrefetchTiles = 8;
    /// <summary>Active le culling rendu par distance caméra (les chunks restent chargés pour éviter les trous de streaming).</summary>
    [Export] public bool ChunkDistanceCullingEnabled = true;
    /// <summary>Distance max (en unités monde) entre la caméra et le centre d'un chunk pour l'afficher.</summary>
    [Export] public float ChunkRenderDistance = 150f;
    /// <summary>Active le culling rendu par frustum caméra.</summary>
    [Export] public bool ChunkFrustumCullingEnabled = true;
    /// <summary>Marge de tolérance monde pour éviter le pop agressif au bord du frustum.</summary>
    [Export] public float ChunkFrustumPadding = 4f;
    /// <summary>Maintient brièvement un chunk visible quand il sort du culling pour éviter le clignotement.</summary>
    [Export] public bool ChunkVisibilityHysteresisEnabled = true;
    /// <summary>Durée de grâce (secondes) avant de masquer un chunk après sortie du culling.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ChunkHideGraceSeconds = 0.2f;
    /// <summary>Active la priorisation de streaming selon la direction caméra (devant en premier).</summary>
    [Export] public bool ChunkDirectionalStreamingEnabled = true;
    /// <summary>Poids appliqué aux chunks situés derrière la caméra (0.1 = très dépriorisés, 1 = neutre).</summary>
    [Export(PropertyHint.Range, "0.1,1,0.05")]
    public float ChunkBehindCameraWeight = 0.35f;
    /// <summary>Nombre max de nouveaux chunks meshés à chaque refresh pour lisser les pics.</summary>
    [Export(PropertyHint.Range, "8,512,1")]
    public int ChunkSpawnBudgetPerRefresh = 96;
    /// <summary>Rayon de la vision des colons en tuiles (sphère, LOS par tuile).</summary>
    [Export(PropertyHint.Range, "4,40,1")]
    public int ColonistVisionRadiusTiles = 12;
    [ExportGroup("Camera Smart Cut")]
    /// <summary>Masque automatiquement les blocs entre caméra et le focus (sélection, optionnellement survol), même sans Q.</summary>
    [Export] public bool CameraSmartCutEnabled = true;
    /// <summary>Si true, le smart cut suit aussi la case sous la souris (sinon « tunnel » transparent qui suit le curseur).</summary>
    [Export] public bool CameraSmartCutUseTerrainHover = false;
    /// <summary>Nombre de blocs conservés côté focus (évite d'ouvrir trop profondément la coupe).</summary>
    [Export(PropertyHint.Range, "0,6,1")]
    public int CameraSmartCutKeepLastSolids = 1;
    /// <summary>Distance max caméra→focus pour activer le smart cut auto (Q/Alt ignore cette limite).</summary>
    [Export] public float CameraSmartCutMaxDistance = 90f;
    [Export] public float CameraSmartCutRadius = 1.35f;
    [Export] public float CameraSmartCutFadeWidth = 1.25f;
    [Export] public float CameraSmartCutMinAlpha = 0.2f;
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
    // Coupe V : plan P vertical par la caméra (Cx,Cy,Cz), n = projection XZ du regard ; P' = P ± delta·n ; s = (bloc - C)·n.
    [ExportGroup("Coupe terrain (touche V)")]
    /// <summary>Legacy: inverse seulement la coupe verticale. Laisser false pour la règle stricte &quot;derrière P&apos; = invisible&quot;.</summary>
    [Export] public bool VerticalSliceInvertVisibility = false;
    [Export] public bool VerticalSliceUseVerticalCut = true;
    /// <summary>True = masquer le demi-espace &quot;derrière&quot; P&apos; (s &lt; -delta). False = masquer l&apos;autre côté (s &gt; +delta).</summary>
    [Export] public bool VerticalSliceHideBehindCameraSide = false;
    /// <summary>Décalage monde entre P et P&apos; le long de n. Masquage derrière : invisible si s &lt; -delta. Masquage devant : invisible si s &gt; +delta.</summary>
    [Export] public float VerticalSliceClipPlaneDelta = 0f;
    /// <summary>≤0 = pas de seconde limite le long de n. &gt;0 = masque aussi les blocs trop loin devant (mode derrière) ou trop loin derrière (mode devant).</summary>
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
    /// <summary>En coupe V, le pelage multimesh ne se recalcule que lorsque la caméra bouge d’environ ce pas (monde). Plus grand = moins de lag.</summary>
    [Export] public float VerticalSlicePeelCameraBucketWorld = 14f;

    [ExportGroup("Sélection blocs")]
    [Export] public bool TerrainSelectionHighlightEnabled = true;
    [Export] public Color TerrainSelectionHighlightMix = new Color(1f, 0.15f, 0.12f, 1f);
    [Export] public float TerrainSelectionHighlightAmount = 0.55f;
    [Export] public bool TerrainHoverHighlightEnabled = true;
    [Export] public Color TerrainHoverHighlightMix = new Color(1f, 0.92f, 0.2f, 1f);
    [Export] public float TerrainHoverHighlightAmount = 0.38f;
    [Export] public Color TerrainMineHoverHighlightMix = new Color(0.35f, 1f, 0.45f, 1f);
    [Export] public float TerrainMineHoverHighlightAmount = 0.48f;
    [Export] public float TerrainMineDragMaxClickPixels = 12f;
    [Export(PropertyHint.Range, "1,4096,1")]
    public int VoxelDragMaxCells = 512;
    [Export] public Color TerrainDragLinePreviewMix = new Color(0.55f, 0.75f, 1f, 1f);
    [Export] public float TerrainDragLinePreviewAmount = 0.42f;
    [ExportGroup("Construction")]
    [Export(PropertyHint.Range, "0.05,1,0.01")]
    public float BuildPreviewGhostAlpha = 0.38f;
    [Export] public bool ShowVirtualScaffolds = true;
    [Export(PropertyHint.Range, "0.05,1,0.01")]
    public float VirtualScaffoldAlpha = 0.3f;
    [Export] public Key BuildLayerUpKey = Key.Pageup;
    [Export] public Key BuildLayerDownKey = Key.Pagedown;
    [Export] public Key BuildDepthUpKey = Key.R;
    [Export] public Key BuildDepthDownKey = Key.F;

    HashSet<Vector3I> currentNeededChunks = new();

    Dictionary<Vector3I, ChunkVisual> _chunkVisuals = new();
    BoxMesh _tileCubeMesh;
    Shader _terrainSmartCutShader;
    int lastChunkRefreshTick = -1;
    int lastChunkVisibilityTick = -1;
    int _chunkVisibilityCursor = 0;
    readonly List<Vector3I> _chunkVisibilityKeysScratch = new();
    long lastChunkRefreshMicroseconds = 0;
    int lastNeededChunkCount = 0;
    int _lastRenderedChunkCount = 0;
    int _lastCulledDistanceChunkCount = 0;
    int _lastCulledFrustumChunkCount = 0;
    int _lastHysteresisKeptChunkCount = 0;
    int _lastSpawnedChunkCount = 0;
    int _lastSpawnPendingChunkCount = 0;
    int _runtimeChunkSpawnBudget = 96;
    float _spawnBudgetAdjustTimer = 0f;
    bool _hasPrevCameraPivotPos = false;
    Vector3 _prevCameraPivotPos = Vector3.Zero;
    double _lastCameraPrefetchSampleTimeSec = -1.0;
    Vector3 _smoothedCameraVelocity = Vector3.Zero;
    int _lastCameraLookaheadOrigins = 0;
    bool _perfBenchmarkRunning = false;
    double _perfBenchmarkElapsedSec = 0.0;
    double _perfBenchmarkSampleTimerSec = 0.0;
    int _perfBenchmarkSampleCount = 0;
    double _perfBenchmarkFpsSum = 0.0;
    int _perfBenchmarkFpsMin = int.MaxValue;
    int _perfBenchmarkFpsMax = 0;
    int _perfBenchmarkFrameCount = 0;
    double _perfBenchmarkFrameMsSum = 0.0;
    double _perfBenchmarkFrameMsMax = 0.0;
    double _perfBenchmarkActiveChunksSum = 0.0;
    double _perfBenchmarkRenderedChunksSum = 0.0;
    double _perfBenchmarkCulledDistSum = 0.0;
    double _perfBenchmarkCulledFrustumSum = 0.0;
    double _perfBenchmarkSpawnedSum = 0.0;
    double _perfBenchmarkPendingSpawnSum = 0.0;
    bool _perfBenchmarkSuiteRunning = false;
    int _perfBenchmarkSuiteIndex = -1;
    string _perfBenchmarkRunLabel = "single";
    bool _benchmarkAutoCameraPathActive = false;
    double _benchmarkAutoCameraPathElapsedSec = 0.0;
    bool _hasBenchmarkSuiteCameraOrigin = false;
    Vector3 _benchmarkSuiteCameraOrigin = Vector3.Zero;
    readonly Dictionary<Vector3I, double> _chunkHideGraceUntilByPos = new();
    ChunkQualityProfile _activeChunkQualityProfile = ChunkQualityProfile.Auto;
    string _activeChunkQualityReason = "scene_defaults";
    float lastFrameDeltaSeconds = 0f;

    WorldSelectionTargetKind _selectionTargetKind = WorldSelectionTargetKind.Colonists;
    Vector3I? _selectedTreeTile;
    readonly HashSet<Vector3I> _selectedTerrainTiles = new();
    bool _terrainMineDragTracking;
    Vector2 _terrainMineDragStartScreen;
    Vector3I? _terrainMineDragStartCell;
    readonly HashSet<Vector3I> _terrainMineDragPreview = new();
    readonly List<Vector3I> _terrainLineBuffer = new();
    readonly List<Vector3I> _mineSelectionSortBuffer = new();
    readonly List<Vector3I> _orderedVoxelSelectionBuffer = new();
    readonly List<Vector3I> _queuedBuildTargetsBuffer = new();
    int _lastBuildQueuedPreviewRefreshTick = -1;
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
    ulong _verticalSliceEvalFrame = ulong.MaxValue;
    Vector3 _verticalSliceEvalCamPos;
    Vector3 _verticalSliceEvalForward;
    Vector3 _verticalSliceEvalPlaneN;
    bool _verticalSliceEvalPlaneNValid;
    bool _verticalSliceEvalNeedScreenBottom;
    float _verticalSliceEvalBottomFrac;
    Transform3D _verticalSliceEvalInvCam;
    float _verticalSliceEvalTanHalfFov;
    float _verticalSliceEvalViewportHeight;

    Label _selectionModeLabel;
    Label _treeTargetLabel;
    Button _btnModeColonists;
    Button _btnModeTrees;
    Button _btnCutTree;
    Button _btnMineStone;
    Button _btnTerrainTiles;
    MeshInstance3D _buildPreviewGhost;
    StandardMaterial3D _buildPreviewMaterial;
    Vector3I? _buildPreviewCell;
    MultiMeshInstance3D _buildDragPreview;
    StandardMaterial3D _buildDragPreviewMaterial;
    MultiMeshInstance3D _buildQueuedPreview;
    StandardMaterial3D _buildQueuedPreviewMaterial;
    MultiMeshInstance3D _virtualScaffoldPreview;
    StandardMaterial3D _virtualScaffoldPreviewMaterial;
    int _lastVirtualScaffoldPreviewVersion = -1;
    bool _lastVirtualScaffoldPreviewVisible;
    int _buildSelectionLayerY = Map.ColonistWalkY;
    bool _buildSelectionLayerInitialized;
    int _buildExtrudeDepth = 1;
    bool _ignoreNextLeftReleaseAfterCancel;
    Label _jobsQueueLabel;
    Label _logisticsLabel;
    Label _terrainTileLabel;
    Label _buildStatusLabel;
    Label _perfProfileLabel;
    VBoxContainer _perfControlsBox;
    OptionButton _perfProfileOption;
    CheckBox _perfAdaptiveBudgetCheck;
    CheckBox _perfTrajectoryPrefetchCheck;
    HSlider _perfRenderDistanceSlider;
    Label _perfRenderDistanceValueLabel;
    HSlider _perfSpawnBudgetSlider;
    Label _perfSpawnBudgetValueLabel;
    HSlider _perfHideGraceSlider;
    Label _perfHideGraceValueLabel;
    bool _isSyncingPerfUi = false;
    readonly List<SimJob> _jobsUiBuffer = new();

    private Simulation _simulation;
    private PlayerController _playerController;
    private PlayerCommands _playerCommands;
    private MapRenderer _mapRenderer;
    GameCommandPipelineModule _commandPipeline;
    GameUiModule _uiModule;
    GameViewModule _viewModule;
    GameSelectionModule _selectionModule;
    GameInputModule _inputModule;


    public override void _Ready()
    {
        cameraPivot = GetNode<Node3D>("CameraPivot");
        camera = GetNode<Camera3D>("CameraPivot/Camera3D");
        ApplyChunkQualityProfileAtStartup();

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
        _logisticsLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/LogisticsLabel");
        _terrainTileLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/TerrainTileLabel");
        _buildStatusLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/BuildStatusLabel");
        _perfProfileLabel = GetNodeOrNull<Label>("UI/SelectionPanel/VBox/PerfProfileLabel");
        if (_logisticsLabel == null)
        {
            var vbox = GetNodeOrNull<VBoxContainer>("UI/SelectionPanel/VBox");
            if (vbox != null)
            {
                _logisticsLabel = new Label { Name = "LogisticsLabel" };
                vbox.AddChild(_logisticsLabel);
            }
        }
        if (_perfProfileLabel == null)
        {
            var vbox = GetNodeOrNull<VBoxContainer>("UI/SelectionPanel/VBox");
            if (vbox != null)
            {
                _perfProfileLabel = new Label { Name = "PerfProfileLabel" };
                vbox.AddChild(_perfProfileLabel);
            }
        }
        InitPerformanceControlsUi();
        LoadSavedPerformanceRuntimeSettings();
        SyncPerformanceControlsFromState();
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
        _commandPipeline = new GameCommandPipelineModule(sim, lockstep, localPlayerId);
        _uiModule = new GameUiModule();
        _viewModule = new GameViewModule();
        _selectionModule = new GameSelectionModule();
        _inputModule = new GameInputModule();
        SpawnVisuals();
        spawnedTiles.Clear();

        _tileCubeMesh = new BoxMesh();
        _terrainSmartCutShader = GD.Load<Shader>("res://project/Godot/Shaders/TerrainSmartCut.gdshader");
        _tileCubeMesh.SurfaceSetMaterial(0, null);
        InitBuildPreviewGhost();
        InitBuildDragPreviewVisual();
        InitBuildQueuedPreviewVisual();
        InitVirtualScaffoldPreviewVisual();

        cameraController = new CameraController(cameraPivot, camera);
        selectionManager = new SelectionManager(camera, colonVisuals, localPlayerId);
        selectionManager.TargetKind = _selectionTargetKind;
        unitController = new UnitController(sim, lockstep, camera, GetNode<Node>("UI"), localPlayerId,
            (out Vector3I anchor) => TryGetMoveOrderAnchorFromScreen(out anchor));
        UpdateSelectionTargetUi();

        sim.OnJobStarted   += OnJobStarted;
        sim.OnJobCompleted   += OnJobCompleted;
        lockstep.OnSnapshotDivergence += OnLockstepDivergence;
    }

    public override void _Process(double delta)
    {
        if (sim.World == null)
            return;

        ComputeTerrainOcclusionHiddenSet();
        UpdateTerrainHoverCell();
        UpdateVoxelDragPreview();
        UpdateBuildPreviewGhost();
        UpdateBuildDragPreviewVisual();
        UpdateBuildQueuedPreviewVisual();
        UpdateVirtualScaffoldPreviewVisual();
        if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
            RefreshBuildStatusLabel();

        lastFrameDeltaSeconds = (float)delta;
        accumulator += delta;
        UpdateAdaptiveChunkSpawnBudget((float)delta);
        UpdatePerformanceBenchmark((float)delta);

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
        int visibilityInterval = Mathf.Max(1, ChunkVisibilityIntervalTicks);
        if (tickAdvanced && sim.Tick % visibilityInterval == 0 && sim.Tick != lastChunkVisibilityTick)
        {
            UpdateChunkVisibility();
            lastChunkVisibilityTick = sim.Tick;
        }
        

        Render();
        bool hasFollow = TryGetCameraFollowPoint(out Vector3 followPos);
        cameraController.SetFocusPoint(followPos, hasFollow);
        cameraController.Update(delta);
        UpdateBenchmarkAutoCameraPath((float)delta);
        bool hasCutFocus = TryGetSmartCutFocusPoint(out Vector3 cutFocus);
        UpdateSmartCutShaderParameters(hasCutFocus, cutFocus);
        RefreshJobsQueueUi();
        RefreshPerformanceProfileLabel();
        RefreshTreeJobOverlays();
        if (_verticalSliceTerrainActive || _verticalSliceTerrainWasActive || _terrainOcclusionHidden.Count > 0)
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
        if (_inputModule == null || _selectionModule == null)
            return;

        // Raccourcis globaux : avec le focus sur les boutons UI, _Input du Game ne reçoit souvent pas les touches.
        if (@event is InputEventKey key && key.Pressed && !key.Echo)
        {
            if ((key.PhysicalKeycode == Key.Escape || key.Keycode == Key.Escape) && _terrainMineDragTracking)
            {
                CancelVoxelDrag(ignoreNextLeftRelease: true);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_inputModule.IsShortcut(key, Key.V))
            {
                _verticalSliceTerrainActive = !_verticalSliceTerrainActive;
                _multimeshPeelStateToken = ulong.MaxValue;
                GD.Print(_verticalSliceTerrainActive
                    ? "[Vue] Coupe terrain : ON (vertical qui suit la caméra + pelage sol si bas)"
                    : "[Vue] Coupe terrain : OFF");
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_inputModule.IsShortcut(key, Key.T))
            {
                if (cameraController != null && selectionManager != null && unitController != null)
                {
                    var next = _selectionModule.Next(_selectionTargetKind);
                    SetSelectionTargetKind(next);
                }
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_inputModule.IsShortcut(key, Key.B))
            {
                SetSelectionTargetKind(WorldSelectionTargetKind.BuildBlocks);
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
                && _inputModule.IsShortcut(key, BuildLayerUpKey))
            {
                _buildSelectionLayerY++;
                _buildSelectionLayerInitialized = true;
                GD.Print($"[Build] Couche Y = {_buildSelectionLayerY}");
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
                && _inputModule.IsShortcut(key, BuildLayerDownKey))
            {
                _buildSelectionLayerY--;
                _buildSelectionLayerInitialized = true;
                GD.Print($"[Build] Couche Y = {_buildSelectionLayerY}");
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
                && _inputModule.IsShortcut(key, BuildDepthUpKey))
            {
                _buildExtrudeDepth = Mathf.Clamp(_buildExtrudeDepth + 1, 1, 128);
                GD.Print($"[Build] Profondeur = {_buildExtrudeDepth}");
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
                && _inputModule.IsShortcut(key, BuildDepthDownKey))
            {
                _buildExtrudeDepth = Mathf.Clamp(_buildExtrudeDepth - 1, 1, 128);
                GD.Print($"[Build] Profondeur = {_buildExtrudeDepth}");
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

        if (sim?.World == null || camera == null)
            return;

        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (!mb.Pressed && _ignoreNextLeftReleaseAfterCancel)
            {
                _ignoreNextLeftReleaseAfterCancel = false;
                return;
            }

            if (mb.Pressed
                && _selectionModule.IsVoxelSelectionMode(_selectionTargetKind))
            {
                _terrainMineDragTracking = true;
                _terrainMineDragStartScreen = mb.Position;
                Vector2 vpPress = mb.Position;
                if (TerrainPickUsePixelCenterRay)
                    vpPress += new Vector2(0.5f, 0.5f);
                _terrainMineDragStartCell = TryGetDragModeCellViewport(vpPress, out Vector3I startCell)
                    ? startCell
                    : null;
            }
            else if (!mb.Pressed)
            {
                if (_selectionTargetKind == WorldSelectionTargetKind.Trees)
                {
                    TryPickTreeAtScreen();
                    GetViewport().SetInputAsHandled();
                }
                else if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
                {
                    FinishVoxelDragOrClick(mb);
                    GetViewport().SetInputAsHandled();
                }
                else if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
                {
                    FinishVoxelDragOrClick(mb);
                    GetViewport().SetInputAsHandled();
                }
            }
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Godot can dispatch input before _Ready has fully wired controllers.
        if (cameraController == null || selectionManager == null || unitController == null)
            return;

        if (@event is InputEventMouseButton wheelEvt
            && wheelEvt.Pressed
            && _selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
            && wheelEvt.ShiftPressed)
        {
            if (wheelEvt.ButtonIndex == MouseButton.WheelUp)
            {
                _buildSelectionLayerY++;
                _buildSelectionLayerInitialized = true;
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }
            if (wheelEvt.ButtonIndex == MouseButton.WheelDown)
            {
                _buildSelectionLayerY--;
                _buildSelectionLayerInitialized = true;
                UpdateSelectionTargetUi();
                GetViewport().SetInputAsHandled();
                return;
            }
        }

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
                GD.Print(
                    $"CHUNK METRICS active={_chunkVisuals.Count} needed={lastNeededChunkCount} " +
                    $"rendered={_lastRenderedChunkCount} culled_dist={_lastCulledDistanceChunkCount} " +
                    $"culled_frustum={_lastCulledFrustumChunkCount} hysteresis_kept={_lastHysteresisKeptChunkCount} " +
                    $"spawned={_lastSpawnedChunkCount} " +
                    $"pending_spawn={_lastSpawnPendingChunkCount} spawn_budget={_runtimeChunkSpawnBudget} " +
                    $"lookahead_origins={_lastCameraLookaheadOrigins} " +
                    $"benchmark_running={_perfBenchmarkRunning} suite_running={_perfBenchmarkSuiteRunning} " +
                    $"benchmark_t={_perfBenchmarkElapsedSec:0.0}s " +
                    $"profile={_activeChunkQualityProfile} " +
                    $"profile_reason={_activeChunkQualityReason} last_us={lastChunkRefreshMicroseconds}");
            }

            if (key.Keycode == Key.F4)
            {
                if (_perfBenchmarkSuiteRunning)
                    CancelPerformanceBenchmarkSuite();
                else
                    StartPerformanceBenchmarkSuite();
            }

            if (key.Keycode == Key.F5)
            {
                if (_perfBenchmarkRunning)
                    StopPerformanceBenchmark(completed: false);
                else
                    StartPerformanceBenchmark();
            }

            if (key.Keycode == Key.F6)
                CycleChunkQualityProfileHotkey();

            if (key.Keycode == Key.F7)
            {
                string saveJson = sim.ExportSaveJson();
                using var file = FileAccess.Open("user://savegame.json", FileAccess.ModeFlags.Write);
                file.StoreString(saveJson);
                GD.Print("[Save] État simulation sauvegardé dans user://savegame.json");
            }

            if (key.Keycode == Key.F8)
            {
                if (!FileAccess.FileExists("user://savegame.json"))
                {
                    GD.PrintErr("[Save] Aucun fichier user://savegame.json à charger.");
                }
                else
                {
                    using var file = FileAccess.Open("user://savegame.json", FileAccess.ModeFlags.Read);
                    sim.ImportSaveJson(file.GetAsText());
                    RebuildColonVisuals();
                    _terrainMeshLayoutVersion++;
                    _multimeshPeelStateToken = ulong.MaxValue;
                    GD.Print("[Save] État simulation rechargé.");
                }
            }
        }
    }

    void Step()
    {
        simFacade.Step();
    }

    void OnLockstepDivergence(SimulationSnapshot local, SimulationSnapshot remote)
    {
        GD.PrintErr($"[Lockstep] Divergence détectée tick={local.Tick} local={local.StateHash} remote={remote.StateHash}");
    }

    void Render()
    {
        _viewModule.RenderColonists(
            colonVisuals,
            selectionManager,
            selectedMat,
            defaultMat,
            sim,
            lastFrameDeltaSeconds);
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

    void RebuildColonVisuals()
    {
        foreach (var pair in colonVisuals)
        {
            if (GodotObject.IsInstanceValid(pair.Value))
                pair.Value.QueueFree();
        }
        colonVisuals.Clear();
        SpawnVisuals();
    }

    void InitSimulation()
    {
        var bootstrap = new WorldBootstrap();
        sim.World = bootstrap.CreateDefaultWorld(ChunkLoadRadius, localPlayerId);
        sim.Init();
        sim.VisionRadiusTiles = ColonistVisionRadiusTiles;
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

    ulong _terrainPickVisibilityCacheToken = ulong.MaxValue;
    readonly Dictionary<Vector3I, bool> _terrainPickHiddenCache = new();

    bool IsTerrainCellPickHiddenRaw(Vector3I cell) =>
        _terrainOcclusionHidden.Contains(cell)
        || (_verticalSliceTerrainActive && IsTerrainCellHiddenByVerticalSlice(cell));

    void RefreshTerrainPickVisibilityCacheIfNeeded()
    {
        ulong token = ComputeMultimeshPeelStateToken();
        if (token == _terrainPickVisibilityCacheToken)
            return;

        _terrainPickVisibilityCacheToken = token;
        _terrainPickHiddenCache.Clear();
    }

    /// <summary>Cellule masquée par la coupe V ou la percée Q — cache paresseux (ne parcourt plus tous les voxels à chaque frame).</summary>
    bool IsTerrainCellPickHidden(Vector3I cell)
    {
        if (!_verticalSliceTerrainActive && _terrainOcclusionHidden.Count == 0)
            return false;
        RefreshTerrainPickVisibilityCacheIfNeeded();
        if (_terrainPickHiddenCache.TryGetValue(cell, out bool hidden))
            return hidden;
        hidden = IsTerrainCellPickHiddenRaw(cell);
        _terrainPickHiddenCache[cell] = hidden;
        return hidden;
    }

    bool IsTerrainFaceExposedForPick(Map map, Vector3I cell, Vector3I faceNormal)
    {
        Vector3I neighbor = cell + faceNormal;
        if (!IsWorldTileSolidForPick(map, neighbor.X, neighbor.Y, neighbor.Z))
            return true;
        return IsTerrainCellPickHidden(neighbor);
    }

    bool TryAcceptEnteredCellFace(Map map, Vector3I cell, Vector3 dir, int nx, int ny, int nz, bool preferSideFaces)
    {
        if (nx == 0 && ny == 0 && nz == 0)
            return false;

        bool AcceptNormal(Vector3I n)
        {
            if (new Vector3(n.X, n.Y, n.Z).Dot(dir) >= -0.01f)
                return false;
            return IsTerrainFaceExposedForPick(map, cell, n);
        }

        bool xOk = nx != 0 && AcceptNormal(new Vector3I(nx, 0, 0));
        bool zOk = nz != 0 && AcceptNormal(new Vector3I(0, 0, nz));
        if (xOk || zOk)
            return true;

        if (preferSideFaces)
            return false;

        if (ny != 0 && AcceptNormal(new Vector3I(0, ny, 0)))
            return true;
        return false;
    }

    /// <summary>DDA face-aware : choisit la première cellule solide visible dont la face d'entrée est exposée dans le monde coupé.</summary>
    bool TryPickTerrainVisibleFaceDda(Vector3 from, Vector3 dirNormalized, float rayLength, out Vector3I pick)
    {
        pick = default;
        var map = sim?.World?.CurrentMap;
        if (map == null)
            return false;

        Vector3 dir = dirNormalized;
        if (dir.LengthSquared() < 1e-12f)
            return false;
        dir = dir.Normalized();

        RefreshTerrainPickVisibilityCacheIfNeeded();
        bool preferSideFaces = _verticalSliceTerrainActive && VerticalSliceUseVerticalCut;

        // DDA sur voxels centrés aux entiers : on translate de +0.5 pour travailler dans une grille "coin entier".
        Vector3 rayOrigin = from + dir * 1e-4f + new Vector3(0.5f, 0.5f, 0.5f);
        int cx = Mathf.FloorToInt(rayOrigin.X);
        int cy = Mathf.FloorToInt(rayOrigin.Y);
        int cz = Mathf.FloorToInt(rayOrigin.Z);

        float dx = dir.X, dy = dir.Y, dz = dir.Z;
        const float eps = 1e-8f;
        int stepX = dx > eps ? 1 : (dx < -eps ? -1 : 0);
        int stepY = dy > eps ? 1 : (dy < -eps ? -1 : 0);
        int stepZ = dz > eps ? 1 : (dz < -eps ? -1 : 0);

        float tDeltaX = stepX != 0 ? Mathf.Abs(1f / dx) : float.MaxValue;
        float tDeltaY = stepY != 0 ? Mathf.Abs(1f / dy) : float.MaxValue;
        float tDeltaZ = stepZ != 0 ? Mathf.Abs(1f / dz) : float.MaxValue;

        float tMaxX = stepX > 0 ? ((cx + 1) - rayOrigin.X) / dx
            : stepX < 0 ? (rayOrigin.X - cx) / (-dx) : float.MaxValue;
        float tMaxY = stepY > 0 ? ((cy + 1) - rayOrigin.Y) / dy
            : stepY < 0 ? (rayOrigin.Y - cy) / (-dy) : float.MaxValue;
        float tMaxZ = stepZ > 0 ? ((cz + 1) - rayOrigin.Z) / dz
            : stepZ < 0 ? (rayOrigin.Z - cz) / (-dz) : float.MaxValue;

        float maxTravel = Mathf.Max(0f, rayLength);
        float traveled = 0f;
        const int maxSteps = 8192;
        int entryNx = 0, entryNy = 0, entryNz = 0;

        for (int step = 0; step < maxSteps; step++)
        {
            if (traveled > maxTravel)
                break;

            var cell = new Vector3I(cx, cy, cz);
            if ((new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f) - from).Length() > rayLength + 1f)
                break;

            var tile = map.GetTile(cell);
            if (tile != null && tile.Solid && !IsTerrainCellPickHidden(cell))
            {
                if (TryAcceptEnteredCellFace(map, cell, dir, entryNx, entryNy, entryNz, preferSideFaces))
                {
                    pick = cell;
                    return true;
                }
            }

            float tStep = Mathf.Min(tMaxX, Mathf.Min(tMaxY, tMaxZ));
            if (float.IsNaN(tStep) || tStep >= float.MaxValue * 0.5f)
                break;
            if (traveled + tStep > maxTravel)
                break;
            traveled += tStep;

            float tieTol = 1e-5f * (1f + Mathf.Abs(tStep));
            bool stepOnX = tMaxX <= tStep + tieTol;
            bool stepOnY = tMaxY <= tStep + tieTol;
            bool stepOnZ = tMaxZ <= tStep + tieTol;
            entryNx = entryNy = entryNz = 0;

            if (stepOnX)
            {
                tMaxX += tDeltaX;
                cx += stepX;
                entryNx = -stepX;
            }
            if (stepOnY)
            {
                tMaxY += tDeltaY;
                cy += stepY;
                entryNy = -stepY;
            }
            if (stepOnZ)
            {
                tMaxZ += tDeltaZ;
                cz += stepZ;
                entryNz = -stepZ;
            }
        }

        return false;
    }

    static void AddCollisionTri(List<Vector3> v, Vector3 a, Vector3 b, Vector3 c)
    {
        v.Add(a);
        v.Add(b);
        v.Add(c);
    }

    /// <summary>Collision picking statique (map seule) ; coupe / Q gérées par <see cref="TerrainPhysicsPicker"/> via prédicat léger.</summary>
    void BuildAndAttachChunkTerrainPickBody(Chunk chunk, Vector3I chunkPos, Map map, ChunkVisual cv)
    {
        int cs = Map.CHUNK_SIZE;
        int ox = chunkPos.X * cs;
        int oy = chunkPos.Y * cs;
        int oz = chunkPos.Z * cs;
        var verts = new List<Vector3>(16384);
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

        int vy = Mathf.Max(0, ChunkVerticalLoadMargin);
        for (int x = -ChunkLoadRadius; x <= ChunkLoadRadius; x++)
        for (int dy = -vy; dy <= vy; dy++)
        for (int z = -ChunkLoadRadius; z <= ChunkLoadRadius; z++)
        {
            var chunkPos = new Vector3I(
                playerChunk.X + x,
                playerChunk.Y + dy,
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
        solidInstance.MaterialOverride = CreateChunkSolidMaterial();
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

            if (tile.Type == "ground" || tile.Type == "platform" || tile.Type == "stairs" || tile.Type == "stone" || tile.Type == "dirt" || tile.Type == "scaffold"
                || tile.Type == BuildPlacementTileType)
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

    Material CreateChunkSolidMaterial()
    {
        if (_terrainSmartCutShader == null)
        {
            var fallback = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded
            };
            return fallback;
        }

        var mat = new ShaderMaterial();
        mat.Shader = _terrainSmartCutShader;
        mat.SetShaderParameter("u_fog_tint", Colors.White);
        mat.SetShaderParameter("u_smart_cut_enabled", false);
        mat.SetShaderParameter("u_cam_pos", Vector3.Zero);
        mat.SetShaderParameter("u_focus_pos", Vector3.Zero);
        mat.SetShaderParameter("u_cut_radius", CameraSmartCutRadius);
        mat.SetShaderParameter("u_cut_fade", CameraSmartCutFadeWidth);
        mat.SetShaderParameter("u_alpha_min", Mathf.Clamp(CameraSmartCutMinAlpha, 0f, 1f));
        return mat;
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
        {
            _chunkVisuals.Remove(pos);
            _chunkHideGraceUntilByPos.Remove(pos);
        }
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
        _lastCameraLookaheadOrigins = 0;

        int r = ChunkLoadRadius + Mathf.Max(0, ChunkPreloadMargin);
        int vy = Mathf.Max(0, ChunkVerticalLoadMargin);

        void AddChunkColumnNeighborhood(int chunkX, int chunkY, int chunkZ)
        {
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -vy; dy <= vy; dy++)
            for (int dz = -r; dz <= r; dz++)
                currentNeededChunks.Add(new Vector3I(chunkX + dx, chunkY + dy, chunkZ + dz));
        }

        void PrefetchNeighborChunkColumnsNearEdges(Vector3I world, int ox, int oy, int oz)
        {
            int m = ChunkEdgePrefetchTiles;
            if (m <= 0)
                return;
            int s = Map.CHUNK_SIZE;
            Vector3I loc = map.WorldToLocal(world);
            if (loc.X < m)
                AddChunkColumnNeighborhood(ox - 1, oy, oz);
            if (loc.X >= s - m)
                AddChunkColumnNeighborhood(ox + 1, oy, oz);
            if (loc.Y < m)
                AddChunkColumnNeighborhood(ox, oy - 1, oz);
            if (loc.Y >= s - m)
                AddChunkColumnNeighborhood(ox, oy + 1, oz);
            if (loc.Z < m)
                AddChunkColumnNeighborhood(ox, oy, oz - 1);
            if (loc.Z >= s - m)
                AddChunkColumnNeighborhood(ox, oy, oz + 1);
        }

        foreach (var colon in map.Colonists)
        {
            if (colon.OwnerId != localPlayerId)
                continue;

            int ox = Mathf.FloorToInt((float)colon.Position.X / Map.CHUNK_SIZE);
            int oy = Mathf.FloorToInt((float)colon.Position.Y / Map.CHUNK_SIZE);
            int oz = Mathf.FloorToInt((float)colon.Position.Z / Map.CHUNK_SIZE);
            AddChunkColumnNeighborhood(ox, oy, oz);
            PrefetchNeighborChunkColumnsNearEdges(colon.Position, ox, oy, oz);
        }

        // Pivot caméra : même logique XZ + hauteur Y pour éviter trous en grotte / étages.
        if (cameraPivot != null)
        {
            Vector3 gp = cameraPivot.GlobalPosition;
            int cx = Mathf.FloorToInt(gp.X / Map.CHUNK_SIZE);
            int cy = Mathf.FloorToInt(gp.Y / Map.CHUNK_SIZE);
            int cz = Mathf.FloorToInt(gp.Z / Map.CHUNK_SIZE);
            AddChunkColumnNeighborhood(cx, cy, cz);

            if (ChunkCameraTrajectoryPrefetchEnabled)
            {
                double nowSec = Time.GetTicksMsec() * 0.001;
                if (_hasPrevCameraPivotPos && _lastCameraPrefetchSampleTimeSec > 0.0)
                {
                    double dt = nowSec - _lastCameraPrefetchSampleTimeSec;
                    if (dt > 0.0001)
                    {
                        Vector3 instantVel = (gp - _prevCameraPivotPos) / (float)dt;
                        _smoothedCameraVelocity = _smoothedCameraVelocity.Lerp(instantVel, 0.25f);
                    }
                }

                _prevCameraPivotPos = gp;
                _hasPrevCameraPivotPos = true;
                _lastCameraPrefetchSampleTimeSec = nowSec;

                float maxLookaheadDist = Mathf.Max(1, ChunkCameraLookaheadMaxChunks) * Map.CHUNK_SIZE;
                Vector3 offset = _smoothedCameraVelocity * Mathf.Max(0.05f, ChunkCameraLookaheadSeconds);
                float len = offset.Length();
                if (len > 0.25f)
                {
                    if (len > maxLookaheadDist)
                        offset = offset / len * maxLookaheadDist;

                    int steps = 3;
                    for (int i = 1; i <= steps; i++)
                    {
                        float t = i / (float)steps;
                        Vector3 p = gp + offset * t;
                        int lx = Mathf.FloorToInt(p.X / Map.CHUNK_SIZE);
                        int ly = Mathf.FloorToInt(p.Y / Map.CHUNK_SIZE);
                        int lz = Mathf.FloorToInt(p.Z / Map.CHUNK_SIZE);
                        AddChunkColumnNeighborhood(lx, ly, lz);
                        _lastCameraLookaheadOrigins++;
                    }
                }
            }
        }

        var spawnCandidates = new List<Vector3I>();
        foreach (var chunkPos in currentNeededChunks)
        {
            if (!_chunkVisuals.ContainsKey(chunkPos))
                spawnCandidates.Add(chunkPos);
        }

        if (ChunkDirectionalStreamingEnabled && spawnCandidates.Count > 1)
        {
            spawnCandidates.Sort((a, b) =>
            {
                float sb = ComputeChunkStreamingScore(b);
                float sa = ComputeChunkStreamingScore(a);
                int cmp = sb.CompareTo(sa);
                if (cmp != 0)
                    return cmp;
                // Tie-break deterministic.
                cmp = a.X.CompareTo(b.X);
                if (cmp != 0)
                    return cmp;
                cmp = a.Y.CompareTo(b.Y);
                if (cmp != 0)
                    return cmp;
                return a.Z.CompareTo(b.Z);
            });
        }

        int budget = _runtimeChunkSpawnBudget <= 0
            ? spawnCandidates.Count
            : Mathf.Min(_runtimeChunkSpawnBudget, spawnCandidates.Count);
        _lastSpawnPendingChunkCount = spawnCandidates.Count - budget;
        _lastSpawnedChunkCount = 0;
        for (int i = 0; i < budget; i++)
        {
            var chunkPos = spawnCandidates[i];
            SpawnChunkMesh(chunkPos, map.GetOrCreateChunk(chunkPos));
            _lastSpawnedChunkCount++;
        }
    }

    float ComputeChunkStreamingScore(Vector3I chunkPos)
    {
        if (camera == null)
            return 0f;

        Vector3 toChunk = GetChunkCenterWorld(chunkPos) - camera.GlobalPosition;
        float distSq = Mathf.Max(1f, toChunk.LengthSquared());
        Vector3 dir = toChunk / Mathf.Sqrt(distSq);
        float forward = dir.Dot(-camera.GlobalTransform.Basis.Z);
        float orientationWeight = forward >= 0f
            ? 1f + forward
            : Mathf.Lerp(ChunkBehindCameraWeight, 1f, forward + 1f);
        return orientationWeight / distSq;
    }

    void ApplyChunkQualityProfileAtStartup()
    {
        if (!AdaptiveChunkQualityEnabled)
        {
            _activeChunkQualityProfile = ChunkQualityProfile.Auto;
            _activeChunkQualityReason = "adaptive_disabled";
            GD.Print("[Perf] Adaptive chunk quality disabled; using scene values.");
            return;
        }

        if (ChunkQualityProfileOverride != ChunkQualityProfile.Auto)
        {
            ApplyChunkQualityProfile(ChunkQualityProfileOverride, "manual_override");
            if (SaveDetectedChunkQualityProfile)
                SaveChunkQualityProfileSetting(_activeChunkQualityProfile);
            return;
        }

        if (LoadSavedChunkQualityProfile && TryLoadSavedChunkQualityProfile(out var savedProfile))
        {
            ApplyChunkQualityProfile(savedProfile, "saved_profile");
            return;
        }

        ChunkQualityProfile detected = DetectChunkQualityProfile();
        ApplyChunkQualityProfile(detected, "auto_detect");
        if (SaveDetectedChunkQualityProfile)
            SaveChunkQualityProfileSetting(detected);
    }

    ChunkQualityProfile DetectChunkQualityProfile()
    {
        int cpu = OS.GetProcessorCount();
        string renderer = RenderingServer.GetCurrentRenderingMethod().ToLowerInvariant();
        bool heavyRenderer = renderer.Contains("forward_plus");
        bool mobileLikeRenderer = renderer.Contains("mobile");

        // Heuristique conservative: évite de surclasser "High" sans preuve.
        if (cpu <= 6 || mobileLikeRenderer)
            return ChunkQualityProfile.Low;
        if (cpu >= 22 && heavyRenderer)
            return ChunkQualityProfile.High;
        return ChunkQualityProfile.Medium;
    }

    void ApplyChunkQualityProfile(ChunkQualityProfile profile, string reason)
    {
        switch (profile)
        {
            case ChunkQualityProfile.Low:
                ChunkLoadRadius = 2;
                ChunkPreloadMargin = 0;
                ChunkVerticalLoadMargin = 1;
                ChunkEdgePrefetchTiles = 4;
                ChunkDistanceCullingEnabled = true;
                ChunkRenderDistance = 90f;
                ChunkFrustumCullingEnabled = true;
                ChunkFrustumPadding = 2f;
                ChunkDirectionalStreamingEnabled = true;
                ChunkBehindCameraWeight = 0.2f;
                ChunkSpawnBudgetPerRefresh = 40;
                ChunkVisibilityIntervalTicks = 3;
                ChunkVisibilityBatchSize = 128;
                break;

            case ChunkQualityProfile.High:
                ChunkLoadRadius = 2;
                ChunkPreloadMargin = 0;
                ChunkVerticalLoadMargin = 1;
                ChunkEdgePrefetchTiles = 7;
                ChunkDistanceCullingEnabled = true;
                ChunkRenderDistance = 150f;
                ChunkFrustumCullingEnabled = true;
                ChunkFrustumPadding = 4f;
                ChunkDirectionalStreamingEnabled = true;
                ChunkBehindCameraWeight = 0.4f;
                ChunkSpawnBudgetPerRefresh = 96;
                ChunkVisibilityIntervalTicks = 2;
                ChunkVisibilityBatchSize = 192;
                break;

            default:
                profile = ChunkQualityProfile.Medium;
                ChunkLoadRadius = 2;
                ChunkPreloadMargin = 0;
                ChunkVerticalLoadMargin = 1;
                ChunkEdgePrefetchTiles = 6;
                ChunkDistanceCullingEnabled = true;
                ChunkRenderDistance = 130f;
                ChunkFrustumCullingEnabled = true;
                ChunkFrustumPadding = 3f;
                ChunkDirectionalStreamingEnabled = true;
                ChunkBehindCameraWeight = 0.3f;
                ChunkSpawnBudgetPerRefresh = 72;
                ChunkVisibilityIntervalTicks = 2;
                ChunkVisibilityBatchSize = 160;
                break;
        }

        _activeChunkQualityProfile = profile;
        _activeChunkQualityReason = reason;
        _runtimeChunkSpawnBudget = Mathf.Clamp(
            ChunkSpawnBudgetPerRefresh,
            Mathf.Max(8, AdaptiveChunkSpawnMinBudget),
            Mathf.Max(Mathf.Max(8, AdaptiveChunkSpawnMinBudget), AdaptiveChunkSpawnMaxBudget));
        GD.Print(
            $"[Perf] Chunk profile={_activeChunkQualityProfile} reason={reason} " +
            $"cpu={OS.GetProcessorCount()} memMb={OS.GetStaticMemoryUsage() / (1024 * 1024)} " +
            $"renderer={RenderingServer.GetCurrentRenderingMethod()}");
    }

    void CycleChunkQualityProfileHotkey()
    {
        ChunkQualityProfile next = _activeChunkQualityProfile switch
        {
            ChunkQualityProfile.Low => ChunkQualityProfile.Medium,
            ChunkQualityProfile.Medium => ChunkQualityProfile.High,
            _ => ChunkQualityProfile.Low
        };

        ChunkQualityProfileOverride = next;
        ApplyChunkQualityProfile(next, "hotkey_cycle");
        if (SaveDetectedChunkQualityProfile)
            SavePerformanceRuntimeSettings();
        RefreshPerformanceProfileLabel();
        SyncPerformanceControlsFromState();
        GD.Print($"[Perf] Switched chunk profile to {next} (F6).");
    }

    void UpdateAdaptiveChunkSpawnBudget(float delta)
    {
        if (!AdaptiveChunkSpawnBudgetEnabled)
        {
            _runtimeChunkSpawnBudget = ChunkSpawnBudgetPerRefresh;
            return;
        }

        _spawnBudgetAdjustTimer += delta;
        float interval = Mathf.Max(0.1f, AdaptiveChunkSpawnAdjustIntervalSeconds);
        if (_spawnBudgetAdjustTimer < interval)
            return;
        _spawnBudgetAdjustTimer = 0f;

        int fps = Mathf.RoundToInt((float)Engine.GetFramesPerSecond());
        int minBudget = Mathf.Max(8, AdaptiveChunkSpawnMinBudget);
        int maxBudget = Mathf.Max(minBudget, AdaptiveChunkSpawnMaxBudget);
        int step = Mathf.Max(1, AdaptiveChunkSpawnStep);
        int target = Mathf.Max(20, AdaptiveChunkSpawnTargetFps);

        int budget = Mathf.Clamp(_runtimeChunkSpawnBudget, minBudget, maxBudget);
        if (fps < target - 3)
            budget = Mathf.Max(minBudget, budget - step);
        else if (fps > target + 6 && _lastSpawnPendingChunkCount > 0)
            budget = Mathf.Min(maxBudget, budget + step);

        _runtimeChunkSpawnBudget = budget;
    }

    void RefreshPerformanceProfileLabel()
    {
        if (_perfProfileLabel == null)
            return;

        string benchmarkSuffix = _perfBenchmarkRunning
            ? $" | BENCH {_perfBenchmarkElapsedSec:0.0}/{PerformanceBenchmarkDurationSeconds}s ({_perfBenchmarkRunLabel})"
            : string.Empty;
        _perfProfileLabel.Text =
            $"Perf: {_activeChunkQualityProfile} ({_activeChunkQualityReason}) | " +
            $"SpawnBudget={_runtimeChunkSpawnBudget} | FPS={Engine.GetFramesPerSecond()}{benchmarkSuffix}";
    }

    void StartPerformanceBenchmark(string runLabel = "single")
    {
        if (!PerformanceBenchmarkEnabled)
        {
            GD.Print("[PerfBenchmark] Disabled via export flag.");
            return;
        }

        _perfBenchmarkRunning = true;
        _perfBenchmarkElapsedSec = 0.0;
        _perfBenchmarkSampleTimerSec = 0.0;
        _perfBenchmarkSampleCount = 0;
        _perfBenchmarkFpsSum = 0.0;
        _perfBenchmarkFpsMin = int.MaxValue;
        _perfBenchmarkFpsMax = 0;
        _perfBenchmarkFrameCount = 0;
        _perfBenchmarkFrameMsSum = 0.0;
        _perfBenchmarkFrameMsMax = 0.0;
        _perfBenchmarkActiveChunksSum = 0.0;
        _perfBenchmarkRenderedChunksSum = 0.0;
        _perfBenchmarkCulledDistSum = 0.0;
        _perfBenchmarkCulledFrustumSum = 0.0;
        _perfBenchmarkSpawnedSum = 0.0;
        _perfBenchmarkPendingSpawnSum = 0.0;
        _perfBenchmarkRunLabel = runLabel;

        GD.Print($"[PerfBenchmark] Started run={runLabel} duration={PerformanceBenchmarkDurationSeconds}s sampleInterval={PerformanceBenchmarkSampleIntervalSeconds:0.00}s");
    }

    void StopPerformanceBenchmark(bool completed)
    {
        if (!_perfBenchmarkRunning && !completed)
            return;

        _perfBenchmarkRunning = false;
        PrintPerformanceBenchmarkSummary(completed);

        if (_perfBenchmarkSuiteRunning)
        {
            if (completed)
                StartNextProfileInBenchmarkSuite();
            else
                CancelPerformanceBenchmarkSuite();
        }
    }

    void UpdatePerformanceBenchmark(float delta)
    {
        if (!_perfBenchmarkRunning)
            return;

        _perfBenchmarkElapsedSec += delta;
        _perfBenchmarkSampleTimerSec += delta;
        _perfBenchmarkFrameCount++;

        double frameMs = delta * 1000.0;
        _perfBenchmarkFrameMsSum += frameMs;
        if (frameMs > _perfBenchmarkFrameMsMax)
            _perfBenchmarkFrameMsMax = frameMs;

        float interval = Mathf.Max(0.1f, PerformanceBenchmarkSampleIntervalSeconds);
        if (_perfBenchmarkSampleTimerSec >= interval)
        {
            _perfBenchmarkSampleTimerSec = 0.0;
            int fps = Mathf.RoundToInt((float)Engine.GetFramesPerSecond());
            _perfBenchmarkSampleCount++;
            _perfBenchmarkFpsSum += fps;
            _perfBenchmarkFpsMin = Mathf.Min(_perfBenchmarkFpsMin, fps);
            _perfBenchmarkFpsMax = Mathf.Max(_perfBenchmarkFpsMax, fps);
            _perfBenchmarkActiveChunksSum += _chunkVisuals.Count;
            _perfBenchmarkRenderedChunksSum += _lastRenderedChunkCount;
            _perfBenchmarkCulledDistSum += _lastCulledDistanceChunkCount;
            _perfBenchmarkCulledFrustumSum += _lastCulledFrustumChunkCount;
            _perfBenchmarkSpawnedSum += _lastSpawnedChunkCount;
            _perfBenchmarkPendingSpawnSum += _lastSpawnPendingChunkCount;
        }

        if (_perfBenchmarkElapsedSec >= Mathf.Max(1, PerformanceBenchmarkDurationSeconds))
            StopPerformanceBenchmark(completed: true);
    }

    void PrintPerformanceBenchmarkSummary(bool completed)
    {
        int n = Mathf.Max(1, _perfBenchmarkSampleCount);
        double avgFps = _perfBenchmarkFpsSum / n;
        double avgFrameMs = _perfBenchmarkFrameMsSum / Math.Max(1, _perfBenchmarkFrameCount);
        double avgActive = _perfBenchmarkActiveChunksSum / n;
        double avgRendered = _perfBenchmarkRenderedChunksSum / n;
        double avgCulledDist = _perfBenchmarkCulledDistSum / n;
        double avgCulledFrustum = _perfBenchmarkCulledFrustumSum / n;
        double avgSpawned = _perfBenchmarkSpawnedSum / n;
        double avgPending = _perfBenchmarkPendingSpawnSum / n;
        string state = completed ? "completed" : "cancelled";
        int fpsMin = _perfBenchmarkFpsMin == int.MaxValue ? 0 : _perfBenchmarkFpsMin;

        GD.Print(
            $"[PerfBenchmark] run={_perfBenchmarkRunLabel} {state} dur={_perfBenchmarkElapsedSec:0.0}s samples={_perfBenchmarkSampleCount} " +
            $"fps_avg={avgFps:0.0} fps_min={fpsMin} fps_max={_perfBenchmarkFpsMax} " +
            $"frame_ms_avg={avgFrameMs:0.00} frame_ms_max={_perfBenchmarkFrameMsMax:0.00} " +
            $"chunks_active_avg={avgActive:0.0} rendered_avg={avgRendered:0.0} " +
            $"culled_dist_avg={avgCulledDist:0.0} culled_frustum_avg={avgCulledFrustum:0.0} " +
            $"spawned_avg={avgSpawned:0.0} pending_avg={avgPending:0.0} " +
            $"profile={_activeChunkQualityProfile} reason={_activeChunkQualityReason}");
    }

    void StartPerformanceBenchmarkSuite()
    {
        if (!PerformanceBenchmarkSuiteEnabled)
        {
            GD.Print("[PerfBenchmarkSuite] Disabled via export flag.");
            return;
        }
        if (_perfBenchmarkRunning)
        {
            GD.Print("[PerfBenchmarkSuite] Stop current benchmark before starting suite.");
            return;
        }

        _perfBenchmarkSuiteRunning = true;
        _perfBenchmarkSuiteIndex = -1;
        _hasBenchmarkSuiteCameraOrigin = false;
        _benchmarkAutoCameraPathActive = false;
        GD.Print("[PerfBenchmarkSuite] Starting suite: Low -> Medium -> High");
        StartNextProfileInBenchmarkSuite();
    }

    void StartNextProfileInBenchmarkSuite()
    {
        _perfBenchmarkSuiteIndex++;
        if (_perfBenchmarkSuiteIndex >= 3)
        {
            GD.Print("[PerfBenchmarkSuite] Completed.");
            _perfBenchmarkSuiteRunning = false;
            _benchmarkAutoCameraPathActive = false;
            if (_hasBenchmarkSuiteCameraOrigin && cameraPivot != null)
                cameraPivot.GlobalPosition = _benchmarkSuiteCameraOrigin;
            return;
        }

        ChunkQualityProfile profile = _perfBenchmarkSuiteIndex switch
        {
            0 => ChunkQualityProfile.Low,
            1 => ChunkQualityProfile.Medium,
            _ => ChunkQualityProfile.High
        };

        if (cameraPivot != null && !_hasBenchmarkSuiteCameraOrigin)
        {
            _benchmarkSuiteCameraOrigin = cameraPivot.GlobalPosition;
            _hasBenchmarkSuiteCameraOrigin = true;
        }

        ChunkQualityProfileOverride = profile;
        ApplyChunkQualityProfile(profile, "benchmark_suite");
        SavePerformanceRuntimeSettings();
        SyncPerformanceControlsFromState();

        if (PerformanceBenchmarkSuiteAutoCameraPath && _hasBenchmarkSuiteCameraOrigin)
        {
            _benchmarkAutoCameraPathActive = true;
            _benchmarkAutoCameraPathElapsedSec = 0.0;
            if (cameraPivot != null)
                cameraPivot.GlobalPosition = _benchmarkSuiteCameraOrigin;
        }
        else
        {
            _benchmarkAutoCameraPathActive = false;
        }

        StartPerformanceBenchmark($"suite_{profile.ToString().ToLowerInvariant()}");
    }

    void CancelPerformanceBenchmarkSuite()
    {
        bool wasRunning = _perfBenchmarkSuiteRunning;
        _perfBenchmarkSuiteRunning = false;
        _benchmarkAutoCameraPathActive = false;
        if (_hasBenchmarkSuiteCameraOrigin && cameraPivot != null)
            cameraPivot.GlobalPosition = _benchmarkSuiteCameraOrigin;
        if (wasRunning)
            GD.Print("[PerfBenchmarkSuite] Cancelled.");
    }

    void UpdateBenchmarkAutoCameraPath(float delta)
    {
        if (!_benchmarkAutoCameraPathActive || cameraPivot == null)
            return;

        _benchmarkAutoCameraPathElapsedSec += delta;
        float t = (float)_benchmarkAutoCameraPathElapsedSec * Mathf.Max(0.05f, PerformanceBenchmarkCameraPathSpeed);
        float x = Mathf.Cos(t) * PerformanceBenchmarkCameraPathRadiusX;
        float z = Mathf.Sin(t * 0.87f) * PerformanceBenchmarkCameraPathRadiusZ;
        float y = Mathf.Sin(t * 0.51f) * PerformanceBenchmarkCameraPathVerticalAmplitude;

        Vector3 origin = _hasBenchmarkSuiteCameraOrigin ? _benchmarkSuiteCameraOrigin : cameraPivot.GlobalPosition;
        cameraPivot.GlobalPosition = new Vector3(origin.X + x, origin.Y + y, origin.Z + z);
    }

    void InitPerformanceControlsUi()
    {
        var vbox = GetNodeOrNull<VBoxContainer>("UI/SelectionPanel/VBox");
        if (vbox == null)
            return;

        _perfControlsBox = GetNodeOrNull<VBoxContainer>("UI/SelectionPanel/VBox/PerfControlsBox");
        if (_perfControlsBox == null)
        {
            _perfControlsBox = new VBoxContainer { Name = "PerfControlsBox" };
            vbox.AddChild(_perfControlsBox);
        }

        if (_perfProfileOption == null)
        {
            _perfControlsBox.AddChild(new Label { Text = "Profil perf chunks" });
            _perfProfileOption = new OptionButton { Name = "PerfProfileOption" };
            _perfProfileOption.AddItem("Auto", (int)ChunkQualityProfile.Auto);
            _perfProfileOption.AddItem("Low", (int)ChunkQualityProfile.Low);
            _perfProfileOption.AddItem("Medium", (int)ChunkQualityProfile.Medium);
            _perfProfileOption.AddItem("High", (int)ChunkQualityProfile.High);
            _perfProfileOption.ItemSelected += OnPerfProfileOptionSelected;
            _perfControlsBox.AddChild(_perfProfileOption);
        }

        if (_perfAdaptiveBudgetCheck == null)
        {
            _perfAdaptiveBudgetCheck = new CheckBox { Name = "PerfAdaptiveBudgetCheck", Text = "Auto budget spawn chunks" };
            _perfAdaptiveBudgetCheck.Toggled += OnPerfAdaptiveBudgetToggled;
            _perfControlsBox.AddChild(_perfAdaptiveBudgetCheck);
        }

        if (_perfTrajectoryPrefetchCheck == null)
        {
            _perfTrajectoryPrefetchCheck = new CheckBox { Name = "PerfTrajectoryPrefetchCheck", Text = "Prefetch trajectoire caméra" };
            _perfTrajectoryPrefetchCheck.Toggled += OnPerfTrajectoryPrefetchToggled;
            _perfControlsBox.AddChild(_perfTrajectoryPrefetchCheck);
        }

        if (_perfRenderDistanceSlider == null)
        {
            _perfControlsBox.AddChild(new Label { Text = "Distance de rendu chunks" });
            _perfRenderDistanceSlider = new HSlider
            {
                Name = "PerfRenderDistanceSlider",
                MinValue = 60,
                MaxValue = 300,
                Step = 5,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _perfRenderDistanceSlider.ValueChanged += OnPerfRenderDistanceChanged;
            _perfControlsBox.AddChild(_perfRenderDistanceSlider);
            _perfRenderDistanceValueLabel = new Label { Name = "PerfRenderDistanceValueLabel" };
            _perfControlsBox.AddChild(_perfRenderDistanceValueLabel);
        }

        if (_perfSpawnBudgetSlider == null)
        {
            _perfControlsBox.AddChild(new Label { Text = "Budget spawn chunks/tick refresh" });
            _perfSpawnBudgetSlider = new HSlider
            {
                Name = "PerfSpawnBudgetSlider",
                MinValue = 8,
                MaxValue = 256,
                Step = 4,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _perfSpawnBudgetSlider.ValueChanged += OnPerfSpawnBudgetChanged;
            _perfControlsBox.AddChild(_perfSpawnBudgetSlider);
            _perfSpawnBudgetValueLabel = new Label { Name = "PerfSpawnBudgetValueLabel" };
            _perfControlsBox.AddChild(_perfSpawnBudgetValueLabel);
        }

        if (_perfHideGraceSlider == null)
        {
            _perfControlsBox.AddChild(new Label { Text = "Grace anti-pop chunks (s)" });
            _perfHideGraceSlider = new HSlider
            {
                Name = "PerfHideGraceSlider",
                MinValue = 0,
                MaxValue = 0.8,
                Step = 0.02,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            _perfHideGraceSlider.ValueChanged += OnPerfHideGraceChanged;
            _perfControlsBox.AddChild(_perfHideGraceSlider);
            _perfHideGraceValueLabel = new Label { Name = "PerfHideGraceValueLabel" };
            _perfControlsBox.AddChild(_perfHideGraceValueLabel);
        }
    }

    void SyncPerformanceControlsFromState()
    {
        _isSyncingPerfUi = true;
        if (_perfProfileOption != null)
            _perfProfileOption.Select((int)ChunkQualityProfileOverride);
        if (_perfAdaptiveBudgetCheck != null)
            _perfAdaptiveBudgetCheck.ButtonPressed = AdaptiveChunkSpawnBudgetEnabled;
        if (_perfTrajectoryPrefetchCheck != null)
            _perfTrajectoryPrefetchCheck.ButtonPressed = ChunkCameraTrajectoryPrefetchEnabled;
        if (_perfRenderDistanceSlider != null)
            _perfRenderDistanceSlider.Value = ChunkRenderDistance;
        if (_perfRenderDistanceValueLabel != null)
            _perfRenderDistanceValueLabel.Text = $"{Mathf.RoundToInt(ChunkRenderDistance)}";
        if (_perfSpawnBudgetSlider != null)
            _perfSpawnBudgetSlider.Value = ChunkSpawnBudgetPerRefresh;
        if (_perfSpawnBudgetValueLabel != null)
            _perfSpawnBudgetValueLabel.Text = $"{_runtimeChunkSpawnBudget}";
        if (_perfHideGraceSlider != null)
            _perfHideGraceSlider.Value = ChunkHideGraceSeconds;
        if (_perfHideGraceValueLabel != null)
            _perfHideGraceValueLabel.Text = $"{ChunkHideGraceSeconds:0.00}";
        _isSyncingPerfUi = false;
    }

    void OnPerfProfileOptionSelected(long index)
    {
        if (_isSyncingPerfUi || _perfProfileOption == null)
            return;

        var selected = (ChunkQualityProfile)_perfProfileOption.GetItemId((int)index);
        ChunkQualityProfileOverride = selected;
        if (selected == ChunkQualityProfile.Auto)
        {
            var detected = DetectChunkQualityProfile();
            ApplyChunkQualityProfile(detected, "ui_auto");
        }
        else
        {
            ApplyChunkQualityProfile(selected, "ui_override");
        }
        SavePerformanceRuntimeSettings();
        SyncPerformanceControlsFromState();
    }

    void OnPerfAdaptiveBudgetToggled(bool enabled)
    {
        if (_isSyncingPerfUi)
            return;
        AdaptiveChunkSpawnBudgetEnabled = enabled;
        SavePerformanceRuntimeSettings();
    }

    void OnPerfTrajectoryPrefetchToggled(bool enabled)
    {
        if (_isSyncingPerfUi)
            return;
        ChunkCameraTrajectoryPrefetchEnabled = enabled;
        SavePerformanceRuntimeSettings();
    }

    void OnPerfRenderDistanceChanged(double value)
    {
        if (_isSyncingPerfUi)
            return;
        ChunkRenderDistance = (float)value;
        if (_perfRenderDistanceValueLabel != null)
            _perfRenderDistanceValueLabel.Text = $"{Mathf.RoundToInt((float)value)}";
        SavePerformanceRuntimeSettings();
    }

    void OnPerfSpawnBudgetChanged(double value)
    {
        if (_isSyncingPerfUi)
            return;
        ChunkSpawnBudgetPerRefresh = Mathf.RoundToInt((float)value);
        if (!AdaptiveChunkSpawnBudgetEnabled)
            _runtimeChunkSpawnBudget = ChunkSpawnBudgetPerRefresh;
        if (_perfSpawnBudgetValueLabel != null)
            _perfSpawnBudgetValueLabel.Text = $"{_runtimeChunkSpawnBudget}";
        SavePerformanceRuntimeSettings();
    }

    void OnPerfHideGraceChanged(double value)
    {
        if (_isSyncingPerfUi)
            return;
        ChunkHideGraceSeconds = (float)value;
        if (_perfHideGraceValueLabel != null)
            _perfHideGraceValueLabel.Text = $"{ChunkHideGraceSeconds:0.00}";
        SavePerformanceRuntimeSettings();
    }

    void LoadSavedPerformanceRuntimeSettings()
    {
        if (!LoadSavedChunkQualityProfile)
            return;

        string path = GetVideoSettingsPath();
        if (!FileAccess.FileExists(path))
            return;

        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok)
            return;

        AdaptiveChunkSpawnBudgetEnabled = cfg.GetValue("video", "adaptive_chunk_spawn_budget_enabled", AdaptiveChunkSpawnBudgetEnabled).AsBool();
        ChunkCameraTrajectoryPrefetchEnabled = cfg.GetValue("video", "chunk_camera_trajectory_prefetch_enabled", ChunkCameraTrajectoryPrefetchEnabled).AsBool();
        ChunkRenderDistance = (float)cfg.GetValue("video", "chunk_render_distance", ChunkRenderDistance).AsDouble();
        ChunkSpawnBudgetPerRefresh = Mathf.RoundToInt((float)cfg.GetValue("video", "chunk_spawn_budget_per_refresh", ChunkSpawnBudgetPerRefresh).AsDouble());
        ChunkHideGraceSeconds = (float)cfg.GetValue("video", "chunk_hide_grace_seconds", ChunkHideGraceSeconds).AsDouble();
        _runtimeChunkSpawnBudget = ChunkSpawnBudgetPerRefresh;
    }

    void SavePerformanceRuntimeSettings()
    {
        string path = GetVideoSettingsPath();
        var cfg = new ConfigFile();
        if (FileAccess.FileExists(path))
            cfg.Load(path);
        cfg.SetValue("video", "chunk_quality_profile", ChunkQualityProfileOverride.ToString());
        cfg.SetValue("video", "adaptive_chunk_spawn_budget_enabled", AdaptiveChunkSpawnBudgetEnabled);
        cfg.SetValue("video", "chunk_camera_trajectory_prefetch_enabled", ChunkCameraTrajectoryPrefetchEnabled);
        cfg.SetValue("video", "chunk_render_distance", ChunkRenderDistance);
        cfg.SetValue("video", "chunk_spawn_budget_per_refresh", ChunkSpawnBudgetPerRefresh);
        cfg.SetValue("video", "chunk_hide_grace_seconds", ChunkHideGraceSeconds);
        cfg.Save(path);
    }

    static string GetVideoSettingsPath() => "user://video_settings.cfg";

    bool TryLoadSavedChunkQualityProfile(out ChunkQualityProfile profile)
    {
        profile = ChunkQualityProfile.Auto;
        string path = GetVideoSettingsPath();
        if (!FileAccess.FileExists(path))
            return false;

        var cfg = new ConfigFile();
        var err = cfg.Load(path);
        if (err != Error.Ok)
            return false;

        Variant value = cfg.GetValue("video", "chunk_quality_profile", "Auto");
        string asText = value.AsString();
        if (Enum.TryParse(asText, ignoreCase: true, out ChunkQualityProfile parsed)
            && parsed != ChunkQualityProfile.Auto)
        {
            profile = parsed;
            return true;
        }

        return false;
    }

    void SaveChunkQualityProfileSetting(ChunkQualityProfile profile)
    {
        if (profile == ChunkQualityProfile.Auto)
            return;

        string path = GetVideoSettingsPath();
        var cfg = new ConfigFile();
        if (FileAccess.FileExists(path))
            cfg.Load(path);
        cfg.SetValue("video", "chunk_quality_profile", profile.ToString());
        cfg.Save(path);
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
        if (_chunkVisuals.Count == 0)
        {
            _chunkVisibilityCursor = 0;
            _chunkVisibilityKeysScratch.Clear();
            _lastRenderedChunkCount = 0;
            _lastCulledDistanceChunkCount = 0;
            _lastCulledFrustumChunkCount = 0;
            _lastHysteresisKeptChunkCount = 0;
            return;
        }

        if (_chunkVisibilityCursor == 0 || _chunkVisibilityKeysScratch.Count != _chunkVisuals.Count)
        {
            _chunkVisibilityKeysScratch.Clear();
            foreach (var pair in _chunkVisuals)
                _chunkVisibilityKeysScratch.Add(pair.Key);

            _lastRenderedChunkCount = 0;
            _lastCulledDistanceChunkCount = 0;
            _lastCulledFrustumChunkCount = 0;
            _lastHysteresisKeptChunkCount = 0;
        }

        int batch = Mathf.Clamp(ChunkVisibilityBatchSize, 16, Math.Max(16, _chunkVisibilityKeysScratch.Count));
        int processed = 0;
        while (processed < batch && _chunkVisibilityCursor < _chunkVisibilityKeysScratch.Count)
        {
            var chunkPos = _chunkVisibilityKeysScratch[_chunkVisibilityCursor++];
            if (_chunkVisuals.TryGetValue(chunkPos, out var cv))
                ApplyChunkFog(cv, chunkPos);
            processed++;
        }

        if (_chunkVisibilityCursor >= _chunkVisibilityKeysScratch.Count)
            _chunkVisibilityCursor = 0;
    }

    void ApplyChunkFog(ChunkVisual cv, Vector3I chunkPos)
    {
        EvaluateChunkFog(chunkPos, out bool discovered, out bool visible);
        bool shouldRenderRaw = ShouldRenderChunk(chunkPos);
        bool shouldRender = ApplyChunkVisibilityHysteresis(chunkPos, shouldRenderRaw);

        if (cv.Solid != null)
        {
            if (!discovered || !shouldRender)
                cv.Solid.Visible = false;
            else
            {
                cv.Solid.Visible = cv.Solid.Multimesh.InstanceCount > 0;
                Color fogTint = visible ? Colors.White : new Color(0.5f, 0.42f, 0.36f);
                if (cv.Solid.MaterialOverride is ShaderMaterial sm)
                {
                    sm.SetShaderParameter("u_fog_tint", fogTint);
                }
                else
                {
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
                    mat.AlbedoColor = fogTint;
                }
            }
        }

        foreach (var r in cv.Resources)
            r.Visible = discovered && shouldRender;

        if (discovered && shouldRender)
            _lastRenderedChunkCount++;
    }

    bool ApplyChunkVisibilityHysteresis(Vector3I chunkPos, bool shouldRenderRaw)
    {
        if (!ChunkVisibilityHysteresisEnabled)
        {
            if (!shouldRenderRaw)
                _chunkHideGraceUntilByPos.Remove(chunkPos);
            return shouldRenderRaw;
        }

        double now = Time.GetTicksMsec() * 0.001;
        double grace = Mathf.Max(0.0f, ChunkHideGraceSeconds);
        if (shouldRenderRaw)
        {
            _chunkHideGraceUntilByPos[chunkPos] = now + grace;
            return true;
        }

        if (_chunkHideGraceUntilByPos.TryGetValue(chunkPos, out double until) && now <= until)
        {
            _lastHysteresisKeptChunkCount++;
            return true;
        }

        _chunkHideGraceUntilByPos.Remove(chunkPos);
        return false;
    }

    bool ShouldRenderChunk(Vector3I chunkPos)
    {
        if (camera == null)
            return true;

        Vector3 center = GetChunkCenterWorld(chunkPos);

        if (ChunkDistanceCullingEnabled && ChunkRenderDistance > 0.1f)
        {
            float maxDistSq = ChunkRenderDistance * ChunkRenderDistance;
            if (camera.GlobalPosition.DistanceSquaredTo(center) > maxDistSq)
            {
                _lastCulledDistanceChunkCount++;
                return false;
            }
        }

        if (ChunkFrustumCullingEnabled && !IsChunkInCameraFrustum(chunkPos, ChunkFrustumPadding))
        {
            _lastCulledFrustumChunkCount++;
            return false;
        }

        return true;
    }

    Vector3 GetChunkCenterWorld(Vector3I chunkPos)
    {
        float half = Map.CHUNK_SIZE * 0.5f;
        return new Vector3(
            chunkPos.X * Map.CHUNK_SIZE + half,
            chunkPos.Y * Map.CHUNK_SIZE + half,
            chunkPos.Z * Map.CHUNK_SIZE + half);
    }

    bool IsChunkInCameraFrustum(Vector3I chunkPos, float padding)
    {
        if (camera == null)
            return true;

        float minX = chunkPos.X * Map.CHUNK_SIZE - padding;
        float minY = chunkPos.Y * Map.CHUNK_SIZE - padding;
        float minZ = chunkPos.Z * Map.CHUNK_SIZE - padding;
        float maxX = (chunkPos.X + 1) * Map.CHUNK_SIZE + padding;
        float maxY = (chunkPos.Y + 1) * Map.CHUNK_SIZE + padding;
        float maxZ = (chunkPos.Z + 1) * Map.CHUNK_SIZE + padding;

        // Test corners + center to reduce false negatives at frustum edges.
        if (camera.IsPositionInFrustum(new Vector3(minX, minY, minZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(minX, minY, maxZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(minX, maxY, minZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(minX, maxY, maxZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(maxX, minY, minZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(maxX, minY, maxZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(maxX, maxY, minZ))) return true;
        if (camera.IsPositionInFrustum(new Vector3(maxX, maxY, maxZ))) return true;
        if (camera.IsPositionInFrustum(GetChunkCenterWorld(chunkPos))) return true;

        return false;
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

        int y0 = chunkPos.Y * Map.CHUNK_SIZE;
        int y1 = y0 + Map.CHUNK_SIZE - 1;
        int midY = y0 + Map.CHUNK_SIZE / 2;

        // Chunk où se tient un colon local : toujours afficher (le fog utilisait Y=12 aux coins XZ,
        // jamais marqués « discovered » alors que la grotte l’est → mesh entier invisible).
        var map = sim.World.CurrentMap;
        foreach (var c in map.Colonists)
        {
            if (c.OwnerId != localPlayerId)
                continue;
            var cc = map.WorldToChunk(c.Position);
            if (cc == chunkPos)
            {
                discovered = true;
                visible = true;
                return;
            }
        }

        if (sim.Vision.ChunksWithDiscoveredTile.Contains(chunkPos))
            discovered = true;

        int sy = Mathf.Clamp(Map.ColonistWalkY, y0, y1);
        Vector3I[] samples =
        {
            new(startX, midY, startZ),
            new(endX, midY, startZ),
            new(startX, midY, endZ),
            new(endX, midY, endZ),
            new(midX, midY, midZ),
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
            _chunkHideGraceUntilByPos.Remove(chunkPos);
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
        if (kind == WorldSelectionTargetKind.BuildBlocks && !_buildSelectionLayerInitialized)
        {
            _buildSelectionLayerY = Mathf.RoundToInt(camera?.GlobalPosition.Y ?? Map.ColonistWalkY);
            _buildSelectionLayerInitialized = true;
        }
        if (kind != WorldSelectionTargetKind.Trees)
            _selectedTreeTile = null;
        if (kind != WorldSelectionTargetKind.TerrainTiles)
        {
            _selectedTerrainTiles.Clear();
            _terrainMineDragTracking = false;
            _terrainMineDragStartCell = null;
            _terrainMineDragPreview.Clear();
        }
        if (kind != WorldSelectionTargetKind.BuildBlocks)
        {
            _buildPreviewCell = null;
        }

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
                WorldSelectionTargetKind.Colonists => "Cible : Colons (clic / cadre) — survol voxel comme blocs",
                WorldSelectionTargetKind.Trees => "Cible : Arbres — même visée que blocs (coupe V, Q/Alt fond) · survol surligné",
                WorldSelectionTargetKind.TerrainTiles => "Cible : Blocs — clic = 1 · glisser = ligne mineable · Shift = ajouter · B = mode build · Q/Alt = fond · V = coupe",
                WorldSelectionTargetKind.BuildBlocks => "Cible : Build — détails et couche dans le bandeau bleu ci‑dessous",
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
        RefreshBuildStatusLabel();
    }

    void RefreshBuildStatusLabel()
    {
        if (_buildStatusLabel == null)
            return;
        if (_selectionTargetKind != WorldSelectionTargetKind.BuildBlocks)
        {
            _buildStatusLabel.Visible = false;
            return;
        }

        _buildStatusLabel.Visible = true;
        var sb = new System.Text.StringBuilder();
        sb.Append("Couche Y = ").Append(_buildSelectionLayerY);
        sb.Append("  ·  Profondeur = ").Append(_buildExtrudeDepth);
        sb.AppendLine();
        sb.Append("Shift+molette ou PgUp/PgDn : couche  ·  R/F : profondeur  ·  Echap : annuler drag");
        if (_terrainMineDragTracking)
        {
            sb.AppendLine();
            sb.Append("Sélection… preview ").Append(_terrainMineDragPreview.Count).Append(" case(s)");
        }
        _buildStatusLabel.Text = sb.ToString();
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

    bool TryGetColonistsFocusAverage(out Vector3 focus)
    {
        focus = Vector3.Zero;
        if (selectionManager == null || selectionManager.SelectedColonists.Count == 0)
            return false;
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (var colon in selectionManager.SelectedColonists)
        {
            if (!colonVisuals.TryGetValue(colon, out var node) || !GodotObject.IsInstanceValid(node))
                continue;
            sum += node.GlobalPosition;
            count++;
        }
        if (count == 0)
            return false;
        focus = sum / count;
        return true;
    }

    /// <summary>Suivi caméra uniquement : colons / sélections stables — pas le survol souris.</summary>
    bool TryGetCameraFollowPoint(out Vector3 focus)
    {
        focus = Vector3.Zero;
        if (TryGetColonistsFocusAverage(out focus))
            return true;

        if (_selectedTreeTile.HasValue)
        {
            var c = _selectedTreeTile.Value;
            focus = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
            return true;
        }

        if (_selectedTerrainTiles.Count > 0)
        {
            foreach (var c in _selectedTerrainTiles)
            {
                focus = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
                return true;
            }
        }

        return false;
    }

    /// <summary>Smart cut shader + occlusion : inclut le bloc sous le curseur pour voir à travers vers la cible.</summary>
    bool TryGetSmartCutFocusPoint(out Vector3 focus)
    {
        focus = Vector3.Zero;
        if (TryGetColonistsFocusAverage(out focus))
            return true;

        if (CameraSmartCutUseTerrainHover && _terrainHoverCell.HasValue)
        {
            var c = _terrainHoverCell.Value;
            focus = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
            return true;
        }

        if (_selectedTreeTile.HasValue)
        {
            var c = _selectedTreeTile.Value;
            focus = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
            return true;
        }

        if (_selectedTerrainTiles.Count > 0)
        {
            foreach (var c in _selectedTerrainTiles)
            {
                focus = new Vector3(c.X + 0.5f, c.Y + 0.5f, c.Z + 0.5f);
                return true;
            }
        }

        return false;
    }

    void UpdateSmartCutShaderParameters(bool hasFocus, Vector3 focus)
    {
        if (_chunkVisuals.Count == 0 || camera == null)
            return;

        Vector3 camPos = camera.GlobalPosition;
        bool enabled = CameraSmartCutEnabled && hasFocus && camPos.DistanceTo(focus) <= Mathf.Max(2f, CameraSmartCutMaxDistance);

        float radius = Mathf.Max(0.05f, CameraSmartCutRadius);
        float fadeWidth = Mathf.Max(0.01f, CameraSmartCutFadeWidth);
        float minAlpha = Mathf.Clamp(CameraSmartCutMinAlpha, 0f, 1f);

        foreach (var pair in _chunkVisuals)
        {
            var solid = pair.Value.Solid;
            if (solid?.MaterialOverride is not ShaderMaterial sm)
                continue;

            sm.SetShaderParameter("u_smart_cut_enabled", enabled);
            sm.SetShaderParameter("u_cam_pos", camPos);
            sm.SetShaderParameter("u_focus_pos", focus);
            sm.SetShaderParameter("u_cut_radius", radius);
            sm.SetShaderParameter("u_cut_fade", fadeWidth);
            sm.SetShaderParameter("u_alpha_min", minAlpha);
        }
    }

    void ComputeTerrainOcclusionHiddenSet()
    {
        _terrainOcclusionHidden.Clear();
        if (camera == null || sim?.World?.CurrentMap == null)
            return;

        // Alt est souvent capturé par Windows : Q = percée fiable. Alt physique en complément.
        bool wantPeel = Input.IsPhysicalKeyPressed(Key.Q) || Input.IsPhysicalKeyPressed(Key.Alt);
        Vector3 smartFocus = Vector3.Zero;
        bool useSmartCut = CameraSmartCutEnabled && TryGetSmartCutFocusPoint(out smartFocus);
        if (!wantPeel && !useSmartCut)
            return;

        var from = camera.GlobalPosition;
        Vector3 dir;
        float rayMax = TerrainOcclusionRayMax;
        if (useSmartCut && !wantPeel)
        {
            Vector3 toFocus = smartFocus - from;
            float dist = toFocus.Length();
            if (dist < 1e-4f || dist > Mathf.Max(4f, CameraSmartCutMaxDistance))
                return;
            dir = toFocus / dist;
            rayMax = Mathf.Min(TerrainOcclusionRayMax, dist + 1.2f);
        }
        else
        {
            var mouse = camera.GetViewport().GetMousePosition();
            dir = camera.ProjectRayNormal(mouse).Normalized();
        }

        TerrainRayDda.CollectOrderedSolidCellsAlongRay(sim.World.CurrentMap, TerrainOcclusionRayMax, from, dir, _raySolidBuffer, 0.35f, rayMax);
        if (_raySolidBuffer.Count < 2)
            return;

        var deepest = _raySolidBuffer[^1];
        var focusCenter = new Vector3(deepest.X + 0.5f, deepest.Y + 0.5f, deepest.Z + 0.5f);
        if (!useSmartCut || wantPeel)
        {
            if (from.DistanceTo(focusCenter) > TerrainOcclusionMaxCameraDistance)
                return;
            float cy = from.Y;
            if (cy < TerrainOcclusionCameraMinY || cy > TerrainOcclusionCameraMaxY)
                return;
        }

        int keepLast = useSmartCut && !wantPeel
            ? Mathf.Clamp(CameraSmartCutKeepLastSolids, 0, 6)
            : 1;
        int hideCount = Mathf.Max(0, _raySolidBuffer.Count - keepLast);
        for (int i = 0; i < hideCount; i++)
            _terrainOcclusionHidden.Add(_raySolidBuffer[i]);
    }

    /// <summary>Repli sans physique : solides le long du rayon, même filtre que le mesh de picking (coupe + Q).</summary>
    List<Vector3I> BuildTerrainFallbackPickList(Vector3 from, Vector3 dir, float rayLen)
    {
        TerrainRayDda.CollectOrderedSolidCellsAlongRay(sim.World.CurrentMap, TerrainOcclusionRayMax, from, dir, _raySolidBuffer, 0f, rayLen);
        if (!_verticalSliceTerrainActive && _terrainOcclusionHidden.Count == 0)
            return _raySolidBuffer;
        _raySolidPickBuffer.Clear();
        foreach (var c in _raySolidBuffer)
        {
            if (!IsTerrainCellPickHidden(c))
                _raySolidPickBuffer.Add(c);
        }
        return _raySolidPickBuffer;
    }

    void RefreshVerticalSliceEvalCache()
    {
        if (camera == null)
            return;

        ulong frame = Engine.GetProcessFrames();
        if (frame == _verticalSliceEvalFrame)
            return;
        _verticalSliceEvalFrame = frame;

        _verticalSliceEvalCamPos = camera.GlobalPosition;
        _verticalSliceEvalForward = -camera.GlobalTransform.Basis.Z;

        Vector3 n = new Vector3(_verticalSliceEvalForward.X, 0f, _verticalSliceEvalForward.Z);
        if (n.LengthSquared() < 1e-8f)
        {
            _verticalSliceEvalPlaneN = Vector3.Right;
            _verticalSliceEvalPlaneNValid = false;
        }
        else
        {
            _verticalSliceEvalPlaneN = n.Normalized();
            _verticalSliceEvalPlaneNValid = true;
        }

        _verticalSliceEvalBottomFrac = Mathf.Clamp(VerticalSliceScreenBottomHideFraction, 0f, 1f);
        _verticalSliceEvalNeedScreenBottom = _verticalSliceEvalBottomFrac > 1e-4f;
        if (!_verticalSliceEvalNeedScreenBottom)
            return;

        if (VerticalSliceScreenBottomFastCameraApprox)
        {
            _verticalSliceEvalInvCam = camera.GlobalTransform.AffineInverse();
            _verticalSliceEvalTanHalfFov = Mathf.Tan(Mathf.DegToRad(camera.Fov * 0.5f));
        }
        else
        {
            _verticalSliceEvalViewportHeight = GetViewport().GetVisibleRect().Size.Y;
        }
    }

    bool IsTerrainCellHiddenByVerticalSlice(Vector3I cell)
    {
        if (!_verticalSliceTerrainActive || camera == null)
            return false;

        RefreshVerticalSliceEvalCache();

        Vector3 c = new Vector3(cell.X + 0.5f, cell.Y + 0.5f, cell.Z + 0.5f);
        Vector3 camPos = _verticalSliceEvalCamPos;

        bool hideVertical = false;
        if (VerticalSliceUseVerticalCut)
        {
            Vector3 n = _verticalSliceEvalPlaneNValid ? _verticalSliceEvalPlaneN : Vector3.Right;
            float s = (c - camPos).Dot(n);
            float delta = VerticalSliceClipPlaneDelta;

            if (VerticalSliceHideBehindCameraSide)
            {
                // P' recule la frontière « visible » : derrière P' ⇔ s < -delta (delta>0 = bande de tolérance côté caméra).
                hideVertical = s < -delta;
                if (VerticalSliceForwardCutDepth > 0f && s > VerticalSliceForwardCutDepth)
                    hideVertical = true;
            }
            else
            {
                hideVertical = s > delta;
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
        if (_verticalSliceEvalNeedScreenBottom)
        {
            if ((c - camPos).Dot(_verticalSliceEvalForward) > 0.02f)
            {
                if (VerticalSliceScreenBottomFastCameraApprox)
                {
                    Vector3 lp = _verticalSliceEvalInvCam * c;
                    float depth = -lp.Z;
                    if (depth > 0.02f)
                    {
                        float yNorm = lp.Y / (depth * _verticalSliceEvalTanHalfFov + 1e-6f);
                        hideScreenBottom = yNorm <= 2f * _verticalSliceEvalBottomFrac - 1f;
                    }
                }
                else
                {
                    Vector2 sp = camera.UnprojectPosition(c);
                    float vh = _verticalSliceEvalViewportHeight;
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

    static int QuantizeWorldPositionForPeel(float worldPos, float bucketWorld)
    {
        if (bucketWorld < 0.5f)
            bucketWorld = 0.5f;
        return Mathf.FloorToInt(worldPos / bucketWorld);
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
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceClipPlaneDelta);
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceForwardCutDepth);
            t ^= VerticalSlicePeelGroundWhenCameraLow ? 13UL : 8UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceGroundPeelBelowY);
            t ^= VerticalSliceGroundPeelHideAbove ? 17UL : 9UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceGroundPeelMargin);
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSliceScreenBottomHideFraction);
            t ^= VerticalSliceScreenBottomFastCameraApprox ? 19UL : 14UL;
            t ^= (ulong)(uint)BitConverter.SingleToInt32Bits(VerticalSlicePeelCameraBucketWorld);

            foreach (var cell in _terrainOcclusionHidden)
                t = t * 0xD1B54A32D192ED03UL + (ulong)cell.GetHashCode();

            if (_verticalSliceTerrainActive && camera != null)
            {
                Vector3 p = camera.GlobalPosition;
                float b = Mathf.Max(0.5f, VerticalSlicePeelCameraBucketWorld);
                t ^= (ulong)(uint)QuantizeWorldPositionForPeel(p.X, b) << 8;
                t ^= (ulong)(uint)QuantizeWorldPositionForPeel(p.Y, b) << 16;
                t ^= (ulong)(uint)QuantizeWorldPositionForPeel(p.Z, b) << 24;
                Vector3 lz = -camera.GlobalTransform.Basis.Z;
                Vector3 horiz = new Vector3(lz.X, 0f, lz.Z);
                if (horiz.LengthSquared() > 1e-10f)
                {
                    horiz = horiz.Normalized();
                    // Regard : ~8° par cran pour limiter les invalidations peel.
                    t ^= (ulong)(uint)QuantizeForSliceToken(horiz.X, 7f) << 4;
                    t ^= (ulong)(uint)QuantizeForSliceToken(horiz.Z, 7f) << 12;
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
        h = unchecked(h * 31 + _terrainMineDragPreview.Count);
        foreach (var v in _terrainMineDragPreview)
            h = unchecked(h * -1521134295 + v.GetHashCode());
        return h;
    }

    int ComputeTerrainHoverHighlightHash()
    {
        if (!TerrainHoverHighlightEnabled)
            return -173_173;
        if (_selectionTargetKind != WorldSelectionTargetKind.TerrainTiles
            && _selectionTargetKind != WorldSelectionTargetKind.Trees
            && _selectionTargetKind != WorldSelectionTargetKind.Colonists)
            return -173_174;
        unchecked
        {
            int h = _terrainHoverCell?.GetHashCode() ?? -1;
            return h * 397 ^ (int)_selectionTargetKind;
        }
    }

    void UpdateTerrainHoverCell()
    {
        if (_selectionTargetKind != WorldSelectionTargetKind.TerrainTiles
            && _selectionTargetKind != WorldSelectionTargetKind.Trees
            && _selectionTargetKind != WorldSelectionTargetKind.Colonists)
        {
            _terrainHoverCell = null;
            return;
        }

        if (!TerrainHoverHighlightEnabled
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

        bool pickedSliceFaceDda = _verticalSliceTerrainActive && !wantPeel
            && TryPickTerrainVisibleFaceDda(from, dir, pickRayLen, out pick);

        bool pickedPhysics = !pickedSliceFaceDda && TerrainPickUsePhysicsRaycast && !wantPeel
            && TerrainPhysicsPicker.TryPickCell(
                camera.GetWorld3D().DirectSpaceState,
                sim.World.CurrentMap,
                from,
                dir,
                pickRayLen,
                TerrainPhysicsPicker.TerrainPickCollisionMask,
                c => !IsTerrainCellPickHidden(c),
                out pick);

        if (!pickedPhysics && !pickedSliceFaceDda)
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

    /// <summary>
    /// Ordre « Aller vers » : même bloc visé que minage/sélection (coupe, occlusion, Q/Alt),
    /// puis première case d’air le long du rayon vers la caméra (évite le sol surface du vieux raycast grille).
    /// </summary>
    bool TryGetMoveOrderAnchorFromScreen(out Vector3I anchor)
    {
        anchor = default;
        if (camera == null || sim?.World?.CurrentMap == null)
            return false;

        Vector2 vpMouse = camera.GetViewport().GetMousePosition();
        if (TerrainPickUsePixelCenterRay)
            vpMouse += new Vector2(0.5f, 0.5f);

        if (!TryGetTerrainPickedCellViewport(vpMouse, out Vector3I hitSolid, respectPeelModifierKeys: true))
            return false;

        var from = camera.ProjectRayOrigin(vpMouse);
        float pickRayLen = Mathf.Max(TerrainOcclusionRayMax, TerrainPickMaxDistance + 120f);
        var map = sim.World.CurrentMap;

        Vector3 center = new Vector3(hitSolid.X + 0.5f, hitSolid.Y + 0.5f, hitSolid.Z + 0.5f);
        Vector3 towardCam = from - center;
        if (towardCam.LengthSquared() < 1e-8f)
            anchor = hitSolid + Vector3I.Up;
        else
        {
            towardCam = towardCam.Normalized();
            Vector3 p = center + towardCam * 0.06f;
            bool found = false;
            for (int i = 0; i < 56; i++)
            {
                p += towardCam * 0.11f;
                var c = new Vector3I(Mathf.FloorToInt(p.X), Mathf.FloorToInt(p.Y), Mathf.FloorToInt(p.Z));
                var tile = map.GetTile(c);
                bool solid = tile != null && tile.Solid;
                if (!solid)
                {
                    anchor = c;
                    found = true;
                    break;
                }

                if (solid && IsTerrainCellPickHidden(c))
                    continue;
            }

            if (!found)
                anchor = hitSolid + Vector3I.Up;
        }

        var anchorCenter = new Vector3(anchor.X + 0.5f, anchor.Y + 0.5f, anchor.Z + 0.5f);
        float maxPick = Mathf.Max(TerrainPickMaxDistance, pickRayLen + 64f);
        return from.DistanceTo(anchorCenter) <= maxPick;
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
                    && (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles
                        || _selectionTargetKind == WorldSelectionTargetKind.Trees
                        || _selectionTargetKind == WorldSelectionTargetKind.Colonists)
                    && _terrainHoverCell.HasValue
                    && w == _terrainHoverCell.Value)
                {
                    bool mine = tile != null && TerrainMineRules.IsMineableBlock(tile);
                    if (mine)
                        c = c.Lerp(TerrainMineHoverHighlightMix, mineHoverMixAmt);
                    else
                        c = c.Lerp(TerrainHoverHighlightMix, hoverMixAmt);
                }

                if (hl && _terrainMineDragPreview.Count > 0 && _terrainMineDragPreview.Contains(w))
                {
                    float prevAmt = Mathf.Clamp(TerrainDragLinePreviewAmount, 0f, 1f);
                    c = c.Lerp(TerrainDragLinePreviewMix, prevAmt);
                }

                if (hl && _selectedTerrainTiles.Contains(w))
                    c = c.Lerp(TerrainSelectionHighlightMix, mix);

                mm.SetInstanceColor(idx, c);
            }
        }
    }

    Vector2 GetTerrainPickViewportMouse()
    {
        Vector2 vpMouse = camera.GetViewport().GetMousePosition();
        if (TerrainPickUsePixelCenterRay)
            vpMouse += new Vector2(0.5f, 0.5f);
        return vpMouse;
    }

    void CancelVoxelDrag(bool ignoreNextLeftRelease = false)
    {
        _terrainMineDragTracking = false;
        _terrainMineDragStartCell = null;
        _terrainMineDragPreview.Clear();
        if (ignoreNextLeftRelease)
            _ignoreNextLeftReleaseAfterCancel = true;
    }

    bool TryGetBuildLayerCellViewport(Vector2 vpMouse, out Vector3I cell)
    {
        cell = default;
        if (camera == null)
            return false;

        Vector3 rayOrigin = camera.ProjectRayOrigin(vpMouse);
        Vector3 rayDir = camera.ProjectRayNormal(vpMouse);
        if (Mathf.Abs(rayDir.Y) < 1e-6f)
            return false;

        float t = (_buildSelectionLayerY - rayOrigin.Y) / rayDir.Y;
        if (t < 0f)
            return false;

        Vector3 p = rayOrigin + rayDir * t;
        cell = new Vector3I(
            Mathf.FloorToInt(p.X + 0.5f),
            _buildSelectionLayerY,
            Mathf.FloorToInt(p.Z + 0.5f));
        return true;
    }

    bool TryGetDragModeCellViewport(Vector2 vpMouse, out Vector3I cell)
    {
        cell = default;
        if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
        {
            return TryGetBuildLayerCellViewport(vpMouse, out cell);
        }
        return TryGetTerrainPickedCellViewport(vpMouse, out cell, respectPeelModifierKeys: true);
    }

    void FillDragPlaneArea(Vector3I a, Vector3I b, List<Vector3I> output)
    {
        output.Clear();

        int dx = Mathf.Abs(b.X - a.X);
        int dy = Mathf.Abs(b.Y - a.Y);
        int dz = Mathf.Abs(b.Z - a.Z);
        int maxCells = Mathf.Max(1, VoxelDragMaxCells);

        if (dy <= dx && dy <= dz) // plan horizontal XZ
        {
            int y = a.Y;
            int minX = Mathf.Min(a.X, b.X);
            int maxX = Mathf.Max(a.X, b.X);
            int minZ = Mathf.Min(a.Z, b.Z);
            int maxZ = Mathf.Max(a.Z, b.Z);
            for (int x = minX; x <= maxX; x++)
            for (int z = minZ; z <= maxZ; z++)
            {
                output.Add(new Vector3I(x, y, z));
                if (output.Count >= maxCells)
                    return;
            }
            return;
        }

        if (dz <= dx && dz <= dy) // plan vertical XY
        {
            int z = a.Z;
            int minX = Mathf.Min(a.X, b.X);
            int maxX = Mathf.Max(a.X, b.X);
            int minY = Mathf.Min(a.Y, b.Y);
            int maxY = Mathf.Max(a.Y, b.Y);
            for (int x = minX; x <= maxX; x++)
            for (int y = minY; y <= maxY; y++)
            {
                output.Add(new Vector3I(x, y, z));
                if (output.Count >= maxCells)
                    return;
            }
            return;
        }

        // plan vertical YZ
        int xx = a.X;
        int minYY = Mathf.Min(a.Y, b.Y);
        int maxYY = Mathf.Max(a.Y, b.Y);
        int minZZ = Mathf.Min(a.Z, b.Z);
        int maxZZ = Mathf.Max(a.Z, b.Z);
        for (int y = minYY; y <= maxYY; y++)
        for (int z = minZZ; z <= maxZZ; z++)
        {
            output.Add(new Vector3I(xx, y, z));
            if (output.Count >= maxCells)
                return;
        }
    }

    void FillBuildExtrudeArea(Vector3I a, Vector3I b, List<Vector3I> output)
    {
        output.Clear();
        int minX = Mathf.Min(a.X, b.X);
        int maxX = Mathf.Max(a.X, b.X);
        int minZ = Mathf.Min(a.Z, b.Z);
        int maxZ = Mathf.Max(a.Z, b.Z);
        int baseY = _buildSelectionLayerY;
        int depth = Mathf.Clamp(_buildExtrudeDepth, 1, 128);
        int maxCells = Mathf.Max(1, VoxelDragMaxCells);

        for (int y = baseY; y < baseY + depth; y++)
        for (int x = minX; x <= maxX; x++)
        for (int z = minZ; z <= maxZ; z++)
        {
            output.Add(new Vector3I(x, y, z));
            if (output.Count >= maxCells)
                return;
        }
    }

    void BuildOrderedMineSelection(System.Collections.Generic.IEnumerable<Vector3I> source, List<Vector3I> output)
    {
        output.Clear();
        var map = sim?.World?.CurrentMap;
        if (map == null)
            return;

        foreach (var c in source)
        {
            if (TerrainMineRules.IsMineableBlock(map.GetTile(c)))
                output.Add(c);
        }

        VoxelSelectionOrder.SortForMineEnqueue(output);
    }

    void UpdateVoxelDragPreview()
    {
        if ((_selectionTargetKind != WorldSelectionTargetKind.TerrainTiles
                && _selectionTargetKind != WorldSelectionTargetKind.BuildBlocks)
            || !_terrainMineDragTracking
            || !Input.IsMouseButtonPressed(MouseButton.Left)
            || sim?.World?.CurrentMap == null
            || camera == null
            || !_terrainMineDragStartCell.HasValue)
        {
            if (_terrainMineDragPreview.Count > 0)
                _terrainMineDragPreview.Clear();
            return;
        }

        Vector2 vp = GetTerrainPickViewportMouse();
        if (!TryGetDragModeCellViewport(vp, out Vector3I endCell))
        {
            if (_terrainMineDragPreview.Count > 0)
                _terrainMineDragPreview.Clear();
            return;
        }

        if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
            FillBuildExtrudeArea(_terrainMineDragStartCell.Value, endCell, _terrainLineBuffer);
        else
            FillDragPlaneArea(_terrainMineDragStartCell.Value, endCell, _terrainLineBuffer);
        _terrainMineDragPreview.Clear();
        var map = sim.World.CurrentMap;
        if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
        {
            BuildOrderedMineSelection(_terrainLineBuffer, _orderedVoxelSelectionBuffer);
            foreach (var c in _orderedVoxelSelectionBuffer)
            {
                _terrainMineDragPreview.Add(c);
            }
            return;
        }

        foreach (var c in _terrainLineBuffer)
        {
            var t = map.GetTile(c);
            bool alreadyPlanned = sim.jobBoard.HasActiveJobOnTarget(c, JobType.BuildBlock);
            if ((t == null || !t.Solid) || alreadyPlanned)
                _terrainMineDragPreview.Add(c);
        }
    }

    void FinishVoxelDragOrClick(InputEventMouseButton mb)
    {
        _terrainMineDragPreview.Clear();

        if (!_terrainMineDragTracking)
            return;

        bool shift = Input.IsPhysicalKeyPressed(Key.Shift);
        Vector2 endScreen = mb.Position;
        float dist = (endScreen - _terrainMineDragStartScreen).Length();
        Vector2 vpEnd = endScreen;
        if (TerrainPickUsePixelCenterRay)
            vpEnd += new Vector2(0.5f, 0.5f);

        bool smallDrag = dist <= TerrainMineDragMaxClickPixels;

        if (smallDrag && _selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
            TryPickTerrainTilesAtViewport(GetTerrainPickViewportMouse());
        else if (smallDrag && _selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
        {
            if (_buildPreviewCell.HasValue)
                TryQueueBuildBlockAtCell(_buildPreviewCell.Value);
        }
        else if (_terrainMineDragStartCell.HasValue && TryGetDragModeCellViewport(vpEnd, out Vector3I endCell))
        {
            if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
                FillBuildExtrudeArea(_terrainMineDragStartCell.Value, endCell, _terrainLineBuffer);
            else
                FillDragPlaneArea(_terrainMineDragStartCell.Value, endCell, _terrainLineBuffer);
            var map = sim.World.CurrentMap;
            if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
            {
                if (!shift)
                    _selectedTerrainTiles.Clear();
                BuildOrderedMineSelection(_terrainLineBuffer, _orderedVoxelSelectionBuffer);
                foreach (var c in _orderedVoxelSelectionBuffer)
                {
                    _selectedTerrainTiles.Add(c);
                }
                UpdateSelectionTargetUi();
            }
            else if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks)
                TryQueueBuildSiteFromCells(_terrainLineBuffer);
        }
        else if (_selectionTargetKind == WorldSelectionTargetKind.TerrainTiles)
            TryPickTerrainTilesAtViewport(vpEnd);
        else if (_selectionTargetKind == WorldSelectionTargetKind.BuildBlocks && _buildPreviewCell.HasValue)
            TryQueueBuildBlockAtCell(_buildPreviewCell.Value);

        CancelVoxelDrag();
    }

    void TryPickTerrainTilesAtViewport(Vector2 viewportMouse)
    {
        bool shift = Input.IsPhysicalKeyPressed(Key.Shift);
        if (!TryGetTerrainPickedCellViewport(viewportMouse, out Vector3I pick, respectPeelModifierKeys: true))
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

    void TryPickTerrainTileAtScreen() => TryPickTerrainTilesAtViewport(GetTerrainPickViewportMouse());

    bool TryGetBuildPlacementCellFromScreen(out Vector3I placeCell)
    {
        placeCell = default;
        return TryGetBuildPlacementCellViewport(GetTerrainPickViewportMouse(), out placeCell);
    }

    bool TryGetBuildPlacementCellViewport(Vector2 vpMouse, out Vector3I placeCell)
    {
        placeCell = default;
        if (!TryGetBuildLayerCellViewport(vpMouse, out Vector3I pick))
            return false;
        var map = sim.World.CurrentMap;
        var existing = map.GetTile(pick);
        if (existing == null)
            return false;

        bool planned = sim.jobBoard != null && sim.jobBoard.HasActiveJobOnTarget(pick, JobType.BuildBlock);
        if (existing.Solid)
        {
            // En mode build, si la cellule visée est déjà un mur solide,
            // on propose un placement latéral plutôt que "dans" le bloc.
            if (!TryFindBuildSidePlacementCell(pick, out placeCell))
                return false;
            return true;
        }

        placeCell = pick;
        return true;
    }

    bool TryFindBuildSidePlacementCell(Vector3I solidCell, out Vector3I sideCell)
    {
        sideCell = default;
        var map = sim?.World?.CurrentMap;
        if (map == null || camera == null)
            return false;

        ReadOnlySpan<Vector3I> offsets = stackalloc Vector3I[]
        {
            new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
            new(1, 0, 1), new(1, 0, -1), new(-1, 0, 1), new(-1, 0, -1),
        };

        Vector3 center = new Vector3(solidCell.X + 0.5f, solidCell.Y + 0.5f, solidCell.Z + 0.5f);
        Vector3 towardCam = (camera.GlobalPosition - center).Normalized();
        bool found = false;
        float bestScore = float.NegativeInfinity;
        Vector3I best = default;

        foreach (var d in offsets)
        {
            var c = solidCell + d;
            var t = map.GetTile(c);
            if (t == null)
                continue;

            bool planned = sim.jobBoard != null && sim.jobBoard.HasActiveJobOnTarget(c, JobType.BuildBlock);
            if (t.Solid && !planned)
                continue;

            Vector3 dir = new Vector3(d.X, d.Y, d.Z).Normalized();
            float score = dir.Dot(towardCam);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = c;
            found = true;
        }

        if (!found)
            return false;

        sideCell = best;
        return true;
    }

    void TryQueueBuildBlockAtHoveredFace()
    {
        if (sim?.World?.CurrentMap == null)
            return;
        if (!TryGetBuildPlacementCellFromScreen(out Vector3I placeCell))
            return;
        TryQueueBuildBlockAtCell(placeCell);
    }

    void TryQueueBuildBlockAtCell(Vector3I placeCell)
    {
        var existingTile = sim.World.CurrentMap.GetTile(placeCell);
        if (existingTile != null && existingTile.Solid
            && !sim.jobBoard.HasActiveJobOnTarget(placeCell, JobType.BuildBlock))
            return;

        if (sim.jobBoard.HasActiveJobOnTarget(placeCell, JobType.BuildBlock))
            return;

        _commandPipeline.QueueBuild(new[] { placeCell }, BuildPlacementTileType, JobPriority.Normal);
        GD.Print($"[Jobs] Construction ajoutée @ {placeCell} (file : {sim.jobBoard.ActiveJobCount})");
    }

    void TryQueueBuildSiteFromCells(List<Vector3I> cells)
    {
        if (sim?.World?.CurrentMap == null || cells == null || cells.Count == 0)
            return;

        var map = sim.World.CurrentMap;
        var valid = new List<Vector3I>();
        foreach (var c in cells)
        {
            var t = map.GetTile(c);
            bool planned = sim.jobBoard.HasActiveJobOnTarget(c, JobType.BuildBlock);
            if (t != null && t.Solid && !planned)
                continue;
            if (planned)
                continue;
            valid.Add(c);
        }

        if (valid.Count >= 2)
        {
            _commandPipeline.QueueBuild(valid, BuildPlacementTileType, JobPriority.Normal);
            return;
        }

        if (valid.Count == 1)
            TryQueueBuildBlockAtCell(valid[0]);
    }

    void InitBuildPreviewGhost()
    {
        _buildPreviewGhost = new MeshInstance3D();
        _buildPreviewGhost.Mesh = _tileCubeMesh;
        _buildPreviewMaterial = new StandardMaterial3D();
        _buildPreviewMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _buildPreviewMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _buildPreviewMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        ApplyBuildPreviewGhostAppearance();
        _buildPreviewGhost.MaterialOverride = _buildPreviewMaterial;
        _buildPreviewGhost.Visible = false;
        AddChild(_buildPreviewGhost);
    }

    void InitBuildDragPreviewVisual()
    {
        _buildDragPreview = new MultiMeshInstance3D();
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            InstanceCount = 0,
            Mesh = _tileCubeMesh
        };
        _buildDragPreview.Multimesh = mm;
        _buildDragPreviewMaterial = new StandardMaterial3D();
        _buildDragPreviewMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _buildDragPreviewMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _buildDragPreviewMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _buildDragPreviewMaterial.NoDepthTest = true;
        _buildDragPreviewMaterial.EmissionEnabled = true;
        _buildDragPreviewMaterial.Emission = new Color(0.25f, 0.25f, 0.25f);
        _buildDragPreviewMaterial.EmissionEnergyMultiplier = 0.6f;
        _buildDragPreview.MaterialOverride = _buildDragPreviewMaterial;
        _buildDragPreview.Visible = false;
        AddChild(_buildDragPreview);
    }

    void InitBuildQueuedPreviewVisual()
    {
        _buildQueuedPreview = new MultiMeshInstance3D();
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            InstanceCount = 0,
            Mesh = _tileCubeMesh
        };
        _buildQueuedPreview.Multimesh = mm;
        _buildQueuedPreviewMaterial = new StandardMaterial3D();
        _buildQueuedPreviewMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _buildQueuedPreviewMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _buildQueuedPreviewMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _buildQueuedPreviewMaterial.NoDepthTest = true;
        _buildQueuedPreview.MaterialOverride = _buildQueuedPreviewMaterial;
        _buildQueuedPreview.Visible = false;
        AddChild(_buildQueuedPreview);
    }

    void InitVirtualScaffoldPreviewVisual()
    {
        _virtualScaffoldPreview = new MultiMeshInstance3D();
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = false,
            InstanceCount = 0,
            Mesh = _tileCubeMesh
        };
        _virtualScaffoldPreview.Multimesh = mm;
        _virtualScaffoldPreviewMaterial = new StandardMaterial3D();
        _virtualScaffoldPreviewMaterial.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _virtualScaffoldPreviewMaterial.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        _virtualScaffoldPreviewMaterial.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        _virtualScaffoldPreviewMaterial.NoDepthTest = true;
        _virtualScaffoldPreviewMaterial.EmissionEnabled = true;
        _virtualScaffoldPreviewMaterial.Emission = new Color(0.85f, 0.65f, 0.25f);
        _virtualScaffoldPreviewMaterial.EmissionEnergyMultiplier = 0.45f;
        _virtualScaffoldPreview.MaterialOverride = _virtualScaffoldPreviewMaterial;
        _virtualScaffoldPreview.Visible = false;
        AddChild(_virtualScaffoldPreview);
    }

    void ApplyBuildPreviewGhostAppearance()
    {
        if (_buildPreviewMaterial == null)
            return;
        Color baseRgb = GetTileColor(BuildPlacementTileType);
        float a = Mathf.Clamp(BuildPreviewGhostAlpha, 0.05f, 1f);
        _buildPreviewMaterial.AlbedoColor = new Color(baseRgb.R, baseRgb.G, baseRgb.B, a);
    }

    void UpdateBuildPreviewGhost()
    {
        if (_buildPreviewGhost == null)
            return;

        if (_selectionTargetKind != WorldSelectionTargetKind.BuildBlocks || sim?.World?.CurrentMap == null)
        {
            _buildPreviewCell = null;
            _buildPreviewGhost.Visible = false;
            return;
        }

        bool hasCell = false;
        Vector3I placeCell = default;
        hasCell = TryGetBuildPlacementCellFromScreen(out placeCell);

        if (hasCell)
        {
            if (_terrainMineDragTracking)
            {
                _buildPreviewGhost.Visible = false;
                return;
            }
            _buildPreviewCell = placeCell;
            ApplyBuildPreviewGhostAppearance();
            _buildPreviewGhost.Position = new Vector3(placeCell.X, placeCell.Y, placeCell.Z);
            _buildPreviewGhost.Visible = true;
            return;
        }

        _buildPreviewCell = null;
        _buildPreviewGhost.Visible = false;
    }

    void UpdateBuildDragPreviewVisual()
    {
        if (_buildDragPreview?.Multimesh == null || _buildDragPreviewMaterial == null)
            return;

        bool show = _selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
            && _terrainMineDragTracking
            && _terrainMineDragPreview.Count > 0;
        if (!show)
        {
            _buildDragPreview.Visible = false;
            _buildDragPreview.Multimesh.InstanceCount = 0;
            return;
        }

        Color baseRgb = GetTileColor(BuildPlacementTileType);
        float a = Mathf.Clamp(BuildPreviewGhostAlpha * 0.85f, 0.05f, 1f);
        _buildDragPreviewMaterial.AlbedoColor = new Color(baseRgb.R, baseRgb.G, baseRgb.B, a);

        var mm = _buildDragPreview.Multimesh;
        mm.InstanceCount = _terrainMineDragPreview.Count;
        int i = 0;
        foreach (var c in _terrainMineDragPreview)
        {
            mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(c.X, c.Y, c.Z)));
            i++;
        }
        _buildDragPreview.Visible = true;
    }

    void UpdateBuildQueuedPreviewVisual()
    {
        if (_buildQueuedPreview?.Multimesh == null
            || _buildQueuedPreviewMaterial == null
            || sim?.jobBoard == null
            || sim?.World?.CurrentMap == null)
            return;

        if (_selectionTargetKind != WorldSelectionTargetKind.BuildBlocks)
        {
            _buildQueuedPreview.Visible = false;
            _buildQueuedPreview.Multimesh.InstanceCount = 0;
            return;
        }

        if (_lastBuildQueuedPreviewRefreshTick == sim.Tick)
            return;
        _lastBuildQueuedPreviewRefreshTick = sim.Tick;

        sim.jobBoard.CopyActiveTargets(JobType.BuildBlock, _queuedBuildTargetsBuffer);
        if (_queuedBuildTargetsBuffer.Count == 0)
        {
            _buildQueuedPreview.Visible = false;
            _buildQueuedPreview.Multimesh.InstanceCount = 0;
            return;
        }

        var map = sim.World.CurrentMap;
        int count = 0;
        foreach (var c in _queuedBuildTargetsBuffer)
        {
            var t = map.GetTile(c);
            if (t == null || !t.Solid)
                count++;
        }

        if (count == 0)
        {
            _buildQueuedPreview.Visible = false;
            _buildQueuedPreview.Multimesh.InstanceCount = 0;
            return;
        }

        Color baseRgb = GetTileColor(BuildPlacementTileType);
        float a = Mathf.Clamp(BuildPreviewGhostAlpha * 0.7f, 0.05f, 1f);
        _buildQueuedPreviewMaterial.AlbedoColor = new Color(baseRgb.R, baseRgb.G, baseRgb.B, a);

        var mm = _buildQueuedPreview.Multimesh;
        mm.InstanceCount = count;
        int i = 0;
        foreach (var c in _queuedBuildTargetsBuffer)
        {
            var t = map.GetTile(c);
            if (t != null && t.Solid)
                continue;
            mm.SetInstanceTransform(i++, new Transform3D(Basis.Identity, new Vector3(c.X, c.Y, c.Z)));
        }
        _buildQueuedPreview.Visible = true;
    }

    void UpdateVirtualScaffoldPreviewVisual()
    {
        if (_virtualScaffoldPreview?.Multimesh == null
            || _virtualScaffoldPreviewMaterial == null
            || sim == null)
            return;

        bool show = ShowVirtualScaffolds
            && _selectionTargetKind == WorldSelectionTargetKind.BuildBlocks
            && sim.VirtualScaffoldCells != null
            && sim.VirtualScaffoldCells.Count > 0;

        if (!show)
        {
            if (_lastVirtualScaffoldPreviewVisible)
            {
                _virtualScaffoldPreview.Visible = false;
                _virtualScaffoldPreview.Multimesh.InstanceCount = 0;
                _lastVirtualScaffoldPreviewVisible = false;
            }
            return;
        }

        _lastVirtualScaffoldPreviewVisible = true;
        int version = sim.VirtualScaffoldVersion;
        if (_lastVirtualScaffoldPreviewVersion == version)
        {
            if (!_virtualScaffoldPreview.Visible)
                _virtualScaffoldPreview.Visible = true;
            return;
        }
        _lastVirtualScaffoldPreviewVersion = version;

        Color baseRgb = GetTileColor("scaffold");
        float a = Mathf.Clamp(VirtualScaffoldAlpha, 0.05f, 1f);
        _virtualScaffoldPreviewMaterial.AlbedoColor = new Color(baseRgb.R, baseRgb.G, baseRgb.B, a);

        var mm = _virtualScaffoldPreview.Multimesh;
        int count = sim.VirtualScaffoldCells.Count;
        mm.InstanceCount = count;
        int i = 0;
        foreach (var c in sim.VirtualScaffoldCells)
            mm.SetInstanceTransform(i++, new Transform3D(Basis.Identity, new Vector3(c.X, c.Y, c.Z)));
        _virtualScaffoldPreview.Visible = true;
    }

    void TryPickTreeAtScreen()
    {
        // Même chaîne que blocs / minage : DDA coupe V, physique, Q/Alt = fond, pixel centré si export.
        if (!TryGetTerrainPickedCellFromScreen(out Vector3I pick, respectPeelModifierKeys: true))
        {
            _selectedTreeTile = null;
            UpdateSelectionTargetUi();
            return;
        }

        var t = sim.World.CurrentMap.GetTile(pick);
        if (t != null && t.Type == "tree")
        {
            _selectedTreeTile = pick;
            UpdateSelectionTargetUi();
            return;
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

        _commandPipeline.QueueCutTree(tilePos, JobPriority.Normal);

        GD.Print($"[Jobs] Coupe d'arbre ajoutée à la file : {tilePos} (total actifs : {sim.jobBoard.ActiveJobCount})");

        _selectedTreeTile = null;
        UpdateSelectionTargetUi();
    }

    void OnMineStonePressed()
    {
        if (sim?.World == null || _selectedTerrainTiles.Count == 0)
            return;

        _mineSelectionSortBuffer.Clear();
        BuildOrderedMineSelection(_selectedTerrainTiles, _mineSelectionSortBuffer);
        int before = sim.jobBoard.ActiveJobCount;
        _commandPipeline.QueueMine(_mineSelectionSortBuffer, JobPriority.Normal);
        int added = Mathf.Max(0, sim.jobBoard.ActiveJobCount - before);

        if (added > 0)
            GD.Print($"[Jobs] {added} job(s) minage ajouté(s) (file : {sim.jobBoard.ActiveJobCount})");

        _selectedTerrainTiles.Clear();
        UpdateSelectionTargetUi();
    }

    void RefreshJobsQueueUi()
    {
        if (_uiModule == null || sim == null)
            return;

        if (_jobsQueueLabel != null)
            _jobsQueueLabel.Text = _uiModule.BuildJobsQueueText(sim);
        if (_logisticsLabel != null)
            _logisticsLabel.Text = _uiModule.BuildLogisticsText(sim);
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
            "scaffold" => new Color(0.68f, 0.53f, 0.32f),
            // Noir pur = trop dur à lire visuellement avec les ombres.
            "build_black" => new Color(0.07f, 0.07f, 0.08f),
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