using System;
using System.Numerics;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit.Nodes;
using PlaterXIV.Game;
using PlaterXIV.Util;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace PlaterXIV.Render;

/// <summary>
/// One pooled enemy plate, layout "A · Stack": my DoTs on top, name, HP bar + %, cast bar underneath.
/// Built once; <see cref="Apply"/> writes only values that changed and never allocates.
/// The HP bar's bottom centre sits on the game's plate anchor; the cast bar hangs below it.
/// </summary>
public sealed unsafe class PlateNode : ResNode
{
    public const int MaxDots = 6;
    private const int MaxTicks = 9;
    public const float PlateWidth = 164f;
    public const float PlateHeight = 88f;
    public const float AnchorY = BarY + BarH; // bottom of the HP bar

    private const float DotW = 18f;
    private const float DotH = 24f;
    private const float DotGap = 3f;
    private const float DotTimerY = 24f;
    private const float NameY = 36f;
    private const float NameH = 15f;
    private const float BarX = 7f;
    private const float BarY = 53f;
    private const float BarW = 150f;
    private const float BarH = 13f;
    private const float CastY = 68f;
    private const float CastBarH = 7f;
    private const float CastTextY = 8f;  // inside the cast group
    private const float CastTextH = 12f;

    private Skin skin;
    private readonly NameCache names;

    // HP block
    private readonly TextNode name;
    private readonly TextNode chip;
    private readonly TextNode level;
    private readonly TextNode tag;
    private readonly ColorImageNode focusMark;
    // Shield after the name (a tank holds it and it has been hit): black outline + coloured fill, each a bar and a diamond.
    private readonly ResNode shield;
    private readonly ColorImageNode shieldEdgeTop, shieldEdgePoint, shieldTop, shieldPoint;
    private int lastShield = -1;
    private int lastFocus = -1;
    private readonly ColorImageNode border;
    private readonly ColorImageNode track;
    private readonly ColorImageNode fill;
    private readonly ColorImageNode[] ticks = new ColorImageNode[MaxTicks];
    private readonly TextNode percent;
    private readonly TextNode percentSign;
    private readonly ColorImageNode arrowLeft;
    private readonly ColorImageNode arrowRight;

    // Cast block
    private readonly ResNode castGroup;
    private readonly ColorImageNode castTrack;
    private readonly ColorImageNode castFill;
    private readonly TextNode castName;
    private readonly TextNode castTime;
    private readonly ColorImageNode lockShackle;
    private readonly ColorImageNode lockHole;
    private readonly ColorImageNode lockBody;

    // DoT block
    private readonly IconImageNode[] dotIcons = new IconImageNode[MaxDots];
    private readonly TextNode[] dotTimers = new TextNode[MaxDots];
    private readonly uint[] dotIconIds = new uint[MaxDots];
    private readonly int[] dotSeconds = new int[MaxDots];
    private readonly int[] dotExpiring = new int[MaxDots];
    private int dotsShown;

    /// <summary>Renderer frame stamp of the last frame this plate was used.</summary>
    public long SeenFrame;

    private bool shown;
    private ulong lastObjectId = ulong.MaxValue;
    private int lastLevel = -1;
    private Vector4 lastLevelColor = new(-1f);
    private int lastPercent = -1;
    private float lastFillWidth = -1f;
    private int lastTargetStyle = -1;
    private Vector4 lastBorderColor = new(-1f);
    private Vector4 lastNameColor = new(-1f);
    private string? lastTag;
    private Vector4 lastTagColor = new(-1f);
    private float lastScale = -1f;
    private float lastAlpha = -1f;
    private Vector2 lastPosition = new(float.NaN, float.NaN);
    private Vector4 lastFillColor = new(-1f);
    private long lastChipSignature = long.MinValue;

    private bool castShown;
    private uint lastCastAction = uint.MaxValue;
    private int lastCastLocked = -1;
    private float lastCastWidth = -1f;
    private int lastCastTenths = -1;

    public PlateNode(Skin skin, NameCache names)
    {
        this.skin = skin;
        this.names = names;
        Size = new Vector2(PlateWidth, PlateHeight);
        OriginX = PlateWidth / 2f; // scale around the anchor point
        OriginY = AnchorY;
        IsVisible = false;

        var white = Skin.Hex(0xFFFFFF);
        var black = Skin.Hex(0x000000);

        // --- DoTs ---
        for (var i = 0; i < MaxDots; i++)
        {
            var x = BarX + i * (DotW + DotGap);
            var icon = new IconImageNode
            {
                Position = new Vector2(x, 0f),
                Size = new Vector2(DotW, DotH),
                TextureSize = new Vector2(24f, 32f), // status icons are 24 x 32
                FitTexture = true,
                IsVisible = false,
            };
            icon.AttachNode(this);
            dotIcons[i] = icon;

            var timer = new TextNode
            {
                Position = new Vector2(x - 4f, DotTimerY),
                Size = new Vector2(DotW + 8f, 11f),
                AlignmentType = AlignmentType.Top,
                FontSize = skin.NumberFontSize,
                TextColor = skin.Text,
                TextOutlineColor = black,
                TextFlags = TextFlags.Edge,
                IsVisible = false,
            };
            timer.AttachNode(this);
            dotTimers[i] = timer;
            dotSeconds[i] = int.MinValue;
            dotExpiring[i] = -1;
        }

        // --- Name + HP ---
        name = new TextNode
        {
            Position = new Vector2(BarX, NameY),
            Size = new Vector2(BarW, NameH),
            AlignmentType = AlignmentType.BottomLeft, // x is set by LayoutNameRow so "Lv90 Name" stays centred
            FontSize = skin.NameFontSize,
            TextColor = skin.Text,
            TextOutlineColor = skin.TextOutline,
            TextFlags = TextFlags.Edge,
        };
        name.AttachNode(this);

        level = new TextNode
        {
            Position = new Vector2(BarX, NameY),
            Size = new Vector2(40f, NameH),
            AlignmentType = AlignmentType.BottomLeft,
            FontSize = skin.NumberFontSize + 1,
            TextColor = skin.Text,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
            IsVisible = false,
        };
        level.AttachNode(this);

        // Focus target: a small diamond in front of the level and name (as on the mockups).
        focusMark = new ColorImageNode
        {
            Position = new Vector2(BarX, NameY + NameH - 9f),
            Size = new Vector2(6f, 6f),
            OriginX = 3f,
            OriginY = 3f,
            RotationDegrees = 45f,
            Color = skin.Focus,
            IsVisible = false,
        };
        focusMark.AttachNode(this);

        shield = new ResNode { Position = new Vector2(BarX, NameY + 3f), Size = new Vector2(10f, 11f), IsVisible = false };
        shield.AttachNode(this);
        shieldEdgeTop = new ColorImageNode { Position = new Vector2(0f, 0f), Size = new Vector2(10f, 7f), Color = black };
        shieldEdgeTop.AttachNode(shield);
        shieldEdgePoint = Diamond(new Vector2(5f, 6f), 7.07f, black);
        shieldEdgePoint.AttachNode(shield);
        shieldTop = new ColorImageNode { Position = new Vector2(1f, 1f), Size = new Vector2(8f, 5f), Color = skin.AggroHave };
        shieldTop.AttachNode(shield);
        shieldPoint = Diamond(new Vector2(5f, 6f), 5.66f, skin.AggroHave);
        shieldPoint.AttachNode(shield);

        // Rule tag like "ADD": dark text on the rule's highlight colour, right after the name.
        tag = new TextNode
        {
            Position = new Vector2(BarX, NameY + 2f),
            Size = new Vector2(30f, NameH - 2f),
            AlignmentType = AlignmentType.Center,
            FontSize = skin.NumberFontSize,
            TextColor = Skin.Hex(0x0B0D0E),
            TextFlags = TextFlags.AutoAdjustNodeSize,
            IsVisible = false,
        };
        tag.AttachNode(this);

        // Who holds aggro / my threat %, placed right after the centred name.
        chip = new TextNode
        {
            Position = new Vector2(BarX + BarW / 2f + 4f, NameY),
            Size = new Vector2(70f, NameH),
            AlignmentType = AlignmentType.BottomLeft,
            FontSize = skin.NumberFontSize,
            TextColor = skin.Muted,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
            IsVisible = false,
        };
        chip.AttachNode(this);

        border = new ColorImageNode { Position = new Vector2(BarX, BarY), Size = new Vector2(BarW, BarH), Color = skin.Line };
        border.AttachNode(this);
        track = new ColorImageNode { Position = new Vector2(BarX + 1f, BarY + 1f), Size = new Vector2(BarW - 2f, BarH - 2f), Color = skin.Track };
        track.AttachNode(this);
        fill = new ColorImageNode { Position = new Vector2(BarX + 1f, BarY + 1f), Size = new Vector2(BarW - 2f, BarH - 2f), Color = skin.Hp };
        fill.AttachNode(this);

        // Segment ticks over the fill (Signal: every 10%, like HealFrames). Positioned by ApplySkin.
        for (var i = 0; i < MaxTicks; i++)
        {
            ticks[i] = new ColorImageNode { Position = new Vector2(BarX + 1f, BarY + 1f), Size = new Vector2(1f, BarH - 2f), Color = skin.SegmentLine, IsVisible = false };
            ticks[i].AttachNode(this);
        }

        percent = new TextNode
        {
            Position = new Vector2(BarX + 1f, BarY),
            Size = new Vector2(BarW - 15f, BarH),
            AlignmentType = AlignmentType.Right,
            FontSize = skin.NumberFontSize,
            TextColor = white,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
        };
        percent.AttachNode(this);

        percentSign = new TextNode
        {
            Position = new Vector2(BarX + BarW - 14f, BarY),
            Size = new Vector2(12f, BarH),
            AlignmentType = AlignmentType.Left,
            FontSize = skin.NumberFontSize,
            TextColor = white,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
            String = "%",
        };
        percentSign.AttachNode(this);

        arrowLeft = MakeDiamond(new Vector2(0f, BarY + 3.5f), skin.Accent);
        arrowLeft.AttachNode(this);
        arrowRight = MakeDiamond(new Vector2(BarX + BarW + 1f, BarY + 3.5f), skin.Accent);
        arrowRight.AttachNode(this);

        // --- Cast ---
        castGroup = new ResNode { Position = new Vector2(BarX, CastY), Size = new Vector2(BarW, CastTextY + CastTextH), IsVisible = false };
        castGroup.AttachNode(this);

        var castBorder = new ColorImageNode { Position = Vector2.Zero, Size = new Vector2(BarW, CastBarH), Color = black };
        castBorder.AttachNode(castGroup);
        castTrack = new ColorImageNode { Position = new Vector2(1f, 1f), Size = new Vector2(BarW - 2f, CastBarH - 2f), Color = skin.Track };
        castTrack.AttachNode(castGroup);
        castFill = new ColorImageNode { Position = new Vector2(1f, 1f), Size = new Vector2(0f, CastBarH - 2f), Color = skin.CastInterruptible };
        castFill.AttachNode(castGroup);

        // Small padlock for uninterruptible casts: shackle, its hole, then the body.
        lockShackle = new ColorImageNode { Position = new Vector2(1.5f, CastTextY + 1f), Size = new Vector2(5f, 5f), Color = skin.CastLocked, IsVisible = false };
        lockShackle.AttachNode(castGroup);
        lockHole = new ColorImageNode { Position = new Vector2(2.5f, CastTextY + 2f), Size = new Vector2(3f, 4f), Color = black, IsVisible = false };
        lockHole.AttachNode(castGroup);
        lockBody = new ColorImageNode { Position = new Vector2(0f, CastTextY + 5f), Size = new Vector2(8f, 6f), Color = skin.CastLocked, IsVisible = false };
        lockBody.AttachNode(castGroup);

        castName = new TextNode
        {
            Position = new Vector2(0f, CastTextY),
            Size = new Vector2(BarW - 34f, CastTextH),
            AlignmentType = AlignmentType.Left,
            FontSize = skin.NumberFontSize,
            TextColor = skin.Text,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
        };
        castName.AttachNode(castGroup);

        castTime = new TextNode
        {
            Position = new Vector2(BarW - 34f, CastTextY),
            Size = new Vector2(34f, CastTextH),
            AlignmentType = AlignmentType.Right,
            FontSize = skin.NumberFontSize,
            TextColor = skin.Text,
            TextOutlineColor = black,
            TextFlags = TextFlags.Edge,
        };
        castTime.AttachNode(castGroup);

        name.FontType = skin.NameFont;
        LayoutTicks();
    }

    /// <summary>Restyles every node for another skin (or changed colours) without rebuilding anything.</summary>
    public void ApplySkin(Skin newSkin)
    {
        skin = newSkin;

        name.FontType = skin.NameFont;
        name.FontSize = skin.NameFontSize;
        name.TextColor = skin.Text;
        name.TextOutlineColor = skin.TextOutline;
        chip.FontSize = skin.NumberFontSize;
        level.FontSize = skin.NumberFontSize + 1;
        percent.FontSize = skin.NumberFontSize;
        percentSign.FontSize = skin.NumberFontSize;
        castName.FontSize = skin.NumberFontSize;
        castName.TextColor = skin.Text;
        castTime.FontSize = skin.NumberFontSize;
        castTime.TextColor = skin.Text;

        track.Color = skin.Track;
        castTrack.Color = skin.Track;
        LayoutTicks();
        lockShackle.Color = skin.CastLocked;
        lockBody.Color = skin.CastLocked;
        arrowLeft.Color = skin.Accent;
        arrowRight.Color = skin.Accent;
        focusMark.Color = skin.Focus;
        lastShield = -1; // recolour the shield with the new skin on the next Apply

        for (var i = 0; i < MaxDots; i++)
            dotTimers[i].FontSize = skin.NumberFontSize;

        // Force every colour that depends on state to be written again on the next Apply.
        lastTargetStyle = -1;
        lastFillColor = new Vector4(-1f);
        lastChipSignature = long.MinValue;
        lastCastLocked = -1;
        Array.Fill(dotExpiring, -1);
        lastObjectId = ulong.MaxValue; // re-set and re-measure the name (the font may have changed)
        lastLevel = -1;
        lastLevelColor = new Vector4(-1f);
        lastBorderColor = new Vector4(-1f);
        lastNameColor = new Vector4(-1f);
        lastTag = null;
        tag.IsVisible = false;
        lastTagColor = new Vector4(-1f);
        tag.FontSize = skin.NumberFontSize;
    }

    /// <summary>A square of the given side turned 45°, centred on <paramref name="centre"/>.</summary>
    private static ColorImageNode Diamond(Vector2 centre, float side, Vector4 color) => new()
    {
        Position = centre - new Vector2(side / 2f, side / 2f),
        Size = new Vector2(side, side),
        OriginX = side / 2f,
        OriginY = side / 2f,
        RotationDegrees = 45f,
        Color = color,
    };

    private static ColorImageNode MakeDiamond(Vector2 position, Vector4 color) => new()
    {
        Position = position,
        Size = new Vector2(6f, 6f),
        OriginX = 3f,
        OriginY = 3f,
        RotationDegrees = 45f,
        Color = color,
        IsVisible = false,
    };

    /// <summary>Shows the plate for this frame's data. Writes only what changed.</summary>
    public void Apply(PlateData d, Vector2 anchor, float scale, float alpha, bool targetStyle, in AggroResult aggro, uint idleRgba, int myLevel, in RuleEffect rule, Configuration config)
    {
        if (!shown)
        {
            IsVisible = true;
            shown = true;
        }

        var relayout = false;
        if (d.GameObjectId != lastObjectId)
        {
            lastObjectId = d.GameObjectId;
            var go = (CSGameObject*)d.GameObject;
            if (go != null)
                name.Node->SetText(go->Name); // copies the null-terminated name bytes, no managed string
            else if (d.PreviewName != null)
                name.Node->SetText(d.PreviewName);
            relayout = true;
        }

        var lvl = config.ShowLevel ? d.Level : 0;
        if (lvl != lastLevel)
        {
            lastLevel = lvl;
            if (lvl > 0) SetLevelText(lvl);
            level.IsVisible = lvl > 0;
            relayout = true;
        }

        var focus = d.IsFocus ? 1 : 0;
        if (focus != lastFocus)
        {
            lastFocus = focus;
            focusMark.IsVisible = focus == 1;
            relayout = true;
        }

        if (!ReferenceEquals(rule.Tag, lastTag))
        {
            lastTag = rule.Tag;
            if (rule.Tag != null) tag.Node->SetText(rule.Tag);
            tag.IsVisible = rule.Tag != null;
            relayout = true;
        }

        var shieldKind = config.SecuredShield ? aggro.Shield : (byte)0;
        if (shieldKind != lastShield)
        {
            lastShield = shieldKind;
            shield.IsVisible = shieldKind != 0;
            if (shieldKind != 0)
                shieldTop.Color = shieldPoint.Color = shieldKind == 1 ? skin.AggroHave : skin.AggroCoTank;
            relayout = true;
        }

        if (rule.Tag != null && rule.HighlightColor != lastTagColor)
        {
            lastTagColor = rule.HighlightColor;
            tag.BackgroundColor = rule.HighlightColor;
        }

        if (relayout) LayoutNameRow(lvl > 0);

        var nameColor = rule.Highlight ? rule.HighlightColor : skin.Text;
        if (nameColor != lastNameColor)
        {
            lastNameColor = nameColor;
            name.TextColor = nameColor;
        }

        if (lvl > 0)
        {
            var levelColor = LevelColor(lvl, myLevel);
            if (levelColor != lastLevelColor)
            {
                lastLevelColor = levelColor;
                level.TextColor = levelColor;
            }
        }

        ApplyHp(d);
        ApplyAggro(aggro, idleRgba, config.PaleUntagged && aggro.Untagged);
        ApplyTargetStyle(targetStyle, rule.Highlight ? rule.HighlightColor : (Vector4?)null);

        if (config.ShowCastBar) ApplyCast(d);
        else HideCast();

        ApplyDots(d, config.ShowMyDots ? Math.Clamp(config.MaxDots, 0, MaxDots) : 0, config.DotExpiringSeconds);

        if (scale != lastScale)
        {
            lastScale = scale;
            Scale = new Vector2(scale, scale);
        }

        if (alpha != lastAlpha)
        {
            lastAlpha = alpha;
            Alpha = alpha;
        }

        var position = new Vector2(MathF.Round(anchor.X - PlateWidth / 2f), MathF.Round(anchor.Y - AnchorY));
        if (position != lastPosition)
        {
            lastPosition = position;
            Position = position;
        }
    }

    private void LayoutTicks()
    {
        var segments = Math.Clamp(skin.Segments, 0, MaxTicks + 1);
        for (var i = 0; i < MaxTicks; i++)
        {
            var visible = segments >= 2 && i < segments - 1;
            ticks[i].IsVisible = visible;
            if (!visible) continue;
            ticks[i].X = MathF.Round(BarX + 1f + (BarW - 2f) * (i + 1) / segments);
            ticks[i].Color = skin.SegmentLine;
        }
    }

    /// <summary>Centres "Lv90 Name" as one line and puts the aggro tag right after it.</summary>
    private void LayoutNameRow(bool withLevel)
    {
        var nameWidth = name.GetTextDrawSize(false).X;
        var levelWidth = withLevel ? level.GetTextDrawSize(false).X + 3f : 0f;
        var tagWidth = tag.IsVisible ? tag.GetTextDrawSize(false).X + 8f : 0f;
        var focusWidth = focusMark.IsVisible ? 11f : 0f;
        var start = BarX + BarW / 2f - (focusWidth + levelWidth + nameWidth + tagWidth) / 2f;
        if (focusWidth > 0f) focusMark.X = MathF.Round(start + 1f);
        start += focusWidth;
        level.X = MathF.Round(start);
        name.X = MathF.Round(start + levelWidth);
        if (tagWidth > 0f)
        {
            tag.X = MathF.Round(start + levelWidth + nameWidth + 4f);
            tag.Width = tagWidth - 4f;
        }

        var shieldWidth = 0f;
        if (shield.IsVisible)
        {
            shield.X = MathF.Round(start + levelWidth + nameWidth + tagWidth + 4f);
            shieldWidth = 13f;
        }

        chip.X = MathF.Round(start + levelWidth + nameWidth + tagWidth + shieldWidth + 4f);
    }

    /// <summary>Muted far below me, normal near my level, warning 3+ above, danger 6+ above.</summary>
    private Vector4 LevelColor(int enemyLevel, int myLevel)
    {
        if (myLevel <= 0) return skin.Text;
        var diff = enemyLevel - myLevel;
        if (diff >= 6) return skin.AggroLost;
        if (diff >= 3) return skin.ThreatWarn;
        if (diff <= -6) return skin.Muted;
        return skin.Text;
    }

    private void SetLevelText(int value)
    {
        Span<byte> text = stackalloc byte[12];
        text[0] = (byte)'L';
        text[1] = (byte)'v';
        Span<byte> digits = stackalloc byte[4];
        var count = 0;
        do
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (value > 0 && count < digits.Length);

        var n = 2;
        while (count > 0) text[n++] = digits[--count];
        text[n] = 0;
        level.Node->SetText(text[..(n + 1)]);
    }

    private void ApplyHp(PlateData d)
    {
        var pct = d.HpMax == 0 ? 0 : (int)((ulong)d.Hp * 100UL / d.HpMax);
        if (pct == 0 && d.Hp > 0) pct = 1;
        if (pct != lastPercent)
        {
            lastPercent = pct;
            percent.Node->SetNumber(pct);
        }

        var width = d.HpMax == 0 ? 0f : MathF.Round((BarW - 2f) * d.Hp / d.HpMax * 2f) / 2f;
        if (width != lastFillWidth)
        {
            lastFillWidth = width;
            fill.IsVisible = width > 0f;
            if (width > 0f) fill.Width = width;
        }
    }

    private void ApplyAggro(in AggroResult aggro, uint idleRgba, bool untagged)
    {
        var color = aggro.State switch
        {
            AggroState.Have => skin.AggroHave,
            AggroState.CoTank => skin.AggroCoTank,
            AggroState.Lost or AggroState.Pulled => skin.AggroLost,
            AggroState.Warn => skin.ThreatWarn,
            AggroState.Idle when idleRgba != 0 => FromGameColor(idleRgba),
            _ => skin.Hp,
        };

        // Nobody has hit it yet: a pale version of the colour. A DPS/healer sees "don't hit it yet" in pale yellow.
        if (untagged && aggro.State != AggroState.Idle)
            color = Pale(aggro.State is AggroState.Safe or AggroState.Warn ? skin.ThreatWarn : color);

        if (color != lastFillColor)
        {
            lastFillColor = color;
            fill.Color = color;
        }

        var signature = ((long)aggro.State << 40) | ((long)aggro.HolderJob << 16) | (uint)(aggro.Percent & 0xFFFF);
        if (signature == lastChipSignature) return;
        lastChipSignature = signature;

        Span<byte> text = stackalloc byte[48];
        var n = 0;
        switch (aggro.State)
        {
            case AggroState.CoTank:
            case AggroState.Lost:
                if (aggro.HolderJob != 0)
                {
                    // U+E06F is the game font's arrow glyph, then the holder's job ("WHM").
                    text[n++] = 0xEE; text[n++] = 0x81; text[n++] = 0xAF; text[n++] = (byte)' ';
                    var abbreviation = names.JobAbbreviation(aggro.HolderJob);
                    if (abbreviation.Length <= 12) n += Encoding.UTF8.GetBytes(abbreviation, text[n..]);
                }

                if (aggro.State == AggroState.Lost)
                {
                    if (n > 0) text[n++] = (byte)' ';
                    text[n++] = (byte)'!';
                }
                break;
            case AggroState.Safe:
            case AggroState.Warn:
                if (aggro.Percent >= 0) n = WriteNumber(text, Math.Min(aggro.Percent, 999), (byte)'%');
                break;
            case AggroState.Pulled:
                "AGGRO!"u8.CopyTo(text);
                n = 6;
                break;
        }

        if (n == 0)
        {
            chip.IsVisible = false;
            return;
        }

        text[n] = 0;
        chip.Node->SetText(text[..(n + 1)]);
        chip.TextColor = aggro.State switch
        {
            AggroState.CoTank => skin.AggroCoTank,
            AggroState.Warn => skin.ThreatWarn,
            AggroState.Lost or AggroState.Pulled => skin.AggroLost,
            _ => skin.Muted,
        };
        chip.IsVisible = true;
    }

    private Vector4 Pale(Vector4 color) => new(
        (color.X + skin.Text.X) * 0.5f, (color.Y + skin.Text.Y) * 0.5f, (color.Z + skin.Text.Z) * 0.5f, 1f);

    /// <summary>The game's plate colour (ByteColor RGBA), a little darker so white text stays readable.</summary>
    private static Vector4 FromGameColor(uint rgba)
        => new((rgba & 0xFF) / 255f * 0.75f, ((rgba >> 8) & 0xFF) / 255f * 0.75f, ((rgba >> 16) & 0xFF) / 255f * 0.75f, 1f);

    private static int WriteNumber(Span<byte> buffer, int value, byte suffix)
    {
        Span<byte> digits = stackalloc byte[10];
        var count = 0;
        do
        {
            digits[count++] = (byte)('0' + value % 10);
            value /= 10;
        }
        while (value > 0 && count < digits.Length);

        var n = 0;
        while (count > 0) buffer[n++] = digits[--count];
        buffer[n++] = suffix;
        return n;
    }

    private void ApplyTargetStyle(bool targetStyle, Vector4? highlight)
    {
        var style = targetStyle ? 1 : 0;
        if (style != lastTargetStyle)
        {
            lastTargetStyle = style;
            var grow = targetStyle ? 1f : 0f;
            border.Position = new Vector2(BarX - grow, BarY - grow);
            border.Size = new Vector2(BarW + 2f * grow, BarH + 2f * grow);
            arrowLeft.IsVisible = targetStyle;
            arrowRight.IsVisible = targetStyle;
        }

        // Target outline wins; a highlighted enemy gets its rule colour as the frame.
        var color = targetStyle ? skin.Accent : highlight ?? skin.Line;
        if (color != lastBorderColor)
        {
            lastBorderColor = color;
            border.Color = color;
        }
    }

    private void ApplyCast(PlateData d)
    {
        if (!d.IsCasting || d.CastTotal <= 0f)
        {
            HideCast();
            return;
        }

        if (!castShown)
        {
            castGroup.IsVisible = true;
            castShown = true;
        }

        if (d.CastActionId != lastCastAction)
        {
            lastCastAction = d.CastActionId;
            castName.Node->SetText(d.CastActionType == 1 ? names.Action(d.CastActionId) : d.PreviewCastName ?? string.Empty);
        }

        var locked = d.CastInterruptible ? 0 : 1;
        if (locked != lastCastLocked)
        {
            lastCastLocked = locked;
            castFill.Color = locked == 1 ? skin.CastLocked : skin.CastInterruptible;
            lockShackle.IsVisible = lockHole.IsVisible = lockBody.IsVisible = locked == 1;
            castName.X = locked == 1 ? 11f : 0f;
        }

        var progress = Math.Clamp(d.CastCurrent / d.CastTotal, 0f, 1f);
        var width = MathF.Round((BarW - 2f) * progress * 2f) / 2f;
        if (width != lastCastWidth)
        {
            lastCastWidth = width;
            castFill.IsVisible = width > 0f;
            if (width > 0f) castFill.Width = width;
        }

        var tenths = (int)MathF.Ceiling(MathF.Max(0f, d.CastTotal - d.CastCurrent) * 10f);
        if (tenths != lastCastTenths)
        {
            lastCastTenths = tenths;
            SetTenths(castTime, tenths);
        }
    }

    private void HideCast()
    {
        if (!castShown) return;
        castGroup.IsVisible = false;
        castShown = false;
        lastCastAction = uint.MaxValue;
        lastCastTenths = -1;
        lastCastWidth = -1f;
    }

    private void ApplyDots(PlateData d, int maxDots, float expiringSeconds)
    {
        var used = 0;
        for (var s = 0; s < d.MyStatusCount && used < maxDots; s++)
        {
            var iconId = names.StatusIcon(d.MyStatusIds[s], d.MyStatusParams[s]);
            if (iconId == 0) continue; // hidden system statuses have no icon

            var slot = used++;
            if (dotIconIds[slot] != iconId)
            {
                dotIconIds[slot] = iconId;
                dotIcons[slot].IconId = iconId; // the node is reused; it is never disposed while loading
            }

            if (slot >= dotsShown)
                dotIcons[slot].IsVisible = true;

            var remaining = d.MyStatusRemaining[s];
            var seconds = remaining > 0f ? (int)MathF.Ceiling(remaining) : -1;
            if (seconds != dotSeconds[slot])
            {
                dotSeconds[slot] = seconds;
                if (seconds >= 0)
                {
                    dotTimers[slot].Node->SetNumber(seconds);
                    dotTimers[slot].IsVisible = true;
                }
                else
                {
                    dotTimers[slot].IsVisible = false; // permanent status
                }
            }

            var expiring = seconds >= 0 && remaining < expiringSeconds ? 1 : 0;
            if (expiring != dotExpiring[slot])
            {
                dotExpiring[slot] = expiring;
                dotTimers[slot].TextColor = expiring == 1 ? skin.Expiring : skin.Text;
            }
        }

        for (var slot = used; slot < dotsShown; slot++)
        {
            dotIcons[slot].IsVisible = false;
            dotTimers[slot].IsVisible = false;
            dotSeconds[slot] = int.MinValue;
        }

        dotsShown = used;
    }

    /// <summary>Writes e.g. "2.4" into a text node without creating a managed string.</summary>
    private static void SetTenths(TextNode node, int tenths)
    {
        Span<byte> buffer = stackalloc byte[16];
        var whole = tenths / 10;
        var n = 0;

        Span<byte> digits = stackalloc byte[10];
        var count = 0;
        do
        {
            digits[count++] = (byte)('0' + whole % 10);
            whole /= 10;
        }
        while (whole > 0 && count < digits.Length);

        while (count > 0) buffer[n++] = digits[--count];
        buffer[n++] = (byte)'.';
        buffer[n++] = (byte)('0' + tenths % 10);
        buffer[n] = 0;

        node.Node->SetText(buffer[..(n + 1)]);
    }

    /// <summary>True while any DoT icon on this plate is still loading its texture.</summary>
    public bool AnyIconLoading()
    {
        for (var i = 0; i < MaxDots; i++)
        {
            if (dotIconIds[i] != 0 && !dotIcons[i].IsTextureReady)
                return true;
        }

        return false;
    }

    /// <summary>The game object this plate showed last (for clicks).</summary>
    public ulong ObjectId => lastObjectId;
    public bool IsShown => shown;
    public bool IsTargetStyle => lastTargetStyle == 1;
    public float CurrentScale => lastScale;

    /// <summary>Is this point (in the overlay layer's space) on the name, HP bar or cast bar?</summary>
    public bool HitTest(Vector2 point)
    {
        if (!shown || lastScale <= 0f) return false;
        var origin = new Vector2(PlateWidth / 2f, AnchorY);
        var local = origin + (point - (lastPosition + origin)) / lastScale;
        return local.X >= 0f && local.X <= PlateWidth && local.Y >= NameY - 2f && local.Y <= CastY + CastTextY + CastTextH;
    }

    public void Hide()
    {
        if (!shown) return;
        IsVisible = false;
        shown = false;
    }
}
