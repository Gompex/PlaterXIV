using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PlaterXIV.Game;
using PlaterXIV.Render;
using PlaterXIV.Util;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace PlaterXIV.Debug;

/// <summary>
/// Developer probe (ImGui, dev-only): shows exactly what the scanner reads from the game, so we can
/// confirm plate positions, enmity and cast/status data before anything is drawn on the plates.
/// </summary>
public sealed unsafe class DebugWindow : Window
{
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.55f, 1f);
    private static readonly Vector4 Warn = new(0.95f, 0.75f, 0.3f, 1f);
    private static readonly Vector4 Bad = new(1f, 0.45f, 0.35f, 1f);
    private static readonly Vector4 Muted = new(0.65f, 0.65f, 0.7f, 1f);

    private readonly PlateScanner scanner;
    private readonly NameCache names;
    private readonly Configuration config;
    private readonly PlateRenderer renderer;
    private readonly NativePlateHider hider;
    private readonly AggroResolver aggro;
    private readonly StringBuilder sb = new();
    private bool allKinds;

    public DebugWindow(PlateScanner scanner, NameCache names, Configuration config, PlateRenderer renderer, NativePlateHider hider, AggroResolver aggro)
        : base("Plater for FF14 · debug probe##PlaterXIVDebug", ImGuiWindowFlags.HorizontalScrollbar)
    {
        this.scanner = scanner;
        this.names = names;
        this.config = config;
        this.renderer = renderer;
        this.hider = hider;
        this.aggro = aggro;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 320),
            MaximumSize = new Vector2(5000, 4000),
        };
    }

    public override void Draw()
    {
        ImGui.TextUnformatted($"Build {Plugin.BuildTag} · frame {scanner.Frame} · scan {scanner.LastScanMicroseconds:F1} µs");
        ImGui.TextUnformatted($"Local {scanner.LocalEntityId:X8} · PvP {scanner.InPvP} · ObjectInfoCount {scanner.ObjectInfoCount} · ActiveNamePlateCount {scanner.ActiveNamePlateCount} · hide refused {hider.RefusedLastFrame} · names repaired {hider.NamesRepaired}");
        ImGui.SameLine();
        if (ImGui.Button("Show every game name now"))
            Services.Log.Information($"[Plater] showed {hider.ForceShowNames()} hidden game names");

        ImGui.Checkbox("Show every plate kind (not only enemies)", ref allKinds);
        ImGui.SameLine();
        var perf = config.PerfLog;
        if (ImGui.Checkbox("Write [Perf] lines to the Dalamud log", ref perf))
        {
            config.PerfLog = perf;
            config.Save();
        }

        if (ImGui.CollapsingHeader("Plates (live settings)", ImGuiTreeNodeFlags.DefaultOpen))
            DrawPlateControls();
        if (ImGui.CollapsingHeader("My enmity list (UIState.Hater)"))
            DrawHater();
        if (ImGui.CollapsingHeader("Party enmity on my target (UIState.Hate)"))
            DrawHate();

        ImGui.Separator();
        DrawPlates();
    }

    private void DrawPlateControls()
    {
        ImGui.TextUnformatted($"Middle-click: {(config.MiddleClickInterrupt ? "on" : "off")} · last: {renderer.LastClickResult}");
        ImGui.TextUnformatted($"Our plates shown {renderer.ShownLastFrame} · pool {renderer.Built}/50 · update {renderer.LastRenderMicroseconds:F1} µs");
        ImGui.TextUnformatted($"NamePlate root {scanner.NamePlateRootPosition.X:F0},{scanner.NamePlateRootPosition.Y:F0} scale {scanner.NamePlateRootScale.X:F3} · overlay root {renderer.OverlayPosition.X:F0},{renderer.OverlayPosition.Y:F0} scale {renderer.OverlayScale:F3} · global UI scale {AtkUnitBase.GetGlobalUIScale():F3}");

        var changed = false;
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Plates on", ref enabled)) { config.Enabled = enabled; changed = true; hider.Refresh(); }
        ImGui.SameLine();
        var hide = config.HideNativeEnemyPlates;
        if (ImGui.Checkbox("Hide the game's enemy name + gauge", ref hide)) { config.HideNativeEnemyPlates = hide; changed = true; hider.Refresh(); }
        ImGui.SameLine();
        var dim = config.DimNonTargets;
        if (ImGui.Checkbox("Dim + shrink non-targets", ref dim)) { config.DimNonTargets = dim; changed = true; }
        ImGui.SameLine();
        var needVisible = config.RequireGamePlateVisible;
        if (ImGui.Checkbox("Only where the game's plate is visible", ref needVisible)) { config.RequireGamePlateVisible = needVisible; changed = true; }
        ImGui.SameLine();
        var matchScale = config.MatchGamePlateScale;
        if (ImGui.Checkbox("Scale like the game's plates", ref matchScale)) { config.MatchGamePlateScale = matchScale; changed = true; }

        var cast = config.ShowCastBar;
        if (ImGui.Checkbox("Cast bar", ref cast)) { config.ShowCastBar = cast; changed = true; }
        ImGui.SameLine();
        var dots = config.ShowMyDots;
        if (ImGui.Checkbox("My DoTs", ref dots)) { config.ShowMyDots = dots; changed = true; }
        ImGui.SameLine();
        var expiring = config.DotExpiringSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat("Red below (s)", ref expiring, 0.1f, 0f, 30f)) { config.DotExpiringSeconds = expiring; changed = true; }

        var showAggro = config.ShowAggroColors;
        if (ImGui.Checkbox("Aggro colours", ref showAggro)) { config.ShowAggroColors = showAggro; changed = true; }
        ImGui.SameLine();
        var idle = config.IdleHostilityColor;
        if (ImGui.Checkbox("Out of combat: game hostility colour", ref idle)) { config.IdleHostilityColor = idle; changed = true; }
        ImGui.SameLine();
        var warn = (float)config.ThreatWarnPercent;
        ImGui.SetNextItemWidth(120);
        if (ImGui.DragFloat("Threat warning %", ref warn, 1f, 10f, 99f)) { config.ThreatWarnPercent = (int)warn; changed = true; }

        ImGui.TextUnformatted($"Aggro mode (now {(aggro.TankMode ? "tank" : "DPS/healer")}, my job {names.JobAbbreviation(aggro.LocalJob)}):");
        ImGui.SameLine();
        if (ImGui.RadioButton("Auto by role", config.AggroMode == AggroMode.Auto)) { config.AggroMode = AggroMode.Auto; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Tank", config.AggroMode == AggroMode.Tank)) { config.AggroMode = AggroMode.Tank; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("DPS / healer", config.AggroMode == AggroMode.DpsHealer)) { config.AggroMode = AggroMode.DpsHealer; changed = true; }

        ImGui.TextUnformatted("Position from:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Root X/Y", config.PositionSource == PositionSource.RootNode)) { config.PositionSource = PositionSource.RootNode; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Root screen X/Y", config.PositionSource == PositionSource.RootScreen)) { config.PositionSource = PositionSource.RootScreen; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Number array X/Y", config.PositionSource == PositionSource.NumberArray)) { config.PositionSource = PositionSource.NumberArray; changed = true; }

        var offX = config.OffsetX;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragFloat("Offset X", ref offX, 0.5f, -400f, 400f)) { config.OffsetX = offX; changed = true; }
        ImGui.SameLine();
        var offY = config.OffsetY;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragFloat("Offset Y", ref offY, 0.5f, -400f, 400f)) { config.OffsetY = offY; changed = true; }
        ImGui.SameLine();
        var scale = config.PlateScale;
        ImGui.SetNextItemWidth(160);
        if (ImGui.DragFloat("Plate scale", ref scale, 0.01f, 0.5f, 3f)) { config.PlateScale = scale; changed = true; }

        if (changed) config.Save();
    }

    private void DrawHater()
    {
        var ui = UIState.Instance();
        if (ui == null) return;
        var count = ui->Hater.HaterCount;
        ImGui.TextUnformatted($"HaterCount {count}");
        if (count <= 0) return;
        if (!ImGui.BeginTable("##hater", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("EntityId");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("My enmity");
        ImGui.TableHeadersRow();
        var haters = ui->Hater.Haters;
        for (var i = 0; i < count && i < haters.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{haters[i].EntityId:X8}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NameByEntityId(haters[i].EntityId));
            ImGui.TableNextColumn();
            var e = haters[i].Enmity;
            ImGui.TextColored(e >= 100 ? Bad : e >= 80 ? Warn : Good, $"{e}");
        }
        ImGui.EndTable();
    }

    private void DrawHate()
    {
        var ui = UIState.Instance();
        if (ui == null) return;
        var len = ui->Hate.HateArrayLength;
        ImGui.TextUnformatted($"HateTargetId {ui->Hate.HateTargetId:X8} · entries {len}");
        if (len <= 0) return;
        if (!ImGui.BeginTable("##hate", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit)) return;
        ImGui.TableSetupColumn("EntityId");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("Enmity");
        ImGui.TableHeadersRow();
        var list = ui->Hate.HateInfo;
        for (var i = 0; i < len && i < list.Length; i++)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{list[i].EntityId:X8}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NameByEntityId(list[i].EntityId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{list[i].Enmity}");
        }
        ImGui.EndTable();
    }

    private void DrawPlates()
    {
        ImGui.TextUnformatted($"Plates this frame: {scanner.Count}");
        const ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##plates", 16, flags)) return;
        ImGui.TableSetupColumn("Slot");
        ImGui.TableSetupColumn("NP# / Arr#");
        ImGui.TableSetupColumn("Kind");
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("HP");
        ImGui.TableSetupColumn("Game X,Y · scale · depth");
        ImGui.TableSetupColumn("Root X,Y");
        ImGui.TableSetupColumn("Root screen X,Y · scale");
        ImGui.TableSetupColumn("Vis");
        ImGui.TableSetupColumn("T/F");
        ImGui.TableSetupColumn("Cast");
        ImGui.TableSetupColumn("My enmity");
        ImGui.TableSetupColumn("Enemy's target");
        ImGui.TableSetupColumn("My statuses");
        ImGui.TableSetupColumn("Aggro");
        ImGui.TableSetupColumn("Game name");
        ImGui.TableHeadersRow();

        for (var i = 0; i < scanner.Count; i++)
        {
            var d = scanner.Plates[i];
            if (!d.Valid) continue;
            if (!allKinds && d.Kind != UIObjectKind.BattleNpcEnemy) continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{d.Slot}");
            ImGui.TableNextColumn();
            if (d.NamePlateIndex == d.ArrayNamePlateIndex)
                ImGui.TextUnformatted($"{d.NamePlateIndex}");
            else
                ImGui.TextColored(Bad, $"{d.NamePlateIndex} / {d.ArrayNamePlateIndex}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{d.Kind} ({d.ObjectKind}/{d.SubKind})");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(NameByGameObjectId(d.GameObjectId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(d.HpMax > 0 ? $"{d.HpPercent:F1}%" : "-");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{d.GameX},{d.GameY} · {d.GameScale} · {d.GameDepth:F2}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{d.RootX:F0},{d.RootY:F0}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{d.RootScreenX:F0},{d.RootScreenY:F0} · {d.RootScale:F2}");
            ImGui.TableNextColumn();
            ImGui.TextColored(d.RootVisible ? Good : Muted, d.RootVisible ? "yes" : "no");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{(d.IsTarget ? "T" : "")}{(d.IsFocus ? "F" : "")}");
            ImGui.TableNextColumn();
            if (d.IsCasting)
                ImGui.TextColored(d.CastInterruptible ? Warn : Muted,
                    $"{names.Action(d.CastActionId)} {d.CastCurrent:F1}/{d.CastTotal:F1}s {(d.CastInterruptible ? "INTERRUPTIBLE" : "lock")} (type {d.CastActionType})");
            else
                ImGui.TextUnformatted("-");
            ImGui.TableNextColumn();
            if (d.MyEnmity < 0)
                ImGui.TextColored(Muted, "not on list");
            else
                ImGui.TextColored(d.MyEnmity >= 100 ? Bad : d.MyEnmity >= 80 ? Warn : Good, $"{d.MyEnmity}%");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(DescribeEnemyTarget(d.EnemyTargetId));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(DescribeStatuses(d));
            ImGui.TableNextColumn();
            var a = aggro.Resolve(d, config.ThreatWarnPercent);
            ImGui.TextUnformatted($"{a.State}{(a.HolderJob != 0 ? " " + names.JobAbbreviation(a.HolderJob) : "")}{(a.Percent >= 0 ? $" {a.Percent}%" : "")} · tag {d.TagType}{(a.Untagged ? " UNTAGGED" : "")}{(a.Shield != 0 ? $" shield {a.Shield}" : "")}");
            ImGui.TableNextColumn();
            var nameState = hider.DescribeName((AddonNamePlate*)scanner.NamePlateAddon, d.ArrayNamePlateIndex, d.Slot);
            if (nameState.StartsWith("HIDDEN")) ImGui.TextColored(Bad, nameState);
            else ImGui.TextUnformatted(nameState);
        }

        ImGui.EndTable();
    }

    private string DescribeEnemyTarget(ulong id)
    {
        if (id is 0 or 0xE0000000) return "-";
        var go = PlateScanner.FindObject(id);
        if (go == null) return $"{id:X} (not found)";
        var name = go->NameString;
        if (go->EntityId == scanner.LocalEntityId) return $"ME ({name})";
        if (go->ObjectKind == ObjectKind.Pc)
        {
            var job = ((CSCharacter*)go)->CharacterData.ClassJob;
            var role = names.JobRole(job) switch { 1 => "tank", 4 => "healer", 2 or 3 => "dps", _ => "?" };
            return $"{name} · {names.JobAbbreviation(job)} ({role})";
        }
        return name;
    }

    private string DescribeStatuses(PlateData d)
    {
        if (d.MyStatusCount == 0) return "-";
        sb.Clear();
        for (var s = 0; s < d.MyStatusCount; s++)
        {
            if (s > 0) sb.Append(" · ");
            sb.Append(names.Status(d.MyStatusIds[s])).Append(' ').Append(d.MyStatusRemaining[s].ToString("F1")).Append('s');
        }
        return sb.ToString();
    }

    private static string NameByGameObjectId(ulong id)
    {
        var go = PlateScanner.FindObject(id);
        return go == null ? "?" : go->NameString;
    }

    private static string NameByEntityId(uint entityId)
    {
        if (entityId is 0 or 0xE0000000) return "-";
        var mgr = GameObjectManager.Instance();
        if (mgr == null) return "?";
        CSGameObject* go = mgr->Objects.GetObjectByEntityId(entityId);
        return go == null ? "?" : go->NameString;
    }
}
