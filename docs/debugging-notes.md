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

## Build-Aware Card Reward Check

- Start an AI teammate run with any supported character.
- Enter a combat and finish it to trigger card rewards, or visit a shop with card offers.
- Check the log lines beginning with `[AITeammate] Card evaluation rank`.
- Expected result: S-tier core cards get strong early bonuses, but A/B-tier build cards can still be selected before any build is locked.
- Cards that are core/support to the active profile can show reasons like `build +...`, and weak off-build cards can be skipped with `skipReason=off_build`.
- Current card scope: card rewards and shop card offers.

## Relic Choice Check

- Trigger an AI relic choice screen.
- Check the log lines beginning with `[AITeammate] Relic evaluation rank`.
- Expected result: build-profile key relics get priority when the deck has profile evidence.
- Shop relic offers also receive build-profile key/good/avoid relic adjustments.

## Rest Site Upgrade Check

- Reach a rest site with an AI teammate.
- Expected result is an upgrade/smith option when available unless HP is truly low or at least 24 HP is missing.
- Small missing HP should not cause a heal.
- Check logs for `[AITeammate][RestSite] Options`, `[AITeammate][RestSite] Selected option`, and `[AITeammate] Upgrade evaluation rank`.
- Upgrade targets should favor build-relevant cards and penalize off-build cards.

## Build-Aware Removal Check

- Visit a shop with card removal available.
- Check shop/removal logs for removal target reasons.
- Expected result: active build core/support cards should show keep bias, while off-build or avoid cards become stronger removal targets.

## Build-Aware Combat Check

- Enter combat with a deck that has active build evidence.
- Check debug semantic score logs for `build=...`.
- Expected result: active build core/support cards get small combat bonuses, but survival and lethal checks still dominate.

## Host Auto-Mode Check

- Start an AI teammate run.
- Press `F4` during the run.
- Expected result: logs show `[AITeammate][AutoMode] Host auto-mode enabled`.
- While enabled, the host player uses the same deterministic AI choices for combat, rewards, card selections, relic selections, shops, events, and rest sites.
- Press `F4` again to disable host auto-mode.

## Build-Aware Combat Rotation Check

- Enter combat with an active build profile and multiple playable cards.
- Expected result: setup/cycle cards are preferred before payoff cards when survival and lethal do not override the line.
- Good checks: Strength before multi-hit attacks, draw/energy before Claw/Sly/Grand Finale payoffs, Necrobinder engine cards before finishers.
