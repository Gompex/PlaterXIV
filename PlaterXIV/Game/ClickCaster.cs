using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using NameCache = PlaterXIV.Util.NameCache; // ClientStructs also has a Client.Game.NameCache

namespace PlaterXIV.Game;

/// <summary>
/// Middle-click on one of our plates: casts the player's interrupt on that enemy, exactly once per click,
/// the same way a hotbar button press would (the game queues or refuses it as usual). Nothing repeats,
/// nothing is decided for the player; jobs without an interrupt simply do nothing.
/// </summary>
public sealed unsafe class ClickCaster
{
    public const uint Interject = 7538;  // tank role action
    public const uint HeadGraze = 7551;  // physical ranged role action

    private readonly NameCache names;

    public string LastResult { get; private set; } = "-";

    public ClickCaster(NameCache names) => this.names = names;

    /// <summary>True on the one frame the middle mouse button went down.</summary>
    public static bool MiddlePressed(out Vector2 cursor)
    {
        cursor = default;
        var input = UIInputData.Instance();
        if (input == null) return false;

        ref var mouse = ref input->InputData.CursorInputs;
        if ((mouse.MouseButtonPressedFlags & MouseButtonFlags.MBUTTON) == 0) return false;

        cursor = new Vector2(mouse.PositionX, mouse.PositionY);
        return true;
    }

    /// <summary>True when the cursor is over a game window (not over the world or nameplates).</summary>
    public static bool CursorOverOtherUi()
    {
        var stage = AtkStage.Instance();
        if (stage == null || stage->AtkCollisionManager == null) return false;
        var addon = stage->AtkCollisionManager->IntersectingAddon;
        if (addon == null) return false;
        var name = addon->NameString; // only on click frames
        return name != "NamePlate" && !name.StartsWith("KTK_Overlay");
    }

    /// <summary>The interrupt for this job, 0 when it has none.</summary>
    public uint InterruptFor(uint classJobId) => names.RoleOf(classJobId) switch
    {
        PlaterRole.Tank => Interject,
        PlaterRole.PhysicalRanged => HeadGraze,
        _ => 0,
    };

    public void CastInterrupt(ulong targetGameObjectId, uint classJobId)
    {
        var actionId = InterruptFor(classJobId);
        if (actionId == 0)
        {
            LastResult = $"no interrupt for {names.JobAbbreviation(classJobId)}";
            return;
        }

        var actions = ActionManager.Instance();
        if (actions == null) return;

        var used = actions->UseAction(ActionType.Action, actionId, targetGameObjectId);
        LastResult = $"{names.Action(actionId)} → {targetGameObjectId:X} {(used ? "sent" : "refused by the game")}";
        Services.Log.Debug($"[Plater] middle-click: {LastResult}");
    }
}
