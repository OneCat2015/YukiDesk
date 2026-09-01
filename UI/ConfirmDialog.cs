using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DesktopBar.UI
{
    /// <summary>同名冲突弹窗的选择结果：替换旧项 / 保留为副本。</summary>
    public enum ConflictChoice { Replace, KeepBoth }

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

        /// <summary>
        /// 同名冲突弹窗：替换（旧项目移入回收站）/ 作为副本（自动编号）/ 取消。
        /// applyToAll 来自“为全部文件执行此操作”勾选框；返回 null 表示取消（中止剩余项）。
        /// </summary>
        public static ConflictChoice? ShowConflict(string title, string message, out bool applyToAll)
        {
            ConflictChoice? result = null;
            bool checkedState = false, applyAll = false;
            applyToAll = false;
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
                MinWidth = 420
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

            // 自绘勾选框：默认 CheckBox 的系统样式与深色弹窗不搭。
            var checkMark = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };
            var box = new Border
            {
                Width = 17,
                Height = 17,
                CornerRadius = new CornerRadius(4),
                Background = Brush("#FFFFFF", 0.08),
                BorderBrush = Brush("#FFFFFF", 0.28),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = checkMark
            };
            var checkRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 0),
                Cursor = Cursors.Hand
            };
            checkRow.Children.Add(box);
            checkRow.Children.Add(new TextBlock
            {
                Text = "为全部文件执行此操作",
                FontSize = 12,
                Foreground = Brush("#CFD3E0"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0)
            });
            checkRow.MouseLeftButtonUp += (s, e) =>
            {
                checkedState = !checkedState;
                checkMark.Visibility = checkedState ? Visibility.Visible : Visibility.Collapsed;
                box.Background = checkedState ? Brush("#4C7DFF") : Brush("#FFFFFF", 0.08);
            };

            var cancel = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.FindResource("Btn"),
                MinWidth = 92,
                IsCancel = true
            };
            var keepBoth = new Button
            {
                Content = "作为副本",
                Style = (Style)Application.Current.FindResource("Btn"),
                MinWidth = 100,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var replace = new Button
            {
                Content = "替换",
                Style = (Style)Application.Current.FindResource("BtnDanger"),
                MinWidth = 92,
                Margin = new Thickness(0, 0, 10, 0),
                IsDefault = true
            };
            cancel.Click += (s, e) => { result = null; win.Close(); };
            keepBoth.Click += (s, e) => { result = ConflictChoice.KeepBoth; applyAll = checkedState; win.Close(); };
            replace.Click += (s, e) => { result = ConflictChoice.Replace; applyAll = checkedState; win.Close(); };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0)
            };
            buttons.Children.Add(cancel);
            buttons.Children.Add(keepBoth);
            buttons.Children.Add(replace);

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#F2F3F8")
            });
            content.Children.Add(msg);
            content.Children.Add(checkRow);
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
                if (e.Key == Key.Escape) { result = null; win.Close(); }
            };
            win.ShowDialog();
            applyToAll = applyAll;
            return result;
        }

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
