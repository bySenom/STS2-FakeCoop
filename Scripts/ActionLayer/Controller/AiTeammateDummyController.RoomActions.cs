using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace AITeammate.Scripts;

internal sealed partial class AiTeammateDummyController
{
    private static readonly MethodInfo? EventChooseOptionForEventMethod =
        AccessTools.Method(typeof(EventSynchronizer), "ChooseOptionForEvent");
    private static readonly MethodInfo? EventVoteForSharedOptionMethod =
        AccessTools.Method(typeof(EventSynchronizer), "PlayerVotedForSharedOptionIndex");
    private static readonly MethodInfo? RestSiteChooseOptionMethod =
        AccessTools.Method(typeof(RestSiteSynchronizer), "ChooseOption");
    private static readonly FieldInfo? EventPageIndexField =
        AccessTools.Field(typeof(EventSynchronizer), "_pageIndex");

    private IReadOnlyList<AiTeammateAvailableAction> DiscoverEventActions(Player player)
    {
        EventSynchronizer synchronizer = RunManager.Instance.EventSynchronizer;
        if (synchronizer.IsShared && synchronizer.GetPlayerVote(player).HasValue)
        {
            return [];
        }

        EventModel eventForPlayer = synchronizer.GetEventForPlayer(player);
        IReadOnlyList<EventOption> options = eventForPlayer.CurrentOptions;
        string eventFingerprint = BuildEventActionFingerprint(synchronizer, eventForPlayer);
        EventPlanningInspection inspection = InspectCurrentEventPlan(player, synchronizer, eventForPlayer, eventFingerprint);
        EventExecutionSelection selection = ResolveEventExecutionSelection(
            player,
            synchronizer,
            eventForPlayer,
            inspection,
            eventFingerprint,
            phase: "discover");

        if (selection.OptionIndex < 0 || selection.SelectedOption == null)
        {
            return [];
        }

        return
        [
            new AiTeammateAvailableAction(
                new AiLegalActionOption
                {
                    ActionId = BuildEventOptionActionId(eventFingerprint, selection.OptionIndex),
                    ActionType = AiTeammateActionKind.ChooseEventOption.ToString(),
                    Description = $"Choose event option {selection.SelectedOption.TextKey}",
                    Label = $"Event option {selection.SelectedOption.TextKey}",
                    Summary = $"Choose event option {selection.SelectedOption.TextKey}."
                },
                async () =>
                {
                    EventModel liveEvent = synchronizer.GetEventForPlayer(player);
                    EventExecutionSelection liveSelection = ResolveEventExecutionSelection(
                        player,
                        synchronizer,
                        liveEvent,
                        inspection,
                        eventFingerprint,
                        phase: "execute");
                    if (liveSelection.OptionIndex < 0 || liveSelection.SelectedOption == null)
                    {
                        return AiActionExecutionResult.Completed;
                    }

                    if (string.Equals(liveSelection.SelectionMode, "planner", System.StringComparison.Ordinal))
                    {
                        Log.Info($"[AITeammate][Event] Executing planner-selected event option player={PlayerId} optionIndex={liveSelection.OptionIndex} textKey={liveSelection.SelectedOption.TextKey} title=\"{DescribeOptionTitle(liveSelection.SelectedOption)}\"");
                    }
                    else
                    {
                        Log.Info($"[AITeammate][Event] Executing fallback event option player={PlayerId} optionIndex={liveSelection.OptionIndex} textKey={liveSelection.SelectedOption.TextKey} title=\"{DescribeOptionTitle(liveSelection.SelectedOption)}\" reason={liveSelection.Reason}");
                    }

                    await ChooseEventOptionAsync(synchronizer, player, liveSelection.OptionIndex);
                    return AiActionExecutionResult.Completed;
                },
                $"{PlayerId}:event:{eventFingerprint}:{selection.OptionIndex}")
        ];
    }

    private IReadOnlyList<AiTeammateAvailableAction> DiscoverRestSiteActions(Player player)
    {
        RestSiteSynchronizer synchronizer = RunManager.Instance.RestSiteSynchronizer;
        IReadOnlyList<RestSiteOption> options = synchronizer.GetOptionsForPlayer(player);
        Log.Info($"[AITeammate][RestSite] Options player={player.NetId} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} options=[{string.Join(", ", options.Select(static option => option.OptionId))}]");

        RestSiteOption? preferredOption = SelectRestSiteOption(player, options, out string reason);
        if (preferredOption == null)
        {
            Log.Info($"[AITeammate][RestSite] No available rest site option player={player.NetId}");
            return [];
        }

        int optionIndex = options.ToList().IndexOf(preferredOption);
        Log.Info($"[AITeammate][RestSite] Selected option player={player.NetId} option={preferredOption.OptionId} index={optionIndex} reason={reason}");
        return
        [
            new AiTeammateAvailableAction(
                new AiLegalActionOption
                {
                    ActionId = BuildRestSiteOptionActionId(preferredOption.OptionId, optionIndex),
                    ActionType = AiTeammateActionKind.ChooseRestSiteOption.ToString(),
                    Description = $"Choose rest site option {preferredOption.OptionId}",
                    Label = $"Rest site option {preferredOption.OptionId}",
                    Summary = $"Choose rest site option {preferredOption.OptionId}: {reason}."
                },
                async () =>
                {
                    Log.Info($"[AITeammate][RestSite] Executing option player={player.NetId} option={preferredOption.OptionId} index={optionIndex} reason={reason}");
                    AiRunTelemetryService.RecordRestSiteChoice(player, preferredOption.OptionId, reason);
                    await ChooseRestSiteOptionAsync(synchronizer, player, optionIndex);
                    return AiActionExecutionResult.Completed;
                })
        ];
    }

    private static RestSiteOption? SelectRestSiteOption(
        Player player,
        IReadOnlyList<RestSiteOption> options,
        out string reason)
    {
        if (options.Count == 0)
        {
            reason = "no options";
            return null;
        }

        RestSiteOption? healOption = options.FirstOrDefault(IsHealOption);
        RestSiteOption? upgradeOption = options.FirstOrDefault(IsUpgradeOption);
        RestSiteOption? nonHealFallback = options.FirstOrDefault(static option => !IsHealOption(option));
        bool hasUpgradableCards = player.Deck.Cards.Any(static card => card.IsUpgradable);
        int missingHp = Math.Max(0, player.Creature.MaxHp - player.Creature.CurrentHp);
        double hpRatio = player.Creature.MaxHp > 0
            ? (double)player.Creature.CurrentHp / player.Creature.MaxHp
            : 0d;
        bool healIsUseful = missingHp >= 12;
        bool shouldHeal = healIsUseful && (player.Creature.CurrentHp <= 18 || hpRatio <= 0.45d || missingHp >= 24);
        bool isSilent = string.Equals(AiCharacterCombatConfigLoader.LoadForPlayer(player).CharacterId, "silent", System.StringComparison.OrdinalIgnoreCase);
        if (isSilent && healOption != null && player.Creature.CurrentHp < 40 && missingHp >= 10)
        {
            reason = $"silent survival rest below 40 hp {player.Creature.CurrentHp}/{player.Creature.MaxHp} missing={missingHp}";
            return healOption;
        }

        if (shouldHeal && healOption != null)
        {
            reason = $"meaningful low hp {player.Creature.CurrentHp}/{player.Creature.MaxHp} missing={missingHp}";
            return healOption;
        }

        if (upgradeOption != null && hasUpgradableCards)
        {
            reason = shouldHeal
                ? "heal unavailable; upgrade best available fallback"
                : $"prefer upgrade hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} missing={missingHp}";
            return upgradeOption;
        }

        if (nonHealFallback != null && (!healIsUseful || player.Creature.CurrentHp >= player.Creature.MaxHp))
        {
            reason = hasUpgradableCards
                ? $"upgrade token unrecognized; prefer non-heal option={nonHealFallback.OptionId}"
                : $"heal not useful missing={missingHp}; prefer non-heal option={nonHealFallback.OptionId}";
            return nonHealFallback;
        }

        if (healOption != null)
        {
            reason = hasUpgradableCards
                ? "upgrade unavailable or unrecognized; heal fallback"
                : "no upgradable cards; heal fallback";
            return healOption;
        }

        reason = "fallback first option";
        return options.FirstOrDefault();
    }

    private static bool IsHealOption(RestSiteOption option)
    {
        return option.OptionId.Contains("HEAL", System.StringComparison.OrdinalIgnoreCase) ||
               option.OptionId.Contains("REST", System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpgradeOption(RestSiteOption option)
    {
        return option.OptionId.Contains("UPGRADE", System.StringComparison.OrdinalIgnoreCase) ||
               option.OptionId.Contains("SMITH", System.StringComparison.OrdinalIgnoreCase) ||
               option.OptionId.Contains("FORGE", System.StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEventActionFingerprint(EventSynchronizer synchronizer, EventModel eventForPlayer)
    {
        uint pageIndex = EventPageIndexField?.GetValue(synchronizer) is uint currentPageIndex
            ? currentPageIndex
            : 0u;
        string optionFingerprint = string.Join(
            ",",
            eventForPlayer.CurrentOptions.Select(static option => $"{option.TextKey}:{option.IsLocked}:{option.IsProceed}"));
        return $"{eventForPlayer.Id}|finished={eventForPlayer.IsFinished}|page={pageIndex}|options={optionFingerprint}";
    }

    private static async Task ChooseEventOptionAsync(EventSynchronizer synchronizer, Player player, int optionIndex)
    {
        using IDisposable selectorScope = PushDeterministicCardSelector();

        if (synchronizer.IsShared)
        {
            uint pageIndex = EventPageIndexField?.GetValue(synchronizer) is uint currentPageIndex
                ? currentPageIndex
                : 0u;
            EventVoteForSharedOptionMethod?.Invoke(synchronizer, new object[] { player, (uint)optionIndex, pageIndex });
            await Task.CompletedTask;
            return;
        }

        EventChooseOptionForEventMethod?.Invoke(synchronizer, new object[] { player, optionIndex });
        await Task.CompletedTask;
    }

    private static async Task ChooseRestSiteOptionAsync(RestSiteSynchronizer synchronizer, Player player, int optionIndex)
    {
        if (RestSiteChooseOptionMethod?.Invoke(synchronizer, new object[] { player, optionIndex }) is Task<bool> task)
        {
            await task;
        }
    }

    private static string BuildEventOptionActionId(string eventFingerprint, int optionIndex)
    {
        return $"event_option_{optionIndex}_{SanitizeActionToken(eventFingerprint)}";
    }

    private static string BuildRestSiteOptionActionId(string optionId, int optionIndex)
    {
        return $"rest_site_option_{optionIndex}_{SanitizeActionToken(optionId)}";
    }
}
