using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopBar.UI
{
    /// <summary>暗色风格的小输入对话框,返回输入内容(取消返回 null)。</summary>
    public static class Prompt
    {
        public static string Show(string title, string label, string initial)
        {
            string result = null;
            bool ok = false;

            var box = new TextBox
            {
                Style = (Style)Application.Current.FindResource("DarkBox"),
                Text = initial ?? "",
                Margin = new Thickness(0, 12, 0, 0),
                FontSize = 13
            };

            var btnCancel = new Button
            {
                Content = "取消",
                Style = (Style)Application.Current.FindResource("Btn"),
                IsCancel = true,
                Margin = new Thickness(0, 18, 8, 0)
            };
            var btnOk = new Button
            {
                Content = "确定",
                Style = (Style)Application.Current.FindResource("BtnAccent"),
                IsDefault = true,
                Margin = new Thickness(0, 18, 0, 0)
            };
            btnCancel.Click += (s, e) => ((Window)btnCancel.Tag).Close();
            btnOk.Click += (s, e) => { ok = true; ((Window)btnOk.Tag).Close(); };

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            btnRow.Children.Add(btnCancel);
            btnRow.Children.Add(btnOk);

            var body = new StackPanel { Margin = new Thickness(22) };
            body.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = Ui.Brush("#ECECF2")
            });
            body.Children.Add(box);
            body.Children.Add(btnRow);

            var w = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Content = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = Ui.Brush("#161622"),
                    BorderBrush = Ui.Brush("#2A2A3C"),
                    BorderThickness = new Thickness(1),
                    Child = body
                }
            };
            btnCancel.Tag = w;
            btnOk.Tag = w;
            w.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();

            if (ok) result = box.Text?.Trim();
            return result;
        }
    }
}
