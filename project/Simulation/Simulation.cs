using System.Collections.Generic;
using Godot;

public class Simulation
{
    public int Tick;
    public World World;

    Pathfinder pathfinder;

    public Queue<PlayerCommand> CommandQueue = new();

    public void Init()
    {
        pathfinder = new Pathfinder(World.CurrentMap.World);
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

        //World?.Update();
    }

    void ApplyCommand(PlayerCommand cmd)
    {
        if (cmd.Type == "MOVE")
        {
            var colon = World.CurrentMap.Colonists[cmd.EntityId];

            var start = new Vector3I(colon.X, colon.Y, colon.Z);
            var end = new Vector3I(cmd.X, cmd.Y, cmd.Z);

            var path = pathfinder.FindPath(start, end);

            if (path != null && path.Count > 1)
			{
				path.RemoveAt(0); // 🔥 enlever position actuelle
				colon.Path.Clear();
				colon.Path = path;
			}
			GD.Print("MOVE reçu pour entity ", cmd.EntityId);
        }
    }

    void UpdateColon(Colonist colon)
    {
        if (colon.Path == null || colon.Path.Count == 0)
            return;

        var next = colon.Path[0];
		if (colon.X == next.X && colon.Y == next.Y && colon.Z == next.Z)
		{
			colon.Path.RemoveAt(0);
			return;
		}
        colon.Path.RemoveAt(0);
		GD.Print("NEXT: ", next);

        colon.X = next.X;
        colon.Y = next.Y;
        colon.Z = next.Z;
    }
}