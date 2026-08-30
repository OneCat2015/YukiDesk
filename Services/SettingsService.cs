using System;
using System.IO;
using System.Text.Json;
using System.Linq;
using DesktopBar.Models;

namespace DesktopBar.Services
{
    public static class SettingsService
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static readonly string LegacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopBar");
        private static readonly string LegacyFilePath = Path.Combine(LegacyDir, "settings.json");
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static AppSettings Load(out bool firstRun)
        {
            firstRun = false;
            try
            {
                if (File.Exists(FilePath))
                {
                    var current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
                                  ?? new AppSettings();
                    MigrateDefaults(current);
                    return current;
                }
                if (File.Exists(LegacyFilePath))
                {
                    var legacy = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(LegacyFilePath), JsonOptions)
                                 ?? new AppSettings();
                    MigrateDefaults(legacy);
                    try { Directory.CreateDirectory(Dir); File.Copy(LegacyFilePath, FilePath, false); } catch { }
                    return legacy;
                }
            }
            catch (Exception ex) { App.Log("读取配置失败: " + ex.Message); }
            firstRun = true;
            return new AppSettings();
        }

        private static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return false;
            if (Path.IsPathRooted(name) || name == "." || name == "..") return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            return string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal);
        }

        private static void MigrateDefaults(AppSettings s)
        {
            if (s == null) return;
            if (string.IsNullOrWhiteSpace(s.ThemeColor) || s.ThemeColor == "#14141C")
            {
                if (s.Folder != null && !string.IsNullOrWhiteSpace(s.Folder.BgColor))
                    s.ThemeColor = s.Folder.BgColor;
            }
            if (s.ThemeOpacity <= 0.01 && s.Folder != null && s.Folder.BgOpacity > 0.01)
                s.ThemeOpacity = s.Folder.BgOpacity;
            if (string.IsNullOrWhiteSpace(s.ThemeTextColor) || s.ThemeTextColor == "#FFFFFF")
            {
                if (s.Folder != null && !string.IsNullOrWhiteSpace(s.Folder.FontColor))
                    s.ThemeTextColor = s.Folder.FontColor;
            }
            if (s.StorageRoot == null || s.StorageRoot.Length == 0)
                s.StorageRoot = Path.Combine(AppContext.BaseDirectory, "MiniFolders");
            s.Folders ??= new System.Collections.Generic.List<string>();
            s.Folders = s.Folders
                .Where(IsValidFolderName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (s.SettingsThemeMode < 0 || s.SettingsThemeMode > 2) s.SettingsThemeMode = 0;
            if (s.DesktopSpaceHeight < 1) s.DesktopSpaceHeight = 46;
            if (s.Scale < 0.75 || s.Scale > 1.5) s.Scale = 1.0;
            if (s.HoverCloseDelayMs < 80) s.HoverCloseDelayMs = 250;
            if (s.AnimationDurationMs < 60) s.AnimationDurationMs = 180;
        }

        public static void Save(AppSettings s)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOptions));
            }
            catch (Exception ex) { App.Log("保存配置失败: " + ex.Message); }
        }
    }
}
