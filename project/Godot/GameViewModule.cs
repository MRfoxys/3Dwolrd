using System.Collections.Generic;
using Godot;

public sealed class GameViewModule
{
    public void RenderColonists(
        Dictionary<Colonist, Node3D> colonVisuals,
        SelectionManager selectionManager,
        StandardMaterial3D selectedMat,
        StandardMaterial3D defaultMat,
        Simulation sim,
        float frameDeltaSeconds)
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
            mesh.MaterialOverride = selectionManager.SelectedColonists.Contains(colon) ? selectedMat : defaultMat;

            var tilePos = colon.Position;
            if (colon.OwnerId != 0 && !sim.Vision.IsVisible(tilePos))
            {
                node.Visible = false;
                continue;
            }

            node.Visible = true;
            float maxStep = colon.MoveSpeed * frameDeltaSeconds;
            node.Position = node.Position.MoveToward(renderPos, maxStep);
        }
    }
}
