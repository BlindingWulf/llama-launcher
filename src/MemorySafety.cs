namespace LlamaLauncher;

public enum MemoryAdmission
{
    Allow,
    Warn,
    Confirm,
    Block
}

public sealed record MemoryPolicyDefinition(
    string Name,
    double VramReserveGiB,
    double RamReserveGiB,
    double TightVramMarginGiB,
    double TightRamMarginGiB,
    MemoryAdmission OverBudgetAdmission,
    string Help);

/// <summary>
/// Central policy table for launcher-only memory advice and admission. Reserves are
/// explicit GiB values rather than hidden percentages. For identical hardware and
/// settings, Strict >= Balanced >= Relaxed for both VRAM and available-system-RAM.
/// No policy changes llama.cpp arguments or runs Auto Optimize.
/// </summary>
public static class MemoryPolicies
{
    public static readonly MemoryPolicyDefinition Relaxed = new(
        "Relaxed", 0.5, 1.5, 1.0, 2.0, MemoryAdmission.Confirm,
        "Keeps a small reserve and allows closer-to-budget configurations. Likely over-budget launches require confirmation.");

    public static readonly MemoryPolicyDefinition Balanced = new(
        "Balanced", 1.0, 3.0, 2.0, 4.0, MemoryAdmission.Confirm,
        "Uses the launcher's proven conservative reserve. Tight estimates warn; likely over-budget launches require confirmation.");

    public static readonly MemoryPolicyDefinition Strict = new(
        "Strict", 2.0, 6.0, 3.0, 6.0, MemoryAdmission.Block,
        "Keeps the largest reserve and blocks launches that do not fit the selected memory budget.");

    public static readonly MemoryPolicyDefinition Off = new(
        "Off", 0, 0, 0, 0, MemoryAdmission.Allow,
        "Attempts loading without launcher memory checks. May fail, spill where supported, or become very slow.");

    public static readonly string[] Names = { "Relaxed", "Balanced", "Strict", "Off", "Custom" };
    public static readonly string[] CustomActions = { "Warn", "Confirm", "Block" };

    public static MemoryPolicyDefinition Resolve(Profile profile)
    {
        Normalize(profile);
        return profile.MemorySafety switch
        {
            "Relaxed" => Relaxed,
            "Strict" => Strict,
            "Off" => Off,
            "Custom" => new(
                "Custom",
                profile.CustomVramReserveGiB,
                profile.CustomRamReserveGiB,
                Balanced.TightVramMarginGiB,
                Balanced.TightRamMarginGiB,
                Enum.TryParse<MemoryAdmission>(profile.CustomOverBudgetAction, true, out var action) ? action : MemoryAdmission.Confirm,
                "Uses the profile's explicit VRAM and available-system-RAM reserves and selected over-budget action."),
            _ => Balanced
        };
    }

    public static void Normalize(Profile profile)
    {
        if (!Names.Contains(profile.MemorySafety))
            profile.MemorySafety = "Balanced";
        profile.CustomVramReserveGiB = Math.Clamp(profile.CustomVramReserveGiB, 0, 128);
        profile.CustomRamReserveGiB = Math.Clamp(profile.CustomRamReserveGiB, 0, 1024);
        if (!CustomActions.Contains(profile.CustomOverBudgetAction))
            profile.CustomOverBudgetAction = "Confirm";
    }

    public static MemoryAdmission Admission(Profile profile, Estimate estimate)
    {
        var policy = Resolve(profile);
        if (policy.Name == "Off")
            return MemoryAdmission.Allow;
        if (!estimate.OverBudget)
            return estimate.State == "Tight" || estimate.State == "Estimate uncertain"
                ? MemoryAdmission.Warn
                : MemoryAdmission.Allow;
        return policy.OverBudgetAdmission;
    }
}
