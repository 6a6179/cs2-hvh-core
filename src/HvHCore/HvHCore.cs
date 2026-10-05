using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace HvHCore;

public partial class HvHCorePlugin : BasePlugin, IPluginConfig<HvHCoreConfig>
{
    public override string ModuleName => "HvHCore";
    public override string ModuleVersion => "1.2.0";
    public override string ModuleAuthor => "6a6179";
    public override string ModuleDescription => "HvH server toolkit: shot validator / doubletap gate, nospread toggle, mode switch, status";

    public HvHCoreConfig Config { get; set; } = new();

    private bool _ns;   // nospread allowed  -> sv_usercmd_custom_random_seed 0
    private bool _dt;   // doubletap allowed -> rapid fire not blocked
    private bool _knife; // dt was forced on for a MatchZy knife round (hvh_knife on/off from MatchZy cfgs)


    public void OnConfigParsed(HvHCoreConfig config)
    {
        Config = config;
        _ns = config.NospreadDefault;
        _dt = config.DoubletapDefault;
    }

    public override void Load(bool hotReload)
    {
        // one lambda per registration: CSS keys CommandListeners by delegate, so reusing a method group
        // overwrites the first entry and that listener is never removed on unload (leaks across hot reloads)
        AddCommandListener("say", (pl, i) => OnSay(pl, i), HookMode.Pre);
        AddCommandListener("say_team", (pl, i) => OnSay(pl, i), HookMode.Pre);
        AddCommandListener("quit", (pl, i) => OnQuit(pl, i), HookMode.Pre);
        AddCommandListener("exit", (pl, i) => OnQuit(pl, i), HookMode.Pre);
        RegisterTeamLock();
        RegisterWarmupMoney();

        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _lastValidShot.Clear();
            _voidShooters.Clear();
            _reloadStart.Clear();
            _regated.Clear();
            _gates.Clear();
            // map changed mid-knife round: drop the forced dt (MatchZy re-execs warmup.cfg anyway)
            if (_knife) { _knife = false; _dt = false; }
            ClearLock();
            // gamemode cfgs run after map start; apply after them too
            AddTimer(1f, Apply, TimerFlags.STOP_ON_MAPCHANGE);
            AddTimer(5f, Apply, TimerFlags.STOP_ON_MAPCHANGE);
        });

        RegisterEventHandler<EventRoundStart>((_, _) => { Apply(); return HookResult.Continue; });

        RegisterListener<Listeners.OnClientDisconnect>(slot =>
        {
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p == null || !p.IsValid) return;
            _voidShooters.Remove(p.Pawn.Index);
        });

        RegisterEventHandler<EventPlayerConnectFull>((ev, _) =>
        {
            if (ev.Userid is { IsValid: true } p) _voidShooters.Remove(p.Pawn.Index);
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnTick>(OnTickSample);
        RegisterListener<Listeners.OnTick>(OnTickGates);
        _sampleSw.Start();
        AddTimer(1f, Sample, TimerFlags.REPEAT);

        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventWeaponReload>((ev, _) =>
        {
            var a = ev.Userid?.Pawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (a != null && a.IsValid) _reloadStart[a.Index] = Server.TickCount;
            return HookResult.Continue;
        });
        RegisterListener<Listeners.OnEntityTakeDamagePre>(OnTakeDamage);

        Apply();
    }

    public override void Unload(bool hotReload)
    {
        foreach (var g in _gates.Values) ReleaseGate(g, Server.TickCount);   // never leave a weapon locked
        _gates.Clear();
        _lastValidShot.Clear();
        _voidShooters.Clear();
    }

    // ---------------- cvars ----------------
    private static void Set(string name, string val)
    {
        var cv = ConVar.Find(name);
        // strip NOTIFY so the engine never prints "Server cvar 'x' changed" to chat
        if (cv != null) cv.Flags &= ~(ConVarFlags.FCVAR_CHEAT | ConVarFlags.FCVAR_NOTIFY);
        Server.ExecuteCommand($"{name} {val}");
    }

    private void Apply()
    {
        Set("sv_usercmd_custom_random_seed", _ns ? "0" : "1");
        foreach (var (k, v) in Config.ForcedCvars) Set(k, v);
    }

    // ---------------- toggles ----------------
    private bool IsAdmin(CCSPlayerController? p) =>
        p == null || AdminManager.PlayerHasPermissions(p, Config.AdminFlag);

    private static bool? ParseArg(string? a) => a?.ToLowerInvariant() switch
    {
        "on" or "1" or "true" => true,
        "off" or "0" or "false" => false,
        _ => null,
    };

    private static readonly string Prefix = $" {ChatColors.DarkRed}[hvh]{ChatColors.Default}";

    // per-player PrintToChat (ClientPrint) instead of PrintToChatAll (UTIL_ClientPrintAll); next frame so it
    // lands after the player's own "!dt" line. Logged so delivery attempts are visible in the CSS log.
    private void Announce(string feature, bool on, string? suffix = null)
    {
        var state = on ? $"{ChatColors.Green}ON" : $"{ChatColors.Red}OFF";
        AnnounceText($"{feature} {state}{ChatColors.Default}{suffix}", $"{feature} {(on ? "ON" : "OFF")}");
    }

    private void AnnounceText(string text, string logText)
    {
        var msg = $"{Prefix} {text}";
        Server.NextFrame(() =>
        {
            var n = 0;
            foreach (var pl in Utilities.GetPlayers())
            {
                if (pl is not { IsValid: true, IsBot: false, IsHLTV: false }) continue;
                pl.PrintToChat(msg);
                n++;
            }
            Logger.LogInformation("[hvh] announced '{T}' to {N} player(s)", logText, n);
        });
    }

    private void Toggle(string what, string? arg, CCSPlayerController? by)
    {
        var want = ParseArg(arg);
        if (what == "ns") { _ns = want ?? !_ns; Apply(); }
        else { _dt = want ?? !_dt; _knife = false; _voidShooters.Clear(); if (_dt) ReleaseAllGates(); }
        var state = what == "ns" ? _ns : _dt;
        var feature = what == "ns" ? "nospread" : "doubletap";
        Announce(feature, state);
        Server.PrintToConsole($"[hvh] {feature} {(state ? "ON" : "OFF")} (by {by?.PlayerName ?? "console"})");
    }

    // MatchZy cfg hook: knife.cfg runs `hvh_knife on`; warmup.cfg + live overrides run `hvh_knife off`
    private void Knife(bool on)
    {
        if (on)
        {
            if (_dt) return;          // dt already allowed, nothing to force or undo
            _knife = true; _dt = true; ReleaseAllGates();
            Announce("doubletap", true, " for the knife round");
        }
        else
        {
            if (!_knife) return;      // dt wasn't forced by us (manual !dt stays as-is)
            _knife = false; _dt = false; _voidShooters.Clear();
            Announce("doubletap", false, " (knife round over)");
        }
        Server.PrintToConsole($"[hvh] knife dt {(on ? "on" : "off")}");
    }

    // chat: !ns / !dt from admins toggle + announce; the typed line still shows in chat
    private HookResult OnSay(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        var msg = info.GetArg(1).Trim().Trim('"').Trim();
        if (msg.Length < 3 || "!/.".IndexOf(msg[0]) < 0) return HookResult.Continue;
        var parts = msg[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return HookResult.Continue;
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;
        if (cmd == "status") { ShowStatus(player); return HookResult.Continue; }   // anyone, reply to caller only
        if (!IsAdmin(player)) return HookResult.Continue;
        if (cmd is "ns" or "dt") Toggle(cmd, arg, player);
        else if (Modes.ContainsKey(cmd)) SwitchMode(cmd, arg, player);
        else if (cmd is "lock" or "unlock")
        {
            if (cmd == "lock" && !Config.TeamLock) player.PrintToChat($"{Prefix} team lock is disabled in the config");
            else SetLock(cmd == "lock", player);
        }
        else if (cmd == "allow")
        {
            var rest = parts.Length > 1 ? string.Join(' ', parts[1..]) : null;
            if (rest == null) player.PrintToChat($"{Prefix} usage: !allow <player> [t|ct]");
            else Allow(rest, player, s => player.PrintToChat($"{Prefix} {s}"));
        }
        return HookResult.Continue;   // typed command stays visible in chat
    }

    // console / RCON: hvh_ns [on|off], hvh_dt [on|off], hvh_status
    [ConsoleCommand("hvh_ns", "Toggle nospread (sv_usercmd_custom_random_seed)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdNs(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p)) Toggle("ns", info.ArgCount > 1 ? info.GetArg(1) : null, p); }

    [ConsoleCommand("hvh_dt", "Toggle doubletap (shot validator + hard gate off/on)")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdDt(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p)) Toggle("dt", info.ArgCount > 1 ? info.GetArg(1) : null, p); }

    [ConsoleCommand("hvh_knife", "MatchZy hook: force doubletap on for the knife round (on) / restore (off)")]
    [CommandHelper(minArgs: 1, usage: "on|off", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdKnife(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p) && ParseArg(info.GetArg(1)) is bool on) Knife(on); }

}
