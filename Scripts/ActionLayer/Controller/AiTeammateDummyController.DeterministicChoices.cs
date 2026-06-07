using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.TestSupport;

namespace AITeammate.Scripts;

internal sealed partial class AiTeammateDummyController
{
    private static readonly FieldInfo? CardRewardCardsField =
        typeof(CardReward).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly CardChoiceEvaluator CardEvaluator = new();
    private static readonly AiRelicChoiceEvaluator RelicEvaluator = new();
    private static readonly CardUpgradeEvaluator UpgradeEvaluator = new();

    public static async Task ExecuteDeterministicRewardSetAsync(RewardsSet rewardsSet)
    {
        using IDisposable selectorScope = PushDeterministicCardSelector();
        Log.Info($"[AITeammate] Deterministic reward set start player={rewardsSet.Player.NetId} room={rewardsSet.Room?.GetType().Name ?? "Custom"} roomCount={rewardsSet.Player.RunState.CurrentRoomCount} currentRoom={rewardsSet.Player.RunState.CurrentRoom?.GetType().Name ?? "null"}");
        await rewardsSet.GenerateWithoutOffering();
        foreach (Reward reward in rewardsSet.Rewards.ToList())
        {
            Log.Info($"[AITeammate] Deterministic reward executing player={rewardsSet.Player.NetId} reward={reward.GetType().Name} roomCount={rewardsSet.Player.RunState.CurrentRoomCount} currentRoom={rewardsSet.Player.RunState.CurrentRoom?.GetType().Name ?? "null"}");
            await ExecuteRewardAsync(reward);
        }
        Log.Info($"[AITeammate] Deterministic reward set complete player={rewardsSet.Player.NetId} room={rewardsSet.Room?.GetType().Name ?? "Custom"} roomCount={rewardsSet.Player.RunState.CurrentRoomCount} currentRoom={rewardsSet.Player.RunState.CurrentRoom?.GetType().Name ?? "null"}");
    }

    public static async Task<bool> ExecuteDeterministicCardRewardAsync(CardReward reward)
    {
        List<CardCreationResult> cards = GetCardRewardCards(reward);
        ulong historyNetId = LocalContext.NetId ?? reward.Player.NetId;
        var historyEntry = reward.Player.RunState.CurrentMapPointHistoryEntry;
        if (historyEntry == null)
        {
            return false;
        }

        CardChoiceDecision decision = CardEvaluator.EvaluateCandidates(
            cards.Select(static card => card.Card),
            CardEvaluator.ContextFactory.Create(
                reward.Player,
                CardChoiceSource.Reward,
                reward.CanSkip,
                debugSource: "card_reward"));
        LogCardChoiceDecision(reward.Player, decision, "reward");

        CardModel? selected = decision.ShouldTakeCard
            ? decision.BestEvaluation?.CandidateCard
            : null;
        AiRunTelemetryService.RecordCardChoice(reward.Player, "reward", decision, selected);
        if (selected != null)
        {
            CardPileAddResult addResult = await CardPileCmd.Add(selected, PileType.Deck);
            if (addResult.success)
            {
                CardModel addedCard = addResult.cardAdded;
                historyEntry
                    .GetEntry(historyNetId)
                    .CardChoices.Add(new CardChoiceHistoryEntry(addedCard, wasPicked: true));
                RunManager.Instance.RewardSynchronizer.SyncLocalObtainedCard(addedCard);
                cards.RemoveAll(card => card.Card == selected);
                Log.Info($"[AITeammate] Deterministic card reward picked player={reward.Player.NetId} card={addedCard.Id.Entry}");
            }
        }
        else
        {
            Log.Info($"[AITeammate] Deterministic card reward skipped player={reward.Player.NetId} threshold={decision.SkipThreshold:F1}");
        }

        foreach (CardCreationResult card in cards)
        {
            historyEntry
                .GetEntry(historyNetId)
                .CardChoices.Add(new CardChoiceHistoryEntry(card.Card, wasPicked: false));
            RunManager.Instance.RewardSynchronizer.SyncLocalSkippedCard(card.Card);
        }

        return false;
    }

    public static async Task<CardModel?> ChooseFirstCardFromChooseScreenAsync(
        PlayerChoiceContext context,
        IReadOnlyList<CardModel> cards,
        Player player,
        bool canSkip)
    {
        CardChoiceSource source = canSkip ? CardChoiceSource.ChooseScreen : CardChoiceSource.ForcedChoice;
        CardChoiceDecision decision = CardEvaluator.EvaluateCandidates(
            cards,
            CardEvaluator.ContextFactory.Create(
                player,
                source,
                canSkip,
                debugSource: "choose_a_card"));
        LogCardChoiceDecision(player, decision, "choose_screen");
        CardModel? selected = decision.ShouldTakeCard
            ? decision.BestEvaluation?.CandidateCard
            : null;
        AiRunTelemetryService.RecordCardChoice(player, "choose_screen", decision, selected);
        return decision.ShouldTakeCard
            ? selected
            : null;
    }

    public static Task<IEnumerable<CardModel>> ChooseRewardGridCardsAsync(
        PlayerChoiceContext context,
        IReadOnlyList<CardCreationResult> cards,
        Player player,
        CardSelectorPrefs prefs)
    {
        List<CardModel> options = cards.Select(static card => card.Card).ToList();
        int desiredCount = ComputeSelectionCount(options.Count, prefs.MinSelect, prefs.MaxSelect);
        if (desiredCount <= 0)
        {
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
        }

        bool canSkip = prefs.MinSelect <= 0;
        CardChoiceDecision decision = CardEvaluator.EvaluateCandidates(
            options,
            CardEvaluator.ContextFactory.Create(
                player,
                canSkip ? CardChoiceSource.Reward : CardChoiceSource.ForcedChoice,
                canSkip,
                debugSource: "simple_grid_reward"));
        LogCardChoiceDecision(player, decision, "simple_grid_reward");

        if (!decision.ShouldTakeCard && canSkip)
        {
            AiRunTelemetryService.RecordCardChoice(player, "simple_grid_reward", decision, null);
            Log.Info($"[AITeammate] Deterministic simple-grid reward skipped player={player.NetId} threshold={decision.SkipThreshold:F1}");
            return Task.FromResult<IEnumerable<CardModel>>(Array.Empty<CardModel>());
        }

        List<CardModel> selected = decision.RankedResults
            .Select(static result => result.CandidateCard)
            .Take(desiredCount)
            .ToList();
        AiRunTelemetryService.RecordCardChoice(player, "simple_grid_reward", decision, selected.FirstOrDefault());
        Log.Info($"[AITeammate] Deterministic simple-grid reward picked player={player.NetId} cards=[{string.Join(", ", selected.Select(static card => card.Id.Entry))}]");
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public static async Task<IEnumerable<CardModel>> ChooseDeterministicCardsAsync(
        PlayerChoiceContext? context,
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect,
        PlayerChoiceOptions choiceOptions = PlayerChoiceOptions.None)
    {
        List<CardModel> list = options.ToList();
        int desiredCount = ComputeSelectionCount(list.Count, minSelect, maxSelect);
        IEnumerable<CardModel> selected = list.Take(desiredCount).ToList();

        return selected;
    }

    public static Task<IEnumerable<CardModel>> ChooseCardsForUpgradeAsync(
        Player player,
        IEnumerable<CardModel> options,
        int minSelect,
        int maxSelect)
    {
        List<CardModel> list = options.Where(static card => card.IsUpgradable).ToList();
        int desiredCount = ComputeSelectionCount(list.Count, minSelect, maxSelect);
        CardUpgradeDecision decision = UpgradeEvaluator.Evaluate(player, list);
        List<CardModel> selected = decision.SelectedCards.Take(desiredCount).ToList();
        Log.Info($"[AITeammate] Upgrade evaluation player={player.NetId} options={list.Count} selected=[{string.Join(", ", selected.Select(static card => card.Id.Entry))}]");
        foreach (CardUpgradeEvaluationResult result in decision.RankedResults.Take(5))
        {
            Log.Info($"[AITeammate] Upgrade evaluation rank player={player.NetId} {result.Describe()}");
        }

        AiRunTelemetryService.RecordUpgradeChoice(player, decision.RankedResults, selected);
        return Task.FromResult<IEnumerable<CardModel>>(selected);
    }

    public static RelicModel? ChooseBestRelic(Player player, IReadOnlyList<RelicModel> relics)
    {
        AiRelicChoiceDecision decision = RelicEvaluator.Evaluate(player, relics);
        Log.Info($"[AITeammate] Relic evaluation player={player.NetId} options={relics.Count} selected={decision.SelectedRelic?.Id.Entry ?? "none"}");
        foreach (AiRelicEvaluationResult result in decision.RankedResults.Take(3))
        {
            Log.Info($"[AITeammate] Relic evaluation rank player={player.NetId} {result.Describe()}");
        }

        AiRunTelemetryService.RecordRelicChoice(player, decision);
        return decision.SelectedRelic;
    }

    public static IReadOnlyList<CardModel> ChooseFirstBundle(IReadOnlyList<IReadOnlyList<CardModel>> bundles)
    {
        return bundles.FirstOrDefault() ?? Array.Empty<CardModel>();
    }

    public static IDisposable PushDeterministicCardSelector()
    {
        var selector = new DeterministicCardSelector();
        return CardSelectCmd.Selector == null
            ? CardSelectCmd.UseSelector(selector)
            : CardSelectCmd.PushSelector(selector);
    }

    private static int ComputeSelectionCount(int optionCount, int minSelect, int maxSelect)
    {
        if (optionCount <= 0 || maxSelect <= 0)
        {
            return 0;
        }

        int desiredCount = minSelect > 0 ? minSelect : 1;
        desiredCount = Math.Min(desiredCount, optionCount);
        desiredCount = Math.Min(desiredCount, maxSelect);
        return Math.Max(desiredCount, 0);
    }

    private static async Task ExecuteRewardAsync(Reward reward)
    {
        switch (reward)
        {
            case CardReward cardReward:
                await ExecuteDeterministicCardRewardAsync(cardReward);
                return;
            case PotionReward potionReward:
                if (await potionReward.SelectUnsynchronized())
                {
                    return;
                }

                PotionModel? incomingPotion = potionReward.Potion;
                if (incomingPotion != null &&
                    PotionHeuristicEvaluator.TryChoosePotionToReplace(
                        potionReward.Player,
                        incomingPotion,
                        out PotionModel? currentPotion,
                        out double incomingScore,
                        out double discardScore) &&
                    currentPotion != null)
                {
                    Log.Info($"[AITeammate] Potion reward replacement player={potionReward.Player.NetId} discard={currentPotion.Id.Entry} discardScore={discardScore:F1} incoming={incomingPotion.Id.Entry} incomingScore={incomingScore:F1}");
                    AiRunTelemetryService.RecordPotionReward(potionReward.Player, incomingPotion.Id.Entry, $"replace:{currentPotion.Id.Entry}:incomingScore={incomingScore:F1}:discardScore={discardScore:F1}");
                    await PotionCmd.Discard(currentPotion);
                    await potionReward.SelectUnsynchronized();
                }
                else if (incomingPotion != null)
                {
                    Log.Info($"[AITeammate] Potion reward skipped replacement player={potionReward.Player.NetId} incoming={incomingPotion.Id.Entry}");
                    AiRunTelemetryService.RecordPotionReward(potionReward.Player, incomingPotion.Id.Entry, "kept_existing_or_no_slot");
                }

                return;
            default:
                await reward.SelectUnsynchronized();
                return;
        }
    }

    private static List<CardCreationResult> GetCardRewardCards(CardReward reward)
    {
        return CardRewardCardsField?.GetValue(reward) as List<CardCreationResult> ?? [];
    }

    private static void LogCardChoiceDecision(Player player, CardChoiceDecision decision, string source)
    {
        Log.Info($"[AITeammate] Card evaluation player={player.NetId} source={source} {decision.Describe()}");
        foreach (CardEvaluationResult result in decision.RankedResults.Take(3))
        {
            string reasons = result.Reasons.Count > 0
                ? string.Join(", ", result.Reasons)
                : "no_reasons";
            Log.Info($"[AITeammate] Card evaluation rank player={player.NetId} source={source} {result.Describe()} reasons=[{reasons}]");
        }
    }

    private sealed class DeterministicCardSelector : ICardSelector
    {
        public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            List<CardModel> list = options.ToList();
            int selectionCount = ComputeSelectionCount(list.Count, minSelect, maxSelect);
            IEnumerable<CardModel> selected = list.Take(selectionCount).ToList();
            return Task.FromResult(selected);
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        {
            CardCreationResult? selected = options.FirstOrDefault();
            if (selected == null)
            {
                return default;
            }

            return new CardRewardSelection
            {
                card = selected.Card
            };
        }
    }
}
