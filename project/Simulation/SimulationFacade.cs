public class SimulationFacade
{
    readonly Simulation simulation;
    readonly LockstepManager lockstep;

    public SimulationFacade(Simulation simulation, LockstepManager lockstep)
    {
        this.simulation = simulation;
        this.lockstep = lockstep;
    }

    public void Step()
    {
        var cmds = lockstep.GetCommandsForTick(simulation.Tick);
        foreach (var cmd in cmds)
            simulation.CommandQueue.Enqueue(cmd);

        simulation.Update();
    }
}
