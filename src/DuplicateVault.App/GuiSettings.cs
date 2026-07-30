using System.IO;
using System.Text.Json;
using System.Xml.Linq;
using DuplicateVault.Core;

namespace DuplicateVault.App;

public sealed record GuiSettings
{
    public string ScanMode { get; init; } = "Quick";
    public string MinimumSizeText { get; init; } = "1MiB";
    public bool RememberScanRoots { get; init; }
    public List<string> ScanRoots { get; init; } = [];
}

public sealed class GuiSettingsStore(AppPaths paths)
{
    private readonly string _settingsPath = Path.Combine(paths.Conf, "gui-settings.json");

    public GuiSettings Load()
    {
        if (!File.Exists(_settingsPath)) return new GuiSettings();
        try
        {
            return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(_settingsPath)) ?? new GuiSettings();
        }
        catch
        {
            return new GuiSettings();
        }
    }

    public void Save(GuiSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_settingsPath, json);
    }
}

public static class VersionDisplay
{
    public static string GetCleanVersion()
    {
        var propsVersion = TryReadDirectoryBuildVersion();
        if (!string.IsNullOrWhiteSpace(propsVersion)) return propsVersion;
        var informational = typeof(VersionDisplay).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return Clean(informational ?? typeof(VersionDisplay).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
    }

    private static string? TryReadDirectoryBuildVersion()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                var document = XDocument.Load(candidate);
                var prefix = document.Descendants("VersionPrefix").SingleOrDefault()?.Value;
                var build = document.Descendants("VersionBuild").SingleOrDefault()?.Value;
                return string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(build) ? null : $"{prefix}.{build}";
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static string Clean(string version)
    {
        var index = version.IndexOf('+', StringComparison.Ordinal);
        return index >= 0 ? version[..index] : version;
    }
}
