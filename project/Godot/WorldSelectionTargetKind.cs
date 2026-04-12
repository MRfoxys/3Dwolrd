/// <summary>
/// Quel type d'entité le joueur cible avec la souris (extensible : blocs, ressources, etc.).
/// Chaque mode peut activer une UI / actions différentes.
/// </summary>
public enum WorldSelectionTargetKind
{
    Colonists,
    Trees,
    /// <summary>Sélection de tuile (minage / ordres au sol) — utilise la percée Alt pour viser au fond.</summary>
    TerrainTiles,
}
