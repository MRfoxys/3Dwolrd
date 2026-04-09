using System.Collections.Generic;

public class LockstepManager
{
    public long CurrentTick;
    public Dictionary<long, List<PlayerCommand>> Commands = new();

    public void AddCommand(PlayerCommand cmd)
    {
        if (!Commands.ContainsKey(cmd.Tick))
            Commands[cmd.Tick] = new List<PlayerCommand>();

        var bucket = Commands[cmd.Tick];

        if (cmd.Type == "MOVE")
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i].Type == "MOVE" && bucket[i].EntityId == cmd.EntityId)
                {
                    bucket[i] = cmd;
                    return;
                }
            }
        }

        bucket.Add(cmd);
    }

    public List<PlayerCommand> GetCommandsForTick(long tick)
    {
        return Commands.ContainsKey(tick) ? Commands[tick] : new List<PlayerCommand>();
    }
    
}