using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AITeammate.Scripts;

internal static class AiTeammateMapAndTreasurePatches
{
    private static readonly AiRelicChoiceEvaluator RelicEvaluator = new();

    [HarmonyPatch(typeof(MapSelectionSynchronizer), nameof(MapSelectionSynchronizer.PlayerVotedForMapCoord))]
    private static class MapSelectionSynchronizerPatch
    {
        private static void Postfix(Player player, MapLocation source, MapVote? destination)
        {
            AiTeammateSessionState? session = AiTeammateSessionRegistry.Current;
            if (session == null || player.NetId != session.HostPlayerId)
            {
                return;
            }

            foreach (AiTeammateSessionParticipant participant in session.Participants.Where(static participant => !participant.IsHost))
            {
                Player? aiPlayer = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(participant.PlayerId);
                if (aiPlayer == null)
                {
                    continue;
                }

                MapVote? existingVote = RunManager.Instance.MapSelectionSynchronizer.GetVote(aiPlayer);
                if (existingVote.Equals(destination))
                {
                    continue;
                }

                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(
                    new VoteForMapCoordAction(aiPlayer, source, destination));
            }
        }
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
    private static class TreasureRoomRelicSynchronizerBeginRelicPickingPatch
    {
        private static void Postfix(TreasureRoomRelicSynchronizer __instance)
        {
            AiTeammateSessionState? session = AiTeammateSessionRegistry.Current;
            if (session == null || RunManager.Instance.NetService is not AiTeammateLoopbackHostGameService)
            {
                return;
            }

            IReadOnlyList<RelicModel>? currentRelics = __instance.CurrentRelics;
            if (currentRelics == null || currentRelics.Count == 0)
            {
                Log.Info("[AITeammate] Treasure relic picking started with no relic options.");
                return;
            }

            List<Player> autoPickPlayers = GetAutoPickTreasurePlayers(session, __instance).ToList();
            if (autoPickPlayers.Count == 0)
            {
                Log.Info($"[AITeammate] Treasure relic picking started. relicCount={currentRelics.Count} autoPickPlayers=0");
                return;
            }

            Log.Info($"[AITeammate] Treasure relic picking started. relicCount={currentRelics.Count} autoPickPlayers={autoPickPlayers.Count}");
            IReadOnlyDictionary<ulong, int> assignments = BuildCoordinatedRelicAssignments(autoPickPlayers, currentRelics);
            foreach (Player player in autoPickPlayers)
            {
                int chosenRelicIndex = assignments.TryGetValue(player.NetId, out int assignedIndex)
                    ? assignedIndex
                    : 0;
                RelicModel relic = currentRelics[Math.Clamp(chosenRelicIndex, 0, currentRelics.Count - 1)];
                Log.Info($"[AITeammate] Auto-voting coordinated treasure relic player={player.NetId} relicIndex={chosenRelicIndex} relic={relic.Id.Entry}");
                __instance.OnPicked(player, chosenRelicIndex);
            }
        }

        private static IEnumerable<Player> GetAutoPickTreasurePlayers(
            AiTeammateSessionState session,
            TreasureRoomRelicSynchronizer synchronizer)
        {
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                yield break;
            }

            foreach (AiTeammateSessionParticipant participant in session.Participants)
            {
                if (participant.IsHost && !AiTeammateHostAutoMode.IsEnabled)
                {
                    continue;
                }

                Player? player = runState.GetPlayer(participant.PlayerId);
                if (player == null)
                {
                    continue;
                }

                TreasureRoomRelicSynchronizer.PlayerVote playerVote = synchronizer.GetPlayerVote(player);
                if (playerVote.voteReceived)
                {
                    continue;
                }

                yield return player;
            }
        }

        private static IReadOnlyDictionary<ulong, int> BuildCoordinatedRelicAssignments(
            IReadOnlyList<Player> players,
            IReadOnlyList<RelicModel> relics)
        {
            Dictionary<ulong, IReadOnlyList<AiRelicEvaluationResult>> rankingsByPlayer = new();
            Dictionary<ulong, Dictionary<int, double>> scoresByPlayer = new();
            for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                Player player = players[playerIndex];
                AiRelicChoiceDecision decision = RelicEvaluator.Evaluate(player, relics);
                rankingsByPlayer[player.NetId] = decision.RankedResults;
                Dictionary<int, double> scoreByIndex = new();
                for (int relicIndex = 0; relicIndex < relics.Count; relicIndex++)
                {
                    AiRelicEvaluationResult? result = decision.RankedResults.FirstOrDefault(candidate =>
                        ReferenceEquals(candidate.Relic, relics[relicIndex]) ||
                        string.Equals(candidate.RelicId, relics[relicIndex].Id.Entry, StringComparison.Ordinal));
                    scoreByIndex[relicIndex] = result?.Score ?? 0d;
                }

                scoresByPlayer[player.NetId] = scoreByIndex;
                Log.Info($"[AITeammate] Treasure relic team evaluation player={player.NetId} top=[{string.Join(" | ", decision.RankedResults.Take(3).Select(static result => result.Describe()))}]");
            }

            Dictionary<ulong, int> bestAssignment = new();
            Dictionary<ulong, int> currentAssignment = new();
            HashSet<int> usedRelicIndexes = new();
            double bestScore = double.NegativeInfinity;
            Search(playerIndex: 0, currentScore: 0d);
            Log.Info($"[AITeammate] Treasure relic coordinated assignment score={bestScore:F1} picks=[{string.Join(", ", bestAssignment.Select(pair => $"{pair.Key}->{relics[pair.Value].Id.Entry}"))}]");
            return bestAssignment;

            void Search(int playerIndex, double currentScore)
            {
                if (playerIndex >= players.Count)
                {
                    if (currentScore > bestScore)
                    {
                        bestScore = currentScore;
                        bestAssignment = new Dictionary<ulong, int>(currentAssignment);
                    }

                    return;
                }

                Player player = players[playerIndex];
                Dictionary<int, double> scores = scoresByPlayer[player.NetId];
                IEnumerable<int> candidateIndexes = relics.Count >= players.Count
                    ? Enumerable.Range(0, relics.Count).Where(index => !usedRelicIndexes.Contains(index))
                    : Enumerable.Range(0, relics.Count);

                foreach (int relicIndex in candidateIndexes.OrderByDescending(index => scores.GetValueOrDefault(index)).ThenBy(static index => index))
                {
                    currentAssignment[player.NetId] = relicIndex;
                    bool added = usedRelicIndexes.Add(relicIndex);
                    Search(playerIndex + 1, currentScore + scores.GetValueOrDefault(relicIndex));
                    if (added)
                    {
                        usedRelicIndexes.Remove(relicIndex);
                    }

                    currentAssignment.Remove(player.NetId);
                }
            }
        }
    }
}
