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

public partial class HvHCorePlugin
{
    // ---------------- mode switch: !comp / !wm [map] ----------------
    private static readonly Dictionary<string, (int Type, int Mode, string Name)> Modes = new()
    {
        ["comp"] = (0, 1, "competitive"),
        ["wm"]   = (0, 2, "wingman"),
    };

    private static string? ResolveMap(string arg)
    {
        var dir = Path.Join(Server.GameDirectory, "csgo", "maps");
        if (!Directory.Exists(dir)) return null;
        var maps = Directory.GetFiles(dir, "*.vpk")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(n => (n.StartsWith("de_") || n.StartsWith("cs_") || n.StartsWith("ar_")) && !n.EndsWith("_vanity"))
            .ToList();
        var a = arg.Trim().ToLowerInvariant();
        foreach (var exact in new[] { a, "de_" + a, "cs_" + a, "ar_" + a })
            if (maps.Contains(exact)) return exact;
        var hits = maps.Where(m => m.Contains(a)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private void SaveEnv(Dictionary<string, string> kv)
    {
        if (string.IsNullOrWhiteSpace(Config.ModeEnvFile)) return;
        try
        {
            var lines = File.Exists(Config.ModeEnvFile) ? File.ReadAllLines(Config.ModeEnvFile).ToList() : new List<string>();
            foreach (var (k, v) in kv)
            {
                var i = lines.FindIndex(l => l.StartsWith(k + "="));
                if (i >= 0) lines[i] = $"{k}={v}"; else lines.Add($"{k}={v}");
            }
            File.WriteAllLines(Config.ModeEnvFile, lines);   // truncates in place, keeps the file mode
        }
        catch (Exception e) { Logger.LogWarning("[hvh] couldn't update {F}: {E}", Config.ModeEnvFile, e.Message); }
    }

    private void SwitchMode(string key, string? mapArg, CCSPlayerController? by)
    {
        var (type, mode, name) = Modes[key];
        var map = Server.MapName;
        if (!string.IsNullOrWhiteSpace(mapArg))
        {
            var m = ResolveMap(mapArg);
            if (m == null)
            {
                var err = $"{Prefix} unknown map '{mapArg}'";
                if (by != null) by.PrintToChat(err); else Server.PrintToConsole(err);
                return;
            }
            map = m;
        }
        Server.ExecuteCommand($"game_type {type}; game_mode {mode}");
        SaveEnv(new() { ["MAP"] = map, ["GAME_TYPE"] = type.ToString(), ["GAME_MODE"] = mode.ToString() });
        AnnounceText($"switching to {ChatColors.Green}{name}{ChatColors.Default} on {map}", $"mode {name} {map}");
        Server.PrintToConsole($"[hvh] mode -> {name} ({type}/{mode}) on {map} (by {by?.PlayerName ?? "console"})");
        AddTimer(2f, () => Server.ExecuteCommand($"changelevel {map}"));
    }


    [ConsoleCommand("hvh_comp", "Switch to competitive (5v5) [map]")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdComp(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p)) SwitchMode("comp", info.ArgCount > 1 ? info.GetArg(1) : null, p); }

    [ConsoleCommand("hvh_wm", "Switch to wingman (2v2) [map]")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdWm(CCSPlayerController? p, CommandInfo info)
    { if (IsAdmin(p)) SwitchMode("wm", info.ArgCount > 1 ? info.GetArg(1) : null, p); }

}
