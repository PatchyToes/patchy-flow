namespace PatchyFlow;

public static class Paths
{
    // Data lives NEXT TO THE EXE, not in %APPDATA%. Reason: when PatchyFlow is
    // launched as a child of a packaged (MSIX) app, Windows silently redirects
    // %APPDATA% into that package's private LocalCache — so different launch
    // paths would see different copies of settings/history/log. An explicit
    // path under the exe folder is never redirected, so every launch shares one
    // real data directory.
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "PatchyFlowData");

    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string DictionaryPath => Path.Combine(DataDir, "dictionary.txt");

    /// <summary>Legacy %APPDATA% location (+ its MSIX-redirected shadow), for one-time migration.</summary>
    public static IEnumerable<string> LegacyDataDirs()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PatchyFlow");
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
    }

    /// <summary>
    /// One-time move of config + model from the old %APPDATA% location into the
    /// exe-adjacent folder. Safe to call every startup — no-ops once migrated.
    /// </summary>
    public static void MigrateFromLegacy()
    {
        try
        {
            EnsureCreated();
            if (File.Exists(SettingsPath)) return; // already migrated

            foreach (var legacy in LegacyDataDirs())
            {
                if (!Directory.Exists(legacy)) continue;

                foreach (var name in new[] { "settings.json", "dictionary.txt", "history.jsonl", "log.txt" })
                {
                    var src = Path.Combine(legacy, name);
                    var dst = Path.Combine(DataDir, name);
                    if (File.Exists(src) && !File.Exists(dst))
                        File.Copy(src, dst);
                }

                var legacyModels = Path.Combine(legacy, "models");
                if (Directory.Exists(legacyModels))
                {
                    foreach (var model in Directory.GetFiles(legacyModels, "*.bin"))
                    {
                        var dst = Path.Combine(ModelsDir, Path.GetFileName(model));
                        if (!File.Exists(dst)) File.Copy(model, dst);
                    }
                }

                if (File.Exists(SettingsPath))
                {
                    Log.Write($"Migrated data from legacy location: {legacy}");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Legacy migration failed (non-fatal): {ex.Message}");
        }
    }
}
