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
    // ---------------- !status ----------------
    private readonly Stopwatch _sampleSw = new();
    private long _lastFrameTs;
    private double _worstGapMsCur, _worstGapMs;   // worst frame-to-frame gap, current second / last second
    private int _lastTickCount;
    private double _lastSampleSec, _tickRate;
    private double _lastCpuSec = -1, _cpuPct;

    private void OnTickSample()
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTs != 0)
        {
            var gap = (now - _lastFrameTs) * 1000.0 / Stopwatch.Frequency;
            if (gap > _worstGapMsCur) _worstGapMsCur = gap;
        }
        _lastFrameTs = now;
    }

    private static double ProcCpuSeconds()
    {
        // /proc/self/stat: utime/stime are fields 14/15, in USER_HZ (100) ticks; comm may contain spaces
        var s = File.ReadAllText("/proc/self/stat");
        var f = s[(s.LastIndexOf(')') + 2)..].Split(' ');
        return (long.Parse(f[11]) + long.Parse(f[12])) / 100.0;
    }

    private void Sample()
    {
        var t = _sampleSw.Elapsed.TotalSeconds;
        var dt = t - _lastSampleSec;
        if (dt <= 0) return;
        _tickRate = (Server.TickCount - _lastTickCount) / dt;
        _lastTickCount = Server.TickCount;
        _worstGapMs = _worstGapMsCur; _worstGapMsCur = 0;
        try
        {
            var cpu = ProcCpuSeconds();
            if (_lastCpuSec >= 0) _cpuPct = (cpu - _lastCpuSec) / dt * 100;
            _lastCpuSec = cpu;
        }
        catch { }
        _lastSampleSec = t;
    }

    private static string RamUsed()
    {
        try
        {
            var l = File.ReadLines("/proc/self/status").First(x => x.StartsWith("VmRSS:"));
            var kb = long.Parse(Regex.Match(l, @"\d+").Value);
            return $"{kb / 1048576.0:0.0}G";
        }
        catch { return "?"; }
    }

    private static string LoadAvg()
    {
        try { return File.ReadAllText("/proc/loadavg").Split(' ')[0]; } catch { return "?"; }
    }

    private static string ModeName()
    {
        var gt = ConVar.Find("game_type")?.GetPrimitiveValue<int>() ?? -1;
        var gm = ConVar.Find("game_mode")?.GetPrimitiveValue<int>() ?? -1;
        return (gt, gm) switch
        {
            (0, 0) => "casual", (0, 1) => "competitive", (0, 2) => "wingman", (1, 2) => "deathmatch",
            _ => $"mode {gt}/{gm}",
        };
    }

    private string Phase()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules == null) return $"{ChatColors.Grey}?";
        if (rules.WarmupPeriod) return $"{ChatColors.Yellow}Warmup";
        var score = new Dictionary<byte, int>();
        foreach (var team in Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager"))
            score[team.TeamNum] = team.Score;
        var s = $"{ChatColors.LightBlue}CT {score.GetValueOrDefault((byte)3)}{ChatColors.Grey} : {ChatColors.Gold}{score.GetValueOrDefault((byte)2)} T";
        if (rules.MatchWaitingForResume) return $"{ChatColors.Red}Paused  {s}";
        return _knife ? $"{ChatColors.Orange}Knife round" : $"{ChatColors.Green}Live  {s}";
    }

    private static string OnOff(bool on) => on ? $"{ChatColors.Lime}ON" : $"{ChatColors.Red}OFF";
    private static readonly string Sep = $"{ChatColors.Grey}  \u2022  ";            // bullet separator
    private static string Label(string s) => $"{ChatColors.Grey}{s} ";

    private static string HealthDot(double tick, double worstMs) =>
        tick >= 63 && worstMs < 40 ? $"{ChatColors.Lime}\u25CF" : tick >= 58 ? $"{ChatColors.Yellow}\u25CF" : $"{ChatColors.Red}\u25CF";

    private static string Pretty(string map) => map.StartsWith("de_") || map.StartsWith("cs_") || map.StartsWith("ar_")
        ? char.ToUpper(map[3]) + map[4..] : map;

    private List<string> StatusLines(CCSPlayerController? forPlayer)
    {
        var all = Utilities.GetPlayers().Where(x => x is { IsValid: true, IsHLTV: false }).ToList();
        var humans = all.Count(x => !x.IsBot);
        var bots = all.Count - humans;
        var up = DateTime.Now - Process.GetCurrentProcess().StartTime;
        var upStr = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:00}m" : $"{up.Minutes}m {up.Seconds:00}s";
        var W = ChatColors.White;
        var tickColor = _tickRate >= 63 ? ChatColors.Lime : _tickRate >= 58 ? ChatColors.Yellow : ChatColors.Red;
        var frameColor = _worstGapMs < 40 ? ChatColors.Lime : _worstGapMs < 80 ? ChatColors.Yellow : ChatColors.Red;
        var cpuColor = _cpuPct < 60 ? ChatColors.Lime : _cpuPct < 90 ? ChatColors.Yellow : ChatColors.Red;
        var mode = ModeName();
        mode = char.ToUpper(mode[0]) + mode[1..];

        var lines = new List<string>
        {
            $" {ChatColors.DarkRed}\u2501\u2501\u2501\u2501\u2501  {ChatColors.Red}hvh {ChatColors.Grey}server status  {ChatColors.DarkRed}\u2501\u2501\u2501\u2501\u2501",
            $" {Label("Nospread")}{OnOff(_ns)}{Sep}{Label("Doubletap")}{OnOff(_dt)}{(_knife ? $"{ChatColors.Orange} (knife)" : "")}" +
                (_voidTotal > 0 ? $"{Sep}{Label("Voided")}{ChatColors.Red}{_voidTotal}" : ""),
            $" {Label("Game")}{W}{mode}{ChatColors.Grey} on {W}{Pretty(Server.MapName)}{Sep}{Phase()}",
            $" {Label("Players")}{W}{humans}{ChatColors.Grey}/{Server.MaxPlayers}{(bots > 0 ? $"{ChatColors.Grey} +{bots} bots" : "")}" +
                (_locked ? $"{Sep}{Label("Teams")}{ChatColors.Red}locked" : "") +
                (forPlayer != null ? $"{Sep}{Label("Your ping")}{W}{forPlayer.Ping}ms" : ""),
            $" {HealthDot(_tickRate, _worstGapMs)} {Label("Tick")}{tickColor}{_tickRate:0}{Sep}{Label("Frame")}{frameColor}{_worstGapMs:0}ms" +
                $"{Sep}{Label("CPU")}{cpuColor}{_cpuPct:0}%{Sep}{Label("RAM")}{W}{RamUsed()}",
            $" {Label("Load")}{W}{LoadAvg()}{Sep}{Label("Uptime")}{W}{upStr}",
        };
        return lines;
    }

    private void ShowStatus(CCSPlayerController? p)
    {
        var lines = StatusLines(p);
        if (p != null) Server.NextFrame(() => { if (p.IsValid) foreach (var l in lines) p.PrintToChat(l); });
        else foreach (var l in lines) Server.PrintToConsole(Regex.Replace(l, "[\x01-\x1F]", ""));
    }

    [ConsoleCommand("hvh_status", "Show toggles, match state and server performance")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdStatus(CCSPlayerController? p, CommandInfo info)
    {
        if (!IsAdmin(p)) return;
        if (p == null) foreach (var l in StatusLines(null)) info.ReplyToCommand(Regex.Replace(l, "[\x01-\x1F]", "").Trim());
        else ShowStatus(p);
    }
}
