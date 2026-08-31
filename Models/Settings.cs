using System.Collections.Generic;
using System.IO;

namespace DesktopBar.Models
{
    /// <summary>旧版配置兼容类型。YukiDesk 运行时统一使用 AppSettings 的主题。</summary>
    public class TabStyle
    {
        public string BgColor { get; set; } = "#14141C";
        public double BgOpacity { get; set; } = 0.55;
        public string FontFamily { get; set; } = "Microsoft YaHei UI";
        public double FontSize { get; set; } = 13;
        public string FontColor { get; set; } = "#FFFFFF";
    }

    public class AppSettings
    {
        /// <summary>配置迁移版本；只对旧配置执行一次兼容转换。</summary>
        public int SchemaVersion { get; set; } = 2;
        // 顶栏背景仍可独立关闭，以保持桌面点击穿透。
        public string BarBgColor { get; set; } = "#000000";
        public double BarBgOpacity { get; set; } = 0.0;

        // 统一选项卡和展开页主题。
        public string ThemeColor { get; set; } = "#14141C";
        public double ThemeOpacity { get; set; } = 0.55;
        public string ThemeTextColor { get; set; } = "#FFFFFF";
        public bool BorderEnabled { get; set; } = true;
        public string BorderColor { get; set; } = "#FFFFFF";
        public double BorderOpacity { get; set; } = 0.32;
        public double BorderThickness { get; set; } = 1;
        public double Scale { get; set; } = 1.0;
        public bool AutoTheme { get; set; } = true;
        public int HoverCloseDelayMs { get; set; } = 250;
        public int AnimationDurationMs { get; set; } = 180;

        // 旧字段保留用于读取旧配置并迁移，不再单独应用。
        public TabStyle Folder { get; set; } = new TabStyle();
        public TabStyle Music { get; set; } = new TabStyle();
        public TabStyle Clock { get; set; } = new TabStyle();
        public TabStyle SettingsBtn { get; set; } = new TabStyle { BgOpacity = 0.25 };

        // 时间
        public bool TimeOnRight { get; set; } = false;
        public bool ShowSeconds { get; set; } = true;
        public bool ShowDate { get; set; } = false;
        public bool ShowWeekday { get; set; } = false;

        // 小文件夹
        public string StorageRoot { get; set; } = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "YukiDesk", "MiniFolders");
        public List<string> Folders { get; set; } = new List<string>();

        // 桌面与通用
        public bool AutoStart { get; set; } = false;
        public bool ReserveDesktopSpace { get; set; } = false;
        public int DesktopSpaceHeight { get; set; } = 46;
        public bool ShowPower { get; set; } = true;
        public bool ShowFolders { get; set; } = true;
        public bool ShowMusic { get; set; } = true;
        public bool ShowVolume { get; set; } = true;
        public bool ShowRecycleBin { get; set; } = true;
        public bool ShowSpectrum { get; set; } = true;

        // 更新(经 GitHub Releases 检测与下载)
        public bool AutoCheckUpdate { get; set; } = true;
        public bool AutoUpdate { get; set; } = false;

        // 设置窗口外观
        public bool SettingsFollowSystemTheme
        {
            get => SettingsThemeMode == 0;
            set => SettingsThemeMode = value ? 0 : 2;
        }
        /// <summary>设置窗口主题模式:0=跟随系统(默认) 1=浅色 2=深色。</summary>
        public int SettingsThemeMode { get; set; } = 0;
    }
}
