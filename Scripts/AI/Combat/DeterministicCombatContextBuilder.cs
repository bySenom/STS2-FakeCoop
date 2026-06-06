using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace AITeammate.Scripts;

internal sealed class DeterministicCombatContextBuilder
{
    private readonly ICardResolver _cardResolver = new CardResolver(
        CardCatalogRepository.Shared,
        new CardDefinitionRepository(),
        new RunCardStateStore(),
        new CombatCardStateStore());

    public DeterministicCombatContext? Build(string actorId, IReadOnlyList<AiLegalActionOption> legalActions)
    {
        if (!ulong.TryParse(actorId, out ulong parsedActorId))
        {
            return null;
        }

        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(parsedActorId);
        if (player?.Creature?.CombatState == null || player.PlayerCombatState == null)
        {
            return null;
        }

        AbstractRoom? currentRoom = RunManager.Instance.DebugOnlyGetState()?.CurrentRoom;
        string roomTypeName = currentRoom?.GetType().Name ?? "UnknownRoom";

        Dictionary<string, ResolvedCardView> handCardsByInstanceId = PileType.Hand.GetPile(player).Cards
            .GroupBy(GetCardInstanceId)
            .ToDictionary(
                group => group.Key,
                group => _cardResolver.Resolve(group.First(), group.Key),
                StringComparer.Ordinal);
        (int handEndTurnDamage, int handEndTurnHpLoss) = EstimateHandEndTurnThreats(handCardsByInstanceId.Values);
        List<ResolvedCardView> deckCards = player.Deck.Cards
            .Select((card, index) => _cardResolver.Resolve(card, $"deck_{index}_{card.Id.Entry.Replace(':', '_').Replace('/', '_').Replace(' ', '_')}"))
            .ToList();

        Dictionary<string, DeterministicEnemyState> enemiesById = new(StringComparer.Ordinal);
        int incomingDamage = 0;
        int combatRound = player.Creature.CombatState.RoundNumber;
        foreach (Creature enemy in player.Creature.CombatState.HittableEnemies)
        {
            int enemyDamage = EstimateIncomingDamage(enemy, player.Creature);
            string enemyId = GetTargetId(enemy);
            string intentSummary = BuildIntentSummary(enemy);
            enemiesById[enemyId] = new DeterministicEnemyState
            {
                Id = enemyId,
                Creature = enemy,
                IncomingDamage = enemyDamage,
                ThreatScore = EstimateThreatScore(enemy, enemyDamage, intentSummary),
                IntentSummary = intentSummary,
                PowerAmounts = BuildVisiblePowerAmounts(enemy)
            };
            incomingDamage += enemyDamage;
        }

        Dictionary<string, int> actorPowerAmounts = player.Creature.Powers
            .Where(static power => power.IsVisible)
            .GroupBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(power => power.DisplayAmount), StringComparer.Ordinal);

        HashSet<string> actorRelicIds = player.Relics
            .Select(static relic => relic.Id.Entry.ToUpperInvariant())
            .ToHashSet(StringComparer.Ordinal);

        return new DeterministicCombatContext
        {
            Actor = player,
            LegalActions = legalActions,
            HandCardsByInstanceId = handCardsByInstanceId,
            DeckCards = deckCards,
            EnemiesById = enemiesById,
            PendingTeamDamageByEnemyId = PendingTeamCombatPlanStore.GetPendingDamageByTarget(player.NetId, combatRound),
            ActorPowerAmounts = actorPowerAmounts,
            ActorRelicIds = actorRelicIds,
            ActiveBuild = AiBuildProfileAnalyzer.SelectActiveProfile(player, deckCards),
            CombatConfig = AiCharacterCombatConfigLoader.LoadForPlayer(player),
            RoomTypeName = roomTypeName,
            IsEliteCombat = roomTypeName.Contains("Elite", StringComparison.OrdinalIgnoreCase),
            IsBossCombat = roomTypeName.Contains("Boss", StringComparison.OrdinalIgnoreCase),
            IncomingDamage = incomingDamage,
            HandEndTurnDamage = handEndTurnDamage,
            HandEndTurnHpLoss = handEndTurnHpLoss
        };
    }

    private static int EstimateIncomingDamage(Creature enemy, Creature target)
    {
        if (enemy.Monster?.NextMove?.Intents == null)
        {
            return 0;
        }

        int total = 0;
        foreach (AttackIntent intent in enemy.Monster.NextMove.Intents.OfType<AttackIntent>())
        {
            total += intent.GetTotalDamage([target], enemy);
        }

        return Math.Max(total, 0);
    }

    private static Dictionary<string, int> BuildVisiblePowerAmounts(Creature creature)
    {
        return creature.Powers
            .Where(static power => power.IsVisible)
            .GroupBy(power => power.Id.Entry, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(power => power.DisplayAmount), StringComparer.Ordinal);
    }

    private static string BuildIntentSummary(Creature enemy)
    {
        if (enemy.Monster?.NextMove?.Intents == null)
        {
            return string.Empty;
        }

        return string.Join(
            ",",
            enemy.Monster.NextMove.Intents.Select(static intent => intent.GetType().Name.ToUpperInvariant()));
    }

    private static int EstimateThreatScore(Creature enemy, int incomingDamage, string intentSummary)
    {
        int score = incomingDamage;
        string name = enemy.Name?.ToUpperInvariant() ?? string.Empty;
        string combined = $"{name},{intentSummary}";

        if (incomingDamage > 0)
        {
            score += Math.Min(incomingDamage, 18);
        }

        if (ContainsAny(combined, "BUFF", "POWER", "STRENGTH", "RITUAL", "GROW", "ENRAGE", "SCALE"))
        {
            score += 18;
        }

        if (ContainsAny(combined, "DEBUFF", "STATUS", "WOUND", "DAZED", "BURN", "SLIME", "CURSE", "HEX"))
        {
            score += 14;
        }

        if (ContainsAny(combined, "SUMMON", "SPAWN", "MINION"))
        {
            score += 12;
        }

        if (ContainsAny(combined, "BLOCK", "DEFEND", "SHIELD"))
        {
            score += 6;
        }

        if (enemy.CurrentHp <= 12)
        {
            score += 4;
        }

        return Math.Max(score, 0);
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetCardInstanceId(CardModel card)
    {
        return NetCombatCardDb.Instance.TryGetCardId(card, out uint cardId)
            ? $"combat_{cardId}"
            : card.Id.Entry.Replace(':', '_').Replace('/', '_').Replace(' ', '_');
    }

    private static string GetTargetId(Creature target)
    {
        if (target.Player != null)
        {
            return $"player_{target.Player.NetId}";
        }

        return $"creature_{target.CombatId?.ToString() ?? target.Name.Replace(' ', '_')}";
    }

    private static (int Damage, int HpLoss) EstimateHandEndTurnThreats(IEnumerable<ResolvedCardView> handCards)
    {
        List<ResolvedCardView> cards = handCards.ToList();
        int damage = 0;
        int hpLoss = 0;
        foreach (ResolvedCardView card in cards)
        {
            (int cardDamage, int cardHpLoss) = EstimateHandEndTurnThreat(card, cards.Count);
            damage += cardDamage;
            hpLoss += cardHpLoss;
        }

        return (damage, hpLoss);
    }

    private static (int Damage, int HpLoss) EstimateHandEndTurnThreat(ResolvedCardView card, int handCount)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        string description = card.Description ?? string.Empty;

        int? textDamage = ExtractEndTurnDamage(description);
        if (textDamage.HasValue)
        {
            return (textDamage.Value, 0);
        }

        int? textHpLoss = ExtractEndTurnHpLoss(description);
        if (textHpLoss.HasValue)
        {
            return (0, textHpLoss.Value);
        }

        if (normalizedName.Contains("REGRET") || normalizedId.Contains("REGRET"))
        {
            return (0, Math.Max(1, handCount));
        }

        if (normalizedName.Contains("BURN") || normalizedId.Contains("BURN") ||
            normalizedName.Contains("SCORCH") || normalizedId.Contains("SCORCH") ||
            normalizedName.Contains("DECAY") || normalizedId.Contains("DECAY") ||
            normalizedName.Contains("DISINTEGRATION") || normalizedId.Contains("DISINTEGRATION") ||
            normalizedName.Contains("ROT") || normalizedId.Contains("ROT"))
        {
            return (5, 0);
        }

        if (normalizedName.Contains("BECKON") || normalizedId.Contains("BECKON"))
        {
            return (0, 6);
        }

        return (0, 0);
    }

    private static int? ExtractEndTurnDamage(string description)
    {
        if (string.IsNullOrWhiteSpace(description) ||
            !description.Contains("end", StringComparison.OrdinalIgnoreCase) ||
            !description.Contains("turn", StringComparison.OrdinalIgnoreCase) ||
            !description.Contains("damage", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match match = Regex.Match(description, @"\b(?<damage>\d+)\b");
        return match.Success ? int.Parse(match.Groups["damage"].Value) : null;
    }

    private static int? ExtractEndTurnHpLoss(string description)
    {
        if (string.IsNullOrWhiteSpace(description) ||
            !description.Contains("end", StringComparison.OrdinalIgnoreCase) ||
            !description.Contains("turn", StringComparison.OrdinalIgnoreCase) ||
            !description.Contains("lose", StringComparison.OrdinalIgnoreCase) ||
            !description.Contains("HP", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        Match match = Regex.Match(description, @"\b(?<hp>\d+)\b");
        return match.Success ? int.Parse(match.Groups["hp"].Value) : null;
    }
}
