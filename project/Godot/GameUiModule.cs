using System.Collections.Generic;
using System.Text;

public sealed class GameUiModule
{
    readonly List<SimJob> scratchJobs = new();

    public string BuildJobsQueueText(Simulation sim, int maxLines = 6)
    {
        if (sim?.jobBoard == null)
            return "Travail en attente : aucun";

        sim.jobBoard.CopyActiveJobs(scratchJobs);
        if (scratchJobs.Count == 0)
            return "Travail en attente : aucun";

        var sb = new StringBuilder();
        sb.Append("Travail en attente (").Append(scratchJobs.Count).Append(") :\n");
        int shown = 0;
        foreach (var j in scratchJobs)
        {
            if (shown >= maxLines)
            {
                sb.Append("…");
                break;
            }

            string state = j.Status switch
            {
                JobStatus.Reserved => "en cours",
                JobStatus.WaitingAccess => "bloqué (accès)",
                _ => "à faire",
            };
            string line = j.Type switch
            {
                JobType.CutTree => $"• Coupe arbre @ {j.Target} ({state})",
                JobType.MineStone => $"• Mine bloc @ {j.Target} ({state})",
                JobType.BuildBlock => $"• Construit bloc @ {j.Target} ({state})",
                JobType.HaulResource => $"• Transporte {j.ResourceType} @ {j.Target} ({state})",
                _ => $"• {j.Type} @ {j.Target} ({state})"
            };
            sb.Append(line).Append('\n');
            shown++;
        }
        return sb.ToString().TrimEnd('\n');
    }

    public string BuildLogisticsText(Simulation sim)
    {
        if (sim == null)
            return "Logistique : indisponible";
        return sim.GetLogisticsStatusText();
    }
}
