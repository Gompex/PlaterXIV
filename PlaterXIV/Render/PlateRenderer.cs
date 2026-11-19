using System;
using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Enums;
using KamiToolKit.UiOverlay;
using PlaterXIV.Game;
using PlaterXIV.Util;

namespace PlaterXIV.Render;

/// <summary>Full-screen container on KamiToolKit's overlay layer just above the game's nameplates.</summary>
public sealed class PlateLayer : OverlayNode
{
    public override OverlayLayer OverlayLayer => OverlayLayer.BehindUserInterface;

    protected override void OnUpdate() { }
}

/// <summary>
/// Keeps one <see cref="PlateNode"/> per game nameplate slot (max 50), creates them lazily a few per frame,
/// and positions them right after the scanner reads the game's plates, before the game draws.
/// </summary>
public sealed unsafe class PlateRenderer : IDisposable
{
    private const int MaxBuildsPerFrame = 3;
    private const double BuildBudgetMicroseconds = 1500;

    private readonly Configuration config;
    private readonly PlateScanner scanner;
    private readonly PerfMeter perf;
    private readonly NativePlateHider hider;
    private readonly NameCache names;
    private readonly AggroResolver aggro;
    private readonly ClickCaster caster;
    private readonly OverlayController overlay;
    private readonly PlateLayer layer;
    private readonly PlateNode?[] pool = new PlateNode?[PlateScanner.MaxPlates];
    private long stamp;
    private Skin skin = Skin.Signal;
    private int skinRevision = -1;
    private uint lastJob;
    private PlateNode? onTop; // the target's plate, moved to the end of the layer so it draws over the others

    // Rule match cache per game plate slot: recomputed only when the enemy or the rules change.
    private readonly ulong[] ruleObject = new ulong[PlateScanner.MaxPlates];
    private readonly int[] ruleRevision = new int[PlateScanner.MaxPlates];
    private readonly int[] ruleIndex = new int[PlateScanner.MaxPlates];

    public int Built { get; private set; }
    public int ShownLastFrame { get; private set; }
    public double LastRenderMicroseconds { get; private set; }

    /// <summary>Transform of the overlay addon our layer lives in (for the debug probe).</summary>
    public Vector2 OverlayPosition { get; private set; }
    public float OverlayScale { get; private set; } = 1f;

    /// <summary>Must be constructed on the framework thread.</summary>
    public PlateRenderer(Configuration config, PlateScanner scanner, PerfMeter perf, NativePlateHider hider, NameCache names, AggroResolver aggro)
    {
        this.names = names;
        this.aggro = aggro;
        caster = new ClickCaster(names);
        this.config = config;
        this.scanner = scanner;
        this.perf = perf;
        this.hider = hider;

        overlay = new OverlayController();
        layer = new PlateLayer { Position = Vector2.Zero };
        overlay.AddNode(layer);

        scanner.AfterScan += Update;
    }

    private void Update()
    {
        var start = Stopwatch.GetTimestamp();
        stamp++;
        var shown = 0;

        SwitchProfileOnJobChange();

        if (config.SkinRevision != skinRevision)
        {
            skinRevision = config.SkinRevision;
            skin = config.EffectiveSkin();
            foreach (var built in pool)
                built?.ApplySkin(skin);
        }

        // Our layer sits under the overlay addon's root node; convert screen pixels into its space.
        var overlayRoot = ((AtkResNode*)layer)->ParentNode;
        if (overlayRoot != null)
        {
            OverlayPosition = new Vector2(overlayRoot->X, overlayRoot->Y);
            OverlayScale = overlayRoot->ScaleX > 0f ? overlayRoot->ScaleX : 1f;
        }

        var addon = (AddonNamePlate*)scanner.NamePlateAddon;
        var hideNative = config.HideNativeEnemyPlates && addon != null;
        hider.BeginFrame(addon);
        var gameScale = config.MatchGamePlateScale ? scanner.NamePlateRootScale.X / OverlayScale : 1f / OverlayScale;

        if (config.Enabled && !scanner.InPvP)
        {
            aggro.BeginFrame(scanner.LocalEntityId, config.AggroMode);
            var dimOthers = config.DimNonTargets && scanner.TargetOnScreen;
            var builds = 0;

            for (var i = 0; i < scanner.Count; i++)
            {
                var d = scanner.Plates[i];
                if (!d.Valid || d.Kind != UIObjectKind.BattleNpcEnemy) continue;
                if (config.RequireGamePlateVisible && !d.RootVisible) continue;

                var index = d.NamePlateIndex;
                if (index < 0 || index >= pool.Length) continue;

                var rule = RuleFor(d, index);
                if (rule.Hide || IsFarBelowMe(d))
                {
                    // Hidden by a rule: no plate of ours, and the game's name stays hidden too.
                    if (hideNative) hider.HideOn(addon, index);
                    continue;
                }

                var plate = pool[index];
                if (plate == null)
                {
                    // Spread node creation over frames: a few plates per frame, never a big burst.
                    if (builds >= MaxBuildsPerFrame) continue;
                    if (Stopwatch.GetElapsedTime(start).TotalMicroseconds > BuildBudgetMicroseconds) continue;
                    plate = new PlateNode(skin, names);
                    plate.AttachNode(layer);
                    pool[index] = plate;
                    Built++;
                    builds++;
                }

                var scale = config.PlateScale * gameScale * rule.Scale;
                var alpha = 1f;
                if (d.IsTarget)
                {
                    scale *= config.TargetScale;
                }
                else if (dimOthers && !d.IsFocus && !rule.NeverDim)
                {
                    scale *= config.NonTargetScale;
                    alpha = config.NonTargetAlpha;
                }

                var useAggro = config.ShowAggroColors && !rule.NoAggroColors;
                var threat = useAggro ? aggro.Resolve(d, config.ThreatWarnPercent) : default;
                var idleColor = threat.State == AggroState.Idle && config.IdleHostilityColor && !rule.NoAggroColors
                    ? AggroResolver.GameHostilityColor(d) : 0u;

                plate.Apply(d, Anchor(d), scale, alpha, d.IsTarget, threat, idleColor, scanner.LocalLevel, rule, config);

                // Target on top: re-link its plate as the last child once, when the target changes.
                if (d.IsTarget && !ReferenceEquals(plate, onTop))
                {
                    plate.DetachNode();
                    plate.AttachNode(layer);
                    onTop = plate;
                }
                plate.SeenFrame = stamp;
                shown++;

                if (hideNative)
                    hider.HideOn(addon, index);
            }
        }

        for (var i = 0; i < pool.Length; i++)
        {
            var plate = pool[i];
            if (plate != null && plate.SeenFrame != stamp)
                plate.Hide();
        }

        if (config.Enabled && !scanner.InPvP && config.MiddleClickInterrupt)
            HandleMiddleClick();

        // Give the game back every plate slot that is no longer one of our enemies (players, NPCs...).
        hider.EndFrame(addon);

        ShownLastFrame = shown;
        LastRenderMicroseconds = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        perf.RecordRender(LastRenderMicroseconds, shown, Built);
    }

    /// <summary>
    /// Bottom centre of the game's plate, converted from the NamePlate addon's space to screen pixels
    /// and then into our overlay layer's space, plus the user offset.
    /// </summary>
    private Vector2 Anchor(PlateData d)
    {
        var sx = d.RootScale > 0f ? d.RootScale : 1f;
        var sy = d.RootScaleY > 0f ? d.RootScaleY : 1f;
        var bottomCentre = new Vector2(d.RootWidth * sx / 2f, d.RootHeight * sy);
        var npPos = scanner.NamePlateRootPosition;
        var npScale = scanner.NamePlateRootScale;

        Vector2 screen;
        switch (config.PositionSource)
        {
            case PositionSource.RootScreen:
                screen = new Vector2(d.RootScreenX, d.RootScreenY) + bottomCentre * npScale;
                break;
            case PositionSource.NumberArray:
                screen = npPos + (new Vector2(d.GameX, d.GameY) + bottomCentre) * npScale;
                break;
            default:
                screen = npPos + (new Vector2(d.RootX, d.RootY) + bottomCentre) * npScale;
                break;
        }

        var local = (screen - OverlayPosition) / OverlayScale;
        return new Vector2(local.X + config.OffsetX, local.Y + config.OffsetY);
    }

    /// <summary>Middle-click on a plate → one interrupt on that enemy. The target's plate wins when plates overlap.</summary>
    private void HandleMiddleClick()
    {
        if (!ClickCaster.MiddlePressed(out var cursor) || ClickCaster.CursorOverOtherUi()) return;

        var point = (cursor - OverlayPosition) / OverlayScale;
        PlateNode? best = null;
        foreach (var plate in pool)
        {
            if (plate == null || plate.SeenFrame != stamp || !plate.HitTest(point)) continue;
            if (best == null || plate.IsTargetStyle || (!best.IsTargetStyle && plate.CurrentScale > best.CurrentScale))
                best = plate;
        }

        if (best != null)
            caster.CastInterrupt(best.ObjectId, aggro.LocalJob);
    }

    public string LastClickResult => caster.LastResult;

    /// <summary>Auto-switches the profile when the player changes job (role mapping + per-job exceptions).</summary>
    private void SwitchProfileOnJobChange()
    {
        var control = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.Instance();
        var player = control != null ? control->LocalPlayer : null;
        if (player == null) return;

        var job = (uint)((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)player)->CharacterData.ClassJob;
        if (job == 0 || job == lastJob) return;
        lastJob = job;

        if (!config.AutoSwitchProfiles) return;
        var wanted = config.ProfileFor(job, names.RoleOf(job));
        if (!string.Equals(wanted, config.Active.Name, StringComparison.OrdinalIgnoreCase))
        {
            config.SetActive(wanted);
            hider.Refresh();
            Services.Log.Information($"[Plater] profile {config.Active.Name} for {names.JobAbbreviation(job)}");
        }
    }

    /// <summary>The first enabled rule matching this enemy's name (cached per plate slot).</summary>
    private RuleEffect RuleFor(PlateData d, int index)
    {
        if (config.Rules.Count == 0) return RuleEffect.None;

        if (ruleObject[index] != d.GameObjectId || ruleRevision[index] != config.RulesRevision + 1)
        {
            ruleObject[index] = d.GameObjectId;
            ruleRevision[index] = config.RulesRevision + 1; // +1 so a fresh slot (0) never counts as cached
            ruleIndex[index] = -1;

            var go = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)d.GameObject;
            if (go != null)
            {
                var enemyName = go->NameString; // one string per enemy per slot, not per frame
                for (var r = 0; r < config.Rules.Count; r++)
                {
                    if (!config.Rules[r].Matches(enemyName)) continue;
                    ruleIndex[index] = r;
                    break;
                }
            }
        }

        var found = ruleIndex[index];
        return found >= 0 && found < config.Rules.Count ? RuleEffect.From(config.Rules[found], found) : RuleEffect.None;
    }

    private bool IsFarBelowMe(PlateData d)
        => config.HideFarBelowMyLevel && d.Level > 0 && scanner.LocalLevel > 0
           && scanner.LocalLevel - d.Level >= config.FarBelowLevels;

    /// <summary>Stops updates and hides every plate (framework thread). Used before unloading.</summary>
    public void Stop()
    {
        scanner.AfterScan -= Update;
        foreach (var plate in pool)
            plate?.Hide();
    }

    /// <summary>True while any DoT icon is still loading; we never free nodes in that state.</summary>
    public bool AnyIconLoading()
    {
        foreach (var plate in pool)
        {
            if (plate != null && plate.AnyIconLoading())
                return true;
        }

        return false;
    }

    /// <summary>Must be called on the framework thread.</summary>
    public void Dispose()
    {
        scanner.AfterScan -= Update;
        onTop = null;
        Array.Clear(pool);
        overlay.Dispose(); // disposes the layer and every plate under it
    }
}
