using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RSM.Core;

/// <summary>Keeps <see cref="AppSettings"/> in %AppData%\RSM\settings.json.</summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AppSettingsStore(string path) => FilePath = path;

    public string FilePath { get; }

    public static AppSettingsStore Default() =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RSM", "settings.json"));

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // a broken settings file should not stop the program from opening
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options), new UTF8Encoding(false));
    }
}
