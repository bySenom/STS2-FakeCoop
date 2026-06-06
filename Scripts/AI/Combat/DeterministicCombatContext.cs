using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal sealed class DeterministicCombatContext
{
    public required Player Actor { get; init; }

    public required IReadOnlyList<AiLegalActionOption> LegalActions { get; init; }

    public required Dictionary<string, ResolvedCardView> HandCardsByInstanceId { get; init; }

    public required IReadOnlyList<ResolvedCardView> DeckCards { get; init; }

    public required Dictionary<string, DeterministicEnemyState> EnemiesById { get; init; }

    public required IReadOnlyDictionary<string, int> PendingTeamDamageByEnemyId { get; init; }

    public required Dictionary<string, int> ActorPowerAmounts { get; init; }

    public required HashSet<string> ActorRelicIds { get; init; }

    public AiBuildProfileMatch? ActiveBuild { get; init; }

    public required AiCharacterCombatConfig CombatConfig { get; init; }

    public required string RoomTypeName { get; init; }

    public bool IsEliteCombat { get; init; }

    public bool IsBossCombat { get; init; }

    public bool IsEliteOrBossCombat => IsEliteCombat || IsBossCombat;

    public bool HasBlockRetention =>
        ActorRelicIds.Contains("CALIPERS") ||
        ActorRelicIds.Contains("CALIPER") ||
        ActorPowerAmounts.ContainsKey("BARRICADE");

    public bool UsesBlockAsResource =>
        ActiveBuild?.Profile.BuildId == "barricade" ||
        HandCardsByInstanceId.Values.Any(IsBodySlamCard) ||
        DeckCards.Any(IsBodySlamCard);

    public bool ValuesExcessBlock => HasBlockRetention || UsesBlockAsResource;

    public int CurrentHp => Actor.Creature.CurrentHp;

    public int CurrentBlock => Actor.Creature.Block;

    public int Energy => Actor.PlayerCombatState?.Energy ?? 0;

    public int IncomingDamage { get; init; }

    public int HandEndTurnDamage { get; init; }

    public int HandEndTurnHpLoss { get; init; }

    public int TotalBlockableIncomingDamage => IncomingDamage + HandEndTurnDamage;

    public int TotalExpectedEndTurnLifeLoss => TotalBlockableIncomingDamage + HandEndTurnHpLoss;

    private static bool IsBodySlamCard(ResolvedCardView card)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        return normalizedName.Contains("BODYSLAM") || normalizedId.Contains("BODYSLAM");
    }
}

internal sealed class DeterministicEnemyState
{
    public required string Id { get; init; }

    public required Creature Creature { get; init; }

    public int CurrentHp => Creature.CurrentHp;

    public int Block => Creature.Block;

    public int IncomingDamage { get; init; }

    public bool IsAttacking => IncomingDamage > 0;
}
