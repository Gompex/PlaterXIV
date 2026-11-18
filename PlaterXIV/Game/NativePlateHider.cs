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
    private const int VisibleGauge = 0x2;
    private const int DrawFlagEnableGauge = 0x20;

    private readonly int[] arraySlotOf = new int[PlateScanner.MaxPlates];

    private readonly byte[] hiddenMask = new byte[PlateScanner.MaxPlates];
    private readonly long[] usedFrame = new long[PlateScanner.MaxPlates];
    private nint lastAddon;
    private long frame;

    /// <summary>Start of a frame. If the game rebuilt its NamePlate window, forget the old slots.</summary>
    public void BeginFrame(AddonNamePlate* addon)
    {
        frame++;
        if ((nint)addon != lastAddon)
        {
            Array.Clear(hiddenMask);
            lastAddon = (nint)addon;
        }
    }

    public void HideOn(AddonNamePlate* addon, int namePlateIndex)
    {
        if (addon == null || namePlateIndex < 0 || namePlateIndex >= PlateScanner.MaxPlates) return;

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
        if (addon == null) return;

        var pending = false;
        for (var i = 0; i < PlateScanner.MaxPlates; i++)
        {
            if (hiddenMask[i] != 0 && usedFrame[i] != frame) { pending = true; break; }
        }
        if (!pending) return;

        var numbers = NamePlateNumberArray.Instance();
        if (numbers == null) return;
        MapArraySlots(numbers);

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
        var numbers = NamePlateNumberArray.Instance();
        if (addon != null && numbers != null && (nint)addon == lastAddon)
        {
            MapArraySlots(numbers);
            for (var i = 0; i < PlateScanner.MaxPlates; i++)
            {
                if (hiddenMask[i] != 0 && arraySlotOf[i] >= 0)
                    Restore(addon, i, ref numbers->ObjectData[arraySlotOf[i]]);
            }
        }

        Refresh();
    }
}
