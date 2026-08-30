using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopBar.UI
{
    /// <summary>
    /// 圆角半透明确认弹窗：始终置顶（呼出即在最上层）、深色毛玻璃质感、支持拖动与回车/Esc。
    /// 用于顶栏模块的确认场景，替代会被其他窗口压住的系统 MessageBox。
    /// </summary>
    public static class ConfirmDialog
    {
        public static bool Show(string title, string message, string confirmText = "确定")
            => ShowInternal(title, message, confirmText, "取消", true);

        /// <summary>单按钮通知弹窗（同样置顶）。</summary>
        public static void Notice(string title, string message)
            => ShowInternal(title, message, "知道了", null, false);

        private static bool ShowInternal(string title, string message, string confirmText, string cancelText, bool showCancel)
        {
            bool result = false;
            var win = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = true,
                Topmost = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = 380
            };

            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#F2F3F8")
            };
            var msg = new TextBlock
            {
                Text = message,
                FontSize = 13,
                LineHeight = 20,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#CFD3E0"),
                Margin = new Thickness(0, 10, 0, 0)
            };

            var cancel = new Button
            {
                Content = cancelText,
                Style = (Style)Application.Current.FindResource("Btn"),
                MinWidth = 92,
                Margin = new Thickness(0, 0, 10, 0),
                IsCancel = true,
                Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed
            };
            var ok = new Button
            {
                Content = confirmText,
                Style = (Style)Application.Current.FindResource("BtnDanger"),
                MinWidth = 92,
                IsDefault = true
            };
            cancel.Click += (s, e) => { result = false; win.Close(); };
            ok.Click += (s, e) => { result = true; win.Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);

            var content = new StackPanel();
            content.Children.Add(titleTb);
            content.Children.Add(msg);
            content.Children.Add(buttons);

            var border = new Border
            {
                // 留出阴影空间
                Margin = new Thickness(18),
                CornerRadius = new CornerRadius(14),
                Background = Brush("#15151E", 0.94),
                BorderBrush = Brush("#FFFFFF", 0.10),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 26,
                    ShadowDepth = 6,
                    Opacity = 0.5,
                    Color = Colors.Black
                },
                Child = content
            };
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { win.DragMove(); } catch (InvalidOperationException) { }
                }
            };

            win.Content = border;
            win.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { result = false; win.Close(); }
            };
            win.ShowDialog();
            return result;
        }

        private static Brush Brush(string hex, double opacity = 1)
            => Ui.Brush(hex, opacity);
    }
}
