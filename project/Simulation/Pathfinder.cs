using System;
using System.Collections.Generic;
using Godot;

public class Pathfinder
{
    Func<Vector3I, bool> isOccupied;
    Func<Vector3I, Tile> getTile;
    Func<Vector3I, bool> hasVirtualScaffold;

    Vector3I[] directions = new Vector3I[]
    {
        new(1,0,0),
        new(-1,0,0),
        new(0,0,1),
        new(0,0,-1),
        new(1,0,1),
        new(1,0,-1),
        new(-1,0,1),
        new(-1,0,-1)
    };
    readonly Vector3I[] verticalDirections = { new(0, 1, 0), new(0, -1, 0), };

    public Pathfinder(Func<Vector3I, Tile> getTile, Func<Vector3I,bool> isOccupied, Func<Vector3I, bool> hasVirtualScaffold = null)
    {
        this.getTile = getTile;
        this.isOccupied = isOccupied;
        this.hasVirtualScaffold = hasVirtualScaffold;
    }

    class Node
    {
        public Vector3I Pos;
        public int G;
        public int H;
        public int F => G + H;
        public Node Parent;
    }

    int Heuristic(Vector3I a, Vector3I b)
    {
        return Mathf.Abs(a.X - b.X)
            + Mathf.Abs(a.Y - b.Y)
            + Mathf.Abs(a.Z - b.Z);
    }

    bool InBounds(Vector3I p)
    {
        return (getTile(p) != null);
    }

    bool Walkable(Vector3I p)
    {
        if (!InBounds(p))
            return false;

        // ❌ case occupée
        if (IsSolid(p))
            return false;

        if (HasScaffoldAt(p))
            return true;

        // ✅ sol obligatoire juste en dessous
        if (p.Y == 0)
            return true;

        var below = p + new Vector3I(0, -1, 0);
        return IsSolid(below) || HasScaffoldAt(below);

    }

    public List<Vector3I> FindPath(Vector3I start, Vector3I end)
    {
        var open = new PriorityQueue<Node, int>();
        var visited = new Dictionary<Vector3I, Node>();
        int maxIterations = 5000;
        int iterations = 0;
        var closed = new HashSet<Vector3I>();

        var startNode = new Node
        {
            Pos = start,
            G = 0,
            H = Heuristic(start, end)
        };

        open.Enqueue(startNode, startNode.F);
        visited[start] = startNode;

        while (open.Count > 0)
        {
            iterations++;

            if (iterations > maxIterations)
            {
                return null;
            }
            var current = open.Dequeue();


            if (closed.Contains(current.Pos))
                continue;

            closed.Add(current.Pos);

            if (current.Pos == end)
                return Reconstruct(current);

            foreach (var dir in directions)
            {
                var basePos = current.Pos + dir;
                Vector3I? validPos = null;

                // 🟢 1. déplacement normal
                if (Walkable(basePos))
                {
                    validPos = basePos;
                }
                else
                {
                    // 🔼 2. monter (step up)
                    var up = new Vector3I(basePos.X, basePos.Y + 1, basePos.Z);

                    bool canStepUp = Walkable(up) &&
                    // ❌ bloc au-dessus de la tête actuelle
                    !IsSolid(new Vector3I(current.Pos.X, current.Pos.Y + 1, current.Pos.Z)) &&

                    // ❌ bloc au-dessus de la destination
                    !IsSolid(new Vector3I(up.X, up.Y + 1, up.Z));

                    if (canStepUp)
                    {
                        validPos = up;
                    }
                    else
                    {
                        // 🔽 3. descendre (step down)
                        var down = new Vector3I(basePos.X, basePos.Y - 1, basePos.Z);

                        bool canStepDown = Walkable(down) &&
                        // ❌ bloc au-dessus pendant la descente
                        !IsSolid(new Vector3I(basePos.X, basePos.Y + 1, basePos.Z));

                        if (canStepDown)
                        {
                            validPos = down;
                        }
                    }
                }

                if (validPos == null)
                    continue;

                var nextPos = validPos.Value;
                if (IsDiagonalXz(dir) && !CanPassDiagonalCorner(current.Pos, dir))
                    continue;

                // Occupied cells are treated as blocked to avoid unstable detours
                // and "zigzag" replanning around moving units.
                if (isOccupied != null && isOccupied(nextPos))
                    continue;

                // 🟨 coût de déplacement
                int cost = IsDiagonalXz(dir) ? 2 : 1;

                // monter = plus cher
                if (nextPos.Y > current.Pos.Y)
                    cost = 2;

                int newG = current.G + cost;

                if (visited.TryGetValue(nextPos, out var existing))
                {
                    if (newG >= existing.G)
                        continue;
                }

                var node = new Node
                {
                    Pos = nextPos,
                    G = newG,
                    H = Heuristic(nextPos, end),
                    Parent = current
                };

                visited[nextPos] = node;
                open.Enqueue(node, node.F);

                // 🔍 DEBUG (optionnel)
                // GD.Print("FROM ", current.Pos, " TO ", nextPos);
            }

            // Mouvement vertical "échelle": autorisé uniquement si un scaffold relie
            // la case courante et la case cible.
            foreach (var vdir in verticalDirections)
            {
                var nextPos = current.Pos + vdir;
                if (!InBounds(nextPos))
                    continue;
                if (!Walkable(nextPos))
                    continue;
                if (!CanClimbScaffoldBetween(current.Pos, nextPos))
                    continue;
                if (isOccupied != null && isOccupied(nextPos))
                    continue;

                int cost = 2;
                int newG = current.G + cost;
                if (visited.TryGetValue(nextPos, out var existing))
                {
                    if (newG >= existing.G)
                        continue;
                }

                var node = new Node
                {
                    Pos = nextPos,
                    G = newG,
                    H = Heuristic(nextPos, end),
                    Parent = current
                };

                visited[nextPos] = node;
                open.Enqueue(node, node.F);
            }
        }

        return null; // aucun chemin trouvé
    }

    List<Vector3I> Reconstruct(Node node)
    {
        var path = new List<Vector3I>();

        while (node != null)
        {
            path.Add(node.Pos);
            node = node.Parent;
        }

        path.Reverse();
        return path;
    }

/// <summary>
/// Checks if a position is walkable by a colonist.
/// </summary>
/// <param name="p">The position to check.</param>
/// <returns>true if the position is walkable, false otherwise.</returns>
    public bool IsWalkable(Vector3I p)
    {
        return Walkable(p);
    }


    public List<Vector3I> SmoothPath(List<Vector3I> path)
    {
        if (path == null || path.Count < 3)
            return path;

        var result = new List<Vector3I>();
        result.Add(path[0]);

        for (int i = 1; i < path.Count - 1; i++)
        {
            var prev = path[i - 1];
            var curr = path[i];
            var next = path[i + 1];

            if ((next - curr) != (curr - prev))
                result.Add(curr);
        }

        result.Add(path[^1]);

        return result;
    }

    bool IsSolid(Vector3I p)
    {
        if (!InBounds(p))
            return false;

        return getTile(p).Solid;
    }

    bool HasScaffoldAt(Vector3I p)
    {
        return hasVirtualScaffold != null && hasVirtualScaffold(p);
    }

    static bool IsDiagonalXz(Vector3I dir)
    {
        return Mathf.Abs(dir.X) == 1 && Mathf.Abs(dir.Z) == 1;
    }

    bool CanPassDiagonalCorner(Vector3I current, Vector3I dir)
    {
        var sideX = current + new Vector3I(dir.X, 0, 0);
        var sideZ = current + new Vector3I(0, 0, dir.Z);

        // Empêche la coupe de coin à travers deux murs collés.
        if (IsSolid(sideX))
            return false;
        if (IsSolid(sideZ))
            return false;

        return true;
    }

    bool CanClimbScaffoldBetween(Vector3I from, Vector3I to)
    {
        if (Mathf.Abs(to.Y - from.Y) != 1 || from.X != to.X || from.Z != to.Z)
            return false;

        var fromBelow = from + new Vector3I(0, -1, 0);
        var toBelow = to + new Vector3I(0, -1, 0);
        return HasScaffoldAt(from) || HasScaffoldAt(to) || HasScaffoldAt(fromBelow) || HasScaffoldAt(toBelow);
    }
}