using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Rooms;

namespace AITeammate.Scripts;

internal static class AiLearningService
{
    private const int SchemaVersion = 1;
    private const int MinSamplesForInfluence = 8;
    private const double LearningRate = 0.02d;
    private const double Decay = 0.995d;
    private const double LearnedScale = 0.35d;
    private const int MaxCombatAdjustment = 6;
    private const int FlushEveryUpdates = 10;

    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private static readonly Dictionary<string, AiLearningExperienceEntry> Entries = new(StringComparer.Ordinal);
    private static readonly List<AiLearningDecisionRecord> RunJournal = [];
    private static readonly Dictionary<string, List<AiLearningDecisionRecord>> PendingCombatRecords = new(StringComparer.Ordinal);
    private static readonly string RunId = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25];
    private static bool _loaded;
    private static int _updatesSinceFlush;

    public static int GetCombatAdjustment(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        ResolvedCardView? card,
        CombatActionCategory category)
    {
        try
        {
            EnsureLoaded();
            string key = BuildCombatKey(context, action, card, category);
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out AiLearningExperienceEntry? entry) ||
                    entry.Samples < MinSamplesForInfluence)
                {
                    return 0;
                }

                double confidence = ComputeConfidence(entry);
                int adjustment = (int)Math.Round(entry.LearnedAdjustment * confidence, MidpointRounding.AwayFromZero);
                return Math.Clamp(adjustment, -MaxCombatAdjustment, MaxCombatAdjustment);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Learning] Failed to read combat adjustment. {ex.Message}");
            return 0;
        }
    }

    public static void RecordCombatDecision(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        ResolvedCardView? card,
        CombatActionScore chosenScore,
        IReadOnlyList<CombatActionScore> rankedScores,
        CombatLinePlan? plan)
    {
        try
        {
            EnsureLoaded();
            int chosenRank = rankedScores
                .Select((score, index) => new { score.ActionId, Rank = index + 1 })
                .FirstOrDefault(candidate => string.Equals(candidate.ActionId, action.ActionId, StringComparison.Ordinal))
                ?.Rank ?? 0;
            AiLearningDecisionRecord record = new()
            {
                RunId = RunId,
                DecisionType = "combat_action",
                CharacterId = context.CombatConfig.CharacterId,
                ActiveBuildId = context.ActiveBuild?.Profile.BuildId ?? "none",
                DeckArchetype = DetectDeckArchetype(context),
                EnemyArchetype = DetectEnemyArchetype(context),
                ActionId = action.ActionId,
                CardOrRelicId = card?.CardId ?? action.CardId ?? action.ActionType,
                ActionRole = BuildActionRole(context, card, chosenScore.Category),
                Act = context.Actor.RunState.CurrentActIndex + 1,
                Floor = context.Actor.RunState.TotalFloor,
                RoomType = context.RoomTypeName,
                EnergyBefore = context.Energy,
                IncomingDamage = context.IncomingDamage,
                CurrentBlock = context.CurrentBlock,
                HpPctBefore = context.Actor.Creature.MaxHp > 0
                    ? (double)context.CurrentHp / context.Actor.Creature.MaxHp
                    : 0d,
                HeuristicScore = chosenScore.TotalScore,
                ChosenRank = chosenRank,
                AlternativeAverageScore = rankedScores.Count > 1
                    ? rankedScores.Where(score => !string.Equals(score.ActionId, action.ActionId, StringComparison.Ordinal)).Average(static score => score.TotalScore)
                    : chosenScore.TotalScore,
                LineScore = plan?.Score ?? chosenScore.TotalScore,
                EstimatedDamage = plan?.EstimatedDamageDealt ?? 0,
                EstimatedDamageTaken = plan?.EstimatedDamageTaken ?? 0,
                ExperienceKey = BuildCombatKey(context, action, card, chosenScore.Category),
                CreatedAtUtc = DateTime.UtcNow
            };

            lock (Sync)
            {
                RunJournal.Add(record);
                string combatKey = BuildPendingCombatKey(context.Actor);
                if (!PendingCombatRecords.TryGetValue(combatKey, out List<AiLearningDecisionRecord>? records))
                {
                    records = [];
                    PendingCombatRecords[combatKey] = records;
                }

                records.Add(record);
            }

            Log.Info($"[AITeammate][Learning] Recorded combat decision player={context.Actor.NetId} key={record.ExperienceKey} card={record.CardOrRelicId} role={record.ActionRole} heuristic={record.HeuristicScore:F1} rank={record.ChosenRank}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Learning] Failed to record combat decision. {ex.Message}");
        }
    }

    public static void CompleteCombatForPlayer(Player player, AbstractRoom room)
    {
        try
        {
            EnsureLoaded();
            List<AiLearningDecisionRecord> records;
            lock (Sync)
            {
                string combatKey = BuildPendingCombatKey(player);
                if (!PendingCombatRecords.TryGetValue(combatKey, out records!) || records.Count == 0)
                {
                    return;
                }

                PendingCombatRecords.Remove(combatKey);
            }

            bool wonCombat = room is CombatRoom;
            int currentHp = player.Creature.CurrentHp;
            foreach (AiLearningDecisionRecord record in records)
            {
                record.CombatWon = wonCombat;
                record.DamageTakenAfterDecision = Math.Max(0, (int)Math.Round(record.HpPctBefore * Math.Max(player.Creature.MaxHp, 1)) - currentHp);
                double reward = ComputeCombatReward(record);
                UpdateExperience(record, reward);
            }

            Log.Info($"[AITeammate][Learning] Completed combat learning player={player.NetId} decisions={records.Count} room={room.GetType().Name}");
            MaybeFlush();
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Learning] Failed to complete combat learning. {ex.Message}");
        }
    }

    public static void Flush()
    {
        try
        {
            EnsureLoaded();
            AiLearningExperienceStoreFile file;
            List<AiLearningDecisionRecord> journal;
            lock (Sync)
            {
                file = new AiLearningExperienceStoreFile
                {
                    SchemaVersion = SchemaVersion,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Entries = Entries.Values
                        .OrderByDescending(static entry => entry.Samples)
                        .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
                        .ToList()
                };
                journal = RunJournal.ToList();
                _updatesSinceFlush = 0;
            }

            Directory.CreateDirectory(GetLearningDirectoryPath());
            File.WriteAllText(GetExperiencePath(), JsonSerializer.Serialize(file, JsonOptions));

            if (journal.Count > 0)
            {
                string runsDirectory = Path.Combine(GetLearningDirectoryPath(), "runs");
                Directory.CreateDirectory(runsDirectory);
                string runPath = Path.Combine(runsDirectory, $"{RunId}.json");
                File.WriteAllText(runPath, JsonSerializer.Serialize(new AiLearningRunJournal
                {
                    RunId = RunId,
                    UpdatedAtUtc = DateTime.UtcNow,
                    Decisions = journal
                }, JsonOptions));
            }

            Log.Info($"[AITeammate][Learning] Flushed experience entries={file.Entries.Count} journal={journal.Count}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Learning] Failed to flush learning store. {ex.Message}");
        }
    }

    private static void UpdateExperience(AiLearningDecisionRecord record, double reward)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(record.ExperienceKey, out AiLearningExperienceEntry? entry))
            {
                entry = new AiLearningExperienceEntry
                {
                    Key = record.ExperienceKey,
                    Samples = 0,
                    AvgReward = 0d,
                    BaselineReward = record.AlternativeAverageScore / 20d,
                    Variance = 0d,
                    LearnedAdjustment = 0d,
                    LastUpdatedUtc = DateTime.UtcNow
                };
                Entries[record.ExperienceKey] = entry;
            }

            entry.Samples++;
            entry.BaselineReward = (entry.BaselineReward * Decay) + ((record.AlternativeAverageScore / 20d) * (1d - Decay));
            double delta = reward - entry.AvgReward;
            entry.AvgReward += LearningRate * delta;
            entry.Variance += LearningRate * ((delta * delta) - entry.Variance);
            double rawAdjustment = (entry.AvgReward - entry.BaselineReward) * LearnedScale;
            entry.LearnedAdjustment = Math.Clamp(rawAdjustment, -MaxCombatAdjustment, MaxCombatAdjustment);
            entry.LastUpdatedUtc = DateTime.UtcNow;
            _updatesSinceFlush++;

            Log.Info($"[AITeammate][Learning] Updated experience key={entry.Key} samples={entry.Samples} reward={reward:F1} avg={entry.AvgReward:F1} baseline={entry.BaselineReward:F1} learned={entry.LearnedAdjustment:F1}");
        }
    }

    private static double ComputeCombatReward(AiLearningDecisionRecord record)
    {
        double heuristicMargin = (record.HeuristicScore - record.AlternativeAverageScore) / 25d;
        double lineValue = (record.EstimatedDamage * 0.35d) - (record.EstimatedDamageTaken * 2.5d);
        double survivalValue = record.CombatWon ? 12d : -20d;
        double damagePenalty = record.DamageTakenAfterDecision * 1.5d;
        double rankPenalty = Math.Max(0, record.ChosenRank - 1) * 0.75d;
        return survivalValue + heuristicMargin + lineValue - damagePenalty - rankPenalty;
    }

    private static void MaybeFlush()
    {
        bool shouldFlush;
        lock (Sync)
        {
            shouldFlush = _updatesSinceFlush >= FlushEveryUpdates;
        }

        if (shouldFlush)
        {
            Flush();
        }
    }

    private static string BuildCombatKey(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        ResolvedCardView? card,
        CombatActionCategory category)
    {
        return string.Join(
            "|",
            "combat_action",
            context.CombatConfig.CharacterId,
            context.ActiveBuild?.Profile.BuildId ?? "none",
            DetectDeckArchetype(context),
            DetectEnemyArchetype(context),
            BuildActionRole(context, card, category),
            card?.CardId ?? action.CardId ?? action.ActionType,
            BuildActBucket(context.Actor.RunState.CurrentActIndex),
            BuildHpBucket(context),
            BuildIncomingBucket(context));
    }

    private static string BuildActionRole(DeterministicCombatContext context, ResolvedCardView? card, CombatActionCategory category)
    {
        if (CombatBuildRoleEvaluator.IsNecrobinderEarlySoulCard(context, card))
        {
            return "early_soul_engine";
        }

        if (CombatBuildRoleEvaluator.IsEngineSetup(context, card))
        {
            return "engine_setup";
        }

        if (CombatBuildRoleEvaluator.IsCoreBuildPower(context, card))
        {
            return "core_power";
        }

        if (card.GetEnemyPoisonAmount() > 0)
        {
            return "poison_stack";
        }

        return category.ToString().ToLowerInvariant();
    }

    private static string DetectDeckArchetype(DeterministicCombatContext context)
    {
        List<ResolvedCardView> cards = context.DeckCards.ToList();
        if (context.ActiveBuild != null)
        {
            return context.ActiveBuild.Profile.BuildId;
        }

        if (cards.Any(card => HasCardToken(card, "POISON", "NOXIOUS", "CATALYST")))
        {
            return "poison";
        }

        if (cards.Any(card => HasCardToken(card, "ZAP", "DEFRA", "CAPACITOR", "GLACIER", "LIGHTNING")))
        {
            return "orb";
        }

        if (cards.Any(card => HasCardToken(card, "SOUL", "OSTY", "BORROWED", "DIRGE")))
        {
            return "necrobinder_engine";
        }

        return "unknown";
    }

    private static string DetectEnemyArchetype(DeterministicCombatContext context)
    {
        if (context.IsBossCombat)
        {
            return "boss";
        }

        if (context.IsEliteCombat)
        {
            return "elite";
        }

        if (context.EnemiesById.Count > 1)
        {
            return context.EnemiesById.Values.Count(static enemy => enemy.IsAttacking) > 1
                ? "multi_enemy_attack"
                : "multi_enemy";
        }

        DeterministicEnemyState? onlyEnemy = context.EnemiesById.Values.FirstOrDefault();
        if (onlyEnemy?.ThreatScore >= 25)
        {
            return "high_threat";
        }

        return onlyEnemy?.IsAttacking == true ? "single_enemy_attack" : "single_enemy";
    }

    private static string BuildActBucket(int currentActIndex)
    {
        return $"act{currentActIndex + 1}";
    }

    private static string BuildHpBucket(DeterministicCombatContext context)
    {
        double hpPct = context.Actor.Creature.MaxHp > 0
            ? (double)context.CurrentHp / context.Actor.Creature.MaxHp
            : 0d;
        return hpPct switch
        {
            <= 0.33d => "hp_low",
            <= 0.66d => "hp_mid",
            _ => "hp_high"
        };
    }

    private static string BuildIncomingBucket(DeterministicCombatContext context)
    {
        int uncovered = Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock) + context.HandEndTurnHpLoss;
        return uncovered switch
        {
            <= 0 => "incoming_none",
            <= 7 => "incoming_low",
            <= 17 => "incoming_mid",
            _ => "incoming_high"
        };
    }

    private static bool HasCardToken(ResolvedCardView card, params string[] tokens)
    {
        string normalizedName = AiBuildProfileAnalyzer.Normalize(card.Name);
        string normalizedId = AiBuildProfileAnalyzer.Normalize(card.CardId);
        return tokens.Any(token =>
        {
            string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
            return normalizedName.Contains(normalizedToken, StringComparison.Ordinal) ||
                   normalizedId.Contains(normalizedToken, StringComparison.Ordinal);
        });
    }

    private static string BuildPendingCombatKey(Player player)
    {
        return $"{player.NetId}:{player.RunState.CurrentActIndex}:{player.RunState.CurrentRoomCount}:{player.RunState.CurrentMapCoord?.ToString() ?? "none"}";
    }

    private static double ComputeConfidence(AiLearningExperienceEntry entry)
    {
        if (entry.Samples < MinSamplesForInfluence)
        {
            return 0d;
        }

        double sampleConfidence = Math.Min(1d, entry.Samples / 50d);
        double variancePenalty = 1d / (1d + Math.Max(0d, entry.Variance) / 100d);
        return sampleConfidence * variancePenalty;
    }

    private static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            Directory.CreateDirectory(GetLearningDirectoryPath());
            if (File.Exists(GetExperiencePath()))
            {
                string json = File.ReadAllText(GetExperiencePath());
                AiLearningExperienceStoreFile? file = JsonSerializer.Deserialize<AiLearningExperienceStoreFile>(json, JsonOptions);
                if (file?.Entries != null)
                {
                    foreach (AiLearningExperienceEntry entry in file.Entries)
                    {
                        Entries[entry.Key] = entry;
                    }
                }
            }

            _loaded = true;
            Log.Info($"[AITeammate][Learning] Loaded experience entries={Entries.Count} path={GetExperiencePath()}");
        }
    }

    private static string GetLearningDirectoryPath()
    {
        return AiTeammateStoragePaths.GetRuntimeDataDirectory("ai-learning");
    }

    private static string GetExperiencePath()
    {
        return Path.Combine(GetLearningDirectoryPath(), "experience.json");
    }

}

internal sealed class AiLearningDecisionRecord
{
    public required string RunId { get; init; }
    public required string DecisionType { get; init; }
    public required string CharacterId { get; init; }
    public required string ActiveBuildId { get; init; }
    public required string DeckArchetype { get; init; }
    public required string EnemyArchetype { get; init; }
    public required string ActionId { get; init; }
    public required string CardOrRelicId { get; init; }
    public required string ActionRole { get; init; }
    public required string RoomType { get; init; }
    public required string ExperienceKey { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }
    public int EnergyBefore { get; init; }
    public int IncomingDamage { get; init; }
    public int CurrentBlock { get; init; }
    public double HpPctBefore { get; init; }
    public double HeuristicScore { get; init; }
    public double AlternativeAverageScore { get; init; }
    public double LineScore { get; init; }
    public int EstimatedDamage { get; init; }
    public int EstimatedDamageTaken { get; init; }
    public int ChosenRank { get; init; }
    public bool CombatWon { get; set; }
    public int DamageTakenAfterDecision { get; set; }
    public DateTime CreatedAtUtc { get; init; }
}

internal sealed class AiLearningExperienceEntry
{
    public required string Key { get; init; }
    public int Samples { get; set; }
    public double AvgReward { get; set; }
    public double BaselineReward { get; set; }
    public double Variance { get; set; }
    public double LearnedAdjustment { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}

internal sealed class AiLearningExperienceStoreFile
{
    public int SchemaVersion { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public List<AiLearningExperienceEntry> Entries { get; init; } = [];
}

internal sealed class AiLearningRunJournal
{
    public required string RunId { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public List<AiLearningDecisionRecord> Decisions { get; init; } = [];
}
