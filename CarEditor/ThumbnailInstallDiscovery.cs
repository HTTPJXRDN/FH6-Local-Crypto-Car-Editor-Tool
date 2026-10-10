using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace FH6CarEditor;

/// <summary>Bounded, read-only install discovery; never recursively searches a drive.</summary>
public static class ThumbnailInstallDiscovery
{
    static bool IsGame(string name, bool motorsport) =>
        Regex.Replace(name, @"\s+", "").Equals(motorsport ? "ForzaMotorsport" : "ForzaHorizon6", StringComparison.OrdinalIgnoreCase);

    static string? Value(string text, string key)
    {
        var match = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"") : null;
    }

    static string ReadSmall(string file) => File.Exists(file) && new FileInfo(file).Length <= 1024 * 1024 ? File.ReadAllText(file) : "";

    // Public pure-filesystem entry point also permits testing custom libraries
    // without changing a user's registry or Steam configuration.
    public static IEnumerable<string> SteamGameRoots(string steamRoot, bool motorsport)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
        try {
            string text = ReadSmall(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
            foreach (Match match in Regex.Matches(text, "\"path\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            // Older Steam libraryfolders.vdf files use numbered string values.
            foreach (Match match in Regex.Matches(text, "\"[0-9]+\"\\s*\"([A-Za-z]:[^\"]+)\""))
                libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string library in libraries) {
            string apps;
            try { apps = Path.Combine(library, "steamapps"); }
            catch (ArgumentException) { continue; }
            try {
                if (Directory.Exists(apps))
                    foreach (string manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf").Take(4096)) {
                        try {
                            string text = ReadSmall(manifest);
                            if (!IsGame(Value(text, "name") ?? "", motorsport)) continue;
                            string? directory = Value(text, "installdir");
                            // Steam install names are a single directory, not an arbitrary path.
                            if (string.IsNullOrWhiteSpace(directory) || directory is "." or ".." || directory.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                            found.Add(Path.Combine(apps, "common", directory));
                        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                    }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (string name in motorsport ? new[] { "Forza Motorsport", "ForzaMotorsport" } : new[] { "ForzaHorizon6", "Forza Horizon 6" })
                found.Add(Path.Combine(apps, "common", name));
        }
        return found;
    }

    public static IEnumerable<string> GameRoots(bool motorsport)
    {
        var steam = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 }) {
                try {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using (var key = registry.OpenSubKey(@"Software\Valve\Steam"))
                        foreach (string value in new[] { "SteamPath", "InstallPath" })
                            if (key?.GetValue(value) is string path && !string.IsNullOrWhiteSpace(path)) steam.Add(path);
                    using var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall != null)
                        foreach (string name in uninstall.GetSubKeyNames()) {
                            try {
                                using var app = uninstall.OpenSubKey(name);
                                if (app?.GetValue("DisplayName") is string title && IsGame(title, motorsport) &&
                                    app.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path)) roots.Add(path);
                            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
                        }
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed)) {
            steam.Add(Path.Combine(drive.Name, "SteamLibrary"));
            steam.Add(Path.Combine(drive.Name, "Program Files (x86)", "Steam"));
            roots.Add(Path.Combine(drive.Name, "XboxGames", motorsport ? "Forza Motorsport" : "Forza Horizon 6", "Content"));
        }
        foreach (string path in steam)
            foreach (string game in SteamGameRoots(path, motorsport)) roots.Add(game);
        return roots;
    }
}
