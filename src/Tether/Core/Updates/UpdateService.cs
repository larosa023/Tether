using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Tether.Core.Settings;

namespace Tether.Core.Updates;

public sealed record UpdateInfo(SemVer Version, string Tag, string Notes, string HtmlUrl, string ExeUrl, string? ShaUrl, long Size, bool IsPrerelease);

/// <summary>
/// Built-in updater backed by GitHub Releases on larosa023/Tether.
/// A release must contain <c>Tether.exe</c> and a checksum asset (<c>Tether.exe.sha256</c> or <c>SHA256SUMS.txt</c>);
/// downloads that don't match the published SHA-256 are refused.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string Owner = "larosa023";
    public const string Repo = "Tether";
    private const string ExeAssetName = "Tether.exe";

    // Declared before Http: static initialisers run in order and the client's User-Agent needs this.
    public static SemVer CurrentVersion { get; } = ReadCurrentVersion();

    private static readonly HttpClient Http = CreateClient();
    private readonly SettingsStore _settings;
    private Timer? _timer;

    public UpdateService(SettingsStore settings) => _settings = settings;

    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised on a worker thread when a newer release is found.</summary>
    public event EventHandler<UpdateInfo>? UpdateAvailable;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tether-Updater", CurrentVersion.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    private static SemVer ReadCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info != null && SemVer.TryParse(info.Split('+')[0], out var v)) return v;
        var a = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new SemVer(a.Major, a.Minor, Math.Max(0, a.Build), null);
    }

    /// <summary>Checks 30 s after launch, then every 6 hours, when auto-check is on.</summary>
    public void StartBackgroundChecks()
    {
        _timer = new Timer(async _ =>
        {
            if (!_settings.Current.AutoCheckUpdates) return;
            try { await CheckAsync(manual: false); }
            catch (Exception ex) { Log.Warn($"Background update check failed: {ex.Message}"); }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(6));
    }

    /// <summary>Returns the newer release, or null if up to date. Throws on network errors when manual.</summary>
    public async Task<UpdateInfo?> CheckAsync(bool manual, CancellationToken ct = default)
    {
        var s = _settings.Current;
        string url = s.IncludePrereleases
            ? $"https://api.github.com/repos/{Owner}/{Repo}/releases?per_page=10"
            : $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using var resp = await Http.GetAsync(url, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            s.LastUpdateCheck = DateTimeOffset.Now;
            _settings.Save();
            return null; // no releases published yet
        }
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var releases = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        UpdateInfo? best = null;
        foreach (var r in releases)
        {
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var info = Parse(r);
            if (info == null) continue;
            if (info.IsPrerelease && !s.IncludePrereleases) continue;
            if (best == null || info.Version.CompareTo(best.Version) > 0) best = info;
        }

        s.LastUpdateCheck = DateTimeOffset.Now;
        _settings.Save();

        if (best == null || best.Version.CompareTo(CurrentVersion) <= 0) { Available = null; return null; }
        if (!manual && string.Equals(s.SkippedVersion, best.Version.ToString(), StringComparison.OrdinalIgnoreCase)) return null;

        Available = best;
        UpdateAvailable?.Invoke(this, best);
        return best;
    }

    private static UpdateInfo? Parse(JsonElement r)
    {
        var tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!SemVer.TryParse(tag, out var ver)) return null;

        string? exe = null, sha = null;
        long size = 0;
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var dl = a.GetProperty("browser_download_url").GetString();
            if (name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase)) { exe = dl; size = a.GetProperty("size").GetInt64(); }
            else if (name.Equals(ExeAssetName + ".sha256", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sha = dl;
        }
        if (exe == null) return null;

        return new UpdateInfo(ver, tag,
            r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            r.GetProperty("html_url").GetString() ?? $"https://github.com/{Owner}/{Repo}/releases",
            exe, sha, size,
            r.TryGetProperty("prerelease", out var p) && p.GetBoolean());
    }

    /// <summary>Downloads and verifies the update. Returns the path of the verified exe.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (info.ShaUrl == null) throw new InvalidOperationException("This release has no checksum, so it can't be installed automatically. Download it from GitHub instead.");

        var expected = await FetchExpectedHashAsync(info.ShaUrl, ct);
        var dir = Path.Combine(SettingsStore.LocalDataDir, "updates");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir)) { try { File.Delete(old); } catch { } }

        var target = Path.Combine(dir, $"Tether-{info.Version}.exe");
        var partial = target + ".part";

        using (var resp = await Http.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? info.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(partial);
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (total > 0) progress?.Report((double)read / total);
            }
        }

        string actual;
        await using (var fs = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidOperationException("The download didn't match the published checksum and was discarded.");
        }

        File.Move(partial, target, overwrite: true);
        Log.Info($"Update {info.Version} downloaded and verified ({actual[..12]}…)");
        return target;
    }

    private static async Task<string> FetchExpectedHashAsync(string url, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(url, ct);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].Length != 64) continue;
            if (parts.Length == 1 || parts[^1].Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }
        throw new InvalidOperationException("The release checksum file couldn't be read.");
    }

    /// <summary>
    /// Swaps the running exe for the new one and restarts. Windows allows renaming a running exe,
    /// so: current → Tether.exe.old, new → Tether.exe, launch, exit. The .old file is removed on next start.
    /// If the install folder isn't writable, an elevated helper does the swap instead.
    /// </summary>
    public static void ApplyAndRestart(string newExe)
    {
        var current = Environment.ProcessPath ?? throw new InvalidOperationException("Can't locate Tether.exe.");
        var old = current + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);
            try
            {
                File.Copy(newExe, current, overwrite: true);
            }
            catch
            {
                File.Move(old, current); // roll back
                throw;
            }
            Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = true });
        }
        catch (UnauthorizedAccessException)
        {
            // e.g. installed under Program Files: let an elevated cmd wait for us to exit, then swap.
            var pid = Environment.ProcessId;
            var script =
                $"/c (for /l %i in (1,1,30) do (tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && timeout /t 1 >nul)) " +
                $"& copy /y \"{newExe}\" \"{current}\" & start \"\" \"{current}\" --updated";
            Process.Start(new ProcessStartInfo("cmd.exe", script) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
        }
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Called on startup: remove the previous exe left behind by an update.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            var current = Environment.ProcessPath;
            if (current == null) return;
            var old = current + ".old";
            for (int i = 0; i < 10 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch { Thread.Sleep(300); }
            }
        }
        catch { }
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>Minimal semantic version: MAJOR.MINOR.PATCH[-prerelease]. Accepts a leading 'v'.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre) : IComparable<SemVer>
{
    public static bool TryParse(string? s, out SemVer v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('v', 'V');
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0) { pre = s[(dash + 1)..]; s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        if (!int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b)) return false;
        int c = 0;
        if (parts.Length > 2 && !int.TryParse(parts[2], out c)) return false;
        v = new SemVer(a, b, c, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemVer o)
    {
        int r = Major.CompareTo(o.Major);
        if (r == 0) r = Minor.CompareTo(o.Minor);
        if (r == 0) r = Patch.CompareTo(o.Patch);
        if (r != 0) return r;
        if (Pre == null && o.Pre == null) return 0;
        if (Pre == null) return 1;   // release > prerelease
        if (o.Pre == null) return -1;
        return ComparePre(Pre, o.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var x = a.Split('.'); var y = b.Split('.');
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = int.TryParse(x[i], out var xi), yn = int.TryParse(y[i], out var yi);
            int r = xn && yn ? xi.CompareTo(yi) : string.CompareOrdinal(x[i], y[i]);
            if (r != 0) return r;
        }
        return x.Length.CompareTo(y.Length);
    }

    public override string ToString() => Pre == null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}
