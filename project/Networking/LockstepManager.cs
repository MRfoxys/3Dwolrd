using System.Collections.Generic;

public class LockstepManager
{
    public long CurrentTick;
    public Dictionary<long, List<PlayerCommand>> Commands = new();

    public void AddCommand(PlayerCommand cmd)
    {
        if (!Commands.ContainsKey(cmd.Tick))
            Commands[cmd.Tick] = new List<PlayerCommand>();

        Commands[cmd.Tick].Add(cmd);
    }

    public List<PlayerCommand> GetCommandsForTick(long tick)
    {
        return Commands.ContainsKey(tick) ? Commands[tick] : new List<PlayerCommand>();
    }
    
}