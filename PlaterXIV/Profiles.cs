using System;
using System.Collections.Generic;
using System.Numerics;
using PlaterXIV.Game;
using PlaterXIV.Render;

namespace PlaterXIV;

/// <summary>Everything in the Plates, Aggro, Cast &amp; DoTs and Skin tabs. One per profile.</summary>
[Serializable]
public sealed class PlateProfile
{
    public string Name { get; set; } = "Default";

    // Plates
    public bool HideNativeEnemyPlates { get; set; } = true;
    public bool ShowLevel { get; set; } = true;
    public float PlateScale { get; set; } = 1.0f;
    public bool MatchGamePlateScale { get; set; } = true;

    // Target emphasis
    public bool DimNonTargets { get; set; } = true;
    public float NonTargetAlpha { get; set; } = 0.55f;
    public float NonTargetScale { get; set; } = 0.82f;
    public float TargetScale { get; set; } = 1.1f;

    // Aggro
    public bool ShowAggroColors { get; set; } = true;
    public AggroMode AggroMode { get; set; } = AggroMode.Auto;
    public int ThreatWarnPercent { get; set; } = 80;
    public bool IdleHostilityColor { get; set; } = true;

    // Click
    /// <summary>Middle-click one of our plates: cast my job's interrupt on that enemy (one click, one action).</summary>
    public bool MiddleClickInterrupt { get; set; }

    // Cast bar and DoTs
    public bool ShowCastBar { get; set; } = true;
    public bool ShowMyDots { get; set; } = true;
    public int MaxDots { get; set; } = 6;
    public float DotExpiringSeconds { get; set; } = 5f;

    // Skin
    public string SkinName { get; set; } = "Signal";
    public Dictionary<string, SkinOverrides> ColourOverrides { get; set; } = new();

    /// <summary>Deep copy under a new name.</summary>
    public PlateProfile Copy(string name)
    {
        var copy = (PlateProfile)MemberwiseClone();
        copy.Name = name;
        copy.ColourOverrides = new Dictionary<string, SkinOverrides>();
        foreach (var (skin, o) in ColourOverrides)
        {
            copy.ColourOverrides[skin] = new SkinOverrides
            {
                Hp = o.Hp, Have = o.Have, CoTank = o.CoTank, Lost = o.Lost, Warn = o.Warn,
                CastInterruptible = o.CastInterruptible, CastLocked = o.CastLocked, Expiring = o.Expiring,
            };
        }

        return copy;
    }
}

/// <summary>Combat roles used to pick a profile.</summary>
public enum PlaterRole
{
    None = 0,
    Tank = 1,
    Healer = 2,
    Melee = 3,
    PhysicalRanged = 4,
    MagicalRanged = 5,
}

public enum NameMatch
{
    Is = 0,
    Contains = 1,
}

/// <summary>One per-enemy rule: matched by name, first match wins.</summary>
[Serializable]
public sealed class NameRule
{
    public bool Enabled { get; set; } = true;
    public NameMatch Match { get; set; } = NameMatch.Is;
    public string Text { get; set; } = string.Empty;

    public bool Highlight { get; set; } = true;
    public Vector4 HighlightColor { get; set; } = new(0.31f, 0.79f, 0.74f, 1f); // #4FC9BC
    public string Tag { get; set; } = "ADD";

    public bool Hide { get; set; }

    public bool ChangeSize { get; set; }
    public int SizePercent { get; set; } = 120;

    public bool NeverDim { get; set; }
    public bool NoAggroColors { get; set; }

    public bool Matches(string enemyName)
    {
        if (!Enabled || Text.Length == 0) return false;
        return Match == NameMatch.Is
            ? string.Equals(enemyName, Text, StringComparison.OrdinalIgnoreCase)
            : enemyName.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Short summary for the rule list, e.g. "Highlight · ADD".</summary>
    public string Summary()
    {
        var parts = new List<string>(4);
        if (Hide) parts.Add("Hide");
        if (Highlight) parts.Add(Tag.Length > 0 ? $"Highlight · {Tag}" : "Highlight");
        if (ChangeSize) parts.Add($"Size {SizePercent}%");
        if (NeverDim) parts.Add("Never dim");
        if (NoAggroColors) parts.Add("No aggro");
        return parts.Count == 0 ? "No effect" : string.Join(", ", parts);
    }
}
