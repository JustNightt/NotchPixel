using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Xunit;

namespace NotchPixel.E2E.Tests.Infrastructure;

[CollectionDefinition("NotchPixelE2E", DisableParallelization = true)]
public sealed class NotchPixelE2ECollection : ICollectionFixture<E2ETestSessionFixture>
{
}

public sealed class E2ETestSessionFixture : IDisposable
{
    private bool _disposed;

    public StateBackupGuard BackupGuard { get; }
    public string SessionTempDir { get; }

    public E2ETestSessionFixture()
    {
        BackupGuard = new StateBackupGuard();
        SessionTempDir = TestWorkspace.CreateIsolatedTempDir("session");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            ProcessAndWindowDriver.RestoreWindowsNativeTaskbar();
        }
        catch { }

        try
        {
            BackupGuard.Dispose();
        }
        catch { }

        try
        {
            if (Directory.Exists(SessionTempDir))
                Directory.Delete(SessionTempDir, recursive: true);
        }
        catch { }
    }
}

public static class TestWorkspace
{
    public const string RunRegistrySubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunRegistryValueName = "OpenPixel";

    private static readonly Lazy<string> LazyRepoRoot = new(ResolveRepoRoot);

    public static string RepoRoot => LazyRepoRoot.Value;

    public static string OpenPixelExePath
    {
        get
        {
            string rootExe = Path.Combine(RepoRoot, "OpenPixel.exe");
            if (File.Exists(rootExe)) return rootExe;

            string releaseExe = Path.Combine(RepoRoot, "bin", "Release", "net10.0-windows", "OpenPixel.exe");
            if (File.Exists(releaseExe)) return releaseExe;

            string debugExe = Path.Combine(RepoRoot, "bin", "Debug", "net10.0-windows", "OpenPixel.exe");
            if (File.Exists(debugExe)) return debugExe;

            return rootExe;
        }
    }

    public static string LocalAppDataOpenPixelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenPixel");

    public static string RoamingAppDataOpenPixelDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenPixel");

    public static string SettingsJsonPath => Path.Combine(LocalAppDataOpenPixelDir, "settings.json");

    public static string DiagnosticLogPath => Path.Combine(RoamingAppDataOpenPixelDir, "openpixel.log");

    public static string RunRegistryKeyPath => @"HKCU\" + RunRegistrySubKey + @"\" + RunRegistryValueName;

    public static string AppDirectory => Path.Combine(RepoRoot, "App");

    public static string CSharpDirectory => Path.Combine(RepoRoot, "csharp");

    public static string ImagesDirectory => Path.Combine(RepoRoot, "Images");

    private static string ResolveRepoRoot()
    {
        foreach (string seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(seed)) continue;
            var dir = new DirectoryInfo(seed);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "OpenPixel.csproj")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate OpenPixel.csproj walking up from BaseDirectory or CurrentDirectory.");
    }

    public static string? ReadRunRegistryValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistrySubKey, writable: false);
        return key?.GetValue(RunRegistryValueName) as string;
    }

    public static void WriteRunRegistryValue(string? value)
    {
        using RegistryKey? key = Registry.CurrentUser.CreateSubKey(RunRegistrySubKey, writable: true);
        if (key == null) return;
        if (value == null)
        {
            key.DeleteValue(RunRegistryValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(RunRegistryValueName, value, RegistryValueKind.String);
        }
    }

    public static string CreateIsolatedTempDir(string prefix = "npx")
    {
        string path = Path.Combine(Path.GetTempPath(), "NotchPixelE2E", $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}

public sealed class StateBackupGuard : IDisposable
{
    private readonly string _backupRoot;
    private readonly string _localBackupDir;
    private readonly string _roamingBackupDir;
    private readonly bool _hadLocalDir;
    private readonly bool _hadRoamingDir;
    private readonly string? _originalRunRegistryValue;
    private bool _disposed;

    public StateBackupGuard()
    {
        _backupRoot = TestWorkspace.CreateIsolatedTempDir("backup");
        _localBackupDir = Path.Combine(_backupRoot, "local");
        _roamingBackupDir = Path.Combine(_backupRoot, "roaming");

        _hadLocalDir = Directory.Exists(TestWorkspace.LocalAppDataOpenPixelDir);
        if (_hadLocalDir)
        {
            CopyDirectory(TestWorkspace.LocalAppDataOpenPixelDir, _localBackupDir);
        }

        _hadRoamingDir = Directory.Exists(TestWorkspace.RoamingAppDataOpenPixelDir);
        if (_hadRoamingDir)
        {
            CopyDirectory(TestWorkspace.RoamingAppDataOpenPixelDir, _roamingBackupDir);
        }

        _originalRunRegistryValue = TestWorkspace.ReadRunRegistryValue();
    }

    public void Restore()
    {
        RestoreDirectory(TestWorkspace.LocalAppDataOpenPixelDir, _hadLocalDir, _localBackupDir);
        RestoreDirectory(TestWorkspace.RoamingAppDataOpenPixelDir, _hadRoamingDir, _roamingBackupDir);
        TestWorkspace.WriteRunRegistryValue(_originalRunRegistryValue);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Restore();
        }
        finally
        {
            try
            {
                if (Directory.Exists(_backupRoot))
                    Directory.Delete(_backupRoot, recursive: true);
            }
            catch { }
        }
    }

    private static void RestoreDirectory(string targetDir, bool existedOriginally, string backupDir)
    {
        if (Directory.Exists(targetDir))
        {
            Directory.Delete(targetDir, recursive: true);
        }
        if (existedOriginally && Directory.Exists(backupDir))
        {
            CopyDirectory(backupDir, targetDir);
        }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSub = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSub);
        }
    }
}

public static class SettingsJsonHelper
{
    public static class ThemeIds
    {
        public const int LiquidGlass = 0;
        public const int CandyGlass = 1;
        public const int Acrylic = 2;
        public const int Black = 3;
        public const int PureBlack = 4;
        public const int Graphite = 5;
        public const int PureWhite = 6;
        public const int Rose = 7;
        public const int Lavender = 8;
        public const int Peach = 9;
        public const int Sakura = 10;
    }

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject LoadOrCreate(string? path = null)
    {
        string target = path ?? TestWorkspace.SettingsJsonPath;
        if (File.Exists(target))
        {
            string raw = File.ReadAllText(target);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                JsonNode? parsed = JsonNode.Parse(raw);
                if (parsed is JsonObject obj) return obj;
            }
        }
        return new JsonObject
        {
            ["ConfigRevision"] = 2,
            ["Theme"] = ThemeIds.LiquidGlass,
            ["GlassBlurRadius"] = 20.0,
            ["LoopbackApiEnabled"] = true,
            ["LoopbackApiPort"] = 47823,
            ["PcDeckPort"] = 47824,
            ["PcDeckTlsPort"] = 47825
        };
    }

    public static void Save(JsonObject root, string? path = null)
    {
        string target = path ?? TestWorkspace.SettingsJsonPath;
        string? dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = target + ".tmp";
        string json = root.ToJsonString(WriteOpts);
        File.WriteAllText(tmp, json);
        File.Move(tmp, target, overwrite: true);
    }

    public static void Patch(Action<JsonObject> mutator, string? path = null)
    {
        JsonObject root = LoadOrCreate(path);
        mutator(root);
        Save(root, path);
    }

    public static void ResetToCleanSettings(Action<JsonObject>? overrides = null, string? path = null)
    {
        var root = new JsonObject
        {
            ["ConfigRevision"] = 2,
            ["LaunchAtStartup"] = false,
            ["Theme"] = ThemeIds.LiquidGlass,
            ["GlassBlurRadius"] = 20.0,
            ["HideFromScreenshots"] = false,
            ["BottomTaskbarEnabled"] = true,
            ["LoopbackApiEnabled"] = true,
            ["LoopbackApiPort"] = 47823,
            ["PcDeckEnabled"] = false,
            ["PcDeckPort"] = 47824,
            ["PcDeckTlsPort"] = 47825
        };
        overrides?.Invoke(root);
        Save(root, path);
    }

    public static void DeleteSettingsFile(string? path = null)
    {
        string target = path ?? TestWorkspace.SettingsJsonPath;
        if (File.Exists(target)) File.Delete(target);
    }

    public static T? GetValue<T>(string propertyName, string? path = null)
    {
        JsonObject root = LoadOrCreate(path);
        if (!root.TryGetPropertyValue(propertyName, out JsonNode? node) || node == null)
            return default;
        return node.Deserialize<T>();
    }

    public static void SetValue(string propertyName, JsonNode? value, string? path = null)
    {
        Patch(obj => obj[propertyName] = value, path);
    }
}

public sealed record SourceMatch(string FilePath, int LineNumber, string LineText);

public static class SourceInspector
{
    private static readonly string[] BannedPlaceholderPatterns =
    {
        @"Midnight\s+City",
        @"GpuPercent\s*=\s*.*cpu\s*\*\s*0\.65",
        @"0\.65f?\s*\*\s*cpu",
        @"Fake\s+Song",
        @"Dummy\s+Track"
    };

    private static readonly string[] IosArtifactExtensions =
    {
        ".xcodeproj", ".xcworkspace", ".swift", ".storyboard", ".xib", ".ipa", ".mobileprovision"
    };

    private static readonly string[] IosArtifactNames =
    {
        "Podfile", "Podfile.lock", "Cartfile", "Package.swift", "Info.plist", "AppDelegate.swift", "SceneDelegate.swift"
    };

    public static IReadOnlyList<string> GetCSharpSourceFiles()
    {
        if (!Directory.Exists(TestWorkspace.CSharpDirectory)) return Array.Empty<string>();
        return Directory.EnumerateFiles(TestWorkspace.CSharpDirectory, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> GetAppFiles(bool excludeBuildOutput = true)
    {
        if (!Directory.Exists(TestWorkspace.AppDirectory)) return Array.Empty<string>();
        string buildPrefix = Path.Combine(TestWorkspace.AppDirectory, "build") + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(TestWorkspace.AppDirectory, "*", SearchOption.AllDirectories)
            .Where(p => !excludeBuildOutput || !p.StartsWith(buildPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SourceMatch> FindMatchesInCSharp(string regexPattern, RegexOptions options = RegexOptions.IgnoreCase)
    {
        return FindMatchesInFiles(GetCSharpSourceFiles(), regexPattern, options);
    }

    public static IReadOnlyList<SourceMatch> FindMatchesInApp(string regexPattern, RegexOptions options = RegexOptions.IgnoreCase)
    {
        return FindMatchesInFiles(GetAppFiles(excludeBuildOutput: true), regexPattern, options);
    }

    public static bool ContainsForbiddenPlaceholder(out IReadOnlyList<SourceMatch> matches)
    {
        var list = new List<SourceMatch>();
        IReadOnlyList<string> files = GetCSharpSourceFiles();
        foreach (string pattern in BannedPlaceholderPatterns)
        {
            list.AddRange(FindMatchesInFiles(files, pattern, RegexOptions.IgnoreCase));
        }
        matches = list;
        return list.Count > 0;
    }

    public static IReadOnlyList<string> FindIosArtifactsInApp()
    {
        if (!Directory.Exists(TestWorkspace.AppDirectory)) return Array.Empty<string>();
        var hits = new List<string>();
        string buildPrefix = Path.Combine(TestWorkspace.AppDirectory, "build") + Path.DirectorySeparatorChar;

        foreach (string dir in Directory.EnumerateDirectories(TestWorkspace.AppDirectory, "*", SearchOption.AllDirectories))
        {
            if (dir.StartsWith(buildPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            string name = Path.GetFileName(dir);
            if (name.Equals("ios", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xcodeproj", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".xcworkspace", StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(dir);
            }
        }

        foreach (string file in GetAppFiles(excludeBuildOutput: true))
        {
            string name = Path.GetFileName(file);
            string ext = Path.GetExtension(file);
            if (IosArtifactNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                IosArtifactExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                hits.Add(file);
            }
        }

        return hits;
    }

    private static IReadOnlyList<SourceMatch> FindMatchesInFiles(IEnumerable<string> files, string regexPattern, RegexOptions options)
    {
        var regex = new Regex(regexPattern, options | RegexOptions.CultureInvariant);
        var results = new List<SourceMatch>();
        foreach (string file in files)
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                {
                    results.Add(new SourceMatch(file, i + 1, lines[i].Trim()));
                }
            }
        }
        return results;
    }
}
