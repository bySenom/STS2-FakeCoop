using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace AITeammate.Scripts;

internal static class AiTeammateHostAutoMode
{
    private static AiTeammateDummyController? _hostController;
    private static ulong? _hostControllerPlayerId;
    private static bool _wasTogglePressed;

    public static bool IsEnabled { get; private set; }

    public static void Tick()
    {
        AiTeammateSessionState? session = AiTeammateSessionRegistry.Current;
        if (session != null)
        {
            Tick(session);
            return;
        }

        HandleToggleInput(TryResolveLocalPlayerId() ?? 0UL, mode: "local");
        if (!IsEnabled)
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

        _wasTogglePressed = true;
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
}
