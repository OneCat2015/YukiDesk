using System;
using System.Drawing;
using System.IO;
using Microsoft.Win32;
using DesktopBar.Models;
using DesktopBar.UI;

namespace DesktopBar.Services
{
    public static class ThemeService
    {
        public static event Action Changed;
        private static bool _started;
        private static bool _stopped;
        private static UserPreferenceChangedEventHandler _preferenceChanged;
        private static string _background = "#14141C";
        private static string _text = "#FFFFFF";

        public static string Background => _background;
        public static string Text => _text;
        public static string Border => App.Settings?.BorderColor ?? "#FFFFFF";

        public static void Start()
        {
            if (_started || _stopped) return;
            _started = true;
            _preferenceChanged = (s, e) => Refresh();
            SystemEvents.UserPreferenceChanged += _preferenceChanged;
            Refresh();
        }

        public static void Stop()
        {
            if (!_started) return;
            _stopped = true;
            if (_preferenceChanged != null)
            {
                try { SystemEvents.UserPreferenceChanged -= _preferenceChanged; }
                catch { }
                _preferenceChanged = null;
            }
            Changed = null;
        }

        public static void Refresh()
        {
            if (_stopped) return;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                try { dispatcher.BeginInvoke(new Action(Refresh)); }
                catch (InvalidOperationException) { }
                return;
            }
            var s = App.Settings;
            if (s == null) return;
            if (!s.AutoTheme)
            {
                _background = s.ThemeColor;
                _text = s.ThemeTextColor;
            }
            else
            {
                bool bright = WallpaperIsBright();
                _background = bright ? "#111118" : "#F6F7FB";
                _text = bright ? "#FFFFFF" : "#171821";
            }
            var resources = System.Windows.Application.Current?.Resources;
            if (resources != null)
            {
                resources["ThemePanelBackground"] = Ui.Brush(_background, s.ThemeOpacity);
                resources["ThemePanelBorder"] = s.BorderEnabled
                    ? Ui.Brush(s.BorderColor, s.BorderOpacity)
                    : System.Windows.Media.Brushes.Transparent;
                resources["ThemePanelSubtle"] = Ui.Brush(_text, 0.06);
                resources["ThemePanelControl"] = Ui.Brush(_text, 0.10);
                resources["ThemePanelControlHover"] = Ui.Brush(_text, 0.18);
                resources["ThemePanelHover"] = Ui.Brush(_text, 0.12);
                resources["ThemePanelSelected"] = Ui.Brush(_text, 0.20);
                resources["ThemePanelText"] = Ui.Brush(_text, 1);
            }
            Changed?.Invoke();
        }

        private static bool WallpaperIsBright()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                string path = key?.GetValue("WallPaper") as string;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
                using var bmp = new Bitmap(path);
                int stepX = Math.Max(1, bmp.Width / 32);
                int stepY = Math.Max(1, bmp.Height / 32);
                double sum = 0;
                int count = 0;
                for (int y = 0; y < bmp.Height; y += stepY)
                    for (int x = 0; x < bmp.Width; x += stepX)
                    {
                        var c = bmp.GetPixel(x, y);
                        sum += (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                        count++;
                    }
                return count > 0 && sum / count > 0.56;
            }
            catch { return false; }
        }
    }
}
