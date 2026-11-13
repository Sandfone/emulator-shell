using System.Text.Json;
using System.Text.Json.Nodes;

namespace EmulatorShell;

/// The app's preferences, the counterpart of macOS user defaults: a JSON object in
/// `%LOCALAPPDATA%\EmulatorShell\settings.json`. Every app instance (the manager, a device opened by the SDK
/// forwarder) reads the file afresh before changing a key, so they do not undo each other's changes.
static class Settings
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmulatorShell");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly object Gate = new();

    public static JsonNode? Get(string key)
    {
        lock (Gate) return Load()[key]?.DeepClone();
    }

    public static bool GetBool(string key, bool fallback) =>
        Get(key) is JsonValue value && value.TryGetValue(out bool result) ? result : fallback;

    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            var root = Load();
            root[key] = value;
            try
            {
                Directory.CreateDirectory(Folder);
                var temporary = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception error)
            {
                Log.Error($"cannot save settings: {error.Message}");
            }
        }
    }

    private static JsonObject Load()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }
}
