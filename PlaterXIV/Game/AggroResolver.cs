using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using PlaterXIV.Util;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace PlaterXIV.Game;

public enum AggroState : byte
{
    /// <summary>Not fighting me or my party (colour comes from the game's own hostility colour).</summary>
    Idle,
    // Tank mode
    Have,
    CoTank,
    Lost,
    // DPS / healer mode
    Safe,
    Warn,
    Pulled,
}

public enum AggroMode
{
    Auto = 0,
    Tank = 1,
    DpsHealer = 2,
}

/// <summary>Result for one enemy: the state, who holds it (job id, 0 = unknown) and my threat %.</summary>
public struct AggroResult
{
    public AggroState State;
    public uint HolderJob;
    public int Percent; // -1 when not shown
}

/// <summary>
/// Decides the aggro colour of each enemy from data the game already shows: the enemy's own target,
/// my enmity list (UIState.Hater) and my party. Display only. No allocations.
/// </summary>
public sealed unsafe class AggroResolver
{
    private const uint InvalidId = 0xE0000000;
    private const byte TankRole = 1;

    private readonly NameCache names;
    private uint localEntityId;
    private GroupManager* groups;
    private GameObjectManager* objects;

    public bool TankMode { get; private set; }
    public uint LocalJob { get; private set; }

    public AggroResolver(NameCache names) => this.names = names;

    /// <summary>Once per frame, before resolving enemies.</summary>
    public void BeginFrame(uint localEntityId, AggroMode mode)
    {
        this.localEntityId = localEntityId;
        groups = GroupManager.Instance();
        objects = GameObjectManager.Instance();

        var control = Control.Instance();
        var player = control != null ? control->LocalPlayer : null;
        LocalJob = player != null ? ((CSCharacter*)player)->CharacterData.ClassJob : 0u;

        TankMode = mode switch
        {
            AggroMode.Tank => true,
            AggroMode.DpsHealer => false,
            _ => names.JobRole(LocalJob) == TankRole,
        };
    }

    public AggroResult Resolve(PlateData d, int warnPercent)
    {
        var result = new AggroResult { State = AggroState.Idle, HolderJob = 0, Percent = -1 };
        var targetObjectId = (uint)(d.EnemyTargetId & 0xFFFFFFFF);
        var hasTarget = targetObjectId != 0 && targetObjectId != InvalidId;
        var onMyList = d.MyEnmity >= 0;

        if (hasTarget && targetObjectId == localEntityId)
        {
            result.State = TankMode ? AggroState.Have : AggroState.Pulled;
            return result;
        }

        // Who is it attacking? Only players in my party count (or anyone, when it also hates me).
        CSGameObject* holder = null;
        if (hasTarget && objects != null)
            holder = objects->Objects.GetObjectByGameObjectId(d.EnemyTargetId);
        var holderIsPartyPlayer = holder != null
                                  && holder->ObjectKind == ObjectKind.Pc
                                  && (onMyList || (groups != null && groups->MainGroup.IsEntityIdInParty(holder->EntityId)));

        if (TankMode)
        {
            if (holderIsPartyPlayer)
            {
                result.HolderJob = ((CSCharacter*)holder)->CharacterData.ClassJob;
                result.State = names.JobRole(result.HolderJob) == TankRole ? AggroState.CoTank : AggroState.Lost;
            }
            else if (onMyList)
            {
                // Attacking a pet/chocobo/NPC, or briefly without a target.
                result.State = d.MyEnmity >= 100 ? AggroState.Have : AggroState.Lost;
            }

            return result;
        }

        if (onMyList)
        {
            result.Percent = d.MyEnmity;
            result.State = d.MyEnmity >= warnPercent ? AggroState.Warn : AggroState.Safe;
        }
        else if (holderIsPartyPlayer)
        {
            result.State = AggroState.Safe; // my party is fighting it, I haven't touched it yet
        }

        return result;
    }

    /// <summary>The game's own nameplate colour for this enemy (passive / aggressive / engaged / claimed).</summary>
    public static uint GameHostilityColor(PlateData d)
    {
        var go = (CSGameObject*)d.GameObject;
        return go == null ? 0u : go->GetNamePlateColors().Color.RGBA;
    }
}
