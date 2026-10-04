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
    // ---------------- restart warning ----------------
    // Panel stop/restart and RCON both send "quit". With humans on, hold it for a loud 5 s warning, then quit for real.
    private float WarnSeconds => Math.Max(1f, Config.RestartWarningSeconds);
    private bool _quitting;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _spam;

    private static List<CCSPlayerController> Humans() =>
        Utilities.GetPlayers().Where(p => p is { IsValid: true, IsBot: false, IsHLTV: false }).ToList();

    private HookResult OnQuit(CCSPlayerController? player, CommandInfo info)
    {
        if (player != null || _quitting || !Config.RestartWarning) return HookResult.Continue;
        if (_spam != null) return HookResult.Stop;            // countdown already running
        if (Humans().Count == 0) return HookResult.Continue;  // nobody to warn
        Logger.LogInformation("[hvh] quit held for a {S}s restart warning ({N} player(s))", WarnSeconds, Humans().Count);
        var start = Server.CurrentTime;
        _spam = AddTimer(0.2f, () =>
        {
            var left = Math.Max(1, (int)Math.Ceiling(WarnSeconds - (Server.CurrentTime - start)));
            foreach (var p in Humans())
            {
                p.PrintToChat($" {ChatColors.Red}[hvh] SERVER RESTARTING {ChatColors.Default}in {ChatColors.Red}{left}");
                p.PrintToCenterHtml($"<font color='#ff3333' class='fontSize-xl'>SERVER RESTARTING</font><br><font class='fontSize-xl'>{left}</font>");
            }
        }, TimerFlags.REPEAT);
        AddTimer(WarnSeconds, () =>
        {
            _spam?.Kill(); _spam = null;
            _quitting = true;
            Server.ExecuteCommand("quit");
        });
        return HookResult.Stop;
    }

}
