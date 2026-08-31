using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Data;
using Microsoft.Win32;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;
using DesktopBar.UI;

namespace DesktopBar
{
    public partial class SettingsWindow : Window
    {
        private AppSettings S => App.Settings;
        private ListBox _folderList;
        private bool _updatingTheme;
        private string _currentPage;
        private int _pageAnimationVersion;

        public SettingsWindow()
        {
            InitializeComponent();
            // 先应用主题再创建动态控件，避免控件捕获初始化阶段的低对比度画刷。
            ApplyWindowTheme();
            BuildAppearancePage();
            BuildTimePage();
            BuildFoldersPage();
            BuildModulesPage();
            BuildGeneralPage();
            BuildAboutPage();
            ShowPage("Appearance");
            ThemeService.Changed += OnThemeChanged;
            ApplyWindowTheme();
            Closed += (s, e) => ThemeService.Changed -= OnThemeChanged;
        }

        // ---------- 导航 ----------
        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (PagesGrid == null) return;
            ShowPage((string)((RadioButton)sender).Tag);
        }

        private void ShowPage(string tag)
        {
            if (PagesGrid == null || string.IsNullOrEmpty(tag)) return;
            var next = PagesGrid.Children.OfType<FrameworkElement>()
                .FirstOrDefault(x => string.Equals((string)x.Tag, tag, StringComparison.Ordinal));
            if (next == null) return;
            if (string.Equals(_currentPage, tag, StringComparison.Ordinal)) return;

            var previous = PagesGrid.Children.OfType<FrameworkElement>()
                .FirstOrDefault(x => string.Equals((string)x.Tag, _currentPage, StringComparison.Ordinal));
            double previousOpacity = previous?.Opacity ?? 1;
            _currentPage = tag;
            int version = ++_pageAnimationVersion;
            var duration = TimeSpan.FromMilliseconds(Math.Max(120, Math.Min(320, S.AnimationDurationMs)));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            foreach (var child in PagesGrid.Children.OfType<FrameworkElement>())
            {
                child.BeginAnimation(OpacityProperty, null);
                child.RenderTransform = null;
                child.IsHitTestVisible = ReferenceEquals(child, next);
                Panel.SetZIndex(child, ReferenceEquals(child, next) ? 1 : 0);
                if (!ReferenceEquals(child, next) && !ReferenceEquals(child, previous))
                {
                    child.Visibility = Visibility.Collapsed;
                    child.Opacity = 0;
                }
            }

            next.Visibility = Visibility.Visible;
            next.IsHitTestVisible = true;
            next.Opacity = 0;
            next.RenderTransformOrigin = new Point(0.5, 0.5);
            next.RenderTransform = new TranslateTransform(16, 0);
            var fadeIn = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var slideIn = new DoubleAnimation(16, 0, duration) { EasingFunction = ease };
            next.BeginAnimation(OpacityProperty, fadeIn);
            ((TranslateTransform)next.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slideIn);

            if (previous != null && !ReferenceEquals(previous, next))
            {
                previous.Visibility = Visibility.Visible;
                previous.IsHitTestVisible = false;
                double currentOpacity = Math.Clamp(previous.Opacity, 0, 1);
                previous.Opacity = currentOpacity;
                var fadeOut = new DoubleAnimation(currentOpacity, 0, duration) { EasingFunction = ease };
                fadeOut.Completed += (s, e) =>
                {
                    if (version == _pageAnimationVersion && !ReferenceEquals(previous, next))
                    {
                        previous.Visibility = Visibility.Collapsed;
                        previous.RenderTransform = null;
                        previous.BeginAnimation(OpacityProperty, null);
                        previous.Opacity = 0;
                        Panel.SetZIndex(previous, 0);
                    }
                };
                previous.BeginAnimation(OpacityProperty, fadeOut);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

        private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null)
            {
                e.Handled = true;
                return;
            }

            // 已移除最大化:双击标题栏不再切换最大化,仅拦截拖动。
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                if (WindowState == WindowState.Maximized)
                    WindowState = WindowState.Normal;
                try { DragMove(); } catch (InvalidOperationException) { }
                e.Handled = true;
            }
        }

        private static T FindParent<T>(DependencyObject element) where T : DependencyObject
        {
            while (element != null)
            {
                if (element is T match) return match;
                element = VisualTreeHelper.GetParent(element);
            }
            return null;
        }

        private void OnThemeChanged() => Dispatcher.BeginInvoke(new Action(ApplyWindowTheme));

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 0) ?? 0) != 0;
            }
            catch { return false; }
        }

        private void ApplyWindowTheme()
        {
            if (_updatingTheme || WindowRoot == null) return;
            _updatingTheme = true;
            bool light = S.SettingsThemeMode == 1
                || (S.SettingsThemeMode == 0 && SystemUsesLightTheme());
            var resources = Application.Current.Resources;
            // 跟随系统:浅色模式用干净的白色纸面,深色模式用黑灰底白字(均无蓝紫色调)。
            resources["SettingsWindowBackground"] = Ui.Brush(light ? "#F5F6F8" : "#0E0E0E");
            resources["SettingsTitleBackground"] = Ui.Brush(light ? "#FFFFFF" : "#141414");
            resources["SettingsWindowBorder"] = Ui.Brush(light ? "#DDDFE6" : "#2E2E2E");
            resources["SettingsTextBrush"] = Ui.Brush(light ? "#1A1B22" : "#FFFFFF");
            resources["SettingsSubTextBrush"] = Ui.Brush(light ? "#494E5C" : "#E4E4E4");
            resources["SettingsLabelBrush"] = Ui.Brush(light ? "#101218" : "#FFFFFF");
            resources["SettingsHoverBackground"] = Ui.Brush(light ? "#EBEDF2" : "#262626");
            resources["SettingsCardBackground"] = Ui.Brush(light ? "#FFFFFF" : "#161616");
            resources["SettingsCardBorder"] = Ui.Brush(light ? "#E3E5EC" : "#2C2C2C");
            resources["SettingsControlBackground"] = Ui.Brush(light ? "#FFFFFF" : "#1E1E1E");
            resources["SettingsControlBorder"] = Ui.Brush(light ? "#C9CDD8" : "#3A3A3A");
            resources["SettingsButtonBackground"] = Ui.Brush(light ? "#EDEFF4" : "#232323");
            resources["SettingsButtonHover"] = Ui.Brush(light ? "#E0E3EB" : "#303030");
            resources["SettingsButtonPressed"] = Ui.Brush(light ? "#D4D8E2" : "#181818");
            resources["SettingsAccent"] = Ui.Brush(light ? "#5575E8" : "#6C8CFF");
            resources["SettingsAccentHover"] = Ui.Brush(light ? "#6786F0" : "#7D9BFF");
            resources["SettingsAccentPressed"] = Ui.Brush(light ? "#4564D0" : "#5A78E6");
            resources["SettingsSwitchTrack"] = Ui.Brush(light ? "#6D84D8" : "#465A9D", 0.62);
            resources["SettingsSwitchThumb"] = Ui.Brush(light ? "#FFFFFF" : "#F5F5F5");
            resources["SettingsSwitchThumbBorder"] = Ui.Brush(light ? "#B9BFCF" : "#555555");
            resources["SettingsTrack"] = Ui.Brush(light ? "#DBDEE6" : "#2E2E2E");
            resources["SettingsNavHover"] = Ui.Brush(light ? "#ECEDF2" : "#262626");
            resources["SettingsNavSelected"] = Ui.Brush(light ? "#5575E8" : "#6C8CFF");
            resources["SettingsSelected"] = Ui.Brush(light ? "#E2E7F6" : "#303030");
            resources["SettingsListHover"] = Ui.Brush(light ? "#E8EAF0" : "#3A3A3A");
            resources["SettingsPopupBackground"] = Ui.Brush(light ? "#FFFFFF" : "#1A1A1A");
            resources["SettingsScrollThumb"] = Ui.Brush(light ? "#B9BFCC" : "#484848");
            _dropLine?.InvalidateVisual();
            _updatingTheme = false;
        }

        private void Save() => App.ApplyAndSave();

        // ---------- 通用构建助手 ----------
        // 注意:主题画刷必须用 SetResourceReference(DynamicResource)引用——
        // FindResource 是一次性快照,主题切换后不会更新,会出现深底深字看不清。

        private Border Card(string title, string subtitle, out StackPanel body)
        {
            var sp = new StackPanel();
            var titleTb = new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            titleTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            sp.Children.Add(titleTb);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var subTb = new TextBlock
                {
                    Text = subtitle,
                    FontSize = 11.5,
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                subTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
                sp.Children.Add(subTb);
            }
            body = sp;
            return new Border { Style = (Style)FindResource("Card"), Child = sp };
        }

        /// <summary>动态主题色的说明文字(顶部间距 12,可自定义)。</summary>
        private TextBlock MakeHint(string text, double topMargin = 12)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                Margin = new Thickness(0, topMargin, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            tb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            return tb;
        }

        private FrameworkElement Row(string label, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 18, 0),
                TextWrapping = TextWrapping.Wrap
            };
            l.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
            Grid.SetColumn(l, 0);
            Grid.SetColumn(control, 1);
            g.Children.Add(l);
            g.Children.Add(control);
            return g;
        }

        private FrameworkElement NumberSlider(double min, double max, double val, string suffix, Action<double> changed)
        {
            var s = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Math.Max(min, Math.Min(max, val)),
                IsSnapToTickEnabled = true,
                TickFrequency = 1,
                Style = (Style)FindResource("SliderDark"),
                Margin = new Thickness(0, 0, 56, 0)
            };
            var t = new TextBlock
            {
                Text = Math.Round(val) + suffix,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            t.SetResourceReference(TextElement.ForegroundProperty, "SettingsLabelBrush");
            var g = new Grid();
            g.Children.Add(s);
            g.Children.Add(t);
            // 拖动中只刷新数字标签;回调可能触发顶栏全量重建(如改缩放/主题),
            // 若在 ValueChanged 里实时执行会每帧重建可视树,表现为滑块卡死拖不动。
            // 统一改为松手后提交一次。
            s.ValueChanged += (o, e) => t.Text = Math.Round(e.NewValue) + suffix;
            // 模板在样式应用后才实例化,Loaded 时 Thumb 才能被找到。
            s.Loaded += (o, e) =>
            {
                var thumb = s.Template?.FindName("thumb", s) as Thumb;
                if (thumb != null) thumb.DragCompleted += (o2, e2) => changed(Math.Round(s.Value));
                else s.ValueChanged += (o2, e2) => changed(Math.Round(e2.NewValue));
            };
            return g;
        }

        private FrameworkElement ToggleRow(string label, bool val, Action<bool> changed)
        {
            var t = new ToggleButton
            {
                IsChecked = val,
                Style = (Style)FindResource("Switch"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            t.Click += (s, e) => changed(t.IsChecked == true);
            return Row(label, t);
        }

        private ColorEditor NewColorEditor(string hex, Action<string> changed)
        {
            var ce = new ColorEditor(hex);
            ce.Changed += changed;
            return ce;
        }

        private FrameworkElement TabStyleCard(string title, TabStyle st)
        {
            return Card(title, null, out var body);
        }

        /// <summary>设置窗口主题三选:跟随系统(默认)/浅色/深色。</summary>
        private FrameworkElement BuildThemeModePicker()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var options = new (string Label, int Mode)[]
            {
                ("跟随系统", 0), ("浅色", 1), ("深色", 2)
            };
            foreach (var (label, mode) in options)
            {
                var rb = new RadioButton
                {
                    Content = label,
                    GroupName = "thememode",
                    IsChecked = S.SettingsThemeMode == mode,
                    Style = (Style)FindResource("NavRadio")
                };
                rb.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
                rb.Checked += (s, e) => { S.SettingsThemeMode = mode; ApplyWindowTheme(); Save(); };
                panel.Children.Add(rb);
            }
            return panel;
        }

        // ---------- 外观 ----------
        private void BuildAppearancePage()
        {
            AppearanceHost.Children.Clear();
            var theme = Card("统一选项卡与展开页", "所有药丸和展开背景使用同一套颜色与透明度。", out var body);
            body.Children.Add(Row("主题色盘", NewColorEditor(S.ThemeColor, h => { S.ThemeColor = h; S.AutoTheme = false; ThemeService.Refresh(); Save(); })));
            // 顶栏文字与矢量图标的颜色；手动指定后关闭壁纸明暗自动适配，避免自动黑白文字覆盖自定义色。
            body.Children.Add(Row("文字与图标颜色", NewColorEditor(S.ThemeTextColor, h => { S.ThemeTextColor = h; S.AutoTheme = false; ThemeService.Refresh(); Save(); })));
            body.Children.Add(Row("透明度", NumberSlider(0, 100, S.ThemeOpacity * 100, "%", v => { S.ThemeOpacity = Math.Round(v) / 100; ThemeService.Refresh(); Save(); })));
            body.Children.Add(Row("缩放", NumberSlider(75, 150, S.Scale * 100, "%", v => { S.Scale = Math.Round(v) / 100; Save(); })));
            body.Children.Add(ToggleRow("自动适配壁纸明暗", S.AutoTheme, v => { S.AutoTheme = v; ThemeService.Refresh(); Save(); }));
            body.Children.Add(Row("设置窗口主题", BuildThemeModePicker()));
            body.Children.Add(ToggleRow("显示统一边框", S.BorderEnabled, v => { S.BorderEnabled = v; ThemeService.Refresh(); Save(); }));
            body.Children.Add(Row("边框颜色", NewColorEditor(S.BorderColor, h => { S.BorderColor = h; ThemeService.Refresh(); Save(); })));
            body.Children.Add(Row("边框透明度", NumberSlider(0, 100, S.BorderOpacity * 100, "%", v => { S.BorderOpacity = Math.Round(v) / 100; ThemeService.Refresh(); Save(); })));
            AppearanceHost.Children.Add(theme);

            var barCard = Card("顶栏背景", "默认完全透明，不拦截桌面点击。", out var barBody);
            barBody.Children.Add(Row("颜色", NewColorEditor(S.BarBgColor, h => { S.BarBgColor = h; Save(); })));
            barBody.Children.Add(Row("不透明度", NumberSlider(0, 100, S.BarBgOpacity * 100, "%", v => { S.BarBgOpacity = Math.Round(v) / 100; Save(); })));
            AppearanceHost.Children.Add(barCard);
        }

        // ---------- 时间 ----------
        private void BuildTimePage()
        {
            TimeHost.Children.Clear();

            var card = Card("时间显示", null, out var body);

            var r1 = new RadioButton
            {
                Content = "靠右",
                GroupName = "timepos",
                IsChecked = S.TimeOnRight,
                Style = (Style)FindResource("NavRadio")
            };
            var r2 = new RadioButton
            {
                Content = "居中",
                GroupName = "timepos",
                IsChecked = !S.TimeOnRight,
                Style = (Style)FindResource("NavRadio")
            };
            r1.Checked += (s, e) => { S.TimeOnRight = true; Save(); };
            r2.Checked += (s, e) => { S.TimeOnRight = false; Save(); };
            var posPanel = new StackPanel { Orientation = Orientation.Horizontal };
            posPanel.Children.Add(r1);
            posPanel.Children.Add(r2);
            body.Children.Add(Row("显示位置", posPanel));

            body.Children.Add(ToggleRow("显示秒数", S.ShowSeconds, v => { S.ShowSeconds = v; Save(); }));
            body.Children.Add(ToggleRow("显示日期", S.ShowDate, v => { S.ShowDate = v; Save(); }));
            body.Children.Add(ToggleRow("显示星期", S.ShowWeekday, v => { S.ShowWeekday = v; Save(); }));
            body.Children.Add(MakeHint("鼠标悬停在时间选项卡上时,会展开显示星期、日期和农历日期。", 14));
            TimeHost.Children.Add(card);
        }

        // ---------- 小文件夹 ----------

        private static bool IsValidFolderName(string name) => SettingsService.IsValidFolderName(name);

        private string GetFolderPath(string name)
        {
            if (!IsValidFolderName(name)) return null;
            try
            {
                string root = Path.GetFullPath(S.StorageRoot);
                string candidate = Path.GetFullPath(Path.Combine(root, name));
                string relative = Path.GetRelativePath(root, candidate);
                if (relative == "." || Path.IsPathRooted(relative)
                    || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)) return null;
                return string.Equals(relative, name, StringComparison.OrdinalIgnoreCase) ? candidate : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 把存储根目录下实际存在的子文件夹同步进 S.Folders:
        /// 磁盘新出现的文件夹自动追加到列表末尾(顶栏最右),配置里已不存在的自动剔除。
        /// 返回是否有变更。
        /// </summary>
        private bool SyncFoldersFromDisk()
        {
            try
            {
                if (!Directory.Exists(S.StorageRoot)) return false;
                var onDisk = Directory.GetDirectories(S.StorageRoot)
                    .Select(Path.GetFileName)
                    .Where(n => IsValidFolderName(n))
                    .ToList();
                var comparer = StringComparer.OrdinalIgnoreCase;
                bool changed = false;
                // 新发现的文件夹追加到末尾(顶栏最右)。
                foreach (var name in onDisk.Where(n => !S.Folders.Contains(n, comparer)))
                {
                    S.Folders.Add(name);
                    changed = true;
                }
                // 配置里有但磁盘上已删掉的,自动剔除。
                for (int i = S.Folders.Count - 1; i >= 0; i--)
                {
                    if (!onDisk.Contains(S.Folders[i], comparer))
                    {
                        S.Folders.RemoveAt(i);
                        changed = true;
                    }
                }
                return changed;
            }
            catch (Exception ex) { App.Log("同步小文件夹失败: " + ex.Message); return false; }
        }

        private void BuildFoldersPage()
        {
            FoldersHost.Children.Clear();
            bool synced = SyncFoldersFromDisk();
            if (synced) Save();

            var pathCard = Card("存储路径",
                "每个小文件夹都是这个目录下的一个真实子文件夹,拖入的文件就存在里面。目录下的所有文件夹会自动加入顶栏。", out var pbody);
            var pathBox = new TextBox
            {
                Style = (Style)FindResource("DarkBox"),
                Text = S.StorageRoot,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                IsReadOnly = true
            };
            var browse = new Button { Content = "浏览…", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) =>
            {
                var dlg = new OpenFolderDialog { Title = "选择存储根目录" };
                if (dlg.ShowDialog() == true)
                {
                    S.StorageRoot = dlg.FolderName;
                    pathBox.Text = S.StorageRoot;
                    SyncFoldersFromDisk();
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
            };
            var pgrid = new Grid();
            pgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(pathBox, 0);
            Grid.SetColumn(browse, 1);
            pgrid.Children.Add(pathBox);
            pgrid.Children.Add(browse);
            pbody.Children.Add(pgrid);
            FoldersHost.Children.Add(pathCard);

            var listCard = Card("小文件夹管理",
                "拖动列表项排序(顶栏同步生效);列表自动包含目录下所有文件夹。「删除文件夹」会将该文件夹及其内容移入回收站。", out var lbody);
            _folderList = new ListBox
            {
                Style = (Style)FindResource("DarkList"),
                Height = 210,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 12.5,
                AllowDrop = true,
                ItemTemplate = MakeFolderItemTemplate()
            };
            _folderList.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            SetupFolderDragSort();
            RefreshFolderList();

            var btnAdd = new Button { Content = "新建文件夹", Style = (Style)FindResource("BtnAccent") };
            var btnRename = new Button { Content = "重命名", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            var btnRemove = new Button { Content = "删除文件夹", Style = (Style)FindResource("Btn"), Margin = new Thickness(8, 0, 0, 0) };
            btnAdd.Click += (s, e) =>
            {
                var name = Prompt.Show("新建小文件夹", "文件夹名称:", "新文件夹");
                if (!IsValidFolderName(name))
                {
                    MessageBox.Show("请输入单级文件夹名称，不能包含路径分隔符或 ..。", "名称无效",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    string dir = GetFolderPath(name);
                    if (dir == null) return;
                    Directory.CreateDirectory(dir);
                    if (!S.Folders.Contains(name, StringComparer.OrdinalIgnoreCase)) S.Folders.Add(name);
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "错误"); }
            };
            btnRename.Click += (s, e) =>
            {
                if (_folderList.SelectedIndex < 0)
                {
                    MessageBox.Show("请先在列表中选中一个小文件夹。", "提示");
                    return;
                }
                int idx = _folderList.SelectedIndex;
                if (idx >= S.Folders.Count) return;
                string old = S.Folders[idx];
                var name = Prompt.Show("重命名", "新的名称:", old);
                if (string.IsNullOrWhiteSpace(name) || name == old) return;
                if (!IsValidFolderName(name))
                {
                    MessageBox.Show("请输入单级文件夹名称，不能包含路径分隔符或 ..。", "名称无效",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                try
                {
                    string oldDir = GetFolderPath(old);
                    string newDir = GetFolderPath(name);
                    if (oldDir == null || newDir == null) return;
                    if (!Directory.Exists(oldDir))
                    {
                        MessageBox.Show("原文件夹不存在，未修改配置。", "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (Directory.Exists(newDir))
                    {
                        MessageBox.Show("目标文件夹已存在。", "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    if (S.Folders.Where((folder, index) => index != idx).Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        MessageBox.Show("同名小文件夹已存在。", "重命名失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    Directory.Move(oldDir, newDir);
                    S.Folders[idx] = name;
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "错误"); }
            };
            btnRemove.Click += (s, e) =>
            {
                if (_folderList.SelectedIndex < 0) return;
                int idx = _folderList.SelectedIndex;
                if (idx >= S.Folders.Count) return;
                string sel = S.Folders[idx];
                var r = MessageBox.Show(
                    "删除小文件夹“" + sel + "”?\n\n文件夹及其中的所有文件将移入回收站(可在回收站还原),顶栏选项卡同步移除。",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
                try
                {
                    string dir = GetFolderPath(sel);
                    if (dir == null)
                    {
                        MessageBox.Show("该配置中的文件夹名称无效，已阻止删除。", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    if (Directory.Exists(dir))
                    {
                        // 目录内容连同文件夹一起移入回收站(带撤销信息);直接删除不可恢复,不用。
                        if (!NativeMethods.DeleteToRecycleBin(new[] { dir }))
                        {
                            MessageBox.Show("删除文件夹失败(可能被其他程序占用)。", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                            return;
                        }
                    }
                    S.Folders.RemoveAt(idx);
                    Save();
                    RefreshFolderList();
                    App.Bar?.ApplySettings();
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "错误"); }
            };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            btnRow.Children.Add(btnAdd);
            btnRow.Children.Add(btnRename);
            btnRow.Children.Add(btnRemove);
            lbody.Children.Add(_folderList);
            lbody.Children.Add(btnRow);
            FoldersHost.Children.Add(listCard);
        }

        /// <summary>列表行模板:名称居左;"打开"与拖动手柄靠右,拖动手柄在最右。</summary>
        private DataTemplate MakeFolderItemTemplate()
        {
            var tpl = new DataTemplate();
            // DockPanel 让右侧按钮先停靠,名称元素占满剩余空间;
            // ColumnDefinition 不是可视树元素,不能通过 FrameworkElementFactory 追加到模板。
            var row = new FrameworkElementFactory(typeof(DockPanel));
            row.SetValue(DockPanel.LastChildFillProperty, true);

            // 名称(剩余空间)
            var label = new FrameworkElementFactory(typeof(TextBlock));
            label.SetBinding(TextBlock.TextProperty, new Binding(nameof(FolderRow.Name)));
            label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            label.SetResourceReference(TextElement.ForegroundProperty, "SettingsTextBrush");
            // "打开"按钮(右侧)
            var open = new FrameworkElementFactory(typeof(Button));
            open.SetValue(ContentControl.ContentProperty, "打开");
            open.SetValue(Control.FontSizeProperty, 11.0);
            open.SetValue(Control.PaddingProperty, new Thickness(8, 3, 8, 3));
            open.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            open.SetValue(ToolTipProperty, "在资源管理器中打开该文件夹");
            open.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("Btn"));
            open.SetValue(DockPanel.DockProperty, Dock.Right);
            open.AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                if ((s as FrameworkElement)?.DataContext is FolderRow folder)
                {
                    string path = GetFolderPath(folder.Name);
                    if (path != null) ShellOpen.OpenPath(path);
                }
            }));

            // 拖动手柄(最右):只有按住它才发起拖拽。
            var grip = new FrameworkElementFactory(typeof(Button));
            grip.SetValue(ContentControl.ContentProperty, "≡");
            grip.SetValue(Control.FontSizeProperty, 14.0);
            grip.SetValue(Control.PaddingProperty, new Thickness(7, 2, 7, 2));
            grip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            grip.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0));
            grip.SetValue(ToolTipProperty, "按住拖动调整顺序");
            grip.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("Btn"));
            grip.SetValue(FrameworkElement.CursorProperty, Cursors.SizeAll);
            grip.SetValue(DockPanel.DockProperty, Dock.Right);
            grip.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler((s, e) =>
                {
                    e.Handled = true;
                    var container = FindParent<ListBoxItem>(s as DependencyObject);
                    if (container == null) return;
                    int from = _folderList.ItemContainerGenerator.IndexFromContainer(container);
                    if (from < 0) return;
                    _folderList.SelectedIndex = from;
                    var data = new DataObject("folderIndex", from);
                    DragDrop.DoDragDrop(_folderList, data, DragDropEffects.Move);
                }));
            row.AppendChild(grip);
            row.AppendChild(open);
            // LastChildFill 让名称填充剩余宽度,同时保持按钮在最右侧。
            row.AppendChild(label);

            tpl.VisualTree = row;
            return tpl;
        }

        private void RefreshFolderList()
        {
            // 行视图:名称 + 内联"打开"按钮。
            _rows = S.Folders.Select(n => new FolderRow(n)).ToList();
            _folderList.ItemsSource = _rows;
        }

        private List<FolderRow> _rows;

        /// <summary>列表行数据:名称 + 内联打开按钮。</summary>
        private class FolderRow
        {
            public string Name { get; }
            public string OpenLabel => "打开";
            public FolderRow(string name) { Name = name; }
        }

        // ---------- 拖拽排序(预览式) ----------
        private DropLineAdorner _dropLine;
        private int _previewIndex = -1;    // 当前预览插入位置

        /// <summary>
        /// 预览式拖拽排序:按住手柄拖动时列表顺序不变,只显示一条跟随鼠标的插入指示线;
        /// 松手后才真正重排一次。彻底解决"拖到奇怪位置、顺序乱七八糟"的问题。
        /// </summary>
        private void SetupFolderDragSort()
        {
            _folderList.AllowDrop = true;
            _dropLine = new DropLineAdorner(_folderList, this);
            void AttachDropLine()
            {
                var layer = AdornerLayer.GetAdornerLayer(_folderList);
                if (layer != null && _dropLine.Parent == null) layer.Add(_dropLine);
            }
            _folderList.Loaded += (s, e) => AttachDropLine();
            AttachDropLine();
            _folderList.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent("folderIndex")) return;
                e.Effects = DragDropEffects.Move;
                _previewIndex = CalcInsertIndex(e.GetPosition(_folderList).Y);
                _dropLine.Show(_previewIndex);
                e.Handled = true;
            };
            _folderList.DragLeave += (s, e) =>
            {
                _dropLine?.Hide();
                _previewIndex = -1;
                e.Handled = true;
            };
            _folderList.Drop += (s, e) =>
            {
                _dropLine?.Hide();
                if (!e.Data.GetDataPresent("folderIndex")) { e.Handled = true; return; }
                int from = (int)e.Data.GetData("folderIndex");
                int to = _previewIndex;
                _previewIndex = -1;
                CommitDragMove(from, to);
                e.Handled = true;
            };
        }

        /// <summary>由列表内 y 坐标算插入位置:行中点以上插到该行前,以下插到该行后。</summary>
        private int CalcInsertIndex(double y)
        {
            int count = S.Folders.Count;
            for (int i = 0; i < count; i++)
            {
                if (_folderList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi)
                {
                    double top = lbi.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y;
                    double mid = top + lbi.ActualHeight / 2;
                    if (y < mid) return i;
                }
            }
            return count;
        }

        /// <summary>指示线的 y 坐标(列表内容坐标):index 行的顶边,末尾则是最后一行底边。</summary>
        private double InsertLineY(int index)
        {
            if (index < S.Folders.Count
                && _folderList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
                return item.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y - 2;
            if (index > 0
                && _folderList.ItemContainerGenerator.ContainerFromIndex(index - 1) is ListBoxItem last)
                return last.TransformToAncestor(_folderList).Transform(new System.Windows.Point(0, 0)).Y + last.ActualHeight + 2;
            return 4;
        }

        private void CommitDragMove(int from, int to)
        {
            if (from < 0 || from >= S.Folders.Count || to < 0 || to > S.Folders.Count || from == to) return;
            var name = S.Folders[from];
            S.Folders.RemoveAt(from);
            if (to > from) to--;
            int insert = Math.Min(to, S.Folders.Count);
            S.Folders.Insert(insert, name);
            Save();
            RefreshFolderList();
            _folderList.SelectedIndex = insert;
            App.Bar?.ApplySettings();
        }

        /// <summary>装饰层插入指示线:横贯列表宽度的强调色圆角条,淡入淡出。</summary>
        private sealed class DropLineAdorner : Adorner
        {
            private readonly SettingsWindow _owner;
            private double _y = -10;
            public DropLineAdorner(ListBox list, SettingsWindow owner) : base(list)
            {
                IsHitTestVisible = false;
                _owner = owner;
            }
            public void Show(int index)
            {
                _y = _owner.InsertLineY(index);
                if (Opacity < 0.95)
                {
                    BeginAnimation(OpacityProperty, null);
                    BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0.95, TimeSpan.FromMilliseconds(90)));
                }
                InvalidateVisual();
            }
            public void Hide()
            {
                BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
                _y = -10;
            }
            protected override void OnRender(DrawingContext dc)
            {
                if (Opacity <= 0.01 || _y < 0) return;
                var list = (ListBox)AdornedElement;
                double w = list.ActualWidth - 8;
                var brush = (Brush)Application.Current.FindResource("SettingsAccent");
                dc.DrawRoundedRectangle(brush, null, new Rect(4, _y - 1.5, w, 3), 1.5, 1.5);
                // 两端小圆点,像系统拖拽指示。
                dc.DrawEllipse(brush, null, new System.Windows.Point(4, _y), 3, 3);
                dc.DrawEllipse(brush, null, new System.Windows.Point(4 + w, _y), 3, 3);
            }
        }

        // ---------- 音乐 ----------
        private void BuildModulesPage()
        {
            ModulesHost.Children.Clear();

            // 切换只更新对应模块的显隐(ApplyModuleVisibility),不重建顶栏,避免其他胶囊闪烁。
            var card = Card("模块显示",
                "控制顶栏各胶囊的显示与隐藏,切换立即生效、互不影响。", out var body);
            body.Children.Add(ToggleRow("电源按钮", S.ShowPower,
                v => { S.ShowPower = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("小文件夹", S.ShowFolders,
                v => { S.ShowFolders = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("回收站", S.ShowRecycleBin,
                v => { S.ShowRecycleBin = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音乐播放器", S.ShowMusic,
                v => { S.ShowMusic = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音频频谱", S.ShowSpectrum,
                v => { S.ShowSpectrum = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("音量按钮", S.ShowVolume,
                v => { S.ShowVolume = v; Save(); App.Bar?.ApplyModuleVisibility(); }));

            body.Children.Add(ToggleRow("CPU 占用率", S.ShowMonitorCpu,
                v => { S.ShowMonitorCpu = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("GPU 占用率", S.ShowMonitorGpu,
                v =>
                {
                    S.ShowMonitorGpu = v;
                    Save();
                    App.Bar?.ApplyModuleVisibility();
                    if (_gpuCombo != null) _gpuCombo.IsEnabled = v;
                    if (v && (_gpuCombo == null || _gpuCombo.Items.Count == 0)) LoadGpuOptions();
                }));
            _gpuCombo = new ComboBox
            {
                Width = 230,
                MinHeight = 32,
                Style = (Style)FindResource("DarkCombo"),
                DisplayMemberPath = nameof(DesktopBar.Services.GpuAdapterInfo.Name),
                IsEnabled = S.ShowMonitorGpu
            };
            _gpuCombo.SelectionChanged += (s, e) =>
            {
                if (_updatingGpuCombo) return;
                S.MonitorGpuLuid = (_gpuCombo.SelectedItem as DesktopBar.Services.GpuAdapterInfo)?.Luid ?? "";
                Save();
            };
            body.Children.Add(Row("监测的 GPU", _gpuCombo));
            body.Children.Add(ToggleRow("内存占用", S.ShowMonitorMemory,
                v => { S.ShowMonitorMemory = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(ToggleRow("网络上下行", S.ShowMonitorNetwork,
                v => { S.ShowMonitorNetwork = v; Save(); App.Bar?.ApplyModuleVisibility(); }));
            body.Children.Add(MakeHint("CPU、GPU、内存、网络胶囊位于音频频谱左侧;采样只在任一胶囊可见时进行。"
                + "GPU 占用率来自系统 GPU 引擎计数器,多显卡时可在上方选择要监测的 GPU。"));
            ModulesHost.Children.Add(card);

            if (S.ShowMonitorGpu) LoadGpuOptions();
        }

        private ComboBox _gpuCombo;
        private bool _updatingGpuCombo;
        private int _gpuLoadVersion;

        private async void LoadGpuOptions()
        {
            int version = ++_gpuLoadVersion;
            try
            {
                var gpus = await System.Threading.Tasks.Task.Run(DesktopBar.Services.PerfService.EnumerateGpus);
                if (_gpuCombo == null || version != _gpuLoadVersion) return;
                _updatingGpuCombo = true;
                _gpuCombo.ItemsSource = gpus;
                int selected = -1;
                for (int i = 0; i < gpus.Count; i++)
                    if (string.Equals(gpus[i].Luid, S.MonitorGpuLuid, StringComparison.OrdinalIgnoreCase))
                    { selected = i; break; }
                _gpuCombo.SelectedIndex = selected >= 0 ? selected : (gpus.Count > 0 ? 0 : -1);
                _updatingGpuCombo = false;
                if (gpus.Count == 0)
                    _gpuCombo.IsEnabled = false;
            }
            catch (Exception ex)
            {
                App.Log("加载显卡列表失败: " + ex.Message);
            }
        }

        // ---------- 通用 ----------
        private void BuildGeneralPage()
        {
            GeneralHost.Children.Clear();

            var card1 = Card("启动", null, out var b1);
            b1.Children.Add(ToggleRow("开机自动启动", S.AutoStart, v =>
            {
                S.AutoStart = v;
                AutoStartService.Set(v);
                Save();
            }));
            b1.Children.Add(MakeHint("通过当前用户注册表 Run 键实现,登录系统后立即启动,无等待。"));
            b1.Children.Add(Row("悬停关闭", NumberSlider(80, 800, S.HoverCloseDelayMs, "ms", v => { S.HoverCloseDelayMs = (int)Math.Round(v); Save(); })));
            b1.Children.Add(Row("动画时长", NumberSlider(80, 500, S.AnimationDurationMs, "ms", v => { S.AnimationDurationMs = (int)Math.Round(v); Save(); })));
            GeneralHost.Children.Add(card1);

            var cardSpace = Card("桌面空间", null, out var bs);
            bs.Children.Add(ToggleRow("为顶栏预留桌面空间", S.ReserveDesktopSpace, v => { S.ReserveDesktopSpace = v; DesktopSpaceService.Apply(S); Save(); }));
            bs.Children.Add(Row("预留高度", NumberSlider(38, 100, S.DesktopSpaceHeight, "px", v => { S.DesktopSpaceHeight = (int)Math.Round(v); if (S.ReserveDesktopSpace) DesktopSpaceService.Apply(S); Save(); })));
            GeneralHost.Children.Add(cardSpace);

            var card2 = Card("数据", null, out var b2);
            var openBtn = new Button { Content = "打开数据目录", Style = (Style)FindResource("Btn") };
            openBtn.Click += (s, e) => ShellOpen.OpenPath(S.StorageRoot);
            b2.Children.Add(Row("存储目录", openBtn));
            b2.Children.Add(MakeHint("配置文件:" + Path.Combine(App.AppDataDir, "settings.json") +
                                     "\n日志文件:" + Path.Combine(App.AppDataDir, "log.txt")));
            GeneralHost.Children.Add(card2);
        }

        // ---------- 关于 ----------
        private TextBlock _updateStatus;
        private Button _checkButton;

        private void BuildAboutPage()
        {
            AboutHost.Children.Clear();

            var about = Card("关于 YukiDesk",
                "一款常驻 Windows 桌面层的顶栏小工具:小文件夹、音乐播放器、音量、回收站、音频频谱,外观全部可自定义。", out var ab);
            var verTb = new TextBlock
            {
                Text = "v" + UpdateService.CurrentVersion.ToString(3),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            verTb.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            ab.Children.Add(Row("当前版本", verTb));
            var ghBtn = new Button { Content = "打开 GitHub 主页", Style = (Style)FindResource("Btn") };
            ghBtn.Click += (s, e) => NativeMethods.ShellExecuteOpen("https://github.com/Akiyuki1130/YukiDesk");
            ab.Children.Add(Row("项目主页", ghBtn));
            ab.Children.Add(MakeHint("github.com/Akiyuki1130/YukiDesk\n如果 YukiDesk 对你有帮助,欢迎去点一个 Star。"));
            AboutHost.Children.Add(about);

            var upd = Card("软件更新",
                "通过 GitHub Releases 检测与下载更新;系统开了代理时自动走系统代理。", out var ub);
            ub.Children.Add(ToggleRow("自动检查更新", S.AutoCheckUpdate, v => { S.AutoCheckUpdate = v; Save(); }));
            ub.Children.Add(ToggleRow("自动下载更新", S.AutoUpdate, v => { S.AutoUpdate = v; Save(); }));
            _checkButton = new Button { Content = "检查更新", Style = (Style)FindResource("BtnAccent") };
            _checkButton.Click += async (s, e) =>
            {
                if (UpdateService.Busy) return;
                _checkButton.IsEnabled = false;
                try { await UpdateService.RunCheckAsync(SetUpdateStatus, autoDownload: S.AutoUpdate); }
                finally { _checkButton.IsEnabled = true; }
            };
            ub.Children.Add(Row("手动检查更新", _checkButton));
            _updateStatus = new TextBlock
            {
                Text = "",
                FontSize = 11.5,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            _updateStatus.SetResourceReference(TextElement.ForegroundProperty, "SettingsSubTextBrush");
            ub.Children.Add(_updateStatus);
            ub.Children.Add(MakeHint("「自动下载更新」需要开启「自动检查更新」;下载完成后会先询问,确认后才安装。" +
                                     "安装新版本时会退出 YukiDesk 并启动安装程序,设置与小文件夹不会丢失。", 14));
            AboutHost.Children.Add(upd);
        }

        private void SetUpdateStatus(string text)
        {
            if (_updateStatus == null) return;
            _updateStatus.Text = text;
        }
    }
}
