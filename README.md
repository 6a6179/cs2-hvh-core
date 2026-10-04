# HvHCore

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 HvH servers.

- **Shot validator / doubletap gate.** Checks every gun shot against the server clock and blocks doubletap, shots during reload, and instant shots after a weapon switch. On slow-firing weapons the second shot of a doubletap never fires at all.
- **`!ns` / `!dt`** let admins toggle nospread and doubletap live, announced to everyone in chat.
- **`!comp` / `!wm`** switch between competitive and wingman, with an optional map.
- **`!status`** shows the toggles, the match state and server performance.
- **Forced cvars.** `sv_airaccelerate 32` and `sv_quantize_movement_input 0` by default, re-applied every round so gamemode or MatchZy configs can't reset them.
- **MatchZy knife round.** Doubletap is allowed during the knife round and turned off again afterwards.
- **Restart warning.** When the server is told to quit with players on, it spams a countdown in chat and center screen first.
- **Team lock.** Once a MatchZy match starts, nobody new can join T/CT, and players can't swap sides, until an admin lets them in.

Requires CounterStrikeSharp **v1.0.376+** (.NET 10). Tested on CS2 1.41.8.x.

## Install

1. Download the latest `HvHCore-x.y.z.zip` from [Releases](../../releases).
2. Extract it into `game/csgo/` so the dll ends up at
   `addons/counterstrikesharp/plugins/HvHCore/HvHCore.dll`.
3. Restart the server (or `css_plugins load HvHCore`).
   The config is generated at `addons/counterstrikesharp/configs/plugins/HvHCore/HvHCore.json`.

Admins are regular CSS admins (`configs/admins.json`). Anyone with `@css/generic` or `@css/root` can use the admin commands. Change this with `AdminFlag`.

## Commands

| Chat | Console / RCON | Who | What |
|---|---|---|---|
| `!ns [on\|off]` | `hvh_ns [on\|off]` | admin | Toggle nospread (`sv_usercmd_custom_random_seed 0` = nospread works) |
| `!dt [on\|off]` | `hvh_dt [on\|off]` | admin | Toggle doubletap (off = shot validator + hard gate active) |
| `!comp [map]` | `hvh_comp [map]` | admin | Switch to competitive, optionally on another map |
| `!wm [map]` | `hvh_wm [map]` | admin | Switch to wingman, optionally on another map |
| `!status` | `hvh_status` | anyone | Toggles, mode/map/score, players, tick rate, frame time, CPU, RAM, uptime |
| `!lock` / `!unlock` | `hvh_lock on\|off` | admin | Lock / unlock teams by hand (MatchZy does it automatically, see below) |
| `!allow <player> [t\|ct]` | `hvh_allow <player\|#userid> [t\|ct]` | admin | Let someone onto a team while locked: no team = they pick, `t`/`ct` = move them now |
| | `hvh_lockinfo` | admin | Team lock state and who is rostered on which side |
| | `hvh_knife on\|off` | admin / cfg | Knife-round doubletap hook (see MatchZy below) |
| | `hvh_shots` | admin | Voided shot counts per player and back-to-back timing stats |
| | `hvh_wpn` | server | Debug dump of every player's weapon timing state |

Chat commands also work with `/` and `.` prefixes. Map names are fuzzy: `mirage`, `de_mirage`, or any unique part of the name.
Every toggle is announced to the whole server, e.g. `[hvh] doubletap OFF`.

## How the shot validator works

It's active whenever doubletap is **off**. Every `weapon_fire` from a gun is checked against `Server.TickCount`, the server's own clock, which a shifted tickbase can't fake:

| Check | Invalid when |
|---|---|
| **rate** | Fewer ticks since that weapon's last *valid* shot than its cycle time (alt-fire and burst aware; revolver exempt) |
| **reload** | Fired mid-reload, inside the engine's no-attack window after the reload started (shell-by-shell shotguns exempt) |
| **deploy** | Fired before the weapon finished deploying (the switch-and-shoot trick) |

An invalid shot is **voided**: its bullet damage is cancelled, the round goes back into the magazine, and the weapon's next attack is pushed back by however early the shot was.

**Hard gate.** After every valid shot from a weapon with cycle time ≥ `HardGateMinCycle` (snipers, deagle, shotguns, burst), the weapon's `m_nNextPrimaryAttackTick` is held locked on the server and re-asserted every tick until the cycle has elapsed on the server clock. The engine refuses the doubletap second shot outright. To avoid eating legit shots, the lock comes off a few ticks early based on the player's ping (`HardGateMaxLeadTicks`). Full-auto sprays aren't gated.

Plugins can't drop or rewrite user commands before the engine runs them, so this can't reject the packet itself. The hard gate stops the shot from happening, and the validator cleans up anything that slips through.

## Team lock

When teams lock, everyone on T/CT goes on the roster as part of their squad.

- **Not on the roster:** can't join T/CT. The team menu is blocked and auto-assign gets moved back to spectator. They're told to ask an admin.
- **On the roster:** can reconnect or come back from spectator, but only onto the side their squad is on *now*. This follows halftime and the knife-round side pick, not the side they started on.
- **Spectator** is always allowed.
- **Overrides:** `!allow <player>` lets them pick a team (they join that side's squad), and `!allow <player> ct` moves them directly. SimpleAdmin's `!team` by an admin also works. `!unlock` drops the lock entirely.
- A map change clears the lock. The next map's knife round or live start locks again.

Turn it off with `"TeamLock": false`.

## MatchZy hooks

Knife-round doubletap and the team lock are driven from MatchZy's configs. Append these lines (also in [`examples/matchzy`](examples/matchzy)):

```
// cfg/MatchZy/knife.cfg
hvh_knife on
hvh_lock on

// cfg/MatchZy/live_override.cfg and live_wingman_override.cfg
hvh_knife off
hvh_lock on

// cfg/MatchZy/warmup.cfg
hvh_knife off
hvh_lock warmup
```

- `hvh_knife off` only undoes what `hvh_knife on` did, so an admin's manual `!dt on` stays on.
- `hvh_lock warmup` unlocks when the match is reset or over. It stays locked through the side-pick warmup MatchZy runs right after the knife round.
- MatchZy updates overwrite its configs, so re-add the lines after updating.

## Config

`addons/counterstrikesharp/configs/plugins/HvHCore/HvHCore.json`

| Key | Default | |
|---|---|---|
| `NospreadDefault` / `DoubletapDefault` | `false` | State on every server start |
| `AdminFlag` | `@css/generic` | CSS permission for admin commands |
| `ForcedCvars` | airaccel 32, quantize 0 | Re-applied on map start and every round start |
| `RestrictedWeaponsCsv` | `all` | Weapons the validator/gate apply to (`all` or `weapon_ssg08,weapon_awp,...`) |
| `ValidateShots` | `true` | Master switch for the validator |
| `CheckRate` / `CheckReload` / `CheckDeploy` | `true` | Individual checks |
| `RateToleranceTicks` | `0` | Slack on the rate check |
| `DeployToleranceTicks` | `2` | Slack on the deploy check |
| `RefundAmmo` | `true` | Give the round back on a voided shot |
| `RegateWeapon` | `true` | Push the next attack back on a voided shot |
| `LogInvalidShots` | `true` | Log voided shots (max 1/s per player) |
| `HardGate` | `true` | Lock slow weapons between shots |
| `HardGateMinCycle` | `0.2` | Seconds; weapons with a shorter cycle aren't gated |
| `HardGateMaxLeadTicks` | `4` | Max ticks the gate opens early for high-ping players |
| `ModeEnvFile` | `""` | Optional `KEY=VALUE` file; `!comp`/`!wm` write `MAP`, `GAME_TYPE`, `GAME_MODE` into it so a launcher script can boot into the same mode |
| `TeamLock` | `true` | Allow the team lock (`hvh_lock on` / `!lock`) |
| `RestartWarning` | `true` | Countdown before a server-side `quit` while players are on |
| `RestartWarningSeconds` | `5` | Countdown length |

## Building

```
dotnet build src/HvHCore/HvHCore.csproj -c Release -o out
```

Pushing a `v*` tag builds the release zip through GitHub Actions.

## Credits

- Rapid-fire detection is based on [HvH-gg/RapidFireFix](https://github.com/HvH-gg/RapidFireFix) (MIT, © 2024 HvH.gg).
- Built on [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp).
