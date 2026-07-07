namespace PatchyFlow;

public static class Paths
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PatchyFlow");

    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string DictionaryPath => Path.Combine(DataDir, "dictionary.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ModelsDir);
    }
}
