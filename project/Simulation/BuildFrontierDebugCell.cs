using Godot;

public enum BuildFrontierDebugState
{
    Unsupported = 0,
    SupportedNoWalkSpot = 1,
    Ready = 2,
}

public sealed class BuildFrontierDebugCell
{
    public Vector3I Position;
    public BuildFrontierDebugState State;
}
