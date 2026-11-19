using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.BaseTypes;
using KamiToolKit.Nodes;
using PlaterXIV.Game;
using PlaterXIV.Render;
using PlaterXIV.Util;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace PlaterXIV.UI;

/// <summary>
/// Native settings window, layout "A · Tabs + live preview". Plates / Aggro / Cast &amp; DoTs / Skin edit
/// the profile in use, with a live preview on the right. Rules (shared) and Profiles use the full width.
/// Every change applies immediately; the config file is written about a second after the last change.
/// </summary>
public sealed unsafe class SettingsWindow : NativeAddon
{
    private const float PreviewWidth = 280f;
    private const float RowWidth = 440f;
    private const int RuleRows = 10;
    private const int ProfileRows = 8;
    private const int JobRows = 5;
    private const int TabCount = 6;

    private static readonly string[] RoleLabels = ["Tank", "Healer", "Melee DPS", "Physical ranged", "Magical ranged"];
    private static readonly PlaterRole[] Roles = [PlaterRole.Tank, PlaterRole.Healer, PlaterRole.Melee, PlaterRole.PhysicalRanged, PlaterRole.MagicalRanged];
    private static readonly string[] PreviewNames = ["Ironclad Warden", "Voidsent Hound", "Ice Sprite"];
    private static readonly int[] PreviewLevelOffsets = [0, -7, 4];

    private readonly Configuration config;
    private readonly NameCache names;
    private readonly NativePlateHider hider;

    // ---- built in OnSetup each time the window opens (the game frees the nodes when it closes)
    private readonly List<ResNode> tabPages = [];
    private readonly List<Action> refreshers = [];
    private readonly List<ColorRow> colorRows = [];
    private ResNode? previewPanel;
    private PlateNode?[] previewPlates = [];
    private RuleRow[] ruleRows = [];
    private RuleEditor? editor;
    private ProfileControls? profiles;
    private int previewSkinRevision = -1;
    private int seenActiveRevision = -1;
    private bool suppress;
    private float y;

    private int selectedRule = -1;
    private int ruleScroll;     // first rule shown in the list
    private int profileScroll;  // first profile shown in the list
    private TextNode? ruleScrollLabel;
    private float ruleListWidth;

    private readonly PlateData[] previewData;
    private readonly AggroResult[] previewAggro;
    private readonly Vector2[] previewAnchors = new Vector2[3];
    private readonly PlateData rulePreviewData = new() { Valid = true, Hp = 72, HpMax = 100, GameObjectId = 1 };
    private Vector2 rulePreviewAnchor;

    private sealed record ColorRow(ColorEditNode Node, Func<Skin, Vector4> BaseColor, Func<SkinOverrides, Vector4?> Get, Action<SkinOverrides, Vector4?> Set);
    private sealed record RuleRow(CheckboxNode On, TextButtonNode Name, TextNode Summary, TextButtonNode Up, TextButtonNode Delete);

    private sealed class RuleEditor
    {
        public required ResNode Controls;
        public required TextNode Empty;
        public required RadioButtonGroupNode Match;
        public required TextInputNode Text;
        public required CheckboxNode Highlight;
        public required ColorEditNode Color;
        public required TextInputNode Tag;
        public required CheckboxNode Hide;
        public required CheckboxNode ChangeSize;
        public required SliderNode Size;
        public required CheckboxNode NeverDim;
        public required CheckboxNode NoAggro;
        public required PlateNode Preview;
    }

    private sealed class ProfileControls
    {
        public required TextButtonNode[] List;
        public required TextInputNode Name;
        public required TextButtonNode[] RoleButtons;
        public required TextNode[] JobLabels;
        public required TextButtonNode[] JobButtons;
        public required TextButtonNode[] JobRemove;
        public required CheckboxNode AutoSwitch;
    }

    public SettingsWindow(Configuration config, NameCache names, NativePlateHider hider)
    {
        this.config = config;
        this.names = names;
        this.hider = hider;

        previewData =
        [
            new PlateData { Valid = true, Hp = 64, HpMax = 100, IsTarget = true, IsCasting = true, CastInterruptible = true, CastCurrent = 1.8f, CastTotal = 4.2f, PreviewName = PreviewNames[0], PreviewCastName = "Crushing Tide" },
            new PlateData { Valid = true, Hp = 88, HpMax = 100, PreviewName = PreviewNames[1] },
            new PlateData { Valid = true, Hp = 31, HpMax = 100, IsCasting = true, CastInterruptible = false, CastCurrent = 2.9f, CastTotal = 3.8f, PreviewName = PreviewNames[2], PreviewCastName = "Rending Howl" },
        ];
        previewAggro =
        [
            new AggroResult { State = AggroState.Have, Percent = -1, Shield = 1 },
            new AggroResult { State = AggroState.CoTank, HolderJob = 19, Percent = -1, Untagged = true }, // PLD, nobody has hit it yet
            new AggroResult { State = AggroState.Lost, HolderJob = 24, Percent = -1 },   // WHM
        ];
    }

    // ================================================================ setup

    protected override void OnSetup(AtkUnitBase* addon, Span<AtkValue> atkValueSpan)
    {
        tabPages.Clear();
        refreshers.Clear();
        colorRows.Clear();
        previewSkinRevision = -1;
        seenActiveRevision = config.ActiveRevision;
        if (selectedRule >= config.Rules.Count) selectedRule = config.Rules.Count - 1;

        var start = ContentStartPosition;
        var size = ContentSize;

        // Build every subtree first and attach each one once, so the game rebuilds its node lists a
        // handful of times instead of once per control.
        var tabs = new TabBarNode { Position = start, Size = new Vector2(size.X, 24f) };

        var pageTop = start.Y + 34f;
        var pageHeight = size.Y - 34f;
        for (var i = 0; i < TabCount; i++)
        {
            var width = i < 4 ? RowWidth : size.X - 8f;
            tabPages.Add(new ResNode { Position = new Vector2(start.X + 4f, pageTop), Size = new Vector2(width, pageHeight), IsVisible = i == 0 });
        }

        tabs.AddTab("Plates", () => ShowTab(0));
        tabs.AddTab("Aggro", () => ShowTab(1));
        tabs.AddTab("Cast & DoTs", () => ShowTab(2));
        tabs.AddTab("Skin", () => ShowTab(3));
        tabs.AddTab("Rules", () => ShowTab(4));
        tabs.AddTab("Profiles", () => ShowTab(5));

        BuildPlatesTab(tabPages[0]);
        BuildAggroTab(tabPages[1]);
        BuildCastTab(tabPages[2]);
        BuildSkinTab(tabPages[3]);
        BuildRulesTab(tabPages[4], size.X - 8f, pageHeight);
        BuildProfilesTab(tabPages[5], size.X - 8f);
        previewPanel = BuildPreview(new Vector2(start.X + size.X - PreviewWidth, pageTop), new Vector2(PreviewWidth, pageHeight));

        AddNode(tabs);
        foreach (var page in tabPages)
            AddNode(page);
        AddNode(previewPanel);

        RefreshAll();
    }

    private void ShowTab(int index)
    {
        for (var i = 0; i < tabPages.Count; i++)
            tabPages[i].IsVisible = i == index;
        if (previewPanel != null) previewPanel.IsVisible = index < 4;
    }

    /// <summary>Puts every control back in sync with the config (after a profile switch, an add, a delete...).</summary>
    private void RefreshAll()
    {
        suppress = true;
        try
        {
            foreach (var refresh in refreshers) refresh();
            RefreshColorRows();
            RefreshRuleList();
            RefreshEditor();
            RefreshProfiles();
        }
        finally
        {
            suppress = false;
        }

        Subtitle = $"Profile: {config.Active.Name}";
    }

    // ================================================================ profile tabs

    private void BuildPlatesTab(ResNode page)
    {
        y = 0f;
        Header(page, "Plates");
        Check(page, "Plates on (all profiles)", () => config.Enabled, v => { config.Enabled = v; hider.Refresh(); });
        Check(page, "Hide the game's enemy name and HP gauge", () => config.HideNativeEnemyPlates, v => { config.HideNativeEnemyPlates = v; hider.Refresh(); });
        Check(page, "Show level before the name (coloured by difficulty)", () => config.ShowLevel, v => config.ShowLevel = v);
        Slider(page, "Plate size %", 50, 200, () => Percent(config.PlateScale), v => config.PlateScale = v / 100f);

        Header(page, "Target emphasis");
        Check(page, "Dim and shrink non-targets (focus stays full)", () => config.DimNonTargets, v => config.DimNonTargets = v);
        Slider(page, "Non-target opacity %", 10, 100, () => Percent(config.NonTargetAlpha), v => config.NonTargetAlpha = v / 100f);
        Slider(page, "Non-target size %", 50, 100, () => Percent(config.NonTargetScale), v => config.NonTargetScale = v / 100f);
        Slider(page, "Target size %", 100, 150, () => Percent(config.TargetScale), v => config.TargetScale = v / 100f);

        Header(page, "Position (all profiles)");
        Number(page, "Offset X", -400, 400, () => (int)MathF.Round(config.OffsetX), v => config.OffsetX = v);
        Number(page, "Offset Y", -400, 400, () => (int)MathF.Round(config.OffsetY), v => config.OffsetY = v);
        Radio(page, ["Root screen X/Y (recommended)", "Root X/Y", "Number array X/Y"],
            () => config.PositionSource switch { PositionSource.RootNode => 1, PositionSource.NumberArray => 2, _ => 0 },
            i => config.PositionSource = i switch { 1 => PositionSource.RootNode, 2 => PositionSource.NumberArray, _ => PositionSource.RootScreen });
    }

    private void BuildAggroTab(ResNode page)
    {
        y = 0f;
        Header(page, "Aggro colours");
        Check(page, "Colour the HP bar by aggro", () => config.ShowAggroColors, v => config.ShowAggroColors = v);
        Check(page, "Before the pull, use the game's hostility colour", () => config.IdleHostilityColor, v => config.IdleHostilityColor = v);

        Header(page, "Mode");
        Radio(page, ["Automatic, by my job's role", "Always tank", "Always DPS / healer"], () => (int)config.AggroMode, i => config.AggroMode = (AggroMode)i);

        Header(page, "DPS / healer");
        Slider(page, "Threat warning from %", 50, 99, () => config.ThreatWarnPercent, v => config.ThreatWarnPercent = v);

        Header(page, "Who has hit it");
        Check(page, "Shield after the name once a tank holds it and it's been hit", () => config.SecuredShield, v => config.SecuredShield = v);
        Check(page, "Pale bar while nobody has hit it yet (light-red in game)", () => config.PaleUntagged, v => config.PaleUntagged = v);

        Header(page, "Click");
        Check(page, "Middle-click a plate: cast my interrupt on it", () => config.MiddleClickInterrupt, v => config.MiddleClickInterrupt = v);
        var note = new TextNode
        {
            Position = new Vector2(30f, y),
            Size = new Vector2(RowWidth - 30f, 36f),
            FontSize = 12,
            AlignmentType = AlignmentType.TopLeft,
            TextFlags = TextFlags.WordWrap | TextFlags.MultiLine,
            String = "Tanks: Interject. Physical ranged: Head Graze. Other jobs: nothing. One click = one cast, like pressing the hotbar button.",
        };
        note.AttachNode(page);
        y += 40f;
    }

    private void BuildCastTab(ResNode page)
    {
        y = 0f;
        Header(page, "Cast bar");
        Check(page, "Show enemy cast bars", () => config.ShowCastBar, v => config.ShowCastBar = v);

        Header(page, "My DoTs");
        Check(page, "Show my DoTs on enemies", () => config.ShowMyDots, v => config.ShowMyDots = v);
        Slider(page, "Most icons per plate", 0, PlateNode.MaxDots, () => config.MaxDots, v => config.MaxDots = v);
        Slider(page, "Timer turns red below (s)", 0, 15, () => (int)MathF.Round(config.DotExpiringSeconds), v => config.DotExpiringSeconds = v);
    }

    private void BuildSkinTab(ResNode page)
    {
        y = 0f;
        Header(page, "Skin");
        var skinNames = Skin.Names;
        Radio(page, skinNames, () => Math.Max(0, Array.IndexOf(skinNames, Skin.Get(config.SkinName).Name)), i =>
        {
            config.SkinName = skinNames[i];
            config.SkinChanged();
            RefreshColorRows();
        });

        Header(page, "Colour overrides for this skin");
        var top = y;
        ColorRowAt(page, 0, top, "HP (calm)", s => s.Hp, o => o.Hp, (o, c) => o.Hp = c);
        ColorRowAt(page, 0, top + 28f, "You hold aggro", s => s.AggroHave, o => o.Have, (o, c) => o.Have = c);
        ColorRowAt(page, 0, top + 56f, "Co-tank has it", s => s.AggroCoTank, o => o.CoTank, (o, c) => o.CoTank = c);
        ColorRowAt(page, 0, top + 84f, "Someone else / pulled", s => s.AggroLost, o => o.Lost, (o, c) => o.Lost = c);
        ColorRowAt(page, 220f, top, "Threat warning", s => s.ThreatWarn, o => o.Warn, (o, c) => o.Warn = c);
        ColorRowAt(page, 220f, top + 28f, "Interruptible cast", s => s.CastInterruptible, o => o.CastInterruptible, (o, c) => o.CastInterruptible = c);
        ColorRowAt(page, 220f, top + 56f, "Uninterruptible cast", s => s.CastLocked, o => o.CastLocked, (o, c) => o.CastLocked = c);
        ColorRowAt(page, 220f, top + 84f, "Expiring DoT", s => s.Expiring, o => o.Expiring, (o, c) => o.Expiring = c);
        y = top + 120f;

        Button(page, "Reset colours", 8f, y, 180f, () =>
        {
            config.ColourOverrides.Remove(Skin.Get(config.SkinName).Name);
            config.SkinChanged();
            config.MarkDirty();
            RefreshColorRows();
        });
    }

    private void ColorRowAt(ResNode page, float x, float rowY, string label,
        Func<Skin, Vector4> baseColor, Func<SkinOverrides, Vector4?> get, Action<SkinOverrides, Vector4?> set)
    {
        var node = new ColorEditNode { Position = new Vector2(x, rowY), Size = new Vector2(210f, 24f), String = label };
        node.AttachNode(page);
        var row = new ColorRow(node, baseColor, get, set);
        colorRows.Add(row);

        Vector4? before = null;
        node.OnColorPreviewed = color =>
        {
            var o = config.OverridesFor(Skin.Get(config.SkinName).Name);
            before ??= get(o) ?? new Vector4(-1f);
            set(o, color);
            config.SkinChanged();
        };
        node.OnColorConfirmed = color =>
        {
            set(config.OverridesFor(Skin.Get(config.SkinName).Name), color);
            before = null;
            config.SkinChanged();
            config.MarkDirty();
        };
        node.OnColorCancelled = () =>
        {
            if (before.HasValue)
                set(config.OverridesFor(Skin.Get(config.SkinName).Name), before.Value.X < 0f ? null : before.Value);
            before = null;
            config.SkinChanged();
        };
    }

    private void RefreshColorRows()
    {
        var baseSkin = Skin.Get(config.SkinName);
        foreach (var row in colorRows)
        {
            var current = config.ColourOverrides.TryGetValue(baseSkin.Name, out var o) ? row.Get(o) : null;
            row.Node.DefaultColor = row.BaseColor(baseSkin);
            row.Node.CurrentColor = current ?? row.BaseColor(baseSkin);
        }
    }

    // ================================================================ rules tab

    private void BuildRulesTab(ResNode page, float width, float height)
    {
        var listWidth = width - PreviewWidth - 12f;
        y = 0f;
        Header(page, "Rules · first match wins · shared by every profile");

        ruleListWidth = listWidth;
        ruleRows = new RuleRow[RuleRows];
        for (var i = 0; i < RuleRows; i++)
        {
            var row = i; // the rule shown in this row is ruleScroll + row
            var rowY = y + i * 26f;
            var on = new CheckboxNode { Position = new Vector2(8f, rowY + 2f), Size = new Vector2(22f, 20f), String = "" };
            on.OnClick = v =>
            {
                var index = ruleScroll + row;
                if (suppress || index >= config.Rules.Count) return;
                config.Rules[index].Enabled = v;
                RulesEdited();
            };
            on.AttachNode(page);

            var name = Button(page, "", 34f, rowY, 190f, () => SelectRule(ruleScroll + row));
            var summary = new TextNode { Position = new Vector2(230f, rowY + 2f), Size = new Vector2(listWidth - 300f, 20f), FontSize = 12, AlignmentType = AlignmentType.Left, TextFlags = TextFlags.Ellipsis };
            summary.AttachNode(page);
            var up = Button(page, "Up", listWidth - 62f, rowY, 30f, () => MoveRuleUp(ruleScroll + row));
            var delete = Button(page, "X", listWidth - 30f, rowY, 28f, () => DeleteRule(ruleScroll + row));
            ruleRows[i] = new RuleRow(on, name, summary, up, delete);
        }

        y += RuleRows * 26f + 8f;
        Button(page, "+ Rule for my target", 8f, y, 180f, AddRuleForTarget);
        Button(page, "+ Empty rule", 196f, y, 130f, () => AddRule(string.Empty));

        // Scrolling: mouse wheel over the list, or these two buttons.
        ruleScrollLabel = new TextNode { Position = new Vector2(listWidth - 132f, y + 3f), Size = new Vector2(70f, 20f), FontSize = 12, AlignmentType = AlignmentType.Right };
        ruleScrollLabel.AttachNode(page);
        Button(page, "<", listWidth - 58f, y, 28f, () => ScrollRules(-RuleRows));
        Button(page, ">", listWidth - 28f, y, 28f, () => ScrollRules(RuleRows));
        y += 34f;

        Check(page, "Hide enemies 10 or more levels below me (built-in)", () => config.HideFarBelowMyLevel, v => { config.HideFarBelowMyLevel = v; config.RulesChanged(); });

        editor = BuildRuleEditor(page, new Vector2(width - PreviewWidth, 0f), new Vector2(PreviewWidth, height));
    }

    private RuleEditor BuildRuleEditor(ResNode page, Vector2 position, Vector2 size)
    {
        var panel = new ResNode { Position = position, Size = size };
        var background = new ColorImageNode { Position = Vector2.Zero, Size = size, Color = Skin.Hex(0x15181A, 0.95f) };
        background.AttachNode(panel);

        var title = new TextNode { Position = new Vector2(8f, 6f), Size = new Vector2(size.X - 16f, 20f), FontSize = 14, TextColor = Skin.Hex(0xE69F00), AlignmentType = AlignmentType.Left, String = "Edit selected rule" };
        title.AttachNode(panel);

        var empty = new TextNode { Position = new Vector2(8f, 34f), Size = new Vector2(size.X - 16f, 40f), FontSize = 13, AlignmentType = AlignmentType.TopLeft, TextFlags = TextFlags.WordWrap | TextFlags.MultiLine, String = "Pick a rule on the left, or add one for your target." };
        empty.AttachNode(panel);

        var controls = new ResNode { Position = Vector2.Zero, Size = size };

        var match = new RadioButtonGroupNode { Position = new Vector2(8f, 32f), Size = new Vector2(size.X - 16f, 36f) };
        match.AttachNode(controls);
        match.AddButton("Name is", () => EditRule(r => r.Match = NameMatch.Is));
        match.AddButton("Name contains", () => EditRule(r => r.Match = NameMatch.Contains));

        var nameLabel = new TextNode { Position = new Vector2(8f, 74f), Size = new Vector2(120f, 18f), FontSize = 12, AlignmentType = AlignmentType.Left, String = "Enemy name" };
        nameLabel.AttachNode(controls);
        var text = new TextInputNode { Position = new Vector2(8f, 92f), Size = new Vector2(size.X - 16f, 28f), MaxCharacters = 40 };
        text.OnInputReceived = s => EditRule(r => r.Text = s.ExtractText().Trim());
        text.AttachNode(controls);

        var highlight = new CheckboxNode { Position = new Vector2(8f, 128f), Size = new Vector2(100f, 20f), String = "Highlight" };
        highlight.OnClick = v => EditRule(r => r.Highlight = v);
        highlight.AttachNode(controls);
        var color = new ColorEditNode { Position = new Vector2(108f, 126f), Size = new Vector2(40f, 24f), String = "" };
        color.OnColorPreviewed = c => EditRule(r => r.HighlightColor = c, save: false);
        color.OnColorConfirmed = c => EditRule(r => r.HighlightColor = c);
        color.AttachNode(controls);
        var tagLabel = new TextNode { Position = new Vector2(154f, 128f), Size = new Vector2(30f, 20f), FontSize = 12, AlignmentType = AlignmentType.Left, String = "Tag" };
        tagLabel.AttachNode(controls);
        var tag = new TextInputNode { Position = new Vector2(186f, 124f), Size = new Vector2(size.X - 194f, 28f), MaxCharacters = 6 };
        tag.OnInputReceived = s => EditRule(r => r.Tag = s.ExtractText().Trim());
        tag.AttachNode(controls);

        var hide = new CheckboxNode { Position = new Vector2(8f, 158f), Size = new Vector2(size.X - 16f, 20f), String = "Hide (ours and the game's name)" };
        hide.OnClick = v => EditRule(r => r.Hide = v);
        hide.AttachNode(controls);

        var changeSize = new CheckboxNode { Position = new Vector2(8f, 184f), Size = new Vector2(80f, 20f), String = "Size %" };
        changeSize.OnClick = v => EditRule(r => r.ChangeSize = v);
        changeSize.AttachNode(controls);
        var sizeSlider = new SliderNode { Position = new Vector2(90f, 184f), Size = new Vector2(size.X - 98f, 20f), Range = 50..200, Step = 5, Value = 120 };
        sizeSlider.OnValueChanged = v => EditRule(r => r.SizePercent = v);
        sizeSlider.AttachNode(controls);

        var neverDim = new CheckboxNode { Position = new Vector2(8f, 210f), Size = new Vector2(size.X - 16f, 20f), String = "Never dim (always full)" };
        neverDim.OnClick = v => EditRule(r => r.NeverDim = v);
        neverDim.AttachNode(controls);
        var noAggro = new CheckboxNode { Position = new Vector2(8f, 236f), Size = new Vector2(size.X - 16f, 20f), String = "No aggro colours (dummies)" };
        noAggro.OnClick = v => EditRule(r => r.NoAggroColors = v);
        noAggro.AttachNode(controls);

        var preview = new PlateNode(config.EffectiveSkin(), names);
        preview.AttachNode(controls);
        rulePreviewAnchor = new Vector2(size.X / 2f, MathF.Min(size.Y - 30f, 350f));

        controls.AttachNode(panel);
        panel.AttachNode(page);

        return new RuleEditor
        {
            Controls = controls, Empty = empty, Match = match, Text = text, Highlight = highlight, Color = color, Tag = tag,
            Hide = hide, ChangeSize = changeSize, Size = sizeSlider, NeverDim = neverDim, NoAggro = noAggro, Preview = preview,
        };
    }

    private void SelectRule(int index)
    {
        if (index >= config.Rules.Count) return;
        selectedRule = index;
        suppress = true;
        try
        {
            RefreshRuleList();
            RefreshEditor();
        }
        finally
        {
            suppress = false;
        }
    }

    private void EditRule(Action<NameRule> change, bool save = true)
    {
        if (suppress || selectedRule < 0 || selectedRule >= config.Rules.Count) return;
        change(config.Rules[selectedRule]);
        config.RulesChanged();
        if (save) config.MarkDirty();
        RefreshRuleRow(selectedRule - ruleScroll);
    }

    private void RulesEdited()
    {
        config.RulesChanged();
        config.MarkDirty();
        suppress = true;
        try
        {
            RefreshRuleList();
            RefreshEditor();
        }
        finally
        {
            suppress = false;
        }
    }

    private void AddRule(string name)
    {
        config.Rules.Add(new NameRule { Text = name, Highlight = true, Tag = "ADD" });
        selectedRule = config.Rules.Count - 1;
        ruleScroll = Math.Max(0, config.Rules.Count - RuleRows); // show the new rule
        RulesEdited();
    }

    private void AddRuleForTarget()
    {
        var targets = TargetSystem.Instance();
        var target = targets != null ? targets->Target : null;
        AddRule(target != null ? target->NameString : string.Empty);
    }

    private void MoveRuleUp(int index)
    {
        if (index <= 0 || index >= config.Rules.Count) return;
        (config.Rules[index - 1], config.Rules[index]) = (config.Rules[index], config.Rules[index - 1]);
        if (selectedRule == index) selectedRule--;
        else if (selectedRule == index - 1) selectedRule++;
        RulesEdited();
    }

    private void DeleteRule(int index)
    {
        if (index >= config.Rules.Count) return;
        config.Rules.RemoveAt(index);
        if (selectedRule >= config.Rules.Count) selectedRule = config.Rules.Count - 1;
        RulesEdited();
    }

    private void ScrollRules(int delta)
    {
        var max = Math.Max(0, config.Rules.Count - RuleRows);
        var next = Math.Clamp(ruleScroll + delta, 0, max);
        if (next == ruleScroll) return;
        ruleScroll = next;
        suppress = true;
        try { RefreshRuleList(); }
        finally { suppress = false; }
    }

    private void RefreshRuleList()
    {
        ruleScroll = Math.Clamp(ruleScroll, 0, Math.Max(0, config.Rules.Count - RuleRows));
        for (var i = 0; i < ruleRows.Length; i++)
            RefreshRuleRow(i);

        if (ruleScrollLabel != null)
        {
            var count = config.Rules.Count;
            ruleScrollLabel.String = count <= RuleRows ? $"{count} rules" : $"{ruleScroll + 1}-{Math.Min(count, ruleScroll + RuleRows)} of {count}";
        }
    }

    /// <summary>Row i of the list shows rule ruleScroll + i.</summary>
    private void RefreshRuleRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= ruleRows.Length) return;
        var row = ruleRows[rowIndex];
        var i = ruleScroll + rowIndex;
        var exists = i < config.Rules.Count;
        row.On.IsVisible = row.Name.IsVisible = row.Summary.IsVisible = row.Up.IsVisible = row.Delete.IsVisible = exists;
        if (!exists) return;

        var rule = config.Rules[i];
        row.On.IsChecked = rule.Enabled;
        var label = rule.Text.Length == 0 ? "(no name yet)" : rule.Text;
        row.Name.String = $"{(i == selectedRule ? "> " : "")}{(rule.Match == NameMatch.Is ? "" : "~ ")}{label}";
        row.Summary.String = rule.Summary();
    }

    private void RefreshEditor()
    {
        if (editor == null) return;
        var has = selectedRule >= 0 && selectedRule < config.Rules.Count;
        editor.Controls.IsVisible = has;
        editor.Empty.IsVisible = !has;
        if (!has) return;

        var rule = config.Rules[selectedRule];
        editor.Match.SelectedOption = rule.Match == NameMatch.Is ? "Name is" : "Name contains";
        editor.Text.String = rule.Text;
        editor.Highlight.IsChecked = rule.Highlight;
        editor.Color.CurrentColor = rule.HighlightColor;
        editor.Color.DefaultColor = new NameRule().HighlightColor;
        editor.Tag.String = rule.Tag;
        editor.Hide.IsChecked = rule.Hide;
        editor.ChangeSize.IsChecked = rule.ChangeSize;
        editor.Size.Value = Math.Clamp(rule.SizePercent, 50, 200);
        editor.NeverDim.IsChecked = rule.NeverDim;
        editor.NoAggro.IsChecked = rule.NoAggroColors;
    }

    // ================================================================ profiles tab

    private void BuildProfilesTab(ResNode page, float width)
    {
        y = 0f;
        Header(page, "Profiles · click one to use and edit it");

        var list = new TextButtonNode[ProfileRows];
        for (var i = 0; i < ProfileRows; i++)
        {
            var row = i; // shows profile profileScroll + row
            list[i] = Button(page, "", 8f, 24f + i * 28f, 212f, () =>
            {
                var index = profileScroll + row;
                if (index >= config.Profiles.Count) return;
                config.SetActive(config.Profiles[index].Name); // refreshes through ActiveRevision
                hider.Refresh();
            });
        }

        Button(page, "<", 222f, 24f, 26f, () => ScrollProfiles(-ProfileRows));
        Button(page, ">", 222f, 24f + (ProfileRows - 1) * 28f, 26f, () => ScrollProfiles(ProfileRows));

        var nameLabel = new TextNode { Position = new Vector2(8f, 254f), Size = new Vector2(240f, 18f), FontSize = 12, AlignmentType = AlignmentType.Left, String = "Name of the profile in use" };
        nameLabel.AttachNode(page);
        var nameInput = new TextInputNode { Position = new Vector2(8f, 272f), Size = new Vector2(240f, 28f), MaxCharacters = 24 };
        nameInput.AttachNode(page);

        Button(page, "Rename", 8f, 306f, 116f, () => RenameActive(nameInput.String.ExtractText().Trim()));
        Button(page, "New", 132f, 306f, 116f, () => AddProfile(new PlateProfile { Name = config.UniqueProfileName("Profile") }));
        Button(page, "Copy", 8f, 338f, 116f, () => AddProfile(config.Active.Copy(config.UniqueProfileName($"{config.Active.Name} copy"))));
        Button(page, "Delete", 132f, 338f, 116f, DeleteActive);

        // Right column: which profile is used when.
        var x = 272f;
        y = 0f;
        var header = new TextNode { Position = new Vector2(x, 0f), Size = new Vector2(width - x, 20f), FontSize = 14, TextColor = Skin.Hex(0xE69F00), AlignmentType = AlignmentType.Left, String = "Which profile to use" };
        header.AttachNode(page);
        var auto = new CheckboxNode { Position = new Vector2(x + 8f, 24f), Size = new Vector2(width - x - 8f, 20f), String = "Switch automatically when I change job" };
        auto.OnClick = v =>
        {
            if (suppress) return;
            config.AutoSwitchProfiles = v;
            config.MarkDirty();
            if (v) ApplyAutoProfile();
        };
        auto.AttachNode(page);

        var roleButtons = new TextButtonNode[Roles.Length];
        for (var i = 0; i < Roles.Length; i++)
        {
            var role = Roles[i];
            var label = new TextNode { Position = new Vector2(x + 8f, 54f + i * 28f), Size = new Vector2(140f, 20f), FontSize = 13, AlignmentType = AlignmentType.Left, String = RoleLabels[i] };
            label.AttachNode(page);
            roleButtons[i] = Button(page, "", x + 150f, 52f + i * 28f, 190f, () =>
            {
                config.RoleProfiles[role] = NextProfile(config.RoleProfiles.TryGetValue(role, out var current) ? current : null);
                config.MarkDirty();
                ApplyAutoProfile();
                RefreshProfiles();
            });
        }

        var exceptions = new TextNode { Position = new Vector2(x, 200f), Size = new Vector2(width - x, 20f), FontSize = 14, TextColor = Skin.Hex(0xE69F00), AlignmentType = AlignmentType.Left, String = "Per-job exceptions" };
        exceptions.AttachNode(page);

        var jobLabels = new TextNode[JobRows];
        var jobButtons = new TextButtonNode[JobRows];
        var jobRemove = new TextButtonNode[JobRows];
        for (var i = 0; i < JobRows; i++)
        {
            var index = i;
            jobLabels[i] = new TextNode { Position = new Vector2(x + 8f, 226f + i * 28f), Size = new Vector2(140f, 20f), FontSize = 13, AlignmentType = AlignmentType.Left };
            jobLabels[i].AttachNode(page);
            jobButtons[i] = Button(page, "", x + 150f, 224f + i * 28f, 160f, () =>
            {
                var job = JobAt(index);
                if (job == 0) return;
                config.JobProfiles[job] = NextProfile(config.JobProfiles[job]);
                config.MarkDirty();
                ApplyAutoProfile();
                RefreshProfiles();
            });
            jobRemove[i] = Button(page, "X", x + 314f, 224f + i * 28f, 26f, () =>
            {
                var job = JobAt(index);
                if (job == 0) return;
                config.JobProfiles.Remove(job);
                config.MarkDirty();
                ApplyAutoProfile();
                RefreshProfiles();
            });
        }

        Button(page, "+ Exception for my current job", x + 8f, 370f, 260f, () =>
        {
            var job = CurrentJob();
            if (job == 0 || config.JobProfiles.Count >= JobRows) return;
            config.JobProfiles[job] = config.Active.Name;
            config.MarkDirty();
            RefreshProfiles();
        });

        profiles = new ProfileControls
        {
            List = list, Name = nameInput, RoleButtons = roleButtons, JobLabels = jobLabels, JobButtons = jobButtons, JobRemove = jobRemove, AutoSwitch = auto,
        };
    }

    private uint JobAt(int index) => config.JobProfiles.Keys.OrderBy(k => k).Skip(index).FirstOrDefault();

    private string NextProfile(string? current)
    {
        var index = current == null ? -1 : config.Profiles.FindIndex(p => string.Equals(p.Name, current, StringComparison.OrdinalIgnoreCase));
        return config.Profiles[(index + 1) % config.Profiles.Count].Name;
    }

    private void ScrollProfiles(int delta)
    {
        var next = Math.Clamp(profileScroll + delta, 0, Math.Max(0, config.Profiles.Count - ProfileRows));
        if (next == profileScroll) return;
        profileScroll = next;
        suppress = true;
        try { RefreshProfiles(); }
        finally { suppress = false; }
    }

    private void AddProfile(PlateProfile profile)
    {
        config.Profiles.Add(profile);
        profileScroll = Math.Max(0, config.Profiles.Count - ProfileRows); // show the new profile
        config.SetActive(profile.Name);
        hider.Refresh();
    }

    private void RenameActive(string newName)
    {
        if (newName.Length == 0 || config.FindProfile(newName) != null) return;
        var old = config.Active.Name;
        foreach (var role in config.RoleProfiles.Keys.ToList())
            if (string.Equals(config.RoleProfiles[role], old, StringComparison.OrdinalIgnoreCase)) config.RoleProfiles[role] = newName;
        foreach (var job in config.JobProfiles.Keys.ToList())
            if (string.Equals(config.JobProfiles[job], old, StringComparison.OrdinalIgnoreCase)) config.JobProfiles[job] = newName;
        config.Active.Name = newName;
        config.ActiveProfileName = newName;
        config.MarkDirty();
        RefreshAll();
    }

    private void DeleteActive()
    {
        if (config.Profiles.Count <= 1) return;
        var old = config.Active;
        config.Profiles.Remove(old);
        var fallback = config.Profiles[0].Name;
        foreach (var role in config.RoleProfiles.Keys.ToList())
            if (string.Equals(config.RoleProfiles[role], old.Name, StringComparison.OrdinalIgnoreCase)) config.RoleProfiles[role] = fallback;
        foreach (var job in config.JobProfiles.Keys.ToList())
            if (string.Equals(config.JobProfiles[job], old.Name, StringComparison.OrdinalIgnoreCase)) config.JobProfiles[job] = fallback;
        config.SetActive(fallback);
        hider.Refresh();
    }

    /// <summary>With auto-switch on, use whatever the current job maps to right away.</summary>
    private void ApplyAutoProfile()
    {
        if (!config.AutoSwitchProfiles) return;
        var job = CurrentJob();
        if (job == 0) return;
        config.SetActive(config.ProfileFor(job, names.RoleOf(job)));
        hider.Refresh();
    }

    private void RefreshProfiles()
    {
        if (profiles == null) return;

        profileScroll = Math.Clamp(profileScroll, 0, Math.Max(0, config.Profiles.Count - ProfileRows));
        for (var row = 0; row < profiles.List.Length; row++)
        {
            var i = profileScroll + row;
            var exists = i < config.Profiles.Count;
            profiles.List[row].IsVisible = exists;
            if (!exists) continue;
            var p = config.Profiles[i];
            profiles.List[row].String = ReferenceEquals(p, config.Active) ? $"> {p.Name}  (in use)" : p.Name;
        }

        profiles.Name.String = config.Active.Name;
        profiles.AutoSwitch.IsChecked = config.AutoSwitchProfiles;

        for (var i = 0; i < Roles.Length; i++)
            profiles.RoleButtons[i].String = config.RoleProfiles.TryGetValue(Roles[i], out var name) ? name : config.Profiles[0].Name;

        for (var i = 0; i < JobRows; i++)
        {
            var job = JobAt(i);
            var exists = job != 0;
            profiles.JobLabels[i].IsVisible = profiles.JobButtons[i].IsVisible = profiles.JobRemove[i].IsVisible = exists;
            if (!exists) continue;
            profiles.JobLabels[i].String = names.JobAbbreviation(job);
            profiles.JobButtons[i].String = config.JobProfiles[job];
        }
    }

    private static uint CurrentJob()
    {
        var control = Control.Instance();
        var player = control != null ? control->LocalPlayer : null;
        return player != null ? ((CSCharacter*)player)->CharacterData.ClassJob : 0u;
    }

    private static int MyLevel()
    {
        var control = Control.Instance();
        var player = control != null ? control->LocalPlayer : null;
        var level = player != null ? ((CSCharacter*)player)->CharacterData.Level : (byte)0;
        return level > 0 ? level : 100;
    }

    // ================================================================ preview

    private ResNode BuildPreview(Vector2 position, Vector2 size)
    {
        var panel = new ResNode { Position = position, Size = size };

        var background = new ColorImageNode { Position = new Vector2(0f, 24f), Size = new Vector2(size.X, size.Y - 24f), Color = Skin.Hex(0x14171C, 0.95f) };
        background.AttachNode(panel);

        var title = new TextNode { Position = Vector2.Zero, Size = new Vector2(size.X, 20f), FontSize = 14, AlignmentType = AlignmentType.Left, String = "Live preview" };
        title.AttachNode(panel);

        var skin = config.EffectiveSkin();
        previewPlates = new PlateNode?[3];
        var step = (size.Y - 24f) / 3f;
        for (var i = 0; i < 3; i++)
        {
            var plate = new PlateNode(skin, names);
            plate.AttachNode(panel);
            previewPlates[i] = plate;
            previewAnchors[i] = new Vector2(size.X / 2f, 24f + step * i + 78f);
        }

        return panel;
    }

    protected override void OnUpdate(AtkUnitBase* addon)
    {
        if (seenActiveRevision != config.ActiveRevision)
        {
            seenActiveRevision = config.ActiveRevision;
            RefreshAll();
        }

        HandleMouseWheel();
        UpdatePreview();

        if (config.Dirty && (DateTime.UtcNow - config.DirtySince).TotalSeconds > 1.0)
            config.Save();
    }

    /// <summary>Mouse wheel over the rule list or the profile list scrolls it by one row.</summary>
    private void HandleMouseWheel()
    {
        var input = UIInputData.Instance();
        if (input == null) return;
        ref var mouse = ref input->InputData.CursorInputs;
        if (mouse.MouseWheel == 0) return;
        var cursor = new Vector2(mouse.PositionX, mouse.PositionY);
        var step = -Math.Sign(mouse.MouseWheel); // wheel up = towards the top of the list

        if (tabPages.Count > 4 && tabPages[4].IsVisible && ruleRows.Length > 1
            && Inside(ruleRows[0].On, ruleRows[^1].On, ruleListWidth, 26f, cursor))
            ScrollRules(step);

        if (tabPages.Count > 5 && tabPages[5].IsVisible && profiles != null && profiles.List.Length > 1
            && Inside(profiles.List[0], profiles.List[^1], 248f, 28f, cursor))
            ScrollProfiles(step);
    }

    /// <summary>Is the cursor inside the box from the first row to the last row of a list (screen pixels)?</summary>
    private static bool Inside(NodeBase first, NodeBase last, float width, float rowHeight, Vector2 cursor)
    {
        var rows = last.Y - first.Y;
        var scale = rows > 0f ? (last.ScreenY - first.ScreenY) / rows : 1f;
        if (scale <= 0f) scale = 1f;
        var left = first.ScreenX - first.X * scale;
        var top = first.ScreenY;
        var right = left + width * scale;
        var bottom = last.ScreenY + rowHeight * scale;
        return cursor.X >= left && cursor.X <= right && cursor.Y >= top && cursor.Y <= bottom;
    }

    private void UpdatePreview()
    {
        if (previewPlates.Length == 0) return;

        if (previewSkinRevision != config.SkinRevision)
        {
            previewSkinRevision = config.SkinRevision;
            var skin = config.EffectiveSkin();
            foreach (var plate in previewPlates)
                plate?.ApplySkin(skin);
            editor?.Preview.ApplySkin(skin);
        }

        var myLevel = MyLevel();

        if (previewPanel is { IsVisible: true })
        {
            for (var i = 0; i < previewPlates.Length; i++)
            {
                var plate = previewPlates[i];
                if (plate == null) continue;

                var d = previewData[i];
                d.Level = (byte)Math.Clamp(myLevel + PreviewLevelOffsets[i], 1, 255);
                var rule = MatchPreviewRule(PreviewNames[i]);

                var scale = config.PlateScale * rule.Scale;
                var alpha = 1f;
                if (d.IsTarget) scale *= config.TargetScale;
                else if (config.DimNonTargets && !rule.NeverDim) { scale *= config.NonTargetScale; alpha = config.NonTargetAlpha; }

                var aggro = config.ShowAggroColors && !rule.NoAggroColors ? previewAggro[i] : default;
                if (rule.Hide) alpha *= 0.25f; // hidden in game; shown faintly here so you can see the rule hit
                plate.Apply(d, previewAnchors[i], scale, alpha, d.IsTarget, aggro, 0u, myLevel, rule, config);
            }
        }

        if (editor != null && selectedRule >= 0 && selectedRule < config.Rules.Count && editor.Controls.IsVisible)
        {
            var rule = config.Rules[selectedRule];
            var effect = RuleEffect.From(rule, selectedRule);
            var name = rule.Text.Length == 0 ? "Ice Sprite" : rule.Text;
            if (!string.Equals(rulePreviewData.PreviewName, name, StringComparison.Ordinal))
            {
                rulePreviewData.PreviewName = name;
                rulePreviewData.GameObjectId++; // makes the plate pick up the new name
            }

            rulePreviewData.Level = (byte)Math.Clamp(myLevel, 1, 255);
            var alpha = effect.Hide ? 0.25f : 1f;
            var aggro = config.ShowAggroColors && !effect.NoAggroColors ? previewAggro[1] : default;
            editor.Preview.Apply(rulePreviewData, rulePreviewAnchor, config.PlateScale * effect.Scale, alpha, false, aggro, 0u, myLevel, effect, config);
        }
    }

    private RuleEffect MatchPreviewRule(string previewName)
    {
        for (var r = 0; r < config.Rules.Count; r++)
        {
            if (config.Rules[r].Matches(previewName))
                return RuleEffect.From(config.Rules[r], r);
        }

        return RuleEffect.None;
    }

    protected override void OnHide(AtkUnitBase* addon)
    {
        if (config.Dirty) config.Save();
    }

    protected override void OnFinalize(AtkUnitBase* addon)
    {
        // The game frees every node of this window now; drop our references.
        previewPlates = [];
        previewPanel = null;
        ruleRows = [];
        ruleScrollLabel = null;
        editor = null;
        profiles = null;
        tabPages.Clear();
        refreshers.Clear();
        colorRows.Clear();
        if (config.Dirty) config.Save();
    }

    // ================================================================ control helpers

    private void Header(ResNode page, string text)
    {
        if (y > 0f) y += 4f;
        var node = new TextNode
        {
            Position = new Vector2(0f, y),
            Size = new Vector2(page.Width, 20f),
            FontSize = 14,
            AlignmentType = AlignmentType.Left,
            TextColor = Skin.Hex(0xE69F00),
            String = text,
        };
        node.AttachNode(page);
        y += 24f;
    }

    private void Check(ResNode page, string label, Func<bool> get, Action<bool> set)
    {
        var node = new CheckboxNode
        {
            Position = new Vector2(8f, y),
            Size = new Vector2(RowWidth - 8f, 20f),
            String = label,
        };
        node.OnClick = v =>
        {
            if (suppress) return;
            set(v);
            config.MarkDirty();
        };
        node.AttachNode(page);
        refreshers.Add(() => node.IsChecked = get());
        y += 26f;
    }

    private void Slider(ResNode page, string label, int min, int max, Func<int> get, Action<int> set)
    {
        var text = new TextNode { Position = new Vector2(8f, y), Size = new Vector2(170f, 20f), FontSize = 13, AlignmentType = AlignmentType.Left, String = label };
        text.AttachNode(page);

        var node = new SliderNode
        {
            Position = new Vector2(180f, y),
            Size = new Vector2(RowWidth - 190f, 20f),
            Range = min..max,
            Step = 1,
            Value = Math.Clamp(get(), min, max),
        };
        node.OnValueChanged = v =>
        {
            if (suppress) return;
            set(v);
            config.MarkDirty();
        };
        node.AttachNode(page);
        refreshers.Add(() => node.Value = Math.Clamp(get(), min, max));
        y += 28f;
    }

    private void Number(ResNode page, string label, int min, int max, Func<int> get, Action<int> set)
    {
        var text = new TextNode { Position = new Vector2(8f, y + 2f), Size = new Vector2(170f, 20f), FontSize = 13, AlignmentType = AlignmentType.Left, String = label };
        text.AttachNode(page);

        var node = new NumericInputNode
        {
            Position = new Vector2(180f, y),
            Size = new Vector2(140f, 24f),
            Min = min,
            Max = max,
            Step = 1,
            Value = Math.Clamp(get(), min, max),
        };
        node.OnValueUpdate = v =>
        {
            if (suppress) return;
            set(v);
            config.MarkDirty();
        };
        node.AttachNode(page);
        refreshers.Add(() => node.Value = Math.Clamp(get(), min, max));
        y += 30f;
    }

    private void Radio(ResNode page, string[] options, Func<int> get, Action<int> set)
    {
        var node = new RadioButtonGroupNode
        {
            Position = new Vector2(8f, y),
            Size = new Vector2(RowWidth - 8f, options.Length * 18f),
        };
        node.AttachNode(page);

        for (var i = 0; i < options.Length; i++)
        {
            var index = i;
            node.AddButton(options[i], () =>
            {
                if (suppress) return;
                set(index);
                config.MarkDirty();
            });
        }

        refreshers.Add(() => node.SelectedOption = options[Math.Clamp(get(), 0, options.Length - 1)]);
        y += options.Length * 20f + 6f;
    }

    private static TextButtonNode Button(ResNode page, string label, float x, float buttonY, float width, Action onClick)
    {
        var node = new TextButtonNode
        {
            Position = new Vector2(x, buttonY),
            Size = new Vector2(width, 26f),
            String = label,
            OnClick = onClick,
        };
        node.AttachNode(page);
        return node;
    }

    private static int Percent(float value) => (int)MathF.Round(value * 100f);
}
