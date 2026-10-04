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
    // ---------------- shot validator (active while doubletap is OFF) ----------------
    // Every gun shot is checked against the SERVER clock (tickbase shifting can't fake it):
    //   rate   - time since this weapon's last VALID shot < weapon cycle time (burst / alt-fire aware)
    //   reload - shot fired while the weapon is mid-reload (shotguns that reload shell-by-shell may interrupt)
    //   deploy - shot fired before the weapon finished deploying (switch + instant shot)
    // An invalid shot is voided: its bullet damage is cancelled, the round is refunded, and the weapon's
    // next-attack tick is pushed back by however early the shot was, so the engine itself refuses follow-ups.
    private const int TickRate = 64;
    private readonly Dictionary<uint, int> _lastValidShot = new();     // weapon entity index -> server tick
    private readonly HashSet<uint> _voidShooters = [];                 // pawn index whose current shot is void
    private readonly Dictionary<ulong, Dictionary<string, int>> _voidCounts = new();
    private readonly Dictionary<ulong, float> _lastVoidLog = new();
    private readonly Dictionary<uint, int> _reloadStart = new();       // weapon entity index -> server tick
    private readonly HashSet<uint> _regated = [];                      // weapons already pushed back since last valid shot
    private sealed class Gate { public CBasePlayerWeapon W = null!; public CCSPlayerController P = null!; public int Hold, Release, End, EngineNext; }
    private readonly Dictionary<uint, Gate> _gates = new();            // weapon entity index -> lock
    private const int GateHoldTicks = 4096;
    private readonly Dictionary<string, (int N, long Extra, int Min)> _spamStats = new();  // legit back-to-back fire timing
    private int _voidTotal;
    private bool _voidClearQueued;

    private static bool IsGun(CSWeaponType t) => t is CSWeaponType.WEAPONTYPE_PISTOL or CSWeaponType.WEAPONTYPE_SUBMACHINEGUN
        or CSWeaponType.WEAPONTYPE_RIFLE or CSWeaponType.WEAPONTYPE_SHOTGUN or CSWeaponType.WEAPONTYPE_SNIPER_RIFLE
        or CSWeaponType.WEAPONTYPE_MACHINEGUN;

    private int MinShotTicks(CCSWeaponBase w, CCSWeaponBaseVData d)
    {
        var cyc = d.CycleTime.Values;
        var cycle = cyc[0];
        if (cyc[1] > 0 && cyc[1] < cycle) cycle = cyc[1];                       // alt fire mode
        if (d.HasBurstMode && w.BurstMode && d.TimeBetweenBurstShots > 0)       // glock / famas burst
            cycle = Math.Min(cycle, d.TimeBetweenBurstShots);
        return (int)Math.Floor(cycle * TickRate) - Config.RateToleranceTicks;
    }

    private static float CycleSeconds(CCSWeaponBase w, CCSWeaponBaseVData d)
    {
        var cyc = d.CycleTime.Values;
        var c = cyc[0];
        if (cyc[1] > 0 && cyc[1] < c) c = cyc[1];
        if (d.HasBurstMode && w.BurstMode && d.TimeBetweenBurstShots > 0) c = Math.Min(c, d.TimeBetweenBurstShots);
        return c;
    }

    private void SetNextAttack(CBasePlayerWeapon w, int tick)
    {
        if (w.NextPrimaryAttackTick == tick) return;
        w.NextPrimaryAttackTick = tick;
        Utilities.SetStateChanged(w, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
    }

    // release slightly early (about half the player's ping) so the unlock reaches a legit client right as its own
    // cycle ends; the last few ticks are still covered by the engine's own gate + the rate check
    private int LeadTicks(CCSPlayerController p) =>
        Math.Clamp((int)Math.Ceiling(p.Ping / 2.0 / (1000.0 / TickRate)) + 1, 1, Math.Max(1, Config.HardGateMaxLeadTicks));

    private void StartGate(CBasePlayerWeapon w, CCSPlayerController p, int now, int cycleTicks)
    {
        var end = now + cycleTicks;
        var g = new Gate { W = w, P = p, End = end, Release = end - LeadTicks(p), Hold = (int)p.TickBase + GateHoldTicks };
        _gates[w.Index] = g;
        SetNextAttack(w, g.Hold);
    }

    private void ReleaseGate(Gate g, int now)
    {
        if (!g.W.IsValid) return;
        var tb = g.P.IsValid ? (int)g.P.TickBase : now;
        // never release earlier than the engine itself wanted (e.g. a weapon switch set a longer deploy lock)
        var engine = g.EngineNext > tb + 512 ? 0 : g.EngineNext;
        SetNextAttack(g.W, Math.Max(tb + Math.Max(0, g.End - now), engine));
    }

    private void ReleaseAllGates()
    {
        foreach (var g in _gates.Values) ReleaseGate(g, Server.TickCount);
        _gates.Clear();
    }

    private void OnTickGates()
    {
        if (_gates.Count == 0) return;
        var now = Server.TickCount;
        List<uint>? done = null;
        foreach (var (k, g) in _gates)
        {
            if (!g.W.IsValid) { (done ??= []).Add(k); continue; }
            if (_dt || now >= g.Release) { ReleaseGate(g, now); (done ??= []).Add(k); continue; }
            var cur = g.W.NextPrimaryAttackTick;
            if (cur != g.Hold) g.EngineNext = Math.Max(g.EngineNext, cur);   // engine rewrote it (shot bookkeeping, redeploy)
            SetNextAttack(g.W, g.Hold);
        }
        if (done != null) foreach (var k in done) _gates.Remove(k);
    }

    private HookResult OnWeaponFire(EventWeaponFire ev, GameEventInfo _)
    {
        if (_dt || !Config.ValidateShots) return HookResult.Continue;
        var p = ev.Userid;
        if (p is not { IsValid: true, IsHLTV: false } || !p.Pawn.IsValid) return HookResult.Continue;
        var active = p.Pawn.Value?.WeaponServices?.ActiveWeapon.Value;
        if (active == null || !active.IsValid) return HookResult.Continue;
        var w = new CCSWeaponBase(active.Handle);
        var d = active.GetVData<CCSWeaponBaseVData>();
        if (d == null || !IsGun(d.WeaponType)) return HookResult.Continue;

        var now = Server.TickCount;
        var pawnIdx = p.Pawn.Index;
        var wIdx = active.Index;
        var name = active.DesignerName ?? "weapon_unknown";
        string? reason = null;
        var early = 0;   // ticks the shot came too early (used to push the next attack back)

        if (Config.CheckReload && w.InReload && !d.ReloadsSingleShells)
        {
            // engine may allow cancelling a reload by firing after DisallowAttackAfterReloadStartDuration
            var window = d.DisallowAttackAfterReloadStartDuration > 0 ? (int)(d.DisallowAttackAfterReloadStartDuration * TickRate) : int.MaxValue;
            // reload must have started on an earlier tick (avoids shot+reload ordering inside one tick)
            if (_reloadStart.TryGetValue(wIdx, out var rs) && now - rs >= 1 && now - rs < window) reason = "reload";
        }

        if (reason == null && Config.CheckDeploy && d.DeployDuration > 0)
        {
            // DeployTick is stamped from the player's tickbase; a shifted (DT) tickbase can put it ahead of the
            // server clock, which means the deploy and the shot happened in the same burst -> treat as 0 elapsed
            var since = Math.Max(0, now - w.DeployTick);
            var need = (int)Math.Floor(d.DeployDuration * TickRate) - Config.DeployToleranceTicks;
            if (since < need) { reason = "deploy"; early = need - since; }
        }

        if (reason == null && Config.CheckRate && name != "weapon_revolver" && IsRestricted(name)
            && _lastValidShot.TryGetValue(wIdx, out var last))
        {
            var diff = now - last;
            var min = MinShotTicks(w, d);
            if (diff >= 0 && diff < min) { reason = "rate"; early = min - diff; }
        }

        if (reason == null)
        {
            var cycleSec = CycleSeconds(w, d);
            var cycleTicks = (int)Math.Floor(cycleSec * TickRate);
            if (_lastValidShot.TryGetValue(wIdx, out var prev))
            {
                var gap = now - prev;
                if (gap >= cycleTicks && gap <= cycleTicks * 3 / 2)   // back-to-back fire: how close to the cycle?
                {
                    var s = _spamStats.TryGetValue(name, out var cur) ? cur : (N: 0, Extra: 0L, Min: int.MaxValue);
                    _spamStats[name] = (s.N + 1, s.Extra + (gap - cycleTicks), Math.Min(s.Min, gap - cycleTicks));
                }
            }
            if (Config.HardGate && name != "weapon_revolver" && cycleSec >= Config.HardGateMinCycle && IsRestricted(name))
                StartGate(active, p, now, cycleTicks);
            _lastValidShot[wIdx] = now;
            _regated.Remove(wIdx);
            _voidShooters.Remove(pawnIdx);   // a later valid shot in the same frame keeps its damage
            return HookResult.Continue;
        }

        // ---- void the shot ----
        _voidShooters.Add(pawnIdx);
        if (!_voidClearQueued) { _voidClearQueued = true; Server.NextFrame(() => { _voidShooters.Clear(); _voidClearQueued = false; }); }

        if (Config.RefundAmmo)
        {
            active.Clip1++;
            Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_iClip1");
        }
        if (Config.RegateWeapon && early > 0 && _regated.Add(wIdx))   // push back once per offence, never stack
        {
            var push = Math.Min(early, TickRate * 2);
            Server.NextFrame(() =>
            {
                if (!active.IsValid) return;
                active.NextPrimaryAttackTick += push;
                Utilities.SetStateChanged(active, "CBasePlayerWeapon", "m_nNextPrimaryAttackTick");
            });
        }

        _voidTotal++;
        var sid = p.SteamID;
        if (!_voidCounts.TryGetValue(sid, out var counts)) _voidCounts[sid] = counts = new();
        counts[reason] = counts.GetValueOrDefault(reason) + 1;
        if (Config.LogInvalidShots && (!_lastVoidLog.TryGetValue(sid, out var t) || Server.CurrentTime - t > 1f))
        {
            _lastVoidLog[sid] = Server.CurrentTime;
            Logger.LogInformation("[hvh] voided shot: {Name} {Weapon} reason={Reason} early={Early}t total={Total}",
                p.PlayerName, name, reason, early, counts.Values.Sum());
        }
        return HookResult.Continue;
    }

    // cancel bullet damage from a voided shot (Handled stops the whole damage application)
    private HookResult OnTakeDamage(CBaseEntity victim, CTakeDamageInfo info)
    {
        if (_dt || _voidShooters.Count == 0) return HookResult.Continue;
        if ((info.BitsDamageType & DamageTypes_t.DMG_BULLET) == 0) return HookResult.Continue;
        if (info.Attacker.Value == null || !_voidShooters.Contains(info.Attacker.Index)) return HookResult.Continue;
        info.Damage = 0;
        return HookResult.Handled;
    }

    private bool IsRestricted(string weapon)
    {
        if (Config.RestrictedWeaponsCsv.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;
        return Config.RestrictedWeaponsCsv.Split(',').Select(w => w.Trim().ToLowerInvariant()).Contains(weapon.ToLowerInvariant());
    }

    // console: per-player voided shot counts
    [ConsoleCommand("hvh_shots", "Voided shot counts per player")]
    [CommandHelper(whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void CmdShots(CCSPlayerController? p, CommandInfo info)
    {
        if (!IsAdmin(p)) return;
        info.ReplyToCommand($"[hvh] voided shots total: {_voidTotal} | active gates: {_gates.Count}");
        foreach (var (wn, s) in _spamStats)
            info.ReplyToCommand($"  back-to-back {wn}: {s.N} shots, avg +{(double)s.Extra / s.N:0.0}t, min +{s.Min}t over cycle");
        foreach (var pl in Utilities.GetPlayers().Where(x => x is { IsValid: true, IsBot: false }))
            if (_voidCounts.TryGetValue(pl.SteamID, out var c))
                info.ReplyToCommand($"  {pl.PlayerName}: " + string.Join(", ", c.Select(kv => $"{kv.Key} {kv.Value}")));
    }

    // console debug: active weapon timing fields for every player (to sanity-check tick units)
    [ConsoleCommand("hvh_wpn", "Debug: weapon timing state per player")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void CmdWpn(CCSPlayerController? _, CommandInfo info)
    {
        info.ReplyToCommand($"server tick {Server.TickCount}");
        foreach (var pl in Utilities.GetPlayers().Where(x => x is { IsValid: true, IsHLTV: false }))
        {
            var a = pl.Pawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (a == null || !a.IsValid) { info.ReplyToCommand($"  {pl.PlayerName}: no weapon"); continue; }
            var w = new CCSWeaponBase(a.Handle);
            var d = a.GetVData<CCSWeaponBaseVData>();
            info.ReplyToCommand($"  {pl.PlayerName}: {a.DesignerName} clip {a.Clip1} reload {w.InReload} deployTick {w.DeployTick} " +
                $"nextAtk {a.NextPrimaryAttackTick} tickbase {pl.TickBase} burst {w.BurstMode} | cycle {d?.CycleTime.Values[0]:0.000}/{d?.CycleTime.Values[1]:0.000} " +
                $"deploy {d?.DeployDuration:0.00}s shells {d?.ReloadsSingleShells} reloadLock {d?.DisallowAttackAfterReloadStartDuration:0.00}s " +
                $"reloadStart {(_reloadStart.TryGetValue(a.Index, out var rs) ? rs : -1)} lastValid {(_lastValidShot.TryGetValue(a.Index, out var lv) ? lv : -1)}");
        }
    }
}
