using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;
using DesktopBar.Models;

namespace DesktopBar.Services
{
    public static class SettingsService
    {
        private const int CurrentSchemaVersion = 2;
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YukiDesk");
        private static readonly string FilePath = Path.Combine(Dir, "settings.json");
        private static readonly string BackupFilePath = Path.Combine(Dir, "settings.backup.json");
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
            if (TryLoad(FilePath, out var current)) return current;
            if (TryLoad(BackupFilePath, out var backup)) return backup;
            if (TryLoad(LegacyFilePath, out var legacy))
            {
                Save(legacy);
                return legacy;
            }
            firstRun = !File.Exists(FilePath) && !File.Exists(LegacyFilePath);
            return Normalize(new AppSettings());
        }

        private static bool TryLoad(string path, out AppSettings settings)
        {
            settings = null;
            if (!File.Exists(path)) return false;
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (settings == null) throw new InvalidDataException("配置内容为空");
                settings = Normalize(settings);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("读取配置失败 " + path + ": " + ex.Message);
                // 保留原始配置以便人工恢复，不把它误判为全新安装再立刻覆盖。
                try
                {
                    string quarantine = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    if (!File.Exists(quarantine)) File.Copy(path, quarantine, false);
                }
                catch { }
                return false;
            }
        }

        public static bool IsValidFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim()) return false;
            if (name.EndsWith(".", StringComparison.Ordinal)) return false;
            if (Path.IsPathRooted(name) || name == "." || name == "..") return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)) return false;
            string stem = Path.GetFileNameWithoutExtension(name);
            string upper = stem.ToUpperInvariant();
            if (upper is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") return false;
            if ((upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && upper.Length == 4 && upper[3] >= '1' && upper[3] <= '9') return false;
            return true;
        }

        private static AppSettings Normalize(AppSettings s)
        {
            if (s == null) s = new AppSettings();
            s.Folders ??= new List<string>();
            s.Folders = s.Folders.Where(IsValidFolderName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList();

            if (s.SchemaVersion < CurrentSchemaVersion)
            {
                // 旧版只有每模块色彩，迁移一次；之后允许用户明确使用默认色而不被反复覆盖。
                if (string.IsNullOrWhiteSpace(s.ThemeColor) && s.Folder != null)
                    s.ThemeColor = s.Folder.BgColor;
                if (s.ThemeOpacity <= 0 && s.Folder != null && s.Folder.BgOpacity > 0)
                    s.ThemeOpacity = s.Folder.BgOpacity;
                if (string.IsNullOrWhiteSpace(s.ThemeTextColor) && s.Folder != null)
                    s.ThemeTextColor = s.Folder.FontColor;
                s.SchemaVersion = CurrentSchemaVersion;
            }

            s.StorageRoot = NormalizeRoot(s.StorageRoot);
            s.BarBgColor = NormalizeColor(s.BarBgColor, "#000000");
            s.ThemeColor = NormalizeColor(s.ThemeColor, "#14141C");
            s.ThemeTextColor = NormalizeColor(s.ThemeTextColor, "#FFFFFF");
            s.BorderColor = NormalizeColor(s.BorderColor, "#FFFFFF");
            s.BarBgOpacity = Clamp(s.BarBgOpacity, 0, 1, 0);
            s.ThemeOpacity = Clamp(s.ThemeOpacity, 0.05, 1, 0.55);
            s.BorderOpacity = Clamp(s.BorderOpacity, 0, 1, 0.32);
            s.BorderThickness = Clamp(s.BorderThickness, 0, 6, 1);
            s.Scale = Clamp(s.Scale, 0.75, 1.5, 1);
            s.DesktopSpaceHeight = Math.Clamp(s.DesktopSpaceHeight, 38, 160);
            s.HoverCloseDelayMs = Math.Clamp(s.HoverCloseDelayMs, 80, 2000);
            s.AnimationDurationMs = Math.Clamp(s.AnimationDurationMs, 60, 1200);
            if (s.SettingsThemeMode < 0 || s.SettingsThemeMode > 2) s.SettingsThemeMode = 0;
            return s;
        }

        private static string NormalizeRoot(string path)
        {
            string fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YukiDesk", "MiniFolders");
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return fallback;
                return Path.GetFullPath(path);
            }
            catch { return fallback; }
        }

        private static string NormalizeColor(string value, string fallback)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(value);
                return color.A == byte.MaxValue
                    ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
                    : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch { return fallback; }
        }

        private static double Clamp(double value, double minimum, double maximum, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        public static void Save(AppSettings s)
        {
            if (s == null) return;
            try
            {
                s = Normalize(s);
                Directory.CreateDirectory(Dir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(s, JsonOptions));
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, BackupFilePath, true);
                else
                    File.Move(temp, FilePath);
            }
            catch (Exception ex) { App.Log("保存配置失败: " + ex.Message); }
        }
    }
}
