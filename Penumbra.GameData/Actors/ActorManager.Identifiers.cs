using System;
using System.Diagnostics.CodeAnalysis;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json.Linq;
using Penumbra.String;

namespace Penumbra.GameData.Actors;

public partial class ActorManager
{
    /// <summary>
    /// Try to create an ActorIdentifier from a already parsed JObject <paramref name="data"/>.
    /// </summary>
    /// <param name="data">A parsed JObject</param>
    /// <returns>ActorIdentifier.Invalid if the JObject can not be converted, a valid ActorIdentifier otherwise.</returns>
    public ActorIdentifier FromJson(JObject? data)
    {
        if (data == null)
            return ActorIdentifier.Invalid;

        var type = data[nameof(ActorIdentifier.Type)]?.ToObject<IdentifierType>() ?? IdentifierType.Invalid;
        switch (type)
        {
            case IdentifierType.Player:
            {
                var name      = ByteString.FromStringUnsafe(data[nameof(ActorIdentifier.PlayerName)]?.ToObject<string>(), false);
                var homeWorld = data[nameof(ActorIdentifier.HomeWorld)]?.ToObject<ushort>() ?? 0;
                return CreatePlayer(name, homeWorld);
            }
            case IdentifierType.Retainer:
            {
                var name = ByteString.FromStringUnsafe(data[nameof(ActorIdentifier.PlayerName)]?.ToObject<string>(), false);
                return CreateRetainer(name);
            }
            case IdentifierType.Owned:
            {
                var name      = ByteString.FromStringUnsafe(data[nameof(ActorIdentifier.PlayerName)]?.ToObject<string>(), false);
                var homeWorld = data[nameof(ActorIdentifier.HomeWorld)]?.ToObject<ushort>() ?? 0;
                var kind      = data[nameof(ActorIdentifier.Kind)]?.ToObject<ObjectKind>() ?? ObjectKind.CardStand;
                var dataId    = data[nameof(ActorIdentifier.DataId)]?.ToObject<uint>() ?? 0;
                return CreateOwned(name, homeWorld, kind, dataId);
            }
            case IdentifierType.Special:
            {
                var special = data[nameof(ActorIdentifier.Special)]?.ToObject<SpecialActor>() ?? 0;
                return CreateSpecial(special);
            }
            case IdentifierType.Npc:
            {
                var index  = data[nameof(ActorIdentifier.Index)]?.ToObject<ushort>() ?? ushort.MaxValue;
                var kind   = data[nameof(ActorIdentifier.Kind)]?.ToObject<ObjectKind>() ?? ObjectKind.CardStand;
                var dataId = data[nameof(ActorIdentifier.DataId)]?.ToObject<uint>() ?? 0;
                return CreateNpc(kind, dataId, index);
            }
            case IdentifierType.UnkObject:
            {
                var index = data[nameof(ActorIdentifier.Index)]?.ToObject<ushort>() ?? ushort.MaxValue;
                var name  = ByteString.FromStringUnsafe(data[nameof(ActorIdentifier.PlayerName)]?.ToObject<string>(), false);
                return CreateIndividualUnchecked(IdentifierType.UnkObject, name, index, ObjectKind.None, 0);
            }
            default: return ActorIdentifier.Invalid;
        }
    }

    /// <summary>
    /// Return the world name including the Any World option.
    /// </summary>
    public string ToWorldName(ushort worldId)
        => worldId == ushort.MaxValue ? "Any World" : Worlds.TryGetValue(worldId, out var name) ? name : "Invalid";

    /// <summary>
    /// Use stored data to convert an ActorIdentifier to a string.
    /// </summary>
    public string ToString(ActorIdentifier id)
    {
        return id.Type switch
        {
            IdentifierType.Player => id.HomeWorld != _clientState.LocalPlayer?.HomeWorld.Id
                ? $"{id.PlayerName} ({ToWorldName(id.HomeWorld)})"
                : id.PlayerName.ToString(),
            IdentifierType.Retainer => id.PlayerName.ToString(),
            IdentifierType.Owned => id.HomeWorld != _clientState.LocalPlayer?.HomeWorld.Id
                ? $"{id.PlayerName} ({ToWorldName(id.HomeWorld)})'s {ToName(id.Kind, id.DataId)}"
                : $"{id.PlayerName}s {ToName(id.Kind,                                id.DataId)}",
            IdentifierType.Special => id.Special.ToName(),
            IdentifierType.Npc =>
                id.Index == ushort.MaxValue
                    ? ToName(id.Kind, id.DataId)
                    : $"{ToName(id.Kind, id.DataId)} at {id.Index}",
            IdentifierType.UnkObject => id.PlayerName.IsEmpty
                ? $"Unknown Object at {id.Index}"
                : $"{id.PlayerName} at {id.Index}",
            _ => "Invalid",
        };
    }


    /// <summary>
    /// Convert a given ID for a certain ObjectKind to a name.
    /// </summary>
    /// <returns>Invalid or a valid name.</returns>
    public string ToName(ObjectKind kind, uint dataId)
        => TryGetName(kind, dataId, out var ret) ? ret : "Invalid";


    /// <summary>
    /// Convert a given ID for a certain ObjectKind to a name.
    /// </summary>
    public bool TryGetName(ObjectKind kind, uint dataId, [NotNullWhen(true)] out string? name)
    {
        name = null;
        return kind switch
        {
            ObjectKind.MountType => Mounts.TryGetValue(dataId, out name),
            ObjectKind.Companion => Companions.TryGetValue(dataId, out name),
            (ObjectKind)15       => Ornaments.TryGetValue(dataId, out name), // TODO: CS Update
            ObjectKind.BattleNpc => BNpcs.TryGetValue(dataId, out name),
            ObjectKind.EventNpc  => ENpcs.TryGetValue(dataId, out name),
            _                    => false,
        };
    }

    /// <summary>
    /// Compute an ActorIdentifier from a GameObject. If check is true, the values are checked for validity.
    /// </summary>
    public unsafe ActorIdentifier FromObject(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* actor, bool check = true)
    {
        if (actor == null)
            return ActorIdentifier.Invalid;

        var idx = actor->ObjectIndex;
        if (idx is >= (ushort)SpecialActor.CutsceneStart and < (ushort)SpecialActor.CutsceneEnd)
        {
            var parentIdx = _toParentIdx(idx);
            if (parentIdx >= 0)
                return FromObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_objects.GetObjectAddress(parentIdx), check);
        }
        else if (idx is >= (ushort)SpecialActor.CharacterScreen and <= (ushort)SpecialActor.Portrait)
        {
            return CreateIndividualUnchecked(IdentifierType.Special, ByteString.Empty, idx, ObjectKind.None, uint.MaxValue);
        }

        switch ((ObjectKind)actor->ObjectKind)
        {
            case ObjectKind.Player:
            {
                var name      = new ByteString(actor->Name);
                var homeWorld = ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor)->HomeWorld;
                return check
                    ? CreatePlayer(name, homeWorld)
                    : CreateIndividualUnchecked(IdentifierType.Player, name, homeWorld, ObjectKind.None, uint.MaxValue);
            }
            case ObjectKind.BattleNpc:
            {
                var ownerId = actor->OwnerID;
                if (ownerId != 0xE0000000)
                {
                    var owner =
                        (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)(_objects.SearchById(ownerId)?.Address ?? IntPtr.Zero);
                    if (owner == null)
                        return ActorIdentifier.Invalid;

                    var name      = new ByteString(owner->GameObject.Name);
                    var homeWorld = owner->HomeWorld;
                    return check
                        ? CreateOwned(name, homeWorld, ObjectKind.BattleNpc,
                            ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor)->NameID)
                        : CreateIndividualUnchecked(IdentifierType.Owned, name, homeWorld, ObjectKind.BattleNpc,
                            ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor)->NameID);
                }

                return check
                    ? CreateNpc(ObjectKind.BattleNpc, ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor)->NameID,
                        actor->ObjectIndex)
                    : CreateIndividualUnchecked(IdentifierType.Npc, ByteString.Empty, actor->ObjectIndex, ObjectKind.BattleNpc,
                        ((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)actor)->NameID);
            }
            case ObjectKind.EventNpc:
            {
                var dataId = actor->DataID;
                // Special case for squadron that is also in the game functions, cf. E8 ?? ?? ?? ?? 89 87 ?? ?? ?? ?? 4C 89 BF
                if (dataId == 0xf845d)
                    dataId = actor->GetNpcID();
                return check
                    ? CreateNpc(ObjectKind.EventNpc, dataId, actor->ObjectIndex)
                    : CreateIndividualUnchecked(IdentifierType.Npc, ByteString.Empty, actor->ObjectIndex, ObjectKind.EventNpc, dataId);
            }
            case ObjectKind.MountType:
            case ObjectKind.Companion:
            case (ObjectKind)15: // TODO: CS Update
            {
                if (actor->ObjectIndex % 2 == 0)
                    return ActorIdentifier.Invalid;

                var owner = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)_objects.GetObjectAddress(actor->ObjectIndex - 1);
                if (owner == null)
                    return ActorIdentifier.Invalid;

                var dataId = GetCompanionId(actor, &owner->GameObject);
                return check
                    ? CreateOwned(new ByteString(owner->GameObject.Name), owner->HomeWorld, (ObjectKind)actor->ObjectKind, dataId)
                    : CreateIndividualUnchecked(IdentifierType.Owned, new ByteString(owner->GameObject.Name), owner->HomeWorld,
                        (ObjectKind)actor->ObjectKind,                dataId);
            }
            case ObjectKind.Retainer:
            {
                var name = new ByteString(actor->Name);
                return check
                    ? CreateRetainer(name)
                    : CreateIndividualUnchecked(IdentifierType.Retainer, name, 0, ObjectKind.None, uint.MaxValue);
            }
            default:
            {
                var name  = new ByteString(actor->Name);
                var index = actor->ObjectIndex;
                return CreateIndividualUnchecked(IdentifierType.UnkObject, name, index, ObjectKind.None, 0);
            }
        }
    }

    /// <summary>
    /// Obtain the current companion ID for an object by its actor and owner.
    /// </summary>
    private unsafe uint GetCompanionId(FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* actor,
        FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* owner) // TODO: CS Update
    {
        return (ObjectKind)actor->ObjectKind switch
        {
            ObjectKind.MountType => *(ushort*)((byte*)owner + 0x650 + 0x18),
            (ObjectKind)15       => *(ushort*)((byte*)owner + 0x860 + 0x18),
            ObjectKind.Companion => *(ushort*)((byte*)actor + 0x1AAC),
            _                    => actor->DataID,
        };
    }

    public unsafe ActorIdentifier FromObject(GameObject? actor, bool check = true)
        => FromObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)(actor?.Address ?? IntPtr.Zero), check);

    public ActorIdentifier CreateIndividual(IdentifierType type, ByteString name, ushort homeWorld, ObjectKind kind, uint dataId)
        => type switch
        {
            IdentifierType.Player    => CreatePlayer(name, homeWorld),
            IdentifierType.Retainer  => CreateRetainer(name),
            IdentifierType.Owned     => CreateOwned(name, homeWorld, kind, dataId),
            IdentifierType.Special   => CreateSpecial((SpecialActor)homeWorld),
            IdentifierType.Npc       => CreateNpc(kind, dataId, homeWorld),
            IdentifierType.UnkObject => CreateIndividualUnchecked(IdentifierType.UnkObject, name, homeWorld, ObjectKind.None, 0),
            _                        => ActorIdentifier.Invalid,
        };

    /// <summary>
    /// Only use this if you are sure the input is valid.
    /// </summary>
    public ActorIdentifier CreateIndividualUnchecked(IdentifierType type, ByteString name, ushort homeWorld, ObjectKind kind, uint dataId)
        => new(type, kind, homeWorld, dataId, name);

    public ActorIdentifier CreatePlayer(ByteString name, ushort homeWorld)
    {
        if (!VerifyWorld(homeWorld) || !VerifyPlayerName(name.Span))
            return ActorIdentifier.Invalid;

        return new ActorIdentifier(IdentifierType.Player, ObjectKind.Player, homeWorld, 0, name);
    }

    public ActorIdentifier CreateRetainer(ByteString name)
    {
        if (!VerifyRetainerName(name.Span))
            return ActorIdentifier.Invalid;

        return new ActorIdentifier(IdentifierType.Retainer, ObjectKind.Retainer, 0, 0, name);
    }

    public ActorIdentifier CreateSpecial(SpecialActor actor)
    {
        if (!VerifySpecial(actor))
            return ActorIdentifier.Invalid;

        return new ActorIdentifier(IdentifierType.Special, ObjectKind.Player, (ushort)actor, 0, ByteString.Empty);
    }

    public ActorIdentifier CreateNpc(ObjectKind kind, uint data, ushort index = ushort.MaxValue)
    {
        if (!VerifyIndex(index) || !VerifyNpcData(kind, data))
            return ActorIdentifier.Invalid;

        return new ActorIdentifier(IdentifierType.Npc, kind, index, data, ByteString.Empty);
    }

    public ActorIdentifier CreateOwned(ByteString ownerName, ushort homeWorld, ObjectKind kind, uint dataId)
    {
        if (!VerifyWorld(homeWorld) || !VerifyPlayerName(ownerName.Span) || !VerifyOwnedData(kind, dataId))
            return ActorIdentifier.Invalid;

        return new ActorIdentifier(IdentifierType.Owned, kind, homeWorld, dataId, ownerName);
    }

    /// <summary> Checks SE naming rules. </summary>
    public static bool VerifyPlayerName(ReadOnlySpan<byte> name)
    {
        // Total no more than 20 characters + space.
        if (name.Length is < 5 or > 21)
            return false;

        // Forename and surname, no more spaces.
        var splitIndex = name.IndexOf((byte)' ');
        if (splitIndex < 0 || name[(splitIndex + 1)..].IndexOf((byte)' ') >= 0)
            return false;

        return CheckNamePart(name[..splitIndex], 2, 15) && CheckNamePart(name[(splitIndex + 1)..], 2, 15);
    }

    /// <summary> Checks SE naming rules. </summary>
    public static bool VerifyPlayerName(ReadOnlySpan<char> name)
    {
        // Total no more than 20 characters + space.
        if (name.Length is < 5 or > 21)
            return false;

        // Forename and surname, no more spaces.
        var splitIndex = name.IndexOf(' ');
        if (splitIndex < 0 || name[(splitIndex + 1)..].IndexOf(' ') >= 0)
            return false;

        return CheckNamePart(name[..splitIndex], 2, 15) && CheckNamePart(name[(splitIndex + 1)..], 2, 15);
    }

    /// <summary> Checks SE naming rules. </summary>
    public static bool VerifyRetainerName(ReadOnlySpan<byte> name)
        => CheckNamePart(name, 3, 20);

    /// <summary> Checks SE naming rules. </summary>
    public static bool VerifyRetainerName(ReadOnlySpan<char> name)
        => CheckNamePart(name, 3, 20);

    private static bool CheckNamePart(ReadOnlySpan<char> part, int minLength, int maxLength)
    {
        // Each name part at least 2 and at most 15 characters for players, and at least 3 and at most 20 characters for retainers.
        if (part.Length < minLength || part.Length > maxLength)
            return false;

        // Each part starting with capitalized letter.
        if (part[0] is < 'A' or > 'Z')
            return false;

        // Every other symbol needs to be lowercase letter, hyphen or apostrophe.
        var last = '\0';
        for (var i = 1; i < part.Length; ++i)
        {
            var current = part[i];
            if (current is not ('\'' or '-' or (>= 'a' and <= 'z')))
                return false;

            // Hyphens can not be used in succession, after or before apostrophes or as the last symbol.
            if (last is '\'' && current is '-')
                return false;
            if (last is '-' && current is '-' or '\'')
                return false;

            last = current;
        }

        return part[^1] != '-';
    }

    private static bool CheckNamePart(ReadOnlySpan<byte> part, int minLength, int maxLength)
    {
        // Each name part at least 2 and at most 15 characters for players, and at least 3 and at most 20 characters for retainers.
        if (part.Length < minLength || part.Length > maxLength)
            return false;

        // Each part starting with capitalized letter.
        if (part[0] is < (byte)'A' or > (byte)'Z')
            return false;

        // Every other symbol needs to be lowercase letter, hyphen or apostrophe.
        var last = (byte)'\0';
        for (var i = 1; i < part.Length; ++i)
        {
            var current = part[i];
            if (current is not ((byte)'\'' or (byte)'-' or (>= (byte)'a' and <= (byte)'z')))
                return false;

            // Hyphens can not be used in succession, after or before apostrophes or as the last symbol.
            if (last is (byte)'\'' && current is (byte)'-')
                return false;
            if (last is (byte)'-' && current is (byte)'-' or (byte)'\'')
                return false;

            last = current;
        }

        return part[^1] != (byte)'-';
    }

    /// <summary> Checks if the world is a valid public world or ushort.MaxValue (any world). </summary>
    public bool VerifyWorld(ushort worldId)
        => worldId == ushort.MaxValue || Worlds.ContainsKey(worldId);

    /// <summary> Verify that the enum value is a specific actor and return the name if it is. </summary>
    public static bool VerifySpecial(SpecialActor actor)
        => actor is >= SpecialActor.CharacterScreen and <= SpecialActor.Portrait;

    /// <summary> Verify that the object index is a valid index for an NPC. </summary>
    public static bool VerifyIndex(ushort index)
    {
        return index switch
        {
            ushort.MaxValue                 => true,
            < 200                           => index % 2 == 0,
            > (ushort)SpecialActor.Portrait => index < 426,
            _                               => false,
        };
    }

    /// <summary> Verify that the object kind is a valid owned object, and the corresponding data Id. </summary>
    public bool VerifyOwnedData(ObjectKind kind, uint dataId)
    {
        return kind switch
        {
            ObjectKind.MountType => Mounts.ContainsKey(dataId),
            ObjectKind.Companion => Companions.ContainsKey(dataId),
            (ObjectKind)15       => Ornaments.ContainsKey(dataId), // TODO: CS Update
            ObjectKind.BattleNpc => BNpcs.ContainsKey(dataId),
            _                    => false,
        };
    }

    public bool VerifyNpcData(ObjectKind kind, uint dataId)
        => kind switch
        {
            ObjectKind.MountType => Mounts.ContainsKey(dataId),
            ObjectKind.Companion => Companions.ContainsKey(dataId),
            (ObjectKind)15       => Ornaments.ContainsKey(dataId), // TODO: CS Update
            ObjectKind.BattleNpc => BNpcs.ContainsKey(dataId),
            ObjectKind.EventNpc  => ENpcs.ContainsKey(dataId),
            _                    => false,
        };
}