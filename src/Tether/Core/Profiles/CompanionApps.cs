using System.Diagnostics;
using Microsoft.Win32;
using Tether.Core.Settings;

namespace Tether.Core.Profiles;

/// <summary>
/// Launches Tonely / Faderly with a command. Resolution order:
///   1. the executable path configured in Settings,
///   2. a standard install location (%LOCALAPPDATA%\Programs\&lt;App&gt;\&lt;App&gt;.exe, next to Tether, Program Files, PATH),
///   3. the app's registered URI scheme (tonely:// or faderly://).
/// The companion app is expected to forward the arguments to its running instance.
/// </summary>
public static class CompanionApps
{
    public static bool Send(string appName, IntegrationSettings cfg, string value, out string? error)
    {
        error = null;
        try
        {
            var exe = Resolve(appName, cfg);
            if (exe != null)
            {
                var args = cfg.ArgumentTemplate.Replace("{value}", Escape(value));
                Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
                Log.Info($"{appName}: {Path.GetFileName(exe)} {args}");
                return true;
            }

            var scheme = appName.ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(cfg.UriTemplate) && IsUriSchemeRegistered(scheme))
            {
                var uri = cfg.UriTemplate.Replace("{value}", Uri.EscapeDataString(value));
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                Log.Info($"{appName}: {uri}");
                return true;
            }

            error = $"{appName} wasn't found. Set its location in Settings → Integrations.";
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"{appName} command failed", ex);
            error = $"{appName} couldn't be started: {ex.Message}";
            return false;
        }
    }

    public static string? Resolve(string appName, IntegrationSettings cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ExecutablePath) && File.Exists(cfg.ExecutablePath)) return cfg.ExecutablePath;

        var exeName = appName + ".exe";
        var here = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", appName, exeName),
            Path.Combine(here, exeName),
            Path.Combine(Path.GetDirectoryName(here) ?? here, appName, exeName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), appName, exeName),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var c = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(c)) return c;
            }
            catch { }
        }
        return null;
    }

    public static bool IsAvailable(string appName, IntegrationSettings cfg) =>
        Resolve(appName, cfg) != null || IsUriSchemeRegistered(appName.ToLowerInvariant());

    private static bool IsUriSchemeRegistered(string scheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(scheme);
            return key?.GetValue("URL Protocol") != null;
        }
        catch { return false; }
    }

    private static string Escape(string v) => v.Replace("\"", "\\\"");
}
