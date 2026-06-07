using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Logging;

namespace AITeammate.Scripts;

internal static class AiTeammateLobbyLimitSupport
{
    public const int VanillaPlayerLimit = 4;
    public const int RmpDefaultPlayerLimit = 8;
    public const int RmpMaxPlayerLimit = 16;
    private const string RmpAssemblyName = "RemoveMultiplayerPlayerLimit";
    private const string RmpConfigFolderName = "RemoveMultiplayerPlayerLimit";
    private const string RmpConfigFileName = "config.ini";

    public static int ResolveMaxPlayerCount()
    {
        if (TryReadLoadedRmpLimit(out int loadedLimit))
        {
            int clamped = ClampRmpLimit(loadedLimit);
            Log.Info($"[AITeammate][RMP] Detected loaded RemoveMultiplayerPlayerLimit target={loadedLimit}; AI setup maxPlayers={clamped}.");
            return clamped;
        }

        if (TryReadRmpConfigLimit(out int configLimit))
        {
            int clamped = ClampRmpLimit(configLimit);
            Log.Info($"[AITeammate][RMP] Detected RemoveMultiplayerPlayerLimit config target={configLimit}; AI setup maxPlayers={clamped}.");
            return clamped;
        }

        Log.Info($"[AITeammate][RMP] RemoveMultiplayerPlayerLimit not detected; AI setup maxPlayers={VanillaPlayerLimit}.");
        return VanillaPlayerLimit;
    }

    private static bool TryReadLoadedRmpLimit(out int limit)
    {
        limit = 0;
        Assembly? rmpAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(static assembly => string.Equals(assembly.GetName().Name, RmpAssemblyName, StringComparison.Ordinal));
        Type? protocolConfig = rmpAssembly?.GetType("RemoveMultiplayerPlayerLimit.Network.ProtocolConfig");
        PropertyInfo? targetLimitProperty = protocolConfig?.GetProperty(
            "TargetPlayerLimit",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (targetLimitProperty?.GetValue(null) is int targetLimit)
        {
            limit = targetLimit;
            return true;
        }

        return false;
    }

    private static bool TryReadRmpConfigLimit(out int limit)
    {
        limit = 0;
        string? configPath = ResolveRmpConfigPath();
        if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
        {
            return Directory.Exists(ResolveRmpConfigDirectory())
                ? UseRmpDefault(out limit)
                : false;
        }

        string section = string.Empty;
        foreach (string rawLine in File.ReadAllLines(configPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            string key = line[..equalsIndex].Trim();
            string value = line[(equalsIndex + 1)..].Trim();
            if (string.Equals(section, "multiplayer", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(key, "max_player_limit", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(value, out int parsed))
            {
                limit = parsed;
                return true;
            }
        }

        return UseRmpDefault(out limit);
    }

    private static bool UseRmpDefault(out int limit)
    {
        limit = RmpDefaultPlayerLimit;
        return true;
    }

    private static int ClampRmpLimit(int limit)
    {
        return Math.Clamp(limit, VanillaPlayerLimit, RmpMaxPlayerLimit);
    }

    private static string? ResolveRmpConfigPath()
    {
        string directory = ResolveRmpConfigDirectory();
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : Path.Combine(directory, RmpConfigFileName);
    }

    private static string ResolveRmpConfigDirectory()
    {
        string? assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            DirectoryInfo? modRoot = Directory.GetParent(assemblyDirectory);
            if (modRoot != null)
            {
                string sibling = Path.Combine(modRoot.FullName, RmpConfigFolderName);
                if (Directory.Exists(sibling))
                {
                    return sibling;
                }
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "mods", RmpConfigFolderName);
    }
}
