using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlaterXIV.Game;
using PlaterXIV.Render;

namespace PlaterXIV;

/// <summary>How our plates find the game's plate position on screen (see the debug probe).</summary>
public enum PositionSource
{
    /// <summary>Game plate root node X/Y + half its width, + its height (bottom centre).</summary>
    RootNode = 0,
    /// <summary>Same, but from the root node's ScreenX/ScreenY.</summary>
    RootScreen = 1,
    /// <summary>NamePlateNumberArray X/Y + the root node's half width / height.</summary>
    NumberArray = 2,
}

/// <summary>
/// Global settings, the list of profiles and the shared rules. The per-profile settings are exposed
/// here as pass-through properties to the profile in use (<see cref="Active"/>), so the rest of the
/// plugin simply reads <c>config.PlateScale</c> and gets the right profile's value.
/// </summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    // ---------------------------------------------------------------- global

    public bool Enabled { get; set; } = true;
    public PositionSource PositionSource { get; set; } = PositionSource.RootScreen;
    public float OffsetX { get; set; }
    public float OffsetY { get; set; }
    public bool RequireGamePlateVisible { get; set; } = true;

    // Profiles
    public List<PlateProfile> Profiles { get; set; } = new();
    public string ActiveProfileName { get; set; } = "Default";
    public bool AutoSwitchProfiles { get; set; } = true;
    public Dictionary<PlaterRole, string> RoleProfiles { get; set; } = new();
    /// <summary>ClassJob id → profile name; wins over the role mapping.</summary>
    public Dictionary<uint, string> JobProfiles { get; set; } = new();

    // Rules (shared by every profile)
    public List<NameRule> Rules { get; set; } = new();
    public bool HideFarBelowMyLevel { get; set; }
    public int FarBelowLevels { get; set; } = 10;

    // Diagnostics
    public bool PerfLog { get; set; } = true;
    public int PerfIntervalSeconds { get; set; } = 5;
    public bool OpenDebugOnLoad { get; set; } = false;

    /// <summary>Settings from older versions that no longer exist at this level (read once by Migrate).</summary>
    [JsonExtensionData] public IDictionary<string, JToken>? Legacy { get; set; }

    // ---------------------------------------------------------------- runtime state

    [JsonIgnore] public PlateProfile Active { get; private set; } = new();

    /// <summary>Bumped when the skin or a colour changes, so the plates restyle once.</summary>
    [JsonIgnore] public int SkinRevision { get; private set; }
    /// <summary>Bumped when rules change, so cached rule matches are recomputed.</summary>
    [JsonIgnore] public int RulesRevision { get; private set; }
    /// <summary>Bumped when the profile in use changes, so open windows refresh their controls.</summary>
    [JsonIgnore] public int ActiveRevision { get; private set; }

    [JsonIgnore] public bool Dirty { get; private set; }
    [JsonIgnore] public DateTime DirtySince { get; private set; }

    public void SkinChanged() => SkinRevision++;
    public void RulesChanged() => RulesRevision++;

    public void MarkDirty()
    {
        if (!Dirty) DirtySince = DateTime.UtcNow;
        Dirty = true;
    }

    public void Save()
    {
        Dirty = false;
        Services.PluginInterface.SavePluginConfig(this);
    }

    // ---------------------------------------------------------------- profiles

    public PlateProfile? FindProfile(string name)
        => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void SetActive(string name)
    {
        var profile = FindProfile(name) ?? Profiles[0];
        if (ReferenceEquals(profile, Active)) return;
        Active = profile;
        ActiveProfileName = profile.Name;
        ActiveRevision++;
        SkinChanged();
        MarkDirty();
    }

    /// <summary>The profile for a job: per-job exception first, then its role, then the first profile.</summary>
    public string ProfileFor(uint classJobId, PlaterRole role)
    {
        if (JobProfiles.TryGetValue(classJobId, out var byJob) && FindProfile(byJob) != null) return byJob;
        if (RoleProfiles.TryGetValue(role, out var byRole) && FindProfile(byRole) != null) return byRole;
        return Profiles[0].Name;
    }

    public string UniqueProfileName(string wanted)
    {
        var name = wanted;
        for (var i = 2; FindProfile(name) != null; i++) name = $"{wanted} {i}";
        return name;
    }

    /// <summary>Called once after loading: migrates old configs and makes sure there is a profile in use.</summary>
    public void Initialize()
    {
        var changed = false;

        if (Version < 2)
        {
            OpenDebugOnLoad = false; // the debug probe is opt-in; /plater opens Settings
            changed = true;
        }

        if (Profiles.Count == 0)
        {
            // Version 2 kept the plate settings at the top level; they are now the "Default" profile.
            var defaults = new PlateProfile { Name = "Default" };
            if (Legacy is { Count: > 0 })
                CopyLegacyInto(defaults);

            Profiles.Add(defaults);
            Profiles.Add(defaults.Copy("Tank"));
            Profiles.Add(defaults.Copy("Healer"));
            Profiles.Add(defaults.Copy("DPS"));
            RoleProfiles[PlaterRole.Tank] = "Tank";
            RoleProfiles[PlaterRole.Healer] = "Healer";
            RoleProfiles[PlaterRole.Melee] = "DPS";
            RoleProfiles[PlaterRole.PhysicalRanged] = "DPS";
            RoleProfiles[PlaterRole.MagicalRanged] = "DPS";
            changed = true;
        }

        Legacy = null;
        Active = FindProfile(ActiveProfileName) ?? Profiles[0];
        ActiveProfileName = Active.Name;

        if (Version < CurrentVersion)
        {
            Version = CurrentVersion;
            changed = true;
        }

        if (changed) Save();
    }

    /// <summary>Copies each old top-level setting that a profile has, one by one; a value that can't be read is skipped.</summary>
    private void CopyLegacyInto(PlateProfile profile)
    {
        foreach (var property in typeof(PlateProfile).GetProperties())
        {
            if (property.Name == nameof(PlateProfile.Name) || !property.CanWrite) continue;
            if (Legacy == null || !Legacy.TryGetValue(property.Name, out var token)) continue;

            try
            {
                if (token is JObject obj) obj.Remove("$type"); // Dalamud writes type hints into dictionaries
                var value = token.ToObject(property.PropertyType);
                if (value != null) property.SetValue(profile, value);
            }
            catch (Exception e)
            {
                Services.Log.Warning($"[Plater] could not migrate setting {property.Name}: {e.Message}");
            }
        }

        profile.Name = "Default";
    }

    // ---------------------------------------------------------------- pass-through to the profile in use

    [JsonIgnore] public bool HideNativeEnemyPlates { get => Active.HideNativeEnemyPlates; set => Active.HideNativeEnemyPlates = value; }
    [JsonIgnore] public bool ShowLevel { get => Active.ShowLevel; set => Active.ShowLevel = value; }
    [JsonIgnore] public float PlateScale { get => Active.PlateScale; set => Active.PlateScale = value; }
    [JsonIgnore] public bool MatchGamePlateScale { get => Active.MatchGamePlateScale; set => Active.MatchGamePlateScale = value; }
    [JsonIgnore] public bool DimNonTargets { get => Active.DimNonTargets; set => Active.DimNonTargets = value; }
    [JsonIgnore] public float NonTargetAlpha { get => Active.NonTargetAlpha; set => Active.NonTargetAlpha = value; }
    [JsonIgnore] public float NonTargetScale { get => Active.NonTargetScale; set => Active.NonTargetScale = value; }
    [JsonIgnore] public float TargetScale { get => Active.TargetScale; set => Active.TargetScale = value; }
    [JsonIgnore] public bool ShowAggroColors { get => Active.ShowAggroColors; set => Active.ShowAggroColors = value; }
    [JsonIgnore] public AggroMode AggroMode { get => Active.AggroMode; set => Active.AggroMode = value; }
    [JsonIgnore] public int ThreatWarnPercent { get => Active.ThreatWarnPercent; set => Active.ThreatWarnPercent = value; }
    [JsonIgnore] public bool IdleHostilityColor { get => Active.IdleHostilityColor; set => Active.IdleHostilityColor = value; }
    [JsonIgnore] public bool MiddleClickInterrupt { get => Active.MiddleClickInterrupt; set => Active.MiddleClickInterrupt = value; }
    [JsonIgnore] public bool ShowCastBar { get => Active.ShowCastBar; set => Active.ShowCastBar = value; }
    [JsonIgnore] public bool ShowMyDots { get => Active.ShowMyDots; set => Active.ShowMyDots = value; }
    [JsonIgnore] public int MaxDots { get => Active.MaxDots; set => Active.MaxDots = value; }
    [JsonIgnore] public float DotExpiringSeconds { get => Active.DotExpiringSeconds; set => Active.DotExpiringSeconds = value; }
    [JsonIgnore] public string SkinName { get => Active.SkinName; set => Active.SkinName = value; }
    [JsonIgnore] public Dictionary<string, SkinOverrides> ColourOverrides { get => Active.ColourOverrides; set => Active.ColourOverrides = value; }

    public SkinOverrides OverridesFor(string skinName)
    {
        if (!ColourOverrides.TryGetValue(skinName, out var o))
        {
            o = new SkinOverrides();
            ColourOverrides[skinName] = o;
        }

        return o;
    }

    public Skin EffectiveSkin()
    {
        var baseSkin = Skin.Get(SkinName);
        return baseSkin.WithOverrides(ColourOverrides.TryGetValue(baseSkin.Name, out var o) ? o : null);
    }
}
