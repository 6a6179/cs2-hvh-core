using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace HvHCore;

// Team lock: when a match goes live (MatchZy knife.cfg / live overrides run `hvh_lock on`), every human on T/CT
// is put on the roster with a squad label. After that:
//   - unrostered players can't join T/CT (jointeam is blocked; auto-assign / anything that slips through is
//     moved back to spectator),
//   - rostered players can only (re)join the side their squad is currently on (sides swap at halftime and
//     after the knife round, so this follows the squad, not the original side),
//   - spectator is always allowed.
// Admin overrides: !allow <player> [t|ct] (free pick or direct move), SimpleAdmin !team, !unlock.
// warmup.cfg runs `hvh_lock warmup`: unlock (match reset / ended) EXCEPT for the warmup MatchZy starts right
// after the knife round (side pick), which runs in the same frame as the knife round's round_end.
// A map change clears the lock.
public partial class HvHCorePlugin
{
    private const int TeamT = 2, TeamCT = 3, TeamSpec = 1;
    private bool _locked;
    private readonly Dictionary<ulong, int> _squad = new();       // steamid -> squad label (2/3), 0 = free pick
    private readonly Dictionary<ulong, int> _lastSide = new();    // steamid -> last T/CT side while locked
    private readonly Dictionary<ulong, float> _lockMsgAt = new(); // rate limit for the "teams are locked" msg
    private float _adminMoveUntil;                                // SimpleAdmin !team by an admin moves freely
    private long _roundEndMs;                                     // Environment.TickCount64 at the last round_end

    private static bool IsSide(int t) => t is TeamT or TeamCT;
    private static int Other(int side) => side == TeamT ? TeamCT : TeamT;
    private static string SideName(int side) => side == TeamT ? "T" : side == TeamCT ? "CT" : "spec";

    private static List<CCSPlayerController> RealPlayers() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();

    private void RegisterTeamLock()
    {
        AddCommandListener("jointeam", (pl, i) => OnJoinTeam(pl, i), HookMode.Pre);
        AddCommandListener("css_team", (pl, i) =>
        {
            if (_locked && IsAdmin(pl)) _adminMoveUntil = Server.CurrentTime + 1f;
            return HookResult.Continue;
        }, HookMode.Pre);
        RegisterEventHandler<EventPlayerTeam>(OnPlayerTeam);
        RegisterEventHandler<EventRoundEnd>((_, _) => { _roundEndMs = Environment.TickCount64; return HookResult.Continue; }, HookMode.Pre);
    }

    private void ClearLock()
    {
        _locked = false;
        _squad.Clear();
        _lastSide.Clear();
        _lockMsgAt.Clear();
    }

    private void SetLock(bool on, CCSPlayerController? by)
    {
        if (on)
        {
            if (!Config.TeamLock) return;
            var added = 0;
            foreach (var p in RealPlayers())
            {
                if (!IsSide(p.TeamNum) || _squad.ContainsKey(p.SteamID)) continue;
                _squad[p.SteamID] = p.TeamNum;
                _lastSide[p.SteamID] = p.TeamNum;
                added++;
            }
            if (_locked)
            {
                if (added > 0) Logger.LogInformation("[hvh] team lock: added {N} player(s) to the roster", added);
                return;   // knife -> live: already locked, nothing to announce
            }
            _locked = true;
            AnnounceText($"teams {ChatColors.Red}LOCKED{ChatColors.Default}, match started", "teams locked");
        }
        else
        {
            if (!_locked) return;
            ClearLock();
            AnnounceText($"teams {ChatColors.Green}unlocked", "teams unlocked");
        }
        Server.PrintToConsole($"[hvh] team lock {(on ? "on" : "off")} (by {by?.PlayerName ?? "console"}), roster {_squad.Count}");
    }

    // side a rostered player may be on right now: where most of their squad-mates are; if none are on a side,
    // opposite the other squad; else the last side they played. -1 = not rostered, 0 = free pick.
    private int SideFor(CCSPlayerController p)
    {
        if (!_squad.TryGetValue(p.SteamID, out var label)) return -1;
        if (label == 0) return 0;
        var onSide = RealPlayers().Where(x => x.SteamID != p.SteamID && IsSide(x.TeamNum) && _squad.ContainsKey(x.SteamID)).ToList();
        var mates = onSide.Where(x => _squad[x.SteamID] == label).GroupBy(x => (int)x.TeamNum)
                          .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (mates != null) return mates.Key;
        var others = onSide.Where(x => _squad[x.SteamID] != label && _squad[x.SteamID] != 0).GroupBy(x => (int)x.TeamNum)
                           .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (others != null) return Other(others.Key);
        return _lastSide.TryGetValue(p.SteamID, out var last) ? last : label;
    }

    // squad label of whoever is on a side now (majority), used to relabel free picks / admin moves
    private int LabelOnSide(int side, ulong except)
    {
        var g = RealPlayers().Where(x => x.SteamID != except && x.TeamNum == side && _squad.TryGetValue(x.SteamID, out var l) && l != 0)
                             .GroupBy(x => _squad[x.SteamID]).OrderByDescending(g => g.Count()).FirstOrDefault();
        return g?.Key ?? side;
    }

    private void LockMsg(CCSPlayerController p, string text)
    {
        var now = Server.CurrentTime;
        if (_lockMsgAt.TryGetValue(p.SteamID, out var t) && now - t < 2f) return;
        _lockMsgAt[p.SteamID] = now;
        p.PrintToChat($"{Prefix} {text}");
    }

    private HookResult OnJoinTeam(CCSPlayerController? p, CommandInfo info)
    {
        if (!_locked || p is not { IsValid: true } || p.IsBot) return HookResult.Continue;
        if (!int.TryParse(info.GetArg(1), out var want)) return HookResult.Continue;
        if (want == TeamSpec || want == p.TeamNum) return HookResult.Continue;
        var side = SideFor(p);
        if (side == 0) return HookResult.Continue;   // admin let them pick
        if (side < 0)
        {
            LockMsg(p, $"teams are {ChatColors.Red}locked{ChatColors.Default}, match in progress. ask an admin to {ChatColors.Yellow}!allow{ChatColors.Default} you");
            return HookResult.Stop;
        }
        if (want == side) return HookResult.Continue;
        if (want == 0)    // auto-select: put them on their squad's side
        {
            if (p.TeamNum != side) Server.NextFrame(() => { if (p.IsValid) p.ChangeTeam((CsTeam)side); });
            return HookResult.Stop;
        }
        LockMsg(p, $"teams are {ChatColors.Red}locked{ChatColors.Default}, you're {ChatColors.Yellow}{SideName(side)}{ChatColors.Default} this match");
        return HookResult.Stop;
    }

    private HookResult OnPlayerTeam(EventPlayerTeam ev, GameEventInfo _)
    {
        var p = ev.Userid;
        if (!_locked || ev.Disconnect || p is not { IsValid: true } || p.IsBot || p.IsHLTV) return HookResult.Continue;
        var team = ev.Team;
        if (!IsSide(team)) return HookResult.Continue;
        var id = p.SteamID;
        var adminMove = Server.CurrentTime <= _adminMoveUntil;
        if (_squad.TryGetValue(id, out var label))
        {
            // free pick landed, or an admin moved them across: they join the squad on that side.
            // (engine side swaps at halftime keep the label; SideFor follows the squad.)
            if (label == 0 || adminMove) _squad[id] = LabelOnSide(team, id);
            _lastSide[id] = team;
            return HookResult.Continue;
        }
        if (adminMove)
        {
            _squad[id] = LabelOnSide(team, id);
            _lastSide[id] = team;
            Logger.LogInformation("[hvh] team lock: admin moved {P} to {S}, added to roster", p.PlayerName, SideName(team));
            return HookResult.Continue;
        }
        // auto-assign or anything else that got past jointeam
        Server.NextFrame(() =>
        {
            if (p.IsValid && _locked && !_squad.ContainsKey(id) && IsSide(p.TeamNum)) p.ChangeTeam(CsTeam.Spectator);
        });
        LockMsg(p, $"teams are {ChatColors.Red}locked{ChatColors.Default}, match in progress. ask an admin to {ChatColors.Yellow}!allow{ChatColors.Default} you");
        return HookResult.Continue;
    }

    private static CCSPlayerController? FindPlayer(string q, out string? error)
    {
        error = null;
        var all = RealPlayers();
        if (q.StartsWith('#') && int.TryParse(q[1..], out var uid))
        {
            var byId = all.FirstOrDefault(x => x.UserId == uid);
            if (byId == null) error = $"no player with userid {uid}";
            return byId;
        }
        var exact = all.Where(x => x.PlayerName.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var part = all.Where(x => x.PlayerName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (part.Count == 1) return part[0];
        error = part.Count == 0 ? $"no player matching '{q}'" : $"'{q}' matches {part.Count} players, be more specific";
        return null;
    }

    // !allow [player] [t|ct]: let a player onto a team while locked (no team = they pick), or move them now
    private void Allow(string? args, CCSPlayerController? by, Action<string> reply)
    {
        if (!_locked) { reply("teams aren't locked"); return; }
        var parts = (args ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var side = 0;
        if (parts.Count > 0)
        {
            side = parts[^1].ToLowerInvariant() switch { "t" => TeamT, "ct" => TeamCT, _ => 0 };
            if (side != 0) parts.RemoveAt(parts.Count - 1);
        }
        var name = string.Join(' ', parts);
        CCSPlayerController? target;
        if (name.Length == 0)
        {
            if (by == null) { reply("usage: hvh_allow <player|#userid> [t|ct]"); return; }
            target = by;
        }
        else
        {
            target = FindPlayer(name, out var err);
            if (target == null) { reply(err!); return; }
        }
        _squad[target.SteamID] = 0;
        _lockMsgAt.Remove(target.SteamID);
        if (side != 0)
        {
            var t = target;
            Server.NextFrame(() => { if (t.IsValid) t.ChangeTeam((CsTeam)side); });
            reply($"moved {target.PlayerName} to {SideName(side)}");
        }
        else
        {
            reply($"{target.PlayerName} can pick a team");
            if (target != by) target.PrintToChat($"{Prefix} an admin let you in, {ChatColors.Yellow}pick a team");
        }
        Logger.LogInformation("[hvh] team lock: {A} allowed {P} ({S})", by?.PlayerName ?? "console", target.PlayerName, side == 0 ? "free pick" : SideName(side));
    }

    [ConsoleCommand("hvh_lock", "Team lock (MatchZy hook: knife/live cfgs run 'on', warmup.cfg runs 'warmup')")]
    [CommandHelper(minArgs: 1, usage: "on|off|warmup", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdLock(CCSPlayerController? p, CommandInfo info)
    {
        if (!IsAdmin(p)) return;
        if (info.GetArg(1).Equals("warmup", StringComparison.OrdinalIgnoreCase))
        {
            // side-pick warmup after the knife round: the cfg exec lands right after that round's round_end
            if (_locked && Environment.TickCount64 - _roundEndMs < 2000)
            {
                Logger.LogInformation("[hvh] team lock: knife round over, staying locked for side pick");
                return;
            }
            SetLock(false, p);
            return;
        }
        if (ParseArg(info.GetArg(1)) is bool on) SetLock(on, p);
    }

    [ConsoleCommand("hvh_allow", "Let a player join a team while teams are locked")]
    [CommandHelper(minArgs: 1, usage: "<player|#userid> [t|ct]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdAllow(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p)) Allow(info.ArgString, p, s => info.ReplyToCommand($"[hvh] {s}")); }

    [ConsoleCommand("hvh_lockinfo", "Show the team lock roster")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdLockInfo(CCSPlayerController? p, CommandInfo info)
    {
        if (!IsAdmin(p)) return;
        info.ReplyToCommand($"[hvh] team lock {(_locked ? "ON" : "off")}, roster {_squad.Count}");
        foreach (var pl in RealPlayers())
        {
            var s = _squad.TryGetValue(pl.SteamID, out var l) ? (l == 0 ? "free pick" : $"squad {SideName(l)} -> {SideName(SideFor(pl))}") : "not rostered";
            info.ReplyToCommand($"  {pl.PlayerName}: on {SideName(pl.TeamNum)}, {s}");
        }
    }
}
