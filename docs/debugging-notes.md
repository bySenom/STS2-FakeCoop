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
- While enabled, the host player uses the same deterministic AI choices for combat.
- Room-end rewards should auto-resolve for the host while auto-mode is enabled.
- Gold should be collected automatically.
- Card rewards should log `[AITeammate] Card evaluation` and either pick the best build-fitting card or skip weak/off-build offers.
- Modded reward pools with more than three cards should be evaluated as one candidate list.
- AI teammates should still resolve their own rewards deterministically after the host reward flow completes.
- Press `F4` again to disable host auto-mode.
- If the reward window does not open while auto-mode is enabled, confirm logs show `Deterministically resolving reward offer`, `GoldReward`, and either `Deterministic card reward picked` or `Deterministic card reward skipped`.
- The action queue wait should not spam `InvalidOperationException` when the front queued action is still `Executing`.

## Build-Aware Combat Rotation Check

- Enter combat with an active build profile and multiple playable cards.
- Expected result: setup/cycle cards are preferred before payoff cards when survival and lethal do not override the line.
- Good checks: Strength before multi-hit attacks, draw/energy before Claw/Sly/Grand Finale payoffs, Necrobinder engine cards before finishers.

## X-Cost Zero-Energy Check

- Enter combat with a playable X-cost card.
- Spend all energy before the bot evaluates the card, or test a hand where only 0 energy remains.
- Expected result: the bot should not play the X-cost card at 0 energy.
- Check logs for `Skipped combat action for X-cost card` or `blocked zero-energy X-cost`.

## Regent and Defect Engine Rotation Check

- Defect: test `lightning`, `frost`, or `dark_orb` evidence with cards like `Zap`, `Ball Lightning`, `Glacier`, `Coolheaded`, `Darkness`, `Capacitor`, or `Storm`.
- Expected result: the line planner prefers filling/building orb setup before `Dualcast`, `Multi-Cast`, `Recursion`, or other orb payoff when no emergency overrides it.
- Regent: test star builds with `Guiding Star`, `Falling Star`, `Stardust`, `Seven Stars`, `Glow`, `Convergence`, or `Venerate`.
- Expected result: star setup is played before payoff cards such as `Gamma Blast`, `Photon Cut`, `Meteor Shower`, `Big Bang`, or `Bombardment` when affordable.

## Block Discipline Check

- Enter combat where enemies deal low incoming damage, such as 5.
- Give the bot at least two pure block cards and one useful damage/setup card.
- Expected result: after incoming damage is covered, the bot should not spend another action on pure block unless it has block retention or a Body Slam/Barricade-style plan.
- Check logs for `blocked redundant block-only` when a redundant block action is scored.
- If only redundant block/status actions remain, expected result is a delayed end turn commit instead of repeatedly canceling end turn.
- Add `Burn`, `Disintegration`, or similar `take X damage` status cards to hand and confirm the bot blocks that extra damage.
- Add `Beckon` or similar `lose X HP` status cards and confirm it is counted as life risk but not treated as blockable damage.

## Core Build Power Priority Check

- Enter combat with an active build profile and 3 energy.
- Good checks: Ironclad `Demon Form` in Strength, Defect `Echo Form` in Lightning/Creative AI, Ironclad `Barricade` in Body Slam/Barricade, or Regent `Void Form`.
- Expected result: the bot plays the affordable core power early, usually first, when there is no lethal/survival emergency.
- If the bot skips the power, compare enemy incoming damage, lethal opportunity, and combat line scores before changing weights.

## Combat Potion Discipline Check

- Enter a safe normal combat while holding a potion.
- Expected result: the bot should not spend the potion just because it is legal; end turn should beat low-value potion actions.
- Enter an elite/boss or dangerous turn with meaningful incoming damage.
- Expected result: if a potion is selected, it should appear early in the combat line, before attacks/payoff cards that benefit from it.
- Check logs for `Combat score ... category=Potion` and `Combat line ... actions=[use_potion_...`.

## Coop Target Overkill Check

- Enter a multi-enemy combat with at least two AI-controlled players able to attack.
- Put one enemy at low HP and leave another target alive.
- Expected result: after the first bot queues lethal damage on the low-HP enemy, later bots should avoid overkilling that same enemy when they have another useful target.
- Check logs for `reserved team damage` and compare later `Combat score`/`Combat line` target choices.

## Enemy-Aware Targeting and AoE Check

- Enter a multi-enemy combat with at least two living enemies.
- Give the bot an all-enemy damage card such as `Sweeping Beam` or another AoE card.
- Expected result: AoE card scores should rise when multiple enemies are alive and should count useful damage/kills across enemies.
- Give one enemy high incoming damage or a recognizable scaling/status/summon-style intent.
- Expected result: single-target attacks should prefer that high-threat enemy unless another target is clearly lethal or already reserved by teammate damage.
