using System;
using System.IO;

namespace AITeammate.Scripts;

internal static class AiTeammateStoragePaths
{
    private const string AppDataFolderName = "SlayTheSpire2";
    private const string ModFolderName = "sts2AITeammate";

    public static string GetRuntimeDataDirectory(string childDirectory)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        string root = !string.IsNullOrWhiteSpace(appData)
            ? Path.Combine(appData, AppDataFolderName, ModFolderName)
            : Path.Combine(AppContext.BaseDirectory, ModFolderName);

        return Path.Combine(root, childDirectory);
    }
}
