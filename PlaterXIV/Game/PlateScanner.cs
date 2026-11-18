using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PlaterXIV.Util;
using CSBattleChara = FFXIVClientStructs.FFXIV.Client.Game.Character.BattleChara;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace PlaterXIV.Game;

/// <summary>
/// Reads every visible game nameplate once per frame, right before the NamePlate addon draws
/// (the game has finished laying the plates out by then). No allocations on this path.
/// </summary>
public sealed unsafe class PlateScanner : IDisposable
{
    public const int MaxPlates = 50;
    private const uint InvalidId = 0xE0000000;

    public readonly PlateData[] Plates = new PlateData[MaxPlates];

    /// <summary>Number of slots filled this frame (some may still be Valid == false).</summary>
    public int Count { get; private set; }
    public long Frame { get; private set; }
    public bool InPvP { get; private set; }
    public uint LocalEntityId { get; private set; }
    public int LocalLevel { get; private set; }
    public int ActiveNamePlateCount { get; private set; }
    public int ObjectInfoCount { get; private set; }
    public double LastScanMicroseconds { get; private set; }

    /// <summary>True when my current target has a nameplate this frame.</summary>
    public bool TargetOnScreen { get; private set; }

    /// <summary>The NamePlate addon seen this frame (0 when none) and its root transform.</summary>
    public nint NamePlateAddon { get; private set; }
    public Vector2 NamePlateRootPosition { get; private set; }
    public Vector2 NamePlateRootScale { get; private set; } = Vector2.One;

    /// <summary>Called right after every scan, on the framework thread, before the game draws the plates.</summary>
    public Action? AfterScan;

    private readonly PerfMeter perf;
    private bool registered;

    public PlateScanner(PerfMeter perf)
    {
        this.perf = perf;
        for (var i = 0; i < MaxPlates; i++)
            Plates[i] = new PlateData();
    }

    /// <summary>Must be called on the framework thread.</summary>
    public void Enable()
    {
        if (registered) return;
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreDraw, "NamePlate", OnNamePlatePreDraw);
        registered = true;
    }

    public void Dispose()
    {
        if (!registered) return;
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreDraw, "NamePlate", OnNamePlatePreDraw);
        registered = false;
    }

    private void OnNamePlatePreDraw(AddonEvent type, AddonArgs args)
    {
        var start = Stopwatch.GetTimestamp();
        Scan((AddonNamePlate*)args.Addon.Address);
        LastScanMicroseconds = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        perf.RecordScan(LastScanMicroseconds, Count);
        AfterScan?.Invoke();
    }

    private void Scan(AddonNamePlate* addon)
    {
        Frame++;
        Count = 0;
        TargetOnScreen = false;

        NamePlateAddon = (nint)addon;
        if (addon != null && addon->RootNode != null)
        {
            var root = addon->RootNode;
            NamePlateRootPosition = new Vector2(root->X, root->Y);
            NamePlateRootScale = new Vector2(root->ScaleX > 0f ? root->ScaleX : 1f, root->ScaleY > 0f ? root->ScaleY : 1f);
        }

        InPvP = Services.ClientState.IsPvP;
        if (InPvP || addon == null) return;

        var control = Control.Instance();
        LocalEntityId = control != null ? control->LocalPlayerEntityId : 0;
        if (LocalEntityId is 0 or InvalidId) return;
        var localPlayer = control->LocalPlayer;
        LocalLevel = localPlayer != null ? ((CSCharacter*)localPlayer)->CharacterData.Level : 0;

        var framework = CSFramework.Instance();
        if (framework == null) return;
        var uiModule = framework->GetUIModule();
        if (uiModule == null) return;
        var ui3d = uiModule->GetUI3DModule();
        var numbers = NamePlateNumberArray.Instance();
        var uiState = UIState.Instance();
        if (ui3d == null || numbers == null || uiState == null) return;

        var targets = TargetSystem.Instance();
        var target = targets != null ? targets->Target : null;
        var focus = targets != null ? targets->FocusTarget : null;

        ObjectInfoCount = ui3d->NamePlateObjectInfoCount;
        ActiveNamePlateCount = numbers->ActiveNamePlateCount;
        var count = Math.Min(Math.Min(ObjectInfoCount, ActiveNamePlateCount), MaxPlates);
        if (count < 0) count = 0;

        var haterCount = Math.Min(uiState->Hater.HaterCount, 32);

        for (var i = 0; i < count; i++)
        {
            var d = Plates[i];
            d.Valid = false;

            var info = ui3d->NamePlateObjectInfoPointers[i].Value;
            if (info == null) continue;
            var go = info->GameObject;
            if (go == null) continue;

            d.Slot = i;
            d.NamePlateIndex = info->NamePlateIndex;
            d.Kind = info->NamePlateObjectKind;

            ref var od = ref numbers->ObjectData[i];
            d.ArrayNamePlateIndex = od.NamePlateObjectIndex;
            d.GameX = od.X;
            d.GameY = od.Y;
            d.GameScale = od.Scale;
            d.GameDepth = od.Depth;

            d.RootVisible = false;
            d.RootX = d.RootY = d.RootScreenX = d.RootScreenY = 0;
            d.RootScale = d.RootScaleY = d.RootWidth = d.RootHeight = 0;
            if (d.NamePlateIndex is >= 0 and < MaxPlates)
            {
                var root = addon->NamePlateObjectArray[d.NamePlateIndex].RootComponentNode;
                if (root != null)
                {
                    var res = (AtkResNode*)root;
                    d.RootX = res->X;
                    d.RootY = res->Y;
                    d.RootScreenX = res->ScreenX;
                    d.RootScreenY = res->ScreenY;
                    d.RootScale = res->ScaleX;
                    d.RootScaleY = res->ScaleY;
                    d.RootWidth = res->Width;
                    d.RootHeight = res->Height;
                    d.RootVisible = res->IsVisible();
                }
            }

            d.GameObject = (nint)go;
            d.EntityId = go->EntityId;
            d.GameObjectId = go->GetGameObjectId();
            d.BaseId = go->BaseId;
            d.ObjectKind = go->ObjectKind;
            d.SubKind = go->SubKind;
            d.IsTarget = go == target;
            if (d.IsTarget) TargetOnScreen = true;
            d.IsFocus = go == focus;

            d.Hp = d.HpMax = 0;
            d.Level = 0;
            d.NameId = 0;
            d.EnemyTargetId = 0;
            d.IsCasting = d.CastInterruptible = false;
            d.CastActionType = 0;
            d.CastActionId = 0;
            d.CastCurrent = d.CastTotal = 0;
            d.MyEnmity = -1;
            d.MyStatusCount = 0;

            if (go->ObjectKind is ObjectKind.BattleNpc or ObjectKind.Pc)
            {
                var ch = (CSCharacter*)go;
                d.Hp = ch->CharacterData.Health;
                d.HpMax = ch->CharacterData.MaxHealth;
                d.Level = ch->CharacterData.Level;
                d.NameId = ch->NameId;
                d.EnemyTargetId = ch->TargetId;

                var bc = (CSBattleChara*)go;
                ref var cast = ref bc->CastInfo;
                d.IsCasting = cast.IsCasting;
                if (d.IsCasting)
                {
                    d.CastInterruptible = cast.Interruptible;
                    d.CastActionType = cast.ActionType;
                    d.CastActionId = cast.ActionId;
                    d.CastCurrent = cast.CurrentCastTime;
                    d.CastTotal = cast.TotalCastTime;
                }

                var statuses = bc->StatusManager.Status;
                for (var s = 0; s < statuses.Length && d.MyStatusCount < PlateData.MaxStatuses; s++)
                {
                    ref var st = ref statuses[s];
                    if (st.StatusId == 0 || st.SourceObject.ObjectId != LocalEntityId) continue;
                    d.MyStatusIds[d.MyStatusCount] = st.StatusId;
                    d.MyStatusRemaining[d.MyStatusCount] = st.RemainingTime;
                    d.MyStatusParams[d.MyStatusCount] = st.Param;
                    d.MyStatusCount++;
                }

                if (d.EntityId != InvalidId)
                {
                    var haters = uiState->Hater.Haters;
                    for (var h = 0; h < haterCount; h++)
                    {
                        if (haters[h].EntityId != d.EntityId) continue;
                        d.MyEnmity = haters[h].Enmity;
                        break;
                    }
                }
            }

            d.Valid = true;
        }

        Count = count;
    }

    /// <summary>Looks up any object by its GameObjectId (used by the debug window, not the hot path).</summary>
    public static CSGameObject* FindObject(ulong gameObjectId)
    {
        if (gameObjectId is 0 or InvalidId) return null;
        var mgr = GameObjectManager.Instance();
        return mgr == null ? null : mgr->Objects.GetObjectByGameObjectId(gameObjectId);
    }
}
