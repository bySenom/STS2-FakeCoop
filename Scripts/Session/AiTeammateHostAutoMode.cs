using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Logging;

namespace AITeammate.Scripts;

internal static class AiTeammateHostAutoMode
{
    private static AiTeammateDummyController? _hostController;
    private static bool _wasTogglePressed;

    public static bool IsEnabled { get; private set; }

    public static void Tick(AiTeammateSessionState session)
    {
        HandleToggleInput(session);
        if (!IsEnabled)
        {
            return;
        }

        AiTeammateDummyController? controller = GetOrCreateHostController(session);
        controller?.Tick();
    }

    public static bool IsAutoControlled(Player? player)
    {
        return IsEnabled &&
               player != null &&
               AiTeammateSessionRegistry.Current?.HostPlayerId == player.NetId;
    }

    public static bool TryGetController(ulong playerId, out AiTeammateDummyController controller)
    {
        controller = null!;
        if (!IsEnabled ||
            AiTeammateSessionRegistry.Current is not { } session ||
            session.HostPlayerId != playerId)
        {
            return false;
        }

        AiTeammateDummyController? hostController = GetOrCreateHostController(session);
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
        _wasTogglePressed = false;
    }

    private static void HandleToggleInput(AiTeammateSessionState session)
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
        }

        string state = IsEnabled ? "enabled" : "disabled";
        Log.Info($"[AITeammate][AutoMode] Host auto-mode {state}. hotkey=F4 host={session.HostPlayerId}");
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
        Log.Info($"[AITeammate][AutoMode] Created host auto-mode controller. host={session.HostPlayerId}");
        return _hostController;
    }
}

