# Debugging Notes

## Build

- Local VS Code build task uses `-p:Sts2Dir=D:\SteamLibrary\steamapps\common\Slay the Spire 2`.
- A plain `dotnet build` uses the default `Sts2Dir` from `sts2AITeammate.csproj`; verify that path exists before relying on it.
- The post-build target copies the DLL, mod JSON, and `config/` files into `$(Sts2Dir)\mods\sts2AITeammate\`.

## Combat Targeting Check

- Start an AI teammate run with at least one AI teammate.
- Enter a combat with multiple hittable enemies.
- Confirm logs show multiple legal `play_card_*_target_*` actions for single-target attacks when more than one target is valid.
- Expected result: the combat scorer can choose between enemy targets instead of always using the first ordered target.

## Combat Pile Selection Check

- Test an AI teammate with `NeowsFury` and at least one card in the discard pile.
- Expected result: after the attack resolves, the AI automatically selects cards from the combat pile prompt instead of stalling on the selection screen.
- Relevant patch: `CardSelectCmd.FromCombatPile` in `AiTeammateCardSelectionPatches`.
