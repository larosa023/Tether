using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tether.Core.Settings;

/// <summary>Loads and saves settings as JSON in %APPDATA%\Tether\settings.json.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private Timer? _debounce;

    public static string AppDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tether");

    public static string LocalDataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tether");

    private static string FilePath => Path.Combine(AppDataDir, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (loaded != null)
                {
                    // Re-wrap so lookups stay case-insensitive after deserialisation.
                    loaded.Profiles = new Dictionary<string, DeviceProfile>(loaded.Profiles ?? new(), StringComparer.OrdinalIgnoreCase);
                    loaded.HiddenDevices ??= new();
                    loaded.Tonely ??= new AppSettings().Tonely;
                    loaded.Faderly ??= new AppSettings().Faderly;
                    Current = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings could not be read; using defaults", ex);
            try { File.Copy(FilePath, FilePath + ".corrupt", true); } catch { }
            Current = new AppSettings();
        }
    }

    /// <summary>Saves shortly after the last change, so slider drags don't hammer the disk.</summary>
    public void Save()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => SaveNow(), null, 400, Timeout.Infinite);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Settings could not be saved", ex);
            }
        }
    }
}
