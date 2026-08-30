using System;
using System.Windows.Media;

namespace DesktopBar.UI
{
    public static class Ui
    {
        public static double Scale => Math.Max(0.75, Math.Min(1.5, App.Settings?.Scale ?? 1.0));

        public static Color ParseColor(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex?.Trim()); }
            catch { return Colors.White; }
        }

        public static SolidColorBrush Brush(string hex, double opacity = 1)
        {
            var c = ParseColor(hex);
            byte a = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
            var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }
    }
}
