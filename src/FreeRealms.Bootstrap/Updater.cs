using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace FreeRealms.Bootstrap;

// Consumes published GitHub Releases, not GitHub Actions artifacts.
// Private repositories require an authenticated distribution approach.
internal static class Updater
{
    private const string Api = "https://api.github.com/repos/DeanSawyer/FreeRealms-PS3-Research/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FreeRealmsPS3Bootstrap", "0.1"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    public static async Task CheckAndInstallAsync(Window owner, Action stopServices)
    {
        try
        {
            using var response = await Client.GetAsync(Api);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                MessageBox.Show(owner,
                    "No GitHub Release was found. Either none has been published yet, or the repository is private (private releases cannot be read without signing in).\n\nA published release must be readable by the application before in-app updating can work.",
                    "Updates unavailable", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var latest))
                throw new InvalidDataException("Release version is not a valid numeric version.");
            var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            if (latest <= current)
            {
                MessageBox.Show(owner, $"You are running version {current}. The latest GitHub Release is {tag}.",
                    "Up to date", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var asset = root.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => string.Equals(a.GetProperty("name").GetString(),
                    "FreeRealmsPS3Bootstrap-Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("The release does not contain the expected Windows installer.");
            var address = asset.GetProperty("browser_download_url").GetString()
                ?? throw new InvalidDataException("The installer URL is missing.");
            if (!Uri.TryCreate(address, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || url.Host != "github.com")
                throw new InvalidDataException("Unexpected installer host.");

            if (MessageBox.Show(owner,
                $"Version {tag} is available (installed: {current}).\n\nDownload and run the official installer? The app will close, and Windows may ask for confirmation.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var folder = Path.Combine(Path.GetTempPath(), "FreeRealmsPS3BootstrapUpdate");
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, $"FreeRealmsPS3Bootstrap-{latest}-Setup.exe");
            using (var download = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                download.EnsureSuccessStatusCode();
                await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await download.Content.CopyToAsync(output);
            }
            // Verify the GitHub-provided SHA-256 digest when present.
            if (asset.TryGetProperty("digest", out var digestField))
            {
                var digest = digestField.GetString();
                if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                {
                    var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target)));
                    if (!actual.Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Downloaded installer failed SHA-256 verification.");
                }
            }

            stopServices();
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                // Installer controls shutdown and replacement; no unattended elevation.
            });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Update failed: {ex.Message}\n\nYour currently installed version has not been replaced.",
                "Update error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
