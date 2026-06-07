using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AITeammate.Scripts;

internal static class AiTeammateRuntimeCharacterResolver
{
    private static readonly string[] CharacterObjectPropertyNames =
    [
        "Character",
        "CharacterModel",
        "Model"
    ];

    private static readonly string[] CharacterIdPropertyNames =
    [
        "CharacterId",
        "CharacterID",
        "CharacterKey"
    ];

    public static CharacterModel? TryResolveCharacterModel(Player player)
    {
        CharacterModel? direct = TryGetCharacterModelFromObject(player);
        if (direct != null)
        {
            return direct;
        }

        CharacterModel? creatureCharacter = TryGetCharacterModelFromObject(player.Creature);
        if (creatureCharacter != null)
        {
            return creatureCharacter;
        }

        string? characterId = TryGetCharacterIdFromObject(player) ??
                              TryGetCharacterIdFromObject(player.Creature);
        if (!string.IsNullOrWhiteSpace(characterId))
        {
            if (AiTeammatePlaceholderCharacters.TryGetByModelId(characterId, out AiTeammatePlaceholderCharacter placeholder))
            {
                return placeholder.ResolveModel();
            }
        }

        return null;
    }

    private static CharacterModel? TryGetCharacterModelFromObject(object? source)
    {
        if (source == null)
        {
            return null;
        }

        foreach (string propertyName in CharacterObjectPropertyNames)
        {
            object? value = GetPropertyValue(source, propertyName);
            if (value is CharacterModel character)
            {
                return character;
            }
        }

        return null;
    }

    private static string? TryGetCharacterIdFromObject(object? source)
    {
        if (source == null)
        {
            return null;
        }

        foreach (string propertyName in CharacterIdPropertyNames)
        {
            object? value = GetPropertyValue(source, propertyName);
            string? text = value switch
            {
                string stringValue => stringValue,
                _ => TryGetEntryValue(value) ?? value?.ToString()
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static object? GetPropertyValue(object source, string propertyName)
    {
        PropertyInfo? property = source.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return property?.GetValue(source);
    }

    private static string? TryGetEntryValue(object? source)
    {
        if (source == null)
        {
            return null;
        }

        object? entry = GetPropertyValue(source, "Entry");
        return entry as string;
    }
}
