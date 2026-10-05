using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace HvHCore;

// Warmup money: every (re)spawn during warmup sets the player's cash to Config.WarmupMoney (0 = off).
// mp_startmoney alone doesn't do it: CS2 keeps your old balance across warmup respawns, and
// gamemode_competitive.cfg resets mp_startmoney to 800 on map load anyway.
public partial class HvHCorePlugin
{
    private void RegisterWarmupMoney()
    {
        RegisterEventHandler<EventPlayerSpawn>((ev, _) =>
        {
            if (Config.WarmupMoney <= 0) return HookResult.Continue;
            var p = ev.Userid;
            if (p == null || !p.IsValid || p.IsBot || p.IsHLTV) return HookResult.Continue;
            // next frame: the engine sets the spawn balance after the event fires
            Server.NextFrame(() => SetWarmupMoney(p));
            return HookResult.Continue;
        });
    }

    private void SetWarmupMoney(CCSPlayerController p)
    {
        if (!p.IsValid || !IsWarmup()) return;
        var money = p.InGameMoneyServices;
        if (money == null || money.Account >= Config.WarmupMoney) return;
        money.Account = Config.WarmupMoney;
        Utilities.SetStateChanged(p, "CCSPlayerController", "m_pInGameMoneyServices");
    }

    private static bool IsWarmup() =>
        Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod == true;
}
