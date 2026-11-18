using System.Numerics;

namespace PlaterXIV.Render;

/// <summary>What the matching rule (if any) does to one plate this frame.</summary>
public struct RuleEffect
{
    public int RuleIndex;          // -1 = no rule
    public bool Hide;
    public bool Highlight;
    public Vector4 HighlightColor;
    public string? Tag;
    public float Scale;            // 1 = unchanged
    public bool NeverDim;
    public bool NoAggroColors;

    public static RuleEffect None => new() { RuleIndex = -1, Scale = 1f };

    public static RuleEffect From(NameRule rule, int index) => new()
    {
        RuleIndex = index,
        Hide = rule.Hide,
        Highlight = rule.Highlight,
        HighlightColor = rule.HighlightColor,
        Tag = rule.Highlight && rule.Tag.Length > 0 ? rule.Tag : null,
        Scale = rule.ChangeSize ? rule.SizePercent / 100f : 1f,
        NeverDim = rule.NeverDim,
        NoAggroColors = rule.NoAggroColors,
    };
}
