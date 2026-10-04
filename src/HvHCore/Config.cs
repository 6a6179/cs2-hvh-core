using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace HvHCore;

public class HvHCoreConfig : BasePluginConfig
{
    // state at server start (toggles reset to these on every restart)
    [JsonPropertyName("NospreadDefault")] public bool NospreadDefault { get; set; } = false;
    [JsonPropertyName("DoubletapDefault")] public bool DoubletapDefault { get; set; } = false;
    // CSS permission needed for !ns / !dt (@css/root implies it)
    [JsonPropertyName("AdminFlag")] public string AdminFlag { get; set; } = "@css/generic";
    // re-applied on map start + every round start so gamemode/MatchZy cfgs can't reset them
    [JsonPropertyName("ForcedCvars")] public Dictionary<string, string> ForcedCvars { get; set; } = new()
    {
        ["sv_airaccelerate"] = "32",
        ["sv_quantize_movement_input"] = "0",
    };
    // weapons the shot validator / hard gate apply to: "all" or csv of weapon_* names
    [JsonPropertyName("RestrictedWeaponsCsv")] public string RestrictedWeaponsCsv { get; set; } = "all";
    [JsonPropertyName("ConfigVersion")] public override int Version { get; set; } = 1;
    // shot validator (only while doubletap is OFF)
    [JsonPropertyName("ValidateShots")] public bool ValidateShots { get; set; } = true;
    [JsonPropertyName("CheckRate")] public bool CheckRate { get; set; } = true;
    [JsonPropertyName("CheckReload")] public bool CheckReload { get; set; } = true;
    [JsonPropertyName("CheckDeploy")] public bool CheckDeploy { get; set; } = true;
    [JsonPropertyName("RateToleranceTicks")] public int RateToleranceTicks { get; set; } = 0;
    [JsonPropertyName("DeployToleranceTicks")] public int DeployToleranceTicks { get; set; } = 2;
    [JsonPropertyName("RefundAmmo")] public bool RefundAmmo { get; set; } = true;
    [JsonPropertyName("RegateWeapon")] public bool RegateWeapon { get; set; } = true;
    [JsonPropertyName("LogInvalidShots")] public bool LogInvalidShots { get; set; } = true;
    // hard gate: after a valid shot, lock the weapon (server clock) until its cycle time has passed, so a
    // doubletap second shot never fires at all. Only weapons with cycle >= HardGateMinCycle (sec) - sprays untouched.
    [JsonPropertyName("HardGate")] public bool HardGate { get; set; } = true;
    [JsonPropertyName("HardGateMinCycle")] public float HardGateMinCycle { get; set; } = 0.2f;
    [JsonPropertyName("HardGateMaxLeadTicks")] public int HardGateMaxLeadTicks { get; set; } = 4;
    // Optional: !comp / !wm also write MAP=, GAME_TYPE=, GAME_MODE= into this KEY=VALUE file so a launcher
    // script can boot into the same mode after a restart. Empty = don't write anything.
    [JsonPropertyName("ModeEnvFile")] public string ModeEnvFile { get; set; } = "";
    // Hold server-side quit/exit for a chat + center-screen countdown while humans are connected.
    [JsonPropertyName("RestartWarning")] public bool RestartWarning { get; set; } = true;
    [JsonPropertyName("RestartWarningSeconds")] public float RestartWarningSeconds { get; set; } = 5f;
}
