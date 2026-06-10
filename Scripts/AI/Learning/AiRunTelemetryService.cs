using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace AITeammate.Scripts;

internal static class AiRunTelemetryService
{
    private const int SchemaVersion = 1;
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string _runId = CreateRunId();
    private static readonly Dictionary<ulong, AiTelemetryPlayerReport> Players = new();
    private static readonly List<AiTelemetryDecisionRecord> Decisions = [];
    private static bool _abandoned;

    public static void MarkRunAbandoned()
    {
        lock (Sync)
        {
            _abandoned = true;
        }

        Log.Info("[AITeammate][Telemetry] Run marked abandoned.");
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
            AiTelemetryPlayerReport player = GetOrCreatePlayer(context.Actor);
            int rank = rankedScores
                .Select((score, index) => new { score.ActionId, Rank = index + 1 })
                .FirstOrDefault(candidate => string.Equals(candidate.ActionId, action.ActionId, StringComparison.Ordinal))
                ?.Rank ?? 0;
            AiTelemetryDecisionRecord record = new()
            {
                DecisionType = "combat_action",
                PlayerId = context.Actor.NetId,
                CharacterId = context.CombatConfig.CharacterId,
                Act = context.Actor.RunState.CurrentActIndex + 1,
                Floor = context.Actor.RunState.TotalFloor,
                RoomType = context.RoomTypeName,
                ActiveBuildId = context.ActiveBuild?.Profile.BuildId ?? "none",
                PickedId = card?.CardId ?? action.CardId ?? action.ActionType,
                PickedName = card?.Name ?? action.Label ?? card?.CardId ?? action.CardId ?? action.ActionType,
                Role = chosenScore.Category.ToString(),
                Score = chosenScore.TotalScore,
                Rank = rank,
                AlternativeCount = Math.Max(0, rankedScores.Count - 1),
                IncomingDamage = context.IncomingDamage,
                CurrentBlock = context.CurrentBlock,
                Energy = context.Energy,
                EstimatedDamage = plan?.EstimatedDamageDealt ?? 0,
                EstimatedDamageTaken = plan?.EstimatedDamageTaken ?? 0,
                Notes = BuildCombatDecisionNotes(context, action, card, chosenScore, rankedScores, plan),
                CreatedAtUtc = DateTime.UtcNow
            };

            lock (Sync)
            {
                Decisions.Add(record);
                player.CombatDecisions++;
                player.TotalEstimatedDamage += record.EstimatedDamage;
                player.TotalEstimatedDamageTaken += record.EstimatedDamageTaken;
                player.TotalIncomingSeen += context.IncomingDamage;
                player.TotalUncoveredIncomingSeen += Math.Max(0, context.TotalBlockableIncomingDamage - context.CurrentBlock);
                if (record.Energy > 0 && chosenScore.Category == CombatActionCategory.EndTurn)
                {
                    player.EndTurnsWithEnergy++;
                }

                if (card != null && IsStarterStrike(card))
                {
                    player.StarterStrikePlays++;
                }

                if (card != null && card.GetEstimatedBlock() > context.TotalBlockableIncomingDamage - context.CurrentBlock + 3)
                {
                    player.LikelyOverblockPlays++;
                }
            }

            Log.Info($"[AITeammate][Telemetry] Combat decision player={record.PlayerId} card={record.PickedId} score={record.Score:F1} rank={record.Rank} notes=[{string.Join(", ", record.Notes)}]");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record combat decision. {ex.Message}");
        }
    }

    public static void CompleteCombatForPlayer(Player player, AbstractRoom room)
    {
        try
        {
            AiTelemetryPlayerReport report = GetOrCreatePlayer(player);
            lock (Sync)
            {
                report.CombatsCompleted++;
                report.LastRoomType = room.GetType().Name;
                report.LastAct = player.RunState.CurrentActIndex + 1;
                report.LastFloor = player.RunState.TotalFloor;
                report.FinalHp = player.Creature.CurrentHp;
                report.MaxHp = player.Creature.MaxHp;
                if (IsEliteRoom(room))
                {
                    report.EliteCombats++;
                }

                if (IsBossRoom(player, room))
                {
                    report.BossCombats++;
                }
            }

            CapturePlayerSnapshot(player, "combat_complete");
            Log.Info($"[AITeammate][Telemetry] Combat complete player={player.NetId} room={room.GetType().Name} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to complete combat telemetry. {ex.Message}");
        }
    }

    public static void RecordCardChoice(Player player, string source, CardChoiceDecision decision, CardModel? selected)
    {
        try
        {
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "card_choice", source, decision.ActiveBuildId);
            record.PickedId = selected?.Id.Entry ?? "skip";
            record.PickedName = selected?.Title?.ToString() ?? record.PickedId;
            record.Score = decision.BestEvaluation?.FinalScore ?? 0d;
            record.Rank = decision.ShouldTakeCard ? 1 : 0;
            record.AlternativeCount = decision.RankedResults.Count;
            record.Threshold = decision.SkipThreshold;
            record.Notes = BuildCardChoiceNotes(decision, selected);

            lock (Sync)
            {
                Decisions.Add(record);
                AiTelemetryPlayerReport report = GetOrCreatePlayer(player);
                if (selected == null)
                {
                    report.CardRewardSkips++;
                }
                else
                {
                    report.CardRewardPicks++;
                }

                if (decision.BestEvaluation?.IsOffBuild == true)
                {
                    report.OffBuildCardOffersSeen++;
                }
            }

            Log.Info($"[AITeammate][Telemetry] Card choice player={player.NetId} source={source} picked={record.PickedId} score={record.Score:F1} notes=[{string.Join(", ", record.Notes)}]");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record card choice. {ex.Message}");
        }
    }

    public static void RecordRelicChoice(Player player, AiRelicChoiceDecision decision)
    {
        try
        {
            AiRelicEvaluationResult? selected = decision.RankedResults.FirstOrDefault(result =>
                decision.SelectedRelic != null && ReferenceEquals(result.Relic, decision.SelectedRelic));
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "relic_choice", "relic_choice");
            record.PickedId = decision.SelectedRelic?.Id.Entry ?? "none";
            record.PickedName = decision.SelectedRelic?.Title?.ToString() ?? record.PickedId;
            record.Score = selected?.Score ?? 0d;
            record.Rank = selected != null ? 1 : 0;
            record.AlternativeCount = Math.Max(0, decision.RankedResults.Count - 1);
            record.Notes = selected?.Reasons.ToList() ?? [];

            lock (Sync)
            {
                Decisions.Add(record);
                GetOrCreatePlayer(player).RelicChoices++;
            }

            Log.Info($"[AITeammate][Telemetry] Relic choice player={player.NetId} relic={record.PickedId} score={record.Score:F1}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record relic choice. {ex.Message}");
        }
    }

    public static void RecordUpgradeChoice(Player player, IReadOnlyList<CardUpgradeEvaluationResult> ranked, IReadOnlyList<CardModel> selected)
    {
        try
        {
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "upgrade_choice", "rest_site_upgrade");
            record.PickedId = string.Join(",", selected.Select(static card => card.Id.Entry));
            record.PickedName = record.PickedId;
            record.Score = ranked.FirstOrDefault()?.Score ?? 0d;
            record.Rank = selected.Count > 0 ? 1 : 0;
            record.AlternativeCount = Math.Max(0, ranked.Count - selected.Count);
            record.Notes = ranked.Take(3).Select(static result => result.Describe()).ToList();

            lock (Sync)
            {
                Decisions.Add(record);
                GetOrCreatePlayer(player).Upgrades++;
            }

            Log.Info($"[AITeammate][Telemetry] Upgrade choice player={player.NetId} selected=[{record.PickedId}] score={record.Score:F1}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record upgrade choice. {ex.Message}");
        }
    }

    public static void RecordRestSiteChoice(Player player, string optionId, string reason)
    {
        try
        {
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "rest_site_choice", "rest_site");
            record.PickedId = optionId;
            record.PickedName = optionId;
            record.Notes = [reason];

            lock (Sync)
            {
                Decisions.Add(record);
                AiTelemetryPlayerReport report = GetOrCreatePlayer(player);
                if (IsOptionHeal(optionId))
                {
                    report.RestSitesHealed++;
                }
                else if (IsOptionUpgrade(optionId))
                {
                    report.RestSitesUpgraded++;
                }
            }

            Log.Info($"[AITeammate][Telemetry] Rest site choice player={player.NetId} option={optionId} reason={reason}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record rest site choice. {ex.Message}");
        }
    }

    public static void RecordShopStep(Player player, ShopVisitState snapshot, ShopPlanStep step, ShopActionEvaluation? evaluation)
    {
        try
        {
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "shop_step", "shop");
            record.PickedId = step.ActionId;
            record.PickedName = step.Description;
            record.Role = step.Kind.ToString();
            record.Score = evaluation?.ImmediateScore ?? step.ScoreContribution;
            record.Notes =
            [
                $"gold={snapshot.Gold}",
                $"remainingGold={step.GoldAfter}",
                $"kind={step.Kind}",
                $"roomKey={snapshot.RoomVisitKey}"
            ];

            lock (Sync)
            {
                Decisions.Add(record);
                AiTelemetryPlayerReport report = GetOrCreatePlayer(player);
                report.ShopSteps++;
                if (step.Kind == ShopActionKind.RemoveCard)
                {
                    report.ShopRemovals++;
                }
                else if (step.Kind == ShopActionKind.BuyOffer)
                {
                    report.ShopPurchases++;
                }
            }

            Log.Info($"[AITeammate][Telemetry] Shop step player={player.NetId} action={step.ActionId} kind={step.Kind} score={record.Score:F1}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record shop step. {ex.Message}");
        }
    }

    public static void RecordPotionReward(Player player, string potionId, string outcome)
    {
        try
        {
            AiTelemetryDecisionRecord record = BuildChoiceRecord(player, "potion_reward", "reward");
            record.PickedId = potionId;
            record.PickedName = potionId;
            record.Notes = [outcome];

            lock (Sync)
            {
                Decisions.Add(record);
                GetOrCreatePlayer(player).PotionRewardChoices++;
            }

            Log.Info($"[AITeammate][Telemetry] Potion reward player={player.NetId} potion={potionId} outcome={outcome}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to record potion reward. {ex.Message}");
        }
    }

    public static void CapturePlayerSnapshot(Player player, string reason)
    {
        try
        {
            CardEvaluationContext context = new CardEvaluationContextFactory().Create(
                player,
                CardChoiceSource.ForcedChoice,
                skipAllowed: false,
                debugSource: $"telemetry_{reason}");
            AiTelemetryPlayerReport report = GetOrCreatePlayer(player);
            lock (Sync)
            {
                report.CharacterId = AiCharacterCombatConfigLoader.LoadForPlayer(player).CharacterId;
                report.LastAct = player.RunState.CurrentActIndex + 1;
                report.LastFloor = player.RunState.TotalFloor;
                report.FinalHp = player.Creature.CurrentHp;
                report.MaxHp = player.Creature.MaxHp;
                report.Gold = player.Gold;
                report.ActiveBuildId = DetectActiveBuildId(context);
                report.Deck = AiTelemetryDeckSnapshot.From(context.DeckSummary, context.DeckCards);
                report.MissingCoreCards = DetectMissingCoreCards(context);
                report.Relics = player.Relics.Select(static relic => relic.Id.Entry).OrderBy(static id => id, StringComparer.Ordinal).ToList();
                report.Potions = player.PotionSlots
                    .Where(static potion => potion != null)
                    .Select(static potion => potion!.Id.Entry)
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToList();
                report.LastSnapshotReason = reason;
                report.ProbableIssues = DiagnoseIssues(report);
            }

            Log.Info($"[AITeammate][Telemetry] Snapshot player={player.NetId} reason={reason} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} deck={report.Deck.CardCount} build={report.ActiveBuildId}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to capture player snapshot. {ex.Message}");
        }
    }

    public static void FlushRun(string reason)
    {
        try
        {
            AiTelemetryRunFile file;
            string flushedRunId;
            lock (Sync)
            {
                foreach (AiTelemetryPlayerReport report in Players.Values)
                {
                    report.ProbableIssues = DiagnoseIssues(report);
                }

                file = new AiTelemetryRunFile
                {
                    SchemaVersion = SchemaVersion,
                    RunId = _runId,
                    CreatedAtUtc = DateTime.UtcNow,
                    FlushReason = reason,
                    Abandoned = _abandoned,
                    PlayerReports = Players.Values.OrderBy(static player => player.PlayerId).ToList(),
                    Decisions = Decisions.ToList()
                };
                flushedRunId = _runId;
            }

            file = SanitizeRunFile(file);
            Directory.CreateDirectory(GetTelemetryRunsDirectoryPath());
            string runPath = Path.Combine(GetTelemetryRunsDirectoryPath(), $"{flushedRunId}.json");
            File.WriteAllText(runPath, JsonSerializer.Serialize(file, JsonOptions));
            File.WriteAllText(GetLatestSummaryPath(), JsonSerializer.Serialize(BuildLatestSummary(file), JsonOptions));

            lock (Sync)
            {
                if (string.Equals(_runId, flushedRunId, StringComparison.Ordinal))
                {
                    ResetInMemoryRun();
                }
            }

            Log.Info($"[AITeammate][Telemetry] Flushed run telemetry players={file.PlayerReports.Count} decisions={file.Decisions.Count} reason={reason} path={runPath} nextRun={_runId}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[AITeammate][Telemetry] Failed to flush run telemetry. {ex.Message}");
        }
    }

    private static AiTelemetryDecisionRecord BuildChoiceRecord(
        Player player,
        string type,
        string source,
        string? activeBuildId = null)
    {
        return new AiTelemetryDecisionRecord
        {
            DecisionType = type,
            PlayerId = player.NetId,
            CharacterId = AiCharacterCombatConfigLoader.LoadForPlayer(player).CharacterId,
            Act = player.RunState.CurrentActIndex + 1,
            Floor = player.RunState.TotalFloor,
            RoomType = player.RunState.CurrentRoom?.GetType().Name ?? "unknown",
            ActiveBuildId = activeBuildId ?? "unknown",
            Role = source,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<string> BuildCombatDecisionNotes(
        DeterministicCombatContext context,
        AiLegalActionOption action,
        ResolvedCardView? card,
        CombatActionScore score,
        IReadOnlyList<CombatActionScore> rankedScores,
        CombatLinePlan? plan)
    {
        List<string> notes =
        [
            $"action={action.ActionId}",
            $"category={score.Category}",
            $"enemy={action.TargetId ?? "none"}"
        ];

        if (plan != null)
        {
            notes.Add($"lineActions={string.Join(">", plan.ActionIds)}");
        }

        if (card != null)
        {
            if (IsStarterStrike(card))
            {
                notes.Add("starter_strike");
            }

            if (card.GetCardsDrawn() > 0 && context.Energy <= 0)
            {
                notes.Add("late_draw_no_energy");
            }

            if (card.GetEstimatedBlock() > 0 && context.TotalBlockableIncomingDamage <= context.CurrentBlock)
            {
                notes.Add("block_when_already_covered");
            }
        }

        notes.AddRange(AiCombatTurnDiagnostics.BuildNotes(context, action, card, score, rankedScores, plan));
        return notes;
    }

    private static List<string> BuildCardChoiceNotes(CardChoiceDecision decision, CardModel? selected)
    {
        List<string> notes = [];
        if (selected == null)
        {
            notes.Add(decision.SkipReason ?? "skipped");
        }

        notes.Add(decision.ActiveBuildLocked
            ? $"active_build={decision.ActiveBuildId}:locked"
            : $"active_build={decision.ActiveBuildId}");

        foreach (CardEvaluationResult result in decision.RankedResults.Take(3))
        {
            notes.Add($"{result.Candidate.CardId}:{result.FinalScore:F1}:offBuild={result.IsOffBuild}");
        }

        return notes;
    }

    private static AiTelemetryPlayerReport GetOrCreatePlayer(Player player)
    {
        lock (Sync)
        {
            if (!Players.TryGetValue(player.NetId, out AiTelemetryPlayerReport? report))
            {
                report = new AiTelemetryPlayerReport
                {
                    PlayerId = player.NetId,
                    CharacterId = AiCharacterCombatConfigLoader.LoadForPlayer(player).CharacterId,
                    MaxHp = player.Creature.MaxHp,
                    FinalHp = player.Creature.CurrentHp
                };
                Players[player.NetId] = report;
            }

            return report;
        }
    }

    private static string DetectActiveBuildId(CardEvaluationContext context)
    {
        AiBuildArchetype? active = AiBuildArchetypeCatalog.ForCharacter(AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId)
            .Select(profile => new
            {
                Profile = profile,
                Score = ScoreBuildProfile(profile, context.DeckCards),
                Locked = IsBuildLocked(profile, context.DeckCards)
            })
            .Where(static candidate => candidate.Score > 0)
            .OrderByDescending(static candidate => candidate.Locked)
            .ThenByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Profile.BuildId, StringComparer.Ordinal)
            .Select(static candidate => candidate.Profile)
            .FirstOrDefault();
        return active?.BuildId ?? "none";
    }

    private static List<string> DetectMissingCoreCards(CardEvaluationContext context)
    {
        AiBuildArchetype? active = AiBuildArchetypeCatalog.ForCharacter(AiCharacterCombatConfigLoader.LoadForPlayer(context.Player).CharacterId)
            .OrderByDescending(profile => ScoreBuildProfile(profile, context.DeckCards))
            .FirstOrDefault();
        if (active == null)
        {
            return [];
        }

        return active.CoreCards
            .Where(core => !context.DeckCards.Any(card => MatchesToken(core, card.CardId, card.Name)))
            .Take(8)
            .ToList();
    }

    private static bool IsBuildLocked(AiBuildArchetype profile, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => MatchesAny(profile.CoreCards, card.CardId, card.Name));
        return coreMatches >= profile.LockCoreCardCount || ScoreBuildProfile(profile, deckCards) >= profile.LockEvidenceScore;
    }

    private static double ScoreBuildProfile(AiBuildArchetype profile, IReadOnlyList<ResolvedCardView> deckCards)
    {
        int coreMatches = deckCards.Count(card => MatchesAny(profile.CoreCards, card.CardId, card.Name));
        int supportMatches = deckCards.Count(card => MatchesAny(profile.SupportCards, card.CardId, card.Name));
        int avoidMatches = deckCards.Count(card => MatchesAny(profile.AvoidCards, card.CardId, card.Name));
        return (coreMatches * 14d) + (supportMatches * 6d) - (avoidMatches * 10d);
    }

    private static bool MatchesAny(IReadOnlyList<string> tokens, params string[] values)
    {
        return tokens.Any(token => MatchesToken(token, values));
    }

    private static bool MatchesToken(string token, params string[] values)
    {
        string normalizedToken = AiBuildProfileAnalyzer.Normalize(token);
        return values.Any(value => AiBuildProfileAnalyzer.Normalize(value).Contains(normalizedToken, StringComparison.Ordinal));
    }

    private static List<string> DiagnoseIssues(AiTelemetryPlayerReport report)
    {
        List<string> issues = [];
        if (report.Deck.CardCount >= 24 && report.CardRewardSkips < Math.Max(2, report.CardRewardPicks / 4))
        {
            issues.Add("deck_may_be_too_large_or_skip_threshold_too_low");
        }

        if (report.Deck.FrontloadDamageSources < 6 && report.LastAct <= 1)
        {
            issues.Add("possible_act1_frontload_damage_shortage");
        }

        if (report.Deck.BlockSources < 6 && report.TotalUncoveredIncomingSeen > 20)
        {
            issues.Add("possible_block_shortage");
        }

        if (report.Deck.ScalingSources < 2 && report.BossCombats > 0)
        {
            issues.Add("possible_scaling_shortage_for_bosses");
        }

        if (report.Potions.Count > 0 && report.FinalHp > 0 && report.FinalHp <= Math.Max(15, report.MaxHp / 4))
        {
            issues.Add("low_hp_with_unused_potions");
        }

        if (report.Potions.Count > 0 && report.FinalHp <= 0)
        {
            issues.Add("death_with_unused_potions");
        }

        if (report.EndTurnsWithEnergy >= 3)
        {
            issues.Add("frequent_end_turn_with_energy");
        }

        if (report.StarterStrikePlays >= 6 && report.LastAct >= 2)
        {
            issues.Add("starter_strikes_still_used_often");
        }

        if (report.LikelyOverblockPlays >= 4)
        {
            issues.Add("possible_overblocking");
        }

        if (report.MissingCoreCards.Count >= 4 && report.ActiveBuildId != "none")
        {
            issues.Add("active_build_missing_many_core_cards");
        }

        return issues;
    }

    private static object BuildLatestSummary(AiTelemetryRunFile file)
    {
        return new
        {
            file.SchemaVersion,
            file.RunId,
            file.CreatedAtUtc,
            file.FlushReason,
            file.Abandoned,
            PlayerCount = file.PlayerReports.Count,
            DecisionCount = file.Decisions.Count,
            Players = file.PlayerReports.Select(static player => new
            {
                player.PlayerId,
                player.CharacterId,
                player.ActiveBuildId,
                player.LastAct,
                player.LastFloor,
                Hp = $"{player.FinalHp}/{player.MaxHp}",
                player.Deck.CardCount,
                player.Deck.UpgradedCardCount,
                player.CardRewardPicks,
                player.CardRewardSkips,
                player.ShopRemovals,
                player.RestSitesUpgraded,
                player.RestSitesHealed,
                player.ProbableIssues
            }).ToList()
        };
    }

    private static AiTelemetryRunFile SanitizeRunFile(AiTelemetryRunFile file)
    {
        return new AiTelemetryRunFile
        {
            SchemaVersion = file.SchemaVersion,
            RunId = file.RunId,
            CreatedAtUtc = file.CreatedAtUtc,
            FlushReason = file.FlushReason,
            Abandoned = file.Abandoned,
            PlayerReports = file.PlayerReports.Select(SanitizePlayerReport).ToList(),
            Decisions = file.Decisions.Select(SanitizeDecisionRecord).ToList()
        };
    }

    private static AiTelemetryPlayerReport SanitizePlayerReport(AiTelemetryPlayerReport report)
    {
        return new AiTelemetryPlayerReport
        {
            PlayerId = report.PlayerId,
            CharacterId = report.CharacterId,
            ActiveBuildId = report.ActiveBuildId,
            LastAct = report.LastAct,
            LastFloor = report.LastFloor,
            LastRoomType = report.LastRoomType,
            LastSnapshotReason = report.LastSnapshotReason,
            FinalHp = report.FinalHp,
            MaxHp = report.MaxHp,
            Gold = report.Gold,
            CombatsCompleted = report.CombatsCompleted,
            EliteCombats = report.EliteCombats,
            BossCombats = report.BossCombats,
            CombatDecisions = report.CombatDecisions,
            TotalEstimatedDamage = report.TotalEstimatedDamage,
            TotalEstimatedDamageTaken = report.TotalEstimatedDamageTaken,
            TotalIncomingSeen = report.TotalIncomingSeen,
            TotalUncoveredIncomingSeen = report.TotalUncoveredIncomingSeen,
            EndTurnsWithEnergy = report.EndTurnsWithEnergy,
            StarterStrikePlays = report.StarterStrikePlays,
            LikelyOverblockPlays = report.LikelyOverblockPlays,
            CardRewardPicks = report.CardRewardPicks,
            CardRewardSkips = report.CardRewardSkips,
            OffBuildCardOffersSeen = report.OffBuildCardOffersSeen,
            RelicChoices = report.RelicChoices,
            PotionRewardChoices = report.PotionRewardChoices,
            Upgrades = report.Upgrades,
            RestSitesUpgraded = report.RestSitesUpgraded,
            RestSitesHealed = report.RestSitesHealed,
            ShopSteps = report.ShopSteps,
            ShopPurchases = report.ShopPurchases,
            ShopRemovals = report.ShopRemovals,
            Deck = SanitizeDeckSnapshot(report.PlayerId, report.Deck),
            MissingCoreCards = report.MissingCoreCards,
            Relics = report.Relics,
            Potions = report.Potions,
            ProbableIssues = report.ProbableIssues
        };
    }

    private static AiTelemetryDeckSnapshot SanitizeDeckSnapshot(ulong playerId, AiTelemetryDeckSnapshot deck)
    {
        return new AiTelemetryDeckSnapshot
        {
            CardCount = deck.CardCount,
            UpgradedCardCount = deck.UpgradedCardCount,
            AttackCount = deck.AttackCount,
            SkillCount = deck.SkillCount,
            PowerCount = deck.PowerCount,
            FrontloadDamageSources = deck.FrontloadDamageSources,
            BlockSources = deck.BlockSources,
            DrawSources = deck.DrawSources,
            EnergySources = deck.EnergySources,
            ScalingSources = deck.ScalingSources,
            AverageCost = SafeTelemetryDouble(deck.AverageCost, $"player={playerId}:deck.averageCost"),
            AverageDamage = SafeTelemetryDouble(deck.AverageDamage, $"player={playerId}:deck.averageDamage"),
            AverageBlock = SafeTelemetryDouble(deck.AverageBlock, $"player={playerId}:deck.averageBlock"),
            CardIds = deck.CardIds
        };
    }

    private static AiTelemetryDecisionRecord SanitizeDecisionRecord(AiTelemetryDecisionRecord record)
    {
        return new AiTelemetryDecisionRecord
        {
            DecisionType = record.DecisionType,
            PlayerId = record.PlayerId,
            CharacterId = record.CharacterId,
            Act = record.Act,
            Floor = record.Floor,
            RoomType = record.RoomType,
            ActiveBuildId = record.ActiveBuildId,
            PickedId = record.PickedId,
            PickedName = record.PickedName,
            Role = record.Role,
            Score = SafeTelemetryDouble(record.Score, $"player={record.PlayerId}:decision={record.DecisionType}:score:{record.PickedId}"),
            Rank = record.Rank,
            AlternativeCount = record.AlternativeCount,
            Threshold = SafeTelemetryDouble(record.Threshold, $"player={record.PlayerId}:decision={record.DecisionType}:threshold:{record.PickedId}"),
            IncomingDamage = record.IncomingDamage,
            CurrentBlock = record.CurrentBlock,
            Energy = record.Energy,
            EstimatedDamage = record.EstimatedDamage,
            EstimatedDamageTaken = record.EstimatedDamageTaken,
            Notes = record.Notes,
            CreatedAtUtc = record.CreatedAtUtc
        };
    }

    private static double SafeTelemetryDouble(double value, string field)
    {
        if (!double.IsNaN(value) && !double.IsInfinity(value))
        {
            return value;
        }

        Log.Warn($"[AITeammate][Telemetry] Replaced non-finite telemetry value field={field} value={value} with 0.");
        return 0d;
    }

    private static bool IsStarterStrike(ResolvedCardView card)
    {
        string id = AiBuildProfileAnalyzer.Normalize(card.CardId);
        string name = AiBuildProfileAnalyzer.Normalize(card.Name);
        return id == "STRIKE" || name == "STRIKE" || id.EndsWith("STRIKE", StringComparison.Ordinal);
    }

    private static bool IsEliteRoom(AbstractRoom room)
    {
        return room.GetType().Name.Contains("Elite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBossRoom(Player player, AbstractRoom room)
    {
        return player.RunState.CurrentMapPoint?.PointType.ToString().Contains("Boss", StringComparison.OrdinalIgnoreCase) == true ||
               room.GetType().Name.Contains("Boss", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptionHeal(string optionId)
    {
        return optionId.Contains("HEAL", StringComparison.OrdinalIgnoreCase) ||
               optionId.Contains("REST", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptionUpgrade(string optionId)
    {
        return optionId.Contains("UPGRADE", StringComparison.OrdinalIgnoreCase) ||
               optionId.Contains("SMITH", StringComparison.OrdinalIgnoreCase) ||
               optionId.Contains("FORGE", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTelemetryDirectoryPath()
    {
        return AiTeammateStoragePaths.GetRuntimeDataDirectory("ai-telemetry");
    }

    private static string GetTelemetryRunsDirectoryPath()
    {
        return Path.Combine(GetTelemetryDirectoryPath(), "runs");
    }

    private static string GetLatestSummaryPath()
    {
        return Path.Combine(GetTelemetryDirectoryPath(), "latest-summary.json");
    }

    private static string CreateRunId()
    {
        return $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..25];
    }

    private static void ResetInMemoryRun()
    {
        Players.Clear();
        Decisions.Clear();
        _abandoned = false;
        _runId = CreateRunId();
    }

}

internal sealed class AiTelemetryRunFile
{
    public int SchemaVersion { get; init; }
    public string RunId { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public string FlushReason { get; init; } = string.Empty;
    public bool Abandoned { get; init; }
    public IReadOnlyList<AiTelemetryPlayerReport> PlayerReports { get; init; } = [];
    public IReadOnlyList<AiTelemetryDecisionRecord> Decisions { get; init; } = [];
}

internal sealed class AiTelemetryPlayerReport
{
    public ulong PlayerId { get; init; }
    public string CharacterId { get; set; } = string.Empty;
    public string ActiveBuildId { get; set; } = "none";
    public int LastAct { get; set; }
    public int LastFloor { get; set; }
    public string LastRoomType { get; set; } = "unknown";
    public string LastSnapshotReason { get; set; } = string.Empty;
    public int FinalHp { get; set; }
    public int MaxHp { get; set; }
    public int Gold { get; set; }
    public int CombatsCompleted { get; set; }
    public int EliteCombats { get; set; }
    public int BossCombats { get; set; }
    public int CombatDecisions { get; set; }
    public int TotalEstimatedDamage { get; set; }
    public int TotalEstimatedDamageTaken { get; set; }
    public int TotalIncomingSeen { get; set; }
    public int TotalUncoveredIncomingSeen { get; set; }
    public int EndTurnsWithEnergy { get; set; }
    public int StarterStrikePlays { get; set; }
    public int LikelyOverblockPlays { get; set; }
    public int CardRewardPicks { get; set; }
    public int CardRewardSkips { get; set; }
    public int OffBuildCardOffersSeen { get; set; }
    public int RelicChoices { get; set; }
    public int PotionRewardChoices { get; set; }
    public int Upgrades { get; set; }
    public int RestSitesUpgraded { get; set; }
    public int RestSitesHealed { get; set; }
    public int ShopSteps { get; set; }
    public int ShopPurchases { get; set; }
    public int ShopRemovals { get; set; }
    public AiTelemetryDeckSnapshot Deck { get; set; } = new();
    public IReadOnlyList<string> MissingCoreCards { get; set; } = [];
    public IReadOnlyList<string> Relics { get; set; } = [];
    public IReadOnlyList<string> Potions { get; set; } = [];
    public IReadOnlyList<string> ProbableIssues { get; set; } = [];
}

internal sealed class AiTelemetryDeckSnapshot
{
    public int CardCount { get; init; }
    public int UpgradedCardCount { get; init; }
    public int AttackCount { get; init; }
    public int SkillCount { get; init; }
    public int PowerCount { get; init; }
    public int FrontloadDamageSources { get; init; }
    public int BlockSources { get; init; }
    public int DrawSources { get; init; }
    public int EnergySources { get; init; }
    public int ScalingSources { get; init; }
    public double AverageCost { get; init; }
    public double AverageDamage { get; init; }
    public double AverageBlock { get; init; }
    public IReadOnlyList<string> CardIds { get; init; } = [];

    public static AiTelemetryDeckSnapshot From(DeckSummary summary, IReadOnlyList<ResolvedCardView> cards)
    {
        return new AiTelemetryDeckSnapshot
        {
            CardCount = summary.CardCount,
            UpgradedCardCount = summary.UpgradedCardCount,
            AttackCount = summary.AttackCount,
            SkillCount = summary.SkillCount,
            PowerCount = summary.PowerCount,
            FrontloadDamageSources = summary.FrontloadDamageSources,
            BlockSources = summary.BlockSources,
            DrawSources = summary.DrawSources,
            EnergySources = summary.EnergySources,
            ScalingSources = summary.ScalingSources,
            AverageCost = summary.AverageCost,
            AverageDamage = summary.AverageDamage,
            AverageBlock = summary.AverageBlock,
            CardIds = cards.Select(static card => card.CardId).OrderBy(static id => id, StringComparer.Ordinal).ToList()
        };
    }
}

internal sealed class AiTelemetryDecisionRecord
{
    public string DecisionType { get; init; } = string.Empty;
    public ulong PlayerId { get; init; }
    public string CharacterId { get; init; } = string.Empty;
    public int Act { get; init; }
    public int Floor { get; init; }
    public string RoomType { get; init; } = string.Empty;
    public string ActiveBuildId { get; init; } = "none";
    public string PickedId { get; set; } = string.Empty;
    public string PickedName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public double Score { get; set; }
    public int Rank { get; set; }
    public int AlternativeCount { get; set; }
    public double Threshold { get; set; }
    public int IncomingDamage { get; set; }
    public int CurrentBlock { get; set; }
    public int Energy { get; set; }
    public int EstimatedDamage { get; set; }
    public int EstimatedDamageTaken { get; set; }
    public IReadOnlyList<string> Notes { get; set; } = [];
    public DateTime CreatedAtUtc { get; init; }
}
