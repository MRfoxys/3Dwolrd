using System.Collections.Generic;
using Godot;
using System;

public class Simulation
{
    public int Tick;
    public World World;

    Pathfinder pathfinder;

    public Queue<PlayerCommand> CommandQueue = new();

    public void Init()
    {
        var map = World.CurrentMap;
        pathfinder = new Pathfinder(
            (pos) => map.GetTile(pos),
            (pos) => IsOccupied(pos) // 🔥 injection propre
        );
    }

    public void Update()
    {
        Tick++;

        // commandes joueur
        while (CommandQueue.Count > 0)
        {
            var cmd = CommandQueue.Dequeue();
            ApplyCommand(cmd);
        }

        // déplacement des colons
        foreach (var colon in World.CurrentMap.Colonists)
        {
            UpdateColon(colon);
        }

    }

    void ApplyCommand(PlayerCommand cmd)
    {
        if (cmd.Type == "MOVE")
        {
            HashSet<Vector3I> reserved = new();
            var colon = World.CurrentMap.Colonists[cmd.EntityId];
            colon.HasPathFailed = false;

            var start = new Vector3I(colon.X, colon.Y, colon.Z);
            var rawTarget = new Vector3I(cmd.X, cmd.Y, cmd.Z);

            //trouve une case libre
            var end = FindNearestFreeWithReservation(rawTarget, reserved);

            if (!pathfinder.IsWalkable(end))
            {
                GD.Print("❌ TARGET NON WALKABLE");
                return;
            }

            reserved.Add(end);
            colon.Target = end;
            GD.Print("=== APPLY COMMAND ===");

            var path = pathfinder.FindPath(start, end);

            if (path == null)
            {
                GD.Print("❌ PATH NULL");
                colon.HasPathFailed = true;
                colon.Path = null;
                return;
            }
            if ( path.Count <= 1)
            {
                GD.Print("❌ PATH INUTILISABLE");
                colon.HasPathFailed = true;
                colon.Path = null;
                return;
            }

            path = pathfinder.SmoothPath(path);

            if (path == null || path.Count <= 1)
            {
                GD.Print("❌ PATH INUTILISABLE");
                colon.HasPathFailed = true;
                colon.Path = null;
                return;
            }

            // OK
            path.RemoveAt(0);

            colon.Path = path;
            colon.HasPathFailed = false;
            colon.Target = end;

            GD.Print("✅ PATH OK: ", path.Count);


//debug part
            var below = World.CurrentMap.GetTile(new Vector3I(end.X, end.Y - 1, end.Z));
            var current = World.CurrentMap.GetTile(end);

            GD.Print("TARGET: ", end);
            GD.Print("BELOW SOLID: ", below.Solid);
            GD.Print("CURRENT SOLID: ", current.Solid);
            if (path == null)
            {
                GD.Print("❌ PATH = NULL");
                return;
            }

            if (path.Count <= 1)
            {
                GD.Print("❌ PATH TROP COURT");
                return;
            }
			GD.Print("MOVE reçu pour entity ", cmd.EntityId);
            GD.Print("TARGET: ", end);
            GD.Print("PATH COUNT: ", path?.Count ?? 0);
            GD.Print("START: ", start);
            GD.Print("END: ", end);

            var tile = World.CurrentMap.GetTile(end);
            GD.Print("END TILE SOLID: ", tile.Solid);
        }
    }

    void UpdateColon(Colonist colon)
    {
        if (colon.RepathTimer > 5)
        {
            var start = new Vector3I(colon.X, colon.Y, colon.Z);

            var newPath = pathfinder.FindPath(start, colon.Target);

            if (newPath == null || newPath.Count <= 1)
            {
                colon.HasPathFailed = true;
                colon.Path = null;
                return; // 🔥 STOP
            }

            if (newPath != null && newPath.Count > 1)
            {
                newPath.RemoveAt(0);
                colon.Path = newPath;
                colon.HasPathFailed = false;
            }
            else
            {
                GD.Print("❌ REPATH IMPOSSIBLE → STOP");
                colon.HasPathFailed = true;
                colon.Path = null;
                return; // 🔥 STOP HARD
            }

            colon.RepathTimer = 0;
        }

        if (colon.HasPathFailed)
            return;

        if (colon.Path == null || colon.Path.Count == 0)
            return;

        var next = colon.Path[0];

        // blocage
        if (!pathfinder.IsWalkable(next) || IsOccupied(next, colon))
        {
            colon.Path = null;
            colon.HasPathFailed = true;
            return;
        }

        // 🔥 progression fluide
        colon.MoveProgress += 0.2f; // vitesse (ajuste ici)

        if (colon.MoveProgress >= 1f)
        {
            colon.X = next.X;
            colon.Y = next.Y;
            colon.Z = next.Z;

            colon.Path.RemoveAt(0);
            colon.MoveProgress = 0f;
        }
    }

    Vector3I FindNearestFree(Vector3I target)
    {
        if (IsWalkableAndFree(target))
            return target;

        int radius = 1;

        while (radius < 10)
        {
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                var pos = new Vector3I(
                    target.X + x,
                    target.Y,
                    target.Z + z
                );

                if (IsWalkableAndFree(pos))
                    return pos;
            }

            radius++;
        }

        return target;
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

            if (c.X == pos.X && c.Y == pos.Y && c.Z == pos.Z)
                return true;
        }
    return false;
    }


   public Vector3I FindNearestFreeWithReservation(Vector3I target, HashSet<Vector3I> reserved)
    {
        if (IsWalkableAndFree(target) && !reserved.Contains(target))
            return target;

        int radius = 1;

        while (radius < 10)
        {
            for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
            {
                var pos = new Vector3I(target.X + x, target.Y, target.Z + z);

                if (IsWalkableAndFree(pos) && !reserved.Contains(pos))
                    return pos;
            }

            radius++;
        }

        return target;
    }

}