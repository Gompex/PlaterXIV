using System;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PlaterXIV.Render;

/// <summary>Per-skin colour overrides the user picked in Settings → Skin. Null = use the skin's colour.</summary>
[Serializable]
public sealed class SkinOverrides
{
    public Vector4? Hp { get; set; }
    public Vector4? Have { get; set; }
    public Vector4? CoTank { get; set; }
    public Vector4? Lost { get; set; }
    public Vector4? Warn { get; set; }
    public Vector4? CastInterruptible { get; set; }
    public Vector4? CastLocked { get; set; }
    public Vector4? Expiring { get; set; }
}

/// <summary>
/// Plate colours and fonts for one skin. Values match the shared Skins.Core tokens on the design canvas.
/// </summary>
public sealed record Skin
{
    public static readonly string[] Names = ["Signal", "Ledger", "Heraldry"];

    public required string Name { get; init; }

    public required Vector4 Text { get; init; }
    public required Vector4 TextOutline { get; init; }
    public required Vector4 Track { get; init; }
    public required Vector4 Line { get; init; }
    public required Vector4 Accent { get; init; }
    public required Vector4 Hp { get; init; }
    public required Vector4 AggroHave { get; init; }
    public required Vector4 AggroCoTank { get; init; }
    public required Vector4 AggroLost { get; init; }
    public required Vector4 ThreatWarn { get; init; }
    public required Vector4 Muted { get; init; }
    public required Vector4 CastInterruptible { get; init; }
    public required Vector4 CastLocked { get; init; }
    public required Vector4 Expiring { get; init; }
    /// <summary>HP bar split into this many segments by thin dark ticks (0 = smooth bar).</summary>
    public int Segments { get; init; }
    public Vector4 SegmentLine { get; init; } = Hex(0x0B0D0E, 0.7f);
    public required FontType NameFont { get; init; }
    public required uint NameFontSize { get; init; }
    public required uint NumberFontSize { get; init; }

    public static readonly Skin Signal = new()
    {
        Name = "Signal",
        Text = Hex(0xF4F6F7),
        TextOutline = Hex(0x000000),
        Track = Hex(0x262B2F),          // HealFrames Signal track
        Line = Hex(0x0B0D0E),           // HealFrames Signal surface, as a thin dark frame
        Segments = 10,                  // HealFrames Signal: 10% ticks on every bar
        SegmentLine = Hex(0x0B0D0E, 0.7f),
        Accent = Hex(0xF4F6F7),
        Hp = Hex(0x56636A),
        AggroHave = Hex(0x009E73),
        AggroCoTank = Hex(0x56B4E9),
        AggroLost = Hex(0xD55E00),
        ThreatWarn = Hex(0xF0E442),
        Muted = Hex(0x8A949A),
        CastInterruptible = Hex(0xE69F00),
        CastLocked = Hex(0x8A949A),
        Expiring = Hex(0xD55E00),
        NameFont = FontType.Axis,
        NameFontSize = 13,
        NumberFontSize = 10,
    };

    public static readonly Skin Ledger = new()
    {
        Name = "Ledger",
        Text = Hex(0xEEEAF4),
        TextOutline = Hex(0x000000),
        Track = Hex(0x0E0D11, 0.88f),
        Line = Hex(0x34323C),
        Accent = Hex(0xE09A2B),
        Hp = Hex(0x9C3A3A),
        AggroHave = Hex(0x4CA36E),
        AggroCoTank = Hex(0x4F7BD9),
        AggroLost = Hex(0xF05A3C),
        ThreatWarn = Hex(0xE8B33A),
        Muted = Hex(0xA6A1B2),
        CastInterruptible = Hex(0xE09A2B),
        CastLocked = Hex(0x8A8594),
        Expiring = Hex(0xF05A3C),
        NameFont = FontType.TrumpGothic, // the game's condensed face
        NameFontSize = 16,
        NumberFontSize = 10,
    };

    public static readonly Skin Heraldry = new()
    {
        Name = "Heraldry",
        Text = Hex(0xF1E8D6),
        TextOutline = Hex(0x1A1200),
        Track = Hex(0x090D16, 0.9f),
        Line = Hex(0x8A7440),
        Accent = Hex(0xE7C77F),
        Hp = Hex(0x8E2F35),
        AggroHave = Hex(0x3E9A74),
        AggroCoTank = Hex(0x4A6CC2),
        AggroLost = Hex(0xE2574C),
        ThreatWarn = Hex(0xD9A441),
        Muted = Hex(0xB3A88F),
        CastInterruptible = Hex(0xC9A45A),
        CastLocked = Hex(0x6B7690),
        Expiring = Hex(0xE2574C),
        NameFont = FontType.Axis,
        NameFontSize = 13,
        NumberFontSize = 10,
    };

    public static Skin Get(string? name) => name switch
    {
        "Ledger" => Ledger,
        "Heraldry" => Heraldry,
        _ => Signal,
    };

    /// <summary>This skin with the user's colour overrides on top.</summary>
    public Skin WithOverrides(SkinOverrides? o) => o == null ? this : this with
    {
        Hp = o.Hp ?? Hp,
        AggroHave = o.Have ?? AggroHave,
        AggroCoTank = o.CoTank ?? AggroCoTank,
        AggroLost = o.Lost ?? AggroLost,
        ThreatWarn = o.Warn ?? ThreatWarn,
        CastInterruptible = o.CastInterruptible ?? CastInterruptible,
        CastLocked = o.CastLocked ?? CastLocked,
        Expiring = o.Expiring ?? Expiring,
    };

    public static Vector4 Hex(uint rgb, float alpha = 1f)
        => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);
}
