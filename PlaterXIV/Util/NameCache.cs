using System;
using System.Collections.Generic;
using Lumina.Excel;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaStatus = Lumina.Excel.Sheets.Status;

namespace PlaterXIV.Util;

/// <summary>Spell, status and job names from the game data, cached after the first lookup.</summary>
public sealed class NameCache
{
    private readonly ExcelSheet<LuminaAction> actions;
    private readonly ExcelSheet<LuminaStatus> statuses;
    private readonly ExcelSheet<LuminaClassJob> jobs;
    private readonly Dictionary<uint, string> actionNames = new();
    private readonly Dictionary<uint, string> statusNames = new();
    private readonly Dictionary<uint, string> jobAbbreviations = new();
    private readonly Dictionary<uint, byte> jobRoles = new();
    private readonly Dictionary<uint, PlaterRole> platerRoles = new();
    private readonly Dictionary<uint, StatusVisual> statusVisuals = new();

    /// <summary>What a status looks like: its icon and how many stacks it can have.</summary>
    public readonly record struct StatusVisual(uint Icon, int MaxStacks);

    public NameCache()
    {
        actions = Services.DataManager.GetExcelSheet<LuminaAction>();
        statuses = Services.DataManager.GetExcelSheet<LuminaStatus>();
        jobs = Services.DataManager.GetExcelSheet<LuminaClassJob>();
    }

    public string Action(uint id)
    {
        if (actionNames.TryGetValue(id, out var name)) return name;
        var row = actions.GetRowOrDefault(id);
        name = row.HasValue ? row.Value.Name.ExtractText() : $"#{id}";
        if (name.Length == 0) name = $"#{id}";
        actionNames[id] = name;
        return name;
    }

    public string Status(uint id)
    {
        if (statusNames.TryGetValue(id, out var name)) return name;
        var row = statuses.GetRowOrDefault(id);
        name = row.HasValue ? row.Value.Name.ExtractText() : $"#{id}";
        if (name.Length == 0) name = $"#{id}";
        statusNames[id] = name;
        return name;
    }

    /// <summary>Icon for a status, adjusted for stacks the way the game does (icon + stacks - 1).</summary>
    public uint StatusIcon(uint statusId, ushort param)
    {
        if (!statusVisuals.TryGetValue(statusId, out var visual))
        {
            var row = statuses.GetRowOrDefault(statusId);
            visual = row.HasValue ? new StatusVisual((uint)row.Value.Icon, (int)row.Value.MaxStacks) : default;
            statusVisuals[statusId] = visual;
        }

        if (visual.Icon == 0) return 0;
        return visual.MaxStacks > 1 && param > 1 ? visual.Icon + (uint)Math.Min((int)param, visual.MaxStacks) - 1 : visual.Icon;
    }

    public string JobAbbreviation(uint classJobId)
    {
        if (jobAbbreviations.TryGetValue(classJobId, out var abbr)) return abbr;
        var row = jobs.GetRowOrDefault(classJobId);
        abbr = row.HasValue ? row.Value.Abbreviation.ExtractText() : "?";
        jobAbbreviations[classJobId] = abbr;
        return abbr;
    }

    /// <summary>Tank / healer / melee / physical ranged / magical ranged (casters have INT as main stat).</summary>
    public PlaterRole RoleOf(uint classJobId)
    {
        if (platerRoles.TryGetValue(classJobId, out var role)) return role;
        var row = jobs.GetRowOrDefault(classJobId);
        role = PlaterRole.None;
        if (row.HasValue)
        {
            role = (byte)row.Value.Role switch
            {
                1 => PlaterRole.Tank,
                4 => PlaterRole.Healer,
                2 => PlaterRole.Melee,
                3 => (byte)row.Value.PrimaryStat == 4 ? PlaterRole.MagicalRanged : PlaterRole.PhysicalRanged,
                _ => PlaterRole.None,
            };
        }

        platerRoles[classJobId] = role;
        return role;
    }

    /// <summary>ClassJob.Role: 1 tank, 2 melee, 3 ranged, 4 healer, 0 none.</summary>
    public byte JobRole(uint classJobId)
    {
        if (jobRoles.TryGetValue(classJobId, out var role)) return role;
        var row = jobs.GetRowOrDefault(classJobId);
        role = row.HasValue ? (byte)row.Value.Role : (byte)0;
        jobRoles[classJobId] = role;
        return role;
    }
}
