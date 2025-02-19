using Dalamud.Game.ClientState.Objects.Enums;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Penumbra.GameData.Data;
using Penumbra.GameData.Enums;
using Penumbra.GameData.Structs;
using Penumbra.Meta.Files;
using Penumbra.Meta.Manipulations;
using Penumbra.String;
using Penumbra.String.Classes;
using static Penumbra.Interop.Structs.CharacterBaseUtility;
using ModelType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase.ModelType;

namespace Penumbra.Interop.ResourceTree;

internal partial record ResolveContext
{
    private Utf8GamePath ResolveModelPath()
    {
        // Correctness:
        // Resolving a model path through the game's code can use EQDP metadata for human equipment models.
        return ModelType switch
        {
            ModelType.Human when SlotIndex < 10 => ResolveEquipmentModelPath(),
            _                                   => ResolveModelPathNative(),
        };
    }

    private Utf8GamePath ResolveEquipmentModelPath()
    {
        var path = SlotIndex < 5
            ? GamePaths.Equipment.Mdl.Path(Equipment.Set, ResolveModelRaceCode(), Slot)
            : GamePaths.Accessory.Mdl.Path(Equipment.Set, ResolveModelRaceCode(), Slot);
        return Utf8GamePath.FromString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private unsafe GenderRace ResolveModelRaceCode()
        => ResolveEqdpRaceCode(Slot, Equipment.Set);

    private unsafe GenderRace ResolveEqdpRaceCode(EquipSlot slot, SetId setId)
    {
        var slotIndex = slot.ToIndex();
        if (slotIndex >= 10 || ModelType != ModelType.Human)
            return GenderRace.MidlanderMale;

        var characterRaceCode = (GenderRace)((Human*)CharacterBase.Value)->RaceSexId;
        if (characterRaceCode == GenderRace.MidlanderMale)
            return GenderRace.MidlanderMale;

        var accessory = slotIndex >= 5;
        if ((ushort)characterRaceCode % 10 != 1 && accessory)
            return GenderRace.MidlanderMale;

        var metaCache = Global.Collection.MetaCache;
        if (metaCache == null)
            return GenderRace.MidlanderMale;

        var entry = metaCache.GetEqdpEntry(characterRaceCode, accessory, setId);
        if (entry.ToBits(slot).Item2)
            return characterRaceCode;

        var fallbackRaceCode = characterRaceCode.Fallback();
        if (fallbackRaceCode == GenderRace.MidlanderMale)
            return GenderRace.MidlanderMale;

        entry = metaCache.GetEqdpEntry(fallbackRaceCode, accessory, setId);
        if (entry.ToBits(slot).Item2)
            return fallbackRaceCode;

        return GenderRace.MidlanderMale;
    }

    private unsafe Utf8GamePath ResolveModelPathNative()
    {
        var path = ResolveMdlPath(CharacterBase, SlotIndex);
        return Utf8GamePath.FromByteString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private unsafe Utf8GamePath ResolveMaterialPath(Utf8GamePath modelPath, ResourceHandle* imc, byte* mtrlFileName)
    {
        // Safety and correctness:
        // Resolving a material path through the game's code can dereference null pointers for materials that involve IMC metadata.
        return ModelType switch
        {
            ModelType.Human when SlotIndex < 10 && mtrlFileName[8] != (byte)'b' => ResolveEquipmentMaterialPath(modelPath, imc, mtrlFileName),
            ModelType.DemiHuman                                                 => ResolveEquipmentMaterialPath(modelPath, imc, mtrlFileName),
            ModelType.Weapon                                                    => ResolveWeaponMaterialPath(modelPath, imc, mtrlFileName),
            ModelType.Monster                                                   => ResolveMonsterMaterialPath(modelPath, imc, mtrlFileName),
            _                                                                   => ResolveMaterialPathNative(mtrlFileName),
        };
    }

    private unsafe Utf8GamePath ResolveEquipmentMaterialPath(Utf8GamePath modelPath, ResourceHandle* imc, byte* mtrlFileName)
    {
        var variant  = ResolveMaterialVariant(imc, Equipment.Variant);
        var fileName = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(mtrlFileName);

        Span<byte> pathBuffer = stackalloc byte[260];
        pathBuffer            = AssembleMaterialPath(pathBuffer, modelPath.Path.Span, variant, fileName);

        return Utf8GamePath.FromSpan(pathBuffer, out var path) ? path.Clone() : Utf8GamePath.Empty;
    }

    private unsafe Utf8GamePath ResolveWeaponMaterialPath(Utf8GamePath modelPath, ResourceHandle* imc, byte* mtrlFileName)
    {
        var setIdHigh = Equipment.Set.Id / 100;
        // All MCH (20??) weapons' materials C are one and the same
        if (setIdHigh is 20 && mtrlFileName[14] == (byte)'c')
            return Utf8GamePath.FromString(GamePaths.Weapon.Mtrl.Path(2001, 1, 1, "c"), out var path) ? path : Utf8GamePath.Empty;

        // MNK (03??, 16??), NIN (18??) and DNC (26??) offhands share materials with the corresponding mainhand
        if (setIdHigh is 3 or 16 or 18 or 26)
        {
            var setIdLow = Equipment.Set.Id % 100;
            if (setIdLow > 50)
            {
                var variant  = ResolveMaterialVariant(imc, Equipment.Variant);
                var fileName = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(mtrlFileName);

                var mirroredSetId = (ushort)(Equipment.Set.Id - 50);

                Span<byte> mirroredFileName = stackalloc byte[32];
                mirroredFileName = mirroredFileName[..fileName.Length];
                fileName.CopyTo(mirroredFileName);
                WriteZeroPaddedNumber(mirroredFileName[4..8], mirroredSetId);

                Span<byte> pathBuffer = stackalloc byte[260];
                pathBuffer            = AssembleMaterialPath(pathBuffer, modelPath.Path.Span, variant, mirroredFileName);

                var weaponPosition = pathBuffer.IndexOf("/weapon/w"u8);
                if (weaponPosition >= 0)
                    WriteZeroPaddedNumber(pathBuffer[(weaponPosition + 9)..(weaponPosition + 13)], mirroredSetId);

                return Utf8GamePath.FromSpan(pathBuffer, out var path) ? path.Clone() : Utf8GamePath.Empty;
            }
        }

        return ResolveEquipmentMaterialPath(modelPath, imc, mtrlFileName);
    }

    private unsafe Utf8GamePath ResolveMonsterMaterialPath(Utf8GamePath modelPath, ResourceHandle* imc, byte* mtrlFileName)
    {
        // TODO: Submit this (Monster->Variant) to ClientStructs
        var variant  = ResolveMaterialVariant(imc, ((byte*)CharacterBase.Value)[0x8F4]);
        var fileName = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(mtrlFileName);

        Span<byte> pathBuffer = stackalloc byte[260];
        pathBuffer            = AssembleMaterialPath(pathBuffer, modelPath.Path.Span, variant, fileName);

        return Utf8GamePath.FromSpan(pathBuffer, out var path) ? path.Clone() : Utf8GamePath.Empty;
    }

    private unsafe byte ResolveMaterialVariant(ResourceHandle* imc, Variant variant)
    {
        var imcFileData = imc->GetDataSpan();
        if (imcFileData.IsEmpty)
        {
            Penumbra.Log.Warning($"IMC resource handle with path {GetResourceHandlePath(imc, false)} doesn't have a valid data span");
            return variant.Id;
        }

        var entry = ImcFile.GetEntry(imcFileData, Slot, variant, out var exists);
        if (!exists)
            return variant.Id;

        return entry.MaterialId;
    }

    private static Span<byte> AssembleMaterialPath(Span<byte> materialPathBuffer, ReadOnlySpan<byte> modelPath, byte variant, ReadOnlySpan<byte> mtrlFileName)
    {
        var modelPosition = modelPath.IndexOf("/model/"u8);
        if (modelPosition < 0)
            return Span<byte>.Empty;

        var baseDirectory = modelPath[..modelPosition];

        baseDirectory.CopyTo(materialPathBuffer);
        "/material/v"u8.CopyTo(materialPathBuffer[baseDirectory.Length..]);
        WriteZeroPaddedNumber(materialPathBuffer.Slice(baseDirectory.Length + 11, 4), variant);
        materialPathBuffer[baseDirectory.Length + 15] = (byte)'/';
        mtrlFileName.CopyTo(materialPathBuffer[(baseDirectory.Length + 16)..]);

        return materialPathBuffer[..(baseDirectory.Length + 16 + mtrlFileName.Length)];
    }

    private static void WriteZeroPaddedNumber(Span<byte> destination, ushort number)
    {
        for (var i = destination.Length; i-- > 0;)
        {
            destination[i] = (byte)('0' + number % 10);
            number /= 10;
        }
    }

    private unsafe Utf8GamePath ResolveMaterialPathNative(byte* mtrlFileName)
    {
        ByteString? path;
        try
        {
            path = ResolveMtrlPath(CharacterBase, SlotIndex, mtrlFileName);
        }
        catch (AccessViolationException)
        {
            Penumbra.Log.Error($"Access violation during attempt to resolve material path\nDraw object: {(nint)CharacterBase.Value:X} (of type {ModelType})\nSlot index: {SlotIndex}\nMaterial file name: {(nint)mtrlFileName:X} ({new string((sbyte*)mtrlFileName)})");
            return Utf8GamePath.Empty;
        }
        return Utf8GamePath.FromByteString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private Utf8GamePath ResolveSkeletonPath(uint partialSkeletonIndex)
    {
        // Correctness and Safety:
        // Resolving a skeleton path through the game's code can use EST metadata for human skeletons.
        // Additionally, it can dereference null pointers for human equipment skeletons.
        return ModelType switch
        {
            ModelType.Human => ResolveHumanSkeletonPath(partialSkeletonIndex),
            _               => ResolveSkeletonPathNative(partialSkeletonIndex),
        };
    }

    private unsafe Utf8GamePath ResolveHumanSkeletonPath(uint partialSkeletonIndex)
    {
        var (raceCode, slot, set) = ResolveHumanSkeletonData(partialSkeletonIndex);
        if (set == 0)
            return Utf8GamePath.Empty;

        var path = GamePaths.Skeleton.Sklb.Path(raceCode, slot, set);
        return Utf8GamePath.FromString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private unsafe (GenderRace RaceCode, string Slot, SetId Set) ResolveHumanSkeletonData(uint partialSkeletonIndex)
    {
        var human             = (Human*)CharacterBase.Value;
        var characterRaceCode = (GenderRace)human->RaceSexId;
        switch (partialSkeletonIndex)
        {
            case 0:
                return (characterRaceCode, "base", 1);
            case 1:
                var faceId    = human->FaceId;
                var tribe     = human->Customize[(int)CustomizeIndex.Tribe];
                var modelType = human->Customize[(int)CustomizeIndex.ModelType];
                if (faceId < 201)
                {
                    faceId -= tribe switch
                    {
                        0xB when modelType == 4 => 100,
                        0xE | 0xF               => 100,
                        _                       => 0,
                    };
                }
                return ResolveHumanExtraSkeletonData(characterRaceCode, EstManipulation.EstType.Face, faceId);
            case 2:
                return ResolveHumanExtraSkeletonData(characterRaceCode, EstManipulation.EstType.Hair, human->HairId);
            case 3:
                return ResolveHumanEquipmentSkeletonData(EquipSlot.Head, EstManipulation.EstType.Head);
            case 4:
                return ResolveHumanEquipmentSkeletonData(EquipSlot.Body, EstManipulation.EstType.Body);
            default:
                return (0, string.Empty, 0);
        }
    }

    private unsafe (GenderRace RaceCode, string Slot, SetId Set) ResolveHumanEquipmentSkeletonData(EquipSlot slot, EstManipulation.EstType type)
    {
        var human     = (Human*)CharacterBase.Value;
        var equipment = ((CharacterArmor*)&human->Head)[slot.ToIndex()];
        return ResolveHumanExtraSkeletonData(ResolveEqdpRaceCode(slot, equipment.Set), type, equipment.Set);
    }

    private unsafe (GenderRace RaceCode, string Slot, SetId Set) ResolveHumanExtraSkeletonData(GenderRace raceCode, EstManipulation.EstType type, SetId set)
    {
        var metaCache   = Global.Collection.MetaCache;
        var skeletonSet = metaCache == null ? default : metaCache.GetEstEntry(type, raceCode, set);
        return (raceCode, EstManipulation.ToName(type), skeletonSet);
    }

    private unsafe Utf8GamePath ResolveSkeletonPathNative(uint partialSkeletonIndex)
    {
        var path = ResolveSklbPath(CharacterBase, partialSkeletonIndex);
        return Utf8GamePath.FromByteString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private Utf8GamePath ResolveSkeletonParameterPath(uint partialSkeletonIndex)
    {
        // Correctness and Safety:
        // Resolving a skeleton parameter path through the game's code can use EST metadata for human skeletons.
        // Additionally, it can dereference null pointers for human equipment skeletons.
        return ModelType switch
        {
            ModelType.Human => ResolveHumanSkeletonParameterPath(partialSkeletonIndex),
            _               => ResolveSkeletonParameterPathNative(partialSkeletonIndex),
        };
    }

    private Utf8GamePath ResolveHumanSkeletonParameterPath(uint partialSkeletonIndex)
    {
        var (raceCode, slot, set) = ResolveHumanSkeletonData(partialSkeletonIndex);
        if (set == 0)
            return Utf8GamePath.Empty;

        var path = GamePaths.Skeleton.Skp.Path(raceCode, slot, set);
        return Utf8GamePath.FromString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }

    private unsafe Utf8GamePath ResolveSkeletonParameterPathNative(uint partialSkeletonIndex)
    {
        var path = ResolveSkpPath(CharacterBase, partialSkeletonIndex);
        return Utf8GamePath.FromByteString(path, out var gamePath) ? gamePath : Utf8GamePath.Empty;
    }
}
