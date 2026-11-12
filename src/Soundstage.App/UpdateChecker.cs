// Checks GitHub for a newer Soundstage release on startup.
//
// Network failures are swallowed: an offline machine simply never shows the
// "update available" line, which is the right behaviour for an audio app.
using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Soundstage.UI;

public static class UpdateChecker
{
    const string ApiUrl = "https://api.github.com/repos/NithishRamamourthy/Soundstage/releases/latest";

    static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>True once a newer release has been found.</summary>
    public static bool UpdateAvailable { get; private set; }

    /// <summary>The newest release's tag, without the leading 'v'.</summary>
    public static string LatestVersion { get; private set; } = "";

    /// <summary>Browser URL of the newest release.</summary>
    public static string ReleaseUrl { get; private set; } = "";

    /// <summary>Raised (possibly on a background thread) when an update is found.</summary>
    public static event Action? Changed;

    static UpdateChecker()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Soundstage");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Fire-and-forget entry point for startup.</summary>
    public static void Check() => Task.Run(async () => { try { await CheckAsync(); } catch { } });

    public static async Task CheckAsync()
    {
        using var response = await _http.GetAsync(ApiUrl);
        if (!response.IsSuccessStatusCode) return;

        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return;
        string tag = tagEl.GetString() ?? "";
        if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase)) tag = tag[1..];

        if (!Version.TryParse(tag, out var remote)) return;
        Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        if (remote <= current) return;

        string url = "";
        if (doc.RootElement.TryGetProperty("html_url", out var urlEl)) url = urlEl.GetString() ?? "";

        LatestVersion = tag;
        ReleaseUrl = url;
        UpdateAvailable = true;
        Changed?.Invoke();
    }
}
