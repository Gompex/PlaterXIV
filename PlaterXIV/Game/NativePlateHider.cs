using System;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PlaterXIV.Game;

/// <summary>
/// Hides the game's own name text and HP gauge on the enemy plates we replace (markers and icons stay).
/// Called every frame right before the game draws its plates. The game reuses its 50 plate slots for
/// other objects (players, NPCs), so every slot we touched is tracked and given back the moment it is no
/// longer one of our enemy plates, otherwise players would inherit the hidden name.
/// Giving back means "show exactly what the game wants for whoever uses the slot now" (its own
/// name/gauge flags), never "show everything": friendly NPCs must not get an enemy HP gauge.
/// </summary>
public sealed unsafe class NativePlateHider : IDisposable
{
    private const byte NameBit = 1;
    private const byte GaugeContainerBit = 2;
    private const byte GaugeFillBit = 4;
    private const byte GaugeBackgroundBit = 8;

    // NamePlateNumberArray flags (see ClientStructs): what the game itself wants drawn on a plate.
    private const int VisibleName = 0x1;
    private const int VisibleGauge = 0x2;
    private const int AuditEveryFrames = 30;
    private const int DrawFlagEnableGauge = 0x20;

    private readonly int[] arraySlotOf = new int[PlateScanner.MaxPlates];

    private readonly byte[] hiddenMask = new byte[PlateScanner.MaxPlates];
    private readonly long[] usedFrame = new long[PlateScanner.MaxPlates];
    private nint lastAddon;
    private long frame;
    private NamePlateNumberArray* numbers;

    /// <summary>Hide requests turned down last frame because the game's slot belonged to something else.</summary>
    public int RefusedLastFrame { get; private set; }

    /// <summary>Names found hidden that the game wants shown (left over from an unload, a reload or an older build), shown again.</summary>
    public int NamesRepaired { get; private set; }
    private int refused;

    /// <summary>Start of a frame. If the game rebuilt its NamePlate window, forget the old slots.</summary>
    public void BeginFrame(AddonNamePlate* addon)
    {
        frame++;
        RefusedLastFrame = refused;
        refused = 0;
        if ((nint)addon != lastAddon)
        {
            Array.Clear(hiddenMask);
            lastAddon = (nint)addon;
        }

        // Which object each of the game's 50 plate slots is drawing this frame (the game's own mapping).
        numbers = NamePlateNumberArray.Instance();
        if (numbers != null) MapArraySlots(numbers);
        else Array.Fill(arraySlotOf, -1);
    }

    /// <summary>
    /// Hides the game's name and gauge in plate slot <paramref name="namePlateIndex"/>, but only after checking
    /// with the game that the slot really draws this enemy right now. The object list we read can lag a
    /// frame behind the slots (a mob dies, a key or chest takes its slot): without the check we would keep
    /// hiding that object's name, and since the game doesn't redraw unchanged plates it stayed hidden.
    /// </summary>
    public void HideOn(AddonNamePlate* addon, int namePlateIndex, uint entityId)
    {
        if (addon == null || numbers == null || namePlateIndex < 0 || namePlateIndex >= PlateScanner.MaxPlates) return;

        var slot = arraySlotOf[namePlateIndex];
        if (slot < 0) return; // the game isn't drawing this slot
        ref var data = ref numbers->ObjectData[slot];
        if (data.NamePlateKind != UIObjectKind.BattleNpcEnemy || (data.EntityId != 0 && data.EntityId != entityId))
        {
            refused++;
            return;
        }

        usedFrame[namePlateIndex] = frame;
        ref var plate = ref addon->NamePlateObjectArray[namePlateIndex];
        var mask = hiddenMask[namePlateIndex];
        if (Hide((AtkResNode*)plate.NameText)) mask |= NameBit;
        if (Hide((AtkResNode*)plate.GaugeContainer)) mask |= GaugeContainerBit;
        if (Hide((AtkResNode*)plate.GaugeFill)) mask |= GaugeFillBit;
        if (Hide((AtkResNode*)plate.GaugeBackground)) mask |= GaugeBackgroundBit;
        hiddenMask[namePlateIndex] = mask;
    }

    /// <summary>End of a frame: give back every slot we hid that is not one of our enemy plates any more.</summary>
    public void EndFrame(AddonNamePlate* addon)
    {
        if (addon == null || numbers == null) return;

        // Safety net, every half second and on the first frame: any name the game wants drawn but that is
        // hidden, on a slot we are not hiding right now, is shown again.
        if (frame % AuditEveryFrames == 1) RepairNames(addon);

        var pending = false;
        for (var i = 0; i < PlateScanner.MaxPlates; i++)
        {
            if (hiddenMask[i] != 0 && usedFrame[i] != frame) { pending = true; break; }
        }
        if (!pending) return;

        var restored = false;
        for (var i = 0; i < PlateScanner.MaxPlates; i++)
        {
            if (hiddenMask[i] == 0 || usedFrame[i] == frame) continue;

            // Slot not in use by anything right now: keep it pending until the game reuses it.
            var slot = arraySlotOf[i];
            if (slot < 0) continue;

            Restore(addon, i, ref numbers->ObjectData[slot]);
            restored = true;
        }

        if (restored)
            Services.NamePlateGui.RequestRedraw(); // let the game re-check its own plates too
    }

    private void RepairNames(AddonNamePlate* addon)
    {
        var count = Math.Min(numbers->ActiveNamePlateCount, PlateScanner.MaxPlates);
        for (var a = 0; a < count; a++)
        {
            ref var data = ref numbers->ObjectData[a];
            var index = data.NamePlateObjectIndex;
            if (index < 0 || index >= PlateScanner.MaxPlates || usedFrame[index] == frame) continue;
            if ((data.VisibilityFlags & VisibleName) == 0) continue;

            var node = (AtkResNode*)addon->NamePlateObjectArray[index].NameText;
            if (node == null || node->IsVisible()) continue;
            node->ToggleVisibility(true);
            NamesRepaired++;
        }
    }

    /// <summary>
    /// Test helper (/plater names, debug button): shows the game's name on every plate we are not hiding
    /// this frame, whatever the game's flags say. Returns how many were hidden.
    /// </summary>
    public int ForceShowNames()
    {
        var addon = (AddonNamePlate*)RaptureAtkUnitManager.Instance()->GetAddonByName("NamePlate");
        var array = NamePlateNumberArray.Instance();
        if (addon == null || array == null) return 0;

        var shownNow = 0;
        var count = Math.Min(array->ActiveNamePlateCount, PlateScanner.MaxPlates);
        for (var a = 0; a < count; a++)
        {
            var index = array->ObjectData[a].NamePlateObjectIndex;
            if (index < 0 || index >= PlateScanner.MaxPlates || usedFrame[index] == frame) continue;
            var node = (AtkResNode*)addon->NamePlateObjectArray[index].NameText;
            if (node == null || node->IsVisible()) continue;
            node->ToggleVisibility(true);
            shownNow++;
        }

        return shownNow;
    }

    /// <summary>Debug: is the game's name node in this slot visible, and what the game's flags say.</summary>
    public string DescribeName(AddonNamePlate* addon, int namePlateIndex, int arraySlot)
    {
        if (addon == null || namePlateIndex < 0 || namePlateIndex >= PlateScanner.MaxPlates) return "-";
        var node = (AtkResNode*)addon->NamePlateObjectArray[namePlateIndex].NameText;
        var array = NamePlateNumberArray.Instance();
        var flags = array != null && arraySlot >= 0 && arraySlot < PlateScanner.MaxPlates ? array->ObjectData[arraySlot].VisibilityFlags : -1;
        var state = node == null ? "no node" : node->IsVisible() ? "shown" : "HIDDEN";
        return $"{state} · flags 0x{flags:X} · ours {(hiddenMask[namePlateIndex] != 0 ? "yes" : "no")}";
    }

    private void MapArraySlots(NamePlateNumberArray* numbers)
    {
        Array.Fill(arraySlotOf, -1);
        var count = Math.Min(numbers->ActiveNamePlateCount, PlateScanner.MaxPlates);
        for (var a = 0; a < count; a++)
        {
            var index = numbers->ObjectData[a].NamePlateObjectIndex;
            if (index >= 0 && index < PlateScanner.MaxPlates)
                arraySlotOf[index] = a;
        }
    }

    private void Restore(AddonNamePlate* addon, int index, ref NamePlateNumberArray.NamePlateObjectIntArrayData data)
    {
        ref var plate = ref addon->NamePlateObjectArray[index];
        var mask = hiddenMask[index];
        // Names: always give them back (that already worked); the game hides them again itself when it
        // wants to. Gauges: only when the game's own flags say this plate has one.
        var wantName = true;
        var wantGauge = (data.VisibilityFlags & VisibleGauge) != 0 && (data.DrawFlags & DrawFlagEnableGauge) != 0;

        if ((mask & NameBit) != 0) Set((AtkResNode*)plate.NameText, wantName);
        if ((mask & GaugeContainerBit) != 0) Set((AtkResNode*)plate.GaugeContainer, wantGauge);
        if ((mask & GaugeFillBit) != 0) Set((AtkResNode*)plate.GaugeFill, wantGauge);
        if ((mask & GaugeBackgroundBit) != 0) Set((AtkResNode*)plate.GaugeBackground, wantGauge);
        hiddenMask[index] = 0;
    }

    private static bool Hide(AtkResNode* node)
    {
        if (node == null || !node->IsVisible()) return false;
        node->ToggleVisibility(false);
        return true;
    }

    private static void Set(AtkResNode* node, bool visible)
    {
        if (node != null) node->ToggleVisibility(visible);
    }

    /// <summary>Asks the game to rebuild its plates.</summary>
    public void Refresh() => Services.NamePlateGui.RequestRedraw();

    /// <summary>Gives every slot back (framework thread).</summary>
    public void Dispose()
    {
        var addon = (AddonNamePlate*)RaptureAtkUnitManager.Instance()->GetAddonByName("NamePlate");
        numbers = NamePlateNumberArray.Instance();
        if (addon != null && numbers != null && (nint)addon == lastAddon)
        {
            MapArraySlots(numbers);
            for (var i = 0; i < PlateScanner.MaxPlates; i++)
            {
                if (hiddenMask[i] == 0) continue;
                if (arraySlotOf[i] >= 0)
                {
                    Restore(addon, i, ref numbers->ObjectData[arraySlotOf[i]]);
                }
                else
                {
                    // Not drawn right now: put back exactly what we hid. The game only re-applies what it thinks
                    // changed, so a name left hidden here stayed hidden on whoever got this slot next.
                    ref var plate = ref addon->NamePlateObjectArray[i];
                    var mask = hiddenMask[i];
                    if ((mask & NameBit) != 0) Set((AtkResNode*)plate.NameText, true);
                    if ((mask & GaugeContainerBit) != 0) Set((AtkResNode*)plate.GaugeContainer, true);
                    if ((mask & GaugeFillBit) != 0) Set((AtkResNode*)plate.GaugeFill, true);
                    if ((mask & GaugeBackgroundBit) != 0) Set((AtkResNode*)plate.GaugeBackground, true);
                    hiddenMask[i] = 0;
                }
            }

            frame++; // nothing is ours any more
            RepairNames(addon);
        }

        Refresh();
    }
}
