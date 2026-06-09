using Godot;
using System.Collections.Generic;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AITeammate.Scripts;

internal static class AiTeammateHostAutoMode
{
    private static readonly TimeSpan ToggleCooldown = TimeSpan.FromMilliseconds(750);
    private static readonly HashSet<ulong> ForegroundRewardPlayers = [];
    private static readonly object ForegroundRewardLock = new();
    private static AiTeammateDummyController? _hostController;
    private static ulong? _hostControllerPlayerId;
    private static bool _wasTogglePressed;
    private static DateTime _nextToggleAllowedAtUtc = DateTime.MinValue;

    public static bool IsEnabled { get; private set; }

    public static void Tick()
    {
        AiTeammateSessionState? session = AiTeammateSessionRegistry.Current;
        if (session != null)
        {
            Tick(session);
            return;
        }

        ulong? localPlayerId = TryResolveLocalPlayerId();
        HandleToggleInput(localPlayerId ?? 0UL, mode: "local");
        if (!IsEnabled)
        {
            return;
        }

        if (localPlayerId.HasValue && IsForegroundRewardResolutionActive(localPlayerId.Value))
        {
            return;
        }

        AiTeammateDummyController? controller = GetOrCreateLocalPlayerController();
        controller?.Tick();
    }

    public static void Tick(AiTeammateSessionState session)
    {
        HandleToggleInput(session.HostPlayerId, mode: "session");
        if (!IsEnabled)
        {
            return;
        }

        if (IsForegroundRewardResolutionActive(session.HostPlayerId))
        {
            return;
        }

        AiTeammateDummyController? controller = GetOrCreateHostController(session);
        controller?.Tick();
    }

    public static bool IsAutoControlled(Player? player)
    {
        if (!IsEnabled || player == null)
        {
            return false;
        }

        if (AiTeammateSessionRegistry.Current is { } session)
        {
            return session.HostPlayerId == player.NetId;
        }

        return TryResolveLocalPlayerId() == player.NetId;
    }

    public static bool TryGetController(ulong playerId, out AiTeammateDummyController controller)
    {
        controller = null!;
        if (!IsEnabled)
        {
            return false;
        }

        AiTeammateDummyController? hostController;
        if (AiTeammateSessionRegistry.Current is { } session)
        {
            if (session.HostPlayerId != playerId)
            {
                return false;
            }

            hostController = GetOrCreateHostController(session);
        }
        else
        {
            if (TryResolveLocalPlayerId() != playerId)
            {
                return false;
            }

            hostController = GetOrCreateLocalPlayerController();
        }

        if (hostController == null)
        {
            return false;
        }

        controller = hostController;
        return true;
    }

    public static void Reset()
    {
        if (IsEnabled)
        {
            Log.Info("[AITeammate][AutoMode] Disabled host auto-mode because the AI teammate session ended.");
        }

        IsEnabled = false;
        _hostController = null;
        _hostControllerPlayerId = null;
        _wasTogglePressed = false;
        _nextToggleAllowedAtUtc = DateTime.MinValue;
        lock (ForegroundRewardLock)
        {
            ForegroundRewardPlayers.Clear();
        }
    }

    public static IDisposable BeginForegroundRewardResolution(Player player)
    {
        ulong playerId = player.NetId;
        bool added;
        lock (ForegroundRewardLock)
        {
            added = ForegroundRewardPlayers.Add(playerId);
        }

        if (added)
        {
            Log.Info($"[AITeammate][AutoMode] Pausing host controller during foreground reward resolution player={playerId}");
        }

        return new ForegroundRewardScope(playerId, added);
    }

    public static bool IsForegroundRewardResolutionActive(ulong playerId)
    {
        lock (ForegroundRewardLock)
        {
            return ForegroundRewardPlayers.Contains(playerId);
        }
    }

    private static void HandleToggleInput(ulong playerId, string mode)
    {
        bool isPressed = Input.IsKeyPressed(Key.F4);
        if (!isPressed)
        {
            _wasTogglePressed = false;
            return;
        }

        if (_wasTogglePressed)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _nextToggleAllowedAtUtc)
        {
            return;
        }

        _wasTogglePressed = true;
        _nextToggleAllowedAtUtc = now + ToggleCooldown;
        IsEnabled = !IsEnabled;
        if (!IsEnabled)
        {
            _hostController = null;
            _hostControllerPlayerId = null;
        }

        string state = IsEnabled ? "enabled" : "disabled";
        Log.Info($"[AITeammate][AutoMode] Host auto-mode {state}. hotkey=F4 player={playerId} mode={mode}");
    }

    private static AiTeammateDummyController? GetOrCreateHostController(AiTeammateSessionState session)
    {
        if (_hostController != null)
        {
            return _hostController;
        }

        if (!AiTeammateSessionRegistry.TryGetParticipant(session.HostPlayerId, out AiTeammateSessionParticipant hostParticipant))
        {
            Log.Warn($"[AITeammate][AutoMode] Could not create host auto-mode controller. host={session.HostPlayerId}");
            return null;
        }

        _hostController = new AiTeammateDummyController(
            slotIndex: hostParticipant.SlotIndex,
            playerId: session.HostPlayerId,
            character: hostParticipant.Character);
        _hostControllerPlayerId = session.HostPlayerId;
        Log.Info($"[AITeammate][AutoMode] Created host auto-mode controller. host={session.HostPlayerId}");
        return _hostController;
    }

    private static AiTeammateDummyController? GetOrCreateLocalPlayerController()
    {
        ulong? playerId = TryResolveLocalPlayerId();
        if (!playerId.HasValue || playerId.Value == 0UL)
        {
            Log.Warn("[AITeammate][AutoMode] Could not create local auto-mode controller because LocalContext.NetId was missing.");
            return null;
        }

        if (_hostController != null && _hostControllerPlayerId == playerId.Value)
        {
            return _hostController;
        }

        Player? player = RunManager.Instance.DebugOnlyGetState()?.GetPlayer(playerId.Value);
        CharacterModel character = TryResolvePlayerCharacter(player) ??
                                   AiTeammatePlaceholderCharacters.All[0].ResolveModel();
        _hostController = new AiTeammateDummyController(
            slotIndex: 0,
            playerId: playerId.Value,
            character: character);
        _hostControllerPlayerId = playerId.Value;
        Log.Info($"[AITeammate][AutoMode] Created local auto-mode controller. player={playerId.Value} character={character.Id.Entry}");
        return _hostController;
    }

    private static ulong? TryResolveLocalPlayerId()
    {
        return LocalContext.NetId;
    }

    private static CharacterModel? TryResolvePlayerCharacter(Player? player)
    {
        if (player == null)
        {
            return null;
        }

        return AiTeammateRuntimeCharacterResolver.TryResolveCharacterModel(player);
    }

    private sealed class ForegroundRewardScope(ulong playerId, bool ownsScope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!ownsScope)
            {
                return;
            }

            lock (ForegroundRewardLock)
            {
                ForegroundRewardPlayers.Remove(playerId);
            }

            Log.Info($"[AITeammate][AutoMode] Resuming host controller after foreground reward resolution player={playerId}");
        }
    }
}
