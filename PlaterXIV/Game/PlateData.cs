using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace PlaterXIV.Game;

/// <summary>
/// One snapshot of one visible game nameplate. 50 instances are created once and refilled every frame,
/// so scanning never allocates.
/// </summary>
public sealed class PlateData
{
    public const int MaxStatuses = 8;

    public bool Valid;

    // Where the plate is in the game's own data
    public int Slot;                 // index in UI3DModule.NamePlateObjectInfoPointers / NamePlateNumberArray.ObjectData
    public int NamePlateIndex;       // UI3DModule.ObjectInfo.NamePlateIndex (index into AddonNamePlate.NamePlateObjectArray)
    public int ArrayNamePlateIndex;  // NamePlateNumberArray.ObjectData[slot].NamePlateObjectIndex (should equal NamePlateIndex)
    public UIObjectKind Kind;

    // The game object
    public nint GameObject;
    public uint EntityId;
    public ulong GameObjectId;
    public uint BaseId;
    public uint NameId;
    public ObjectKind ObjectKind;
    public byte SubKind;

    // Level and health
    public byte Level;
    public uint Hp;
    public uint HpMax;

    // Position as the game computed it (NamePlateNumberArray) and as its plate node is placed
    public int GameX;
    public int GameY;
    public int GameScale;
    public float GameDepth;
    public float RootX;
    public float RootY;
    public float RootScreenX;
    public float RootScreenY;
    public float RootScale;
    public float RootScaleY;
    public float RootWidth;
    public float RootHeight;
    public bool RootVisible;

    // Targeting
    public bool IsTarget;
    public bool IsFocus;
    public ulong EnemyTargetId;      // the enemy's own hard target (who it is attacking)

    // Cast
    public bool IsCasting;
    public bool CastInterruptible;
    public byte CastActionType;
    public uint CastActionId;
    public float CastCurrent;
    public float CastTotal;

    // Enmity: my enmity % on this enemy from UIState.Hater, -1 when I'm not on its list
    public int MyEnmity = -1;

    // Statuses I applied to this object
    public int MyStatusCount;
    public readonly ushort[] MyStatusIds = new ushort[MaxStatuses];
    public readonly float[] MyStatusRemaining = new float[MaxStatuses];
    public readonly ushort[] MyStatusParams = new ushort[MaxStatuses];

    /// <summary>Settings preview only: shown instead of a game object's name / spell name.</summary>
    public string? PreviewName;
    public string? PreviewCastName;

    public float HpPercent => HpMax == 0 ? 0f : Hp * 100f / HpMax;
}
