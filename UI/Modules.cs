using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Threading;
using FilePath = System.IO.Path;
using DesktopBar.Interop;
using DesktopBar.Models;
using DesktopBar.Services;

namespace DesktopBar.UI
{
    public class SettingsModule : ModuleBase
    {
        public SettingsModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            // 与文件夹图标同级的雪花：六臂双层分叉，24 网格等比缩放。
            Pill.Child = CenterIcon(MakeSnowflakeGlyph(21));
            Pill.MouseLeftButtonUp += (s, e) => App.ShowSettings();
        }
        protected override bool HasPanel => false;
    }

    public class PowerModule : ModuleBase
    {
        public PowerModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.Child = CenterIcon(MakePowerGlyph(20.5));
            Pill.MouseLeftButtonUp += (s, e) => ShowPanel();
        }
        protected override bool HoverOpensPanel => false;

        protected override void OnPanelShown()
        {
            var body = new StackPanel { Width = 190 * Ui.Scale };
            body.Children.Add(MakePanelTitle("电源"));
            AddPowerButton(body, "关机", "将关闭 Windows", NativeMethods.Shutdown, true);
            AddPowerButton(body, "重启", "重新启动 Windows", NativeMethods.Restart, true);
            AddPowerButton(body, "睡眠", "进入睡眠模式", NativeMethods.Sleep, true);
            AddPowerButton(body, "锁定", "锁定当前会话", NativeMethods.Lock, false);
            AddPowerButton(body, "息屏", "关闭显示器，移动鼠标可唤醒", NativeMethods.DisplayOff, false);
            Panel.Child = body;
        }

        private void AddPowerButton(StackPanel body, string title, string sub, Action action, bool confirm)
        {
            var b = new Button { Content = title, Style = (Style)Application.Current.FindResource("Btn"), HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            b.ToolTip = sub;
            b.Click += (s, e) =>
            {
                if (!confirm || ConfirmDialog.Show(title, "确定要" + title + "吗？", title))
                    action();
            };
            body.Children.Add(b);
        }
    }

    public class ClockModule : ModuleBase
    {
        private static readonly string[] WeekNames = { "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六" };
        private readonly TextBlock _pillText, _pTime, _pWeek, _pDate, _pLunar, _pFest;
        private readonly DispatcherTimer _timer;
        public ClockModule(TabStyle st) : base(st)
        {
            _pillText = MakeText(""); Pill.Child = _pillText;
            // 下拉内容整体居中:文字居中对齐,并设最小宽度留出左右呼吸感。
            var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 170 * Ui.Scale };
            _pTime = MakeText("", 12); _pWeek = MakeText(""); _pDate = MakeText(""); _pLunar = MakeText(""); _pFest = MakeText("");
            foreach (var tb in new[] { _pTime, _pWeek, _pDate, _pLunar, _pFest }) tb.HorizontalAlignment = HorizontalAlignment.Center;
            _pTime.Margin = new Thickness(4, 2, 4, 6); _pWeek.Margin = new Thickness(4, 2, 4, 2); _pDate.Margin = new Thickness(4, 2, 4, 2); _pLunar.Margin = new Thickness(4, 2, 4, 2);
            _pFest.Margin = new Thickness(4, 0, 4, 4);
            _pFest.FontSize = Math.Max(9, 12 * Ui.Scale);
            body.Children.Add(_pTime); body.Children.Add(_pWeek); body.Children.Add(_pDate); body.Children.Add(_pLunar); body.Children.Add(_pFest); Panel.Child = body;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) }; _timer.Tick += (s, e) => Refresh(); _timer.Start(); Refresh();
        }
        protected override bool CenterPanelOnPill => true;
        private void Refresh()
        {
            var now = DateTime.Now;
            var parts = new List<string> { now.ToString(App.Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm") };
            if (App.Settings.ShowDate) parts.Add(now.ToString("M月d日"));
            if (App.Settings.ShowWeekday) parts.Add("周" + "日一二三四五六"[(int)now.DayOfWeek]);
            _pillText.Text = string.Join("  ", parts); _pTime.Text = now.ToString("HH:mm:ss"); _pWeek.Text = WeekNames[(int)now.DayOfWeek]; _pDate.Text = now.ToString("yyyy年M月d日"); _pLunar.Text = Lunar.ToLunarString(now);
            string fest = Festivals.GetTodayLabel(now);
            _pFest.Text = fest.Length > 0 ? "今 " + fest : "";
            _pFest.Visibility = fest.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        public override void Dispose() { _timer.Stop(); base.Dispose(); }
    }

    public class MusicModule : ModuleBase
    {
        private readonly Image _smallImg, _bigImg;
        private readonly MarqueeText _pillMarquee, _panelMarquee;
        private readonly TextBlock _tArtist, _tCur, _tTotal, _playGlyph, _placeholderGlyph;
        private readonly ProgressBar _progress;
        private readonly Border _progressHost;
        private readonly Grid _timeRow;
        private readonly Ellipse _seekThumb;
        private Action<TimeSpan> _seek;
        private string _mediaKey = "";
        private long _lastMediaGeneration;
        private string _lastTitle = "暂无音乐";
        public MusicModule(TabStyle st) : base(st)
        {
            var coverHost = new Grid { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale };
            _placeholderGlyph = Glyph("♫", 14);
            _placeholderGlyph.Width = 26 * Ui.Scale;
            _placeholderGlyph.Height = 26 * Ui.Scale;
            _placeholderGlyph.HorizontalAlignment = HorizontalAlignment.Center;
            _placeholderGlyph.VerticalAlignment = VerticalAlignment.Center;
            coverHost.Children.Add(_placeholderGlyph);
            _smallImg = new Image { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale, Stretch = Stretch.UniformToFill }; coverHost.Children.Add(_smallImg);
            var coverBorder = new Border { Width = 26 * Ui.Scale, Height = 26 * Ui.Scale, CornerRadius = new CornerRadius(7 * Ui.Scale), Child = coverHost, VerticalAlignment = VerticalAlignment.Center }; coverBorder.Clip = new RectangleGeometry(new Rect(0, 0, 26 * Ui.Scale, 26 * Ui.Scale), 7 * Ui.Scale, 7 * Ui.Scale);
            // 歌名超宽时在药丸内滚动；未超宽时药丸收缩到内容宽度。
            _pillMarquee = new MarqueeText(MakeText("暂无音乐"), 150 * Ui.Scale, 20 * Ui.Scale, hug: true);
            _pillMarquee.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            var pillSp = new StackPanel { Orientation = Orientation.Horizontal }; pillSp.Children.Add(coverBorder); pillSp.Children.Add(_pillMarquee); Pill.Child = pillSp;

            _bigImg = new Image { Width = 96 * Ui.Scale, Height = 96 * Ui.Scale, Stretch = Stretch.UniformToFill };
            var bigCover = new Border { Width = 96 * Ui.Scale, Height = 96 * Ui.Scale, CornerRadius = new CornerRadius(14 * Ui.Scale), Child = _bigImg, HorizontalAlignment = HorizontalAlignment.Center, Background = Ui.Brush(ThemeService.Text, 0.06) }; bigCover.Clip = new RectangleGeometry(new Rect(0, 0, 96 * Ui.Scale, 96 * Ui.Scale), 14 * Ui.Scale, 14 * Ui.Scale);
            // 展开页歌名同样滚动；超宽前保持居中。宽度 = 面板 300 - 内边距 24 - 标题左右边距 8。
            _panelMarquee = new MarqueeText(MakeText("暂无音乐", 2), 268 * Ui.Scale, 24 * Ui.Scale, hug: false);
            _pillMarquee.SetText("暂无音乐");
            _panelMarquee.SetText("暂无音乐");
            _tArtist = MakeText(""); _tArtist.TextAlignment = TextAlignment.Center; _tCur = MakeText("0:00"); _tTotal = MakeText("0:00");

            _progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6 * Ui.Scale, Style = null, Foreground = Ui.Brush("#6C8CFF"), Background = Ui.Brush(ThemeService.Text, 0.15), VerticalAlignment = VerticalAlignment.Center };
            _seekThumb = new Ellipse { Width = 14 * Ui.Scale, Height = 14 * Ui.Scale, Fill = Ui.Brush("#FFFFFF"), Stroke = Ui.Brush("#6C8CFF"), StrokeThickness = 2 * Ui.Scale, Visibility = Visibility.Collapsed };
            var trackGrid = new Grid(); trackGrid.Children.Add(_progress); trackGrid.Children.Add(_seekThumb);
            _progressHost = new Border { Height = 16 * Ui.Scale, Margin = new Thickness(8 * Ui.Scale, 0, 8 * Ui.Scale, 0), Child = trackGrid, CornerRadius = new CornerRadius(3 * Ui.Scale), Clip = new RectangleGeometry(new Rect(0, 0, 1000, 16 * Ui.Scale), 3 * Ui.Scale, 3 * Ui.Scale) };
            _progressHost.MouseLeftButtonUp += (s, e) => { if (_seek == null || _progress.Maximum <= 1) return; var pt = e.GetPosition(_progressHost); _seek(TimeSpan.FromSeconds(Math.Max(0, Math.Min(_progress.Maximum, pt.X / Math.Max(1, _progressHost.ActualWidth) * _progress.Maximum)))); };
            _timeRow = new Grid { Margin = new Thickness(0, 14 * Ui.Scale, 0, 0) }; _timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); _timeRow.ColumnDefinitions.Add(new ColumnDefinition()); _timeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(_tCur, 0); Grid.SetColumn(_progressHost, 1); Grid.SetColumn(_tTotal, 2); _timeRow.Children.Add(_tCur); _timeRow.Children.Add(_progressHost); _timeRow.Children.Add(_tTotal);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14 * Ui.Scale, 0, 0) }; btnRow.Children.Add(ShellButton(Glyph("⏮", 19), () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PREV))); _playGlyph = Glyph("▶", 19); btnRow.Children.Add(ShellButton(_playGlyph, () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_PLAY_PAUSE))); btnRow.Children.Add(ShellButton(Glyph("⏭", 19), () => NativeMethods.SendMediaKey(NativeMethods.VK_MEDIA_NEXT)));
            var body = new StackPanel { Width = 300 * Ui.Scale }; body.Children.Add(bigCover); _panelMarquee.Margin = new Thickness(4 * Ui.Scale, 12 * Ui.Scale, 4 * Ui.Scale, 0); body.Children.Add(_panelMarquee); _tArtist.Margin = new Thickness(4 * Ui.Scale, 4 * Ui.Scale, 4 * Ui.Scale, 0); body.Children.Add(_tArtist); body.Children.Add(_timeRow); body.Children.Add(btnRow); Panel.Child = body;
        }
        public void SetSeek(Action<TimeSpan> seek) { _seek = seek; }

        public void Update(MediaInfo info)
        {
            if (info == null || info.Generation < _lastMediaGeneration) return;
            _lastMediaGeneration = info.Generation;

            string key = (info.TrackKey ?? "") + "|" + (info.ThumbnailKey ?? "");
            if (!string.Equals(key, _mediaKey, StringComparison.Ordinal))
            {
                _mediaKey = key;
                _smallImg.Source = null;
                _bigImg.Source = null;
            }

            string title = string.IsNullOrEmpty(info.Title) ? "暂无音乐" : info.Title;
            if (!string.Equals(title, _lastTitle, StringComparison.Ordinal))
            {
                _lastTitle = title;
                _pillMarquee.SetText(title);
                _panelMarquee.SetText(title);
            }

            _tArtist.Text = info.Artist ?? "";
            _smallImg.Source = info.Cover;
            _bigImg.Source = info.Cover;
            _progress.Maximum = Math.Max(1, info.Duration.TotalSeconds); _progress.Value = Math.Min(Math.Max(0, info.Position.TotalSeconds), _progress.Maximum); bool hasTimeline = info.Duration > TimeSpan.Zero; bool canSeek = hasTimeline && info.SeekSupported; _timeRow.Visibility = hasTimeline ? Visibility.Visible : Visibility.Collapsed; _progressHost.Visibility = canSeek ? Visibility.Visible : Visibility.Collapsed; _seekThumb.Visibility = canSeek ? Visibility.Visible : Visibility.Collapsed;
            _tCur.Text = Fmt(info.Position); _tTotal.Text = Fmt(info.Duration); _playGlyph.Text = info.Playing ? "⏸" : "▶"; NudgeGlyph(_playGlyph, info.Playing ? "⏸" : "▶");
        }
        private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        // Segoe UI Symbol 媒体符号的行盒下留白偏大，墨迹在圆形按键里整体偏下；
        // 按实测（48px 按钮内 19px 字形）逐字形向上做光学补偿。
        private static readonly Dictionary<string, double> GlyphNudge = new Dictionary<string, double>
        {
            ["⏮"] = -3.0,
            ["⏭"] = -2.5,
            ["⏸"] = -2.5,
            ["▶"] = -1.5
        };

        private TextBlock Glyph(string g, double size = 19)
        {
            var t = MakeIconGlyph(g, size, "Segoe UI Symbol");
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            NudgeGlyph(t, g);
            return t;
        }

        private void NudgeGlyph(TextBlock t, string g)
        {
            if (GlyphNudge.TryGetValue(g, out double dy))
                t.RenderTransform = new TranslateTransform(0, dy * Ui.Scale);
            else
                t.RenderTransform = null;
        }
        private Border ShellButton(FrameworkElement content, Action click) { var b = new Border { Width = 48 * Ui.Scale, Height = 48 * Ui.Scale, CornerRadius = new CornerRadius(24 * Ui.Scale), Background = Ui.Brush(ThemeService.Text, 0.08), Cursor = Cursors.Hand, Child = content, Margin = new Thickness(5 * Ui.Scale, 0, 5 * Ui.Scale, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; b.MouseEnter += (s, e) => b.Background = Ui.Brush(ThemeService.Text, 0.18); b.MouseLeave += (s, e) => b.Background = Ui.Brush(ThemeService.Text, 0.08); b.MouseLeftButtonUp += (s, e) => click(); return b; }
    }

    public class VolumeModule : ModuleBase
    {
        private readonly VolumeService _volume = new VolumeService();
        private readonly FrameworkElement _summary;
        private readonly System.Windows.Shapes.Path _speakerWave1, _speakerWave2;
        private readonly TextBlock _value;
        private readonly TextBlock _deviceCaption;
        private readonly Slider _slider;
        private readonly ComboBox _devices;
        private DispatcherTimer _poll;
        private bool _syncing;
        private bool _syncingDevices;
        private bool _muted;
        private bool _volumeAvailable;
        private bool _volumeWritesEnabled;
        private bool _disposed;
        private bool _deviceRefreshInFlight;
        private int _deviceRefreshVersion;
        private string _deviceListSignature = "";
        public VolumeModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.MinWidth = Pill.MaxWidth = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.HorizontalAlignment = HorizontalAlignment.Left;
            _summary = CenterIcon(MakeSpeakerGlyph(out _speakerWave1, out _speakerWave2, 23));
            Pill.Child = _summary;
            // 读取启动值只用于显示；初始化期间禁止任何回写系统音量。
            _syncing = true;
            float? initialVolumeRead = _volume.GetVolume();
            bool initialVolumeAvailable = initialVolumeRead.HasValue;
            _volumeAvailable = initialVolumeAvailable;
            float initialVolume = initialVolumeRead ?? 0;
            _muted = _volume.GetMute() ?? false;
            _syncing = false;
            _devices = new ComboBox
            {
                Width = 230 * Ui.Scale,
                Height = 34 * Ui.Scale,
                Style = (Style)Application.Current.FindResource("DarkCombo"),
                Margin = new Thickness(0, 2 * Ui.Scale, 0, 4 * Ui.Scale)
            };
            _deviceCaption = new TextBlock
            {
                Foreground = Ui.Brush(ThemeService.Text, 0.65),
                FontSize = 11 * Ui.Scale,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4 * Ui.Scale, 8 * Ui.Scale, 4 * Ui.Scale, 0)
            };
            var body = new StackPanel { Width = 230 * Ui.Scale };
            body.Children.Add(MakePanelTitle("音量"));
            body.Children.Add(_slider = new Slider
            {
                Minimum = 0,
                Maximum = 1,
                Value = initialVolume,
                Style = (Style)Application.Current.FindResource("SliderDark")
            });
            body.Children.Add(_value = MakeText(initialVolumeAvailable ? Math.Round(initialVolume * 100) + "%" : "读取失败"));
            _slider.IsEnabled = initialVolumeAvailable;
            _value.HorizontalAlignment = HorizontalAlignment.Center;
            body.Children.Add(_deviceCaption);
            body.Children.Add(_devices);
            Panel.Child = body;
            RefreshDevicesAsync();
            _devices.SelectionChanged += (s, e) =>
            {
                if (_syncingDevices || !(_devices.SelectedItem is AudioDeviceInfo d)) return;
                if (_volume.SelectDevice(d.Id, d.Name))
                {
                    _deviceCaption.Text = _volume.DeviceName;
                    SyncFromDevice();
                }
            };
            _slider.ValueChanged += (s, e) =>
            {
                _value.Text = Math.Round(e.NewValue * 100) + "%";
                UpdateIcon(e.NewValue, _muted);
                if (!_syncing && _volumeAvailable && _volumeWritesEnabled && IsLoaded)
                    _volume.SetVolume((float)e.NewValue);
            };
            Pill.MouseLeftButtonUp += (s, e) =>
            {
                _volumeWritesEnabled = true;
                _muted = !_muted;
                if (_volumeAvailable && _volumeWritesEnabled && IsLoaded)
                    _volume.SetMute(_muted);
                UpdateIcon(_slider.Value, _muted);
            };
            UpdateIcon(initialVolume, _muted);
            // 启动阶段只读系统音量；必须先发生明确的用户操作才允许写回。
            Unloaded += (s, e) => _volumeWritesEnabled = false;
            _slider.PreviewMouseLeftButtonDown += (s, e) => _volumeWritesEnabled = true;
            _slider.PreviewKeyDown += (s, e) => _volumeWritesEnabled = true;
            _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _poll.Tick += (s, e) =>
            {
                float? current = _volume.GetVolume();
                bool? muted = _volume.GetMute();
                if (current.HasValue)
                {
                    _volumeAvailable = true;
                    _slider.IsEnabled = true;
                    UpdateIcon(current.Value, muted ?? _muted);
                    SyncControls(current.Value);
                }
                else
                {
                    _volumeAvailable = false;
                    _slider.IsEnabled = false;
                    _value.Text = "读取失败";
                }
                if (muted.HasValue) _muted = muted.Value;
            };
            Pill.MouseWheel += (s, e) => { _volumeWritesEnabled = true; Adjust(e.Delta); };
            Panel.MouseWheel += (s, e) => { _volumeWritesEnabled = true; Adjust(e.Delta); };
        }

        protected override void OnPanelShown()
        {
            RefreshDevicesAsync();
            SyncFromDevice();
            _poll.Start();
        }

        protected override void OnPanelHidden() => _poll.Stop();

        private async void RefreshDevicesAsync()
        {
            if (_devices == null || _deviceRefreshInFlight) return;
            _deviceRefreshInFlight = true;
            int version = ++_deviceRefreshVersion;
            try
            {
                var list = await _volume.RefreshDevicesAsync();
                if (_disposed || version != _deviceRefreshVersion) return;

                string signature = string.Join("\n", list.Select(x => x.Id + "|" + x.Name + "|" + x.IsDefault));
                bool changed = !string.Equals(signature, _deviceListSignature, StringComparison.Ordinal);
                var selected = list.Find(x => string.Equals(x.Id, _volume.CurrentDeviceId, StringComparison.OrdinalIgnoreCase))
                               ?? list.Find(x => x.IsDefault)
                               ?? list.FirstOrDefault();
                if (changed)
                {
                    _syncingDevices = true;
                    _devices.ItemsSource = list;
                    if (selected != null) _devices.SelectedItem = selected;
                    _deviceListSignature = signature;
                    _syncingDevices = false;
                }
                else if (selected != null && !ReferenceEquals(_devices.SelectedItem, selected)
                         && !string.Equals((_devices.SelectedItem as AudioDeviceInfo)?.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _syncingDevices = true;
                    _devices.SelectedItem = selected;
                    _syncingDevices = false;
                }
                _deviceCaption.Text = _volume.DeviceName;
            }
            catch (Exception ex)
            {
                App.Log("刷新播放设备列表失败: " + ex.Message);
            }
            finally
            {
                if (version == _deviceRefreshVersion)
                    _deviceRefreshInFlight = false;
            }
        }

        private void SyncControls(float value)
        {
            _syncing = true;
            _slider.Value = value;
            _value.Text = Math.Round(value * 100) + "%";
            _syncing = false;
        }

        private void SyncFromDevice()
        {
            float? value = _volume.GetVolume();
            bool? muted = _volume.GetMute();
            if (muted.HasValue) _muted = muted.Value;
            if (value.HasValue)
            {
                _volumeAvailable = true;
                _slider.IsEnabled = true;
                SyncControls(value.Value);
                UpdateIcon(value.Value, _muted);
            }
            else
            {
                _volumeAvailable = false;
                _slider.IsEnabled = false;
                _value.Text = "读取失败";
            }
        }
        private void UpdateIcon(double volume, bool muted)
        {
            _speakerWave1.Opacity = muted || volume < 0.01 ? 0 : 1;
            _speakerWave2.Opacity = muted || volume < 0.34 ? 0 : 1;
        }
        private void Adjust(int delta) { _slider.Value = Math.Max(0, Math.Min(1, _slider.Value + (delta > 0 ? 0.04 : -0.04))); }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _deviceRefreshVersion++;
            _poll.Stop();
            _volume.Dispose();
            base.Dispose();
        }
    }

    public class FolderModule : ModuleBase
    {
        private readonly string _name; private readonly WrapPanel _wrap = new WrapPanel(); private readonly ScrollViewer _scroll; private FileSystemWatcher _watcher; private readonly DispatcherTimer _debounce;
        private bool _disposed;
        private bool _filesDirty = true;
        private int _fileLoadVersion;
        private readonly List<DispatcherTimer> _itemOpenTimers = new List<DispatcherTimer>();
        private string Dir
        {
            get
            {
                if (!IsSafeLeafName(_name)) return string.Empty;
                try
                {
                    string root = FilePath.GetFullPath(App.Settings.StorageRoot);
                    string candidate = FilePath.GetFullPath(FilePath.Combine(root, _name));
                    string relative = FilePath.GetRelativePath(root, candidate);
                    if (relative == "." || FilePath.IsPathRooted(relative)
                        || relative.StartsWith(".." + FilePath.DirectorySeparatorChar, StringComparison.Ordinal)
                        || relative.StartsWith(".." + FilePath.AltDirectorySeparatorChar, StringComparison.Ordinal)) return string.Empty;
                    return string.Equals(relative, _name, StringComparison.OrdinalIgnoreCase) ? candidate : string.Empty;
                }
                catch { return string.Empty; }
            }
        }

        private static bool IsSafeLeafName(string name) => SettingsService.IsValidFolderName(name);
        public FolderModule(string name, TabStyle st) : base(st)
        {
            _name = name;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(MakeFolderGlyph(18));
            var tb = MakeText(name);
            tb.Margin = new Thickness(8 * Ui.Scale, 0, 0, 0);
            sp.Children.Add(tb);
            Pill.Child = sp;
            _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5 }; _scroll.Content = _wrap; Panel.Child = _scroll;
            try { if (!string.IsNullOrEmpty(Dir)) Directory.CreateDirectory(Dir); } catch (Exception ex) { App.Log("创建小文件夹失败: " + ex.Message); }
            // 空白处右键粘贴（项目上的右键会被项目自身处理并标记 Handled）。
            Panel.MouseRightButtonUp += (s, e) => { ShowBlankMenu(); e.Handled = true; };
            // 双击胶囊：在资源管理器中打开该文件夹。
            Pill.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { ShellOpen.OpenPath(Dir); e.Handled = true; } };
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                if (_disposed) return;
                _filesDirty = true;
                if (Open) LoadFiles();
            };
            SetupWatcher(); EnableDragDrop();
        }
        private void SetupWatcher()
        {
            try
            {
                    _watcher = new FileSystemWatcher(Dir) { EnableRaisingEvents = true };
                _watcher.Created += (s, e) => RestartDebounce();
                _watcher.Changed += (s, e) => RestartDebounce();
                _watcher.Deleted += (s, e) => RestartDebounce();
                _watcher.Renamed += (s, e) => RestartDebounce();
                _watcher.Error += (s, e) => RestartDebounce();
            }
            catch (Exception ex) { App.Log("小文件夹监听失败: " + ex.Message); }
        }
        private void RestartDebounce()
        {
            if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    _debounce.Stop();
                    _debounce.Start();
                }));
            }
            catch (InvalidOperationException) { }
        }
        private void ShowBlankMenu()
        {
            // 剪贴板没有文件时“粘贴”置灰不可点。
            bool has = FileClipboard.HasPasteableFiles && !string.IsNullOrEmpty(Dir);
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("粘贴", () => { if (!string.IsNullOrEmpty(Dir)) FileClipboard.PasteInto(Dir); }, enabled: has)
            }));
        }

        protected override void OnPanelShown()
        {
            if (_filesDirty) LoadFiles();
        }

        private async void LoadFiles()
        {
            int version = ++_fileLoadVersion;
            string directory = Dir;
            try
            {
                var items = await Task.Run(() => SnapshotFiles(directory));
                if (_disposed || !Open || version != _fileLoadVersion)
                {
                    _filesDirty = true;
                    return;
                }

                _wrap.Children.Clear();
                int cols = Math.Min(4, Math.Max(1, items.Count));
                // 最多四列；为四列预留完整 viewport，细滚动条不会把第四列挤到下一行。
                double cell = 76 * Ui.Scale;
                _wrap.Width = cols * cell;
                Panel.Width = (cols * 76 + 26) * Ui.Scale;

                const int batchSize = 8;
                for (int index = 0; index < items.Count; index += batchSize)
                {
                    if (_disposed || !Open || version != _fileLoadVersion)
                    {
                        _filesDirty = true;
                        return;
                    }
                    int end = Math.Min(items.Count, index + batchSize);
                    for (int i = index; i < end; i++)
                        _wrap.Children.Add(MakeItem(items[i]));

                    if (end < items.Count)
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
                _filesDirty = false;
            }
            catch (InvalidOperationException) { _filesDirty = true; }
        }

        private static List<FileSystemInfo> SnapshotFiles(string directory)
        {
            try
            {
                if (!Directory.Exists(directory)) return new List<FileSystemInfo>();
                return new DirectoryInfo(directory)
                    .GetFileSystemInfos()
                    .OrderBy(item => item is DirectoryInfo ? 0 : 1)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<FileSystemInfo>();
            }
        }
        private FrameworkElement MakeItem(FileSystemInfo f)
        {
            bool isDir = f is DirectoryInfo;
            double scale = Ui.Scale;
            var img = new Image
            {
                Width = 40 * scale,
                Height = 40 * scale,
                Source = IconCache.GetFallback(isDir, f.Extension),
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            IconCache.LoadAsync(f.FullName, isDir, f.Extension, icon =>
            {
                // 缓存命中时会在 img 加入可视化树之前同步回调，此时 IsVisible 必为 false，
                // 不能用它做守卫，否则真实图标会被静默丢弃、条目永远停在占位图标上。
                if (!_disposed) img.Source = icon;
            });
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);

            string displayName =
                (string.Equals(f.Extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(f.Extension, ".url", StringComparison.OrdinalIgnoreCase))
                    ? f.Name.Substring(0, f.Name.Length - f.Extension.Length)
                    : f.Name;
            var name = new TextBlock
            {
                Text = displayName,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 11 * scale,
                Foreground = Ui.Brush(ThemeService.Text, 0.9),
                MaxHeight = 28 * scale,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0)
            };
            var sp = new StackPanel
            {
                Width = 68 * scale,
                Margin = new Thickness(3 * scale, 8 * scale, 3 * scale, 8 * scale)
            };
            sp.Children.Add(img);
            sp.Children.Add(name);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8 * scale),
                Background = Brushes.Transparent,
                Child = sp,
                Cursor = Cursors.Hand,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            var transform = (ScaleTransform)border.RenderTransform;
            bool suppressClickAfterDrag = false;
            // 拖出：按住左键移动超过系统阈值后发起 OLE 拖拽（Move 语义），
            // 落点接受 Move 后由这里删除源（若目标是我们自己的其他面板则已被移动，删除自然失败可忽略）。
            Point? dragStart = null;
            bool dragTaken = false;
            void AnimateBack()
            {
                transform.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 }
                    });
                transform.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(150))
                    {
                        EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 }
                    });
            }
            border.MouseEnter += (s, e) => border.Background = Ui.Brush(ThemeService.Text, 0.10);
            border.MouseLeave += (s, e) =>
            {
                border.Background = Brushes.Transparent;
                AnimateBack();
            };
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(border);
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = 0.93;
                transform.ScaleY = 0.93;
                border.Background = Ui.Brush(ThemeService.Text, 0.18);
            };
            border.PreviewMouseMove += (s, e) =>
            {
                if (dragStart == null || dragTaken || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(border);
                if (Math.Abs(pos.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(pos.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragTaken = true;
                suppressClickAfterDrag = true;
                try
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { f.FullName });
                    data.SetData("Preferred DropEffect", FileClipboard.EffectStream(FileClipboard.MoveEffect));
                    var effect = DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                    if ((effect & DragDropEffects.Move) != 0)
                    {
                        // 仅在源已被目标实际移走后刷新；不按旧路径盲删，避免误删新建同名文件。
                        bool sourceStillExists = isDir ? Directory.Exists(f.FullName) : File.Exists(f.FullName);
                        if (!sourceStillExists) LoadFiles();
                    }
                }
                catch (Exception ex) { App.Log("拖出移动失败: " + ex.Message); }
                finally
                {
                    dragStart = null;
                    dragTaken = false;
                    AnimateBack();
                    border.Background = Brushes.Transparent;
                }
            };
            DispatcherTimer openTimer = null;
            border.MouseLeftButtonUp += (s, e) =>
            {
                AnimateBack();
                border.Background = Ui.Brush(ThemeService.Text, 0.10);
                if (suppressClickAfterDrag)
                {
                    suppressClickAfterDrag = false;
                    e.Handled = true;
                    return;
                }
                if (e.ClickCount >= 2)
                {
                    if (openTimer != null) _itemOpenTimers.Remove(openTimer);
                    openTimer?.Stop();
                    openTimer = null;
                    ShellOpen.OpenPath(f.FullName);
                }
                else
                {
                    openTimer?.Stop();
                    openTimer = new DispatcherTimer { Interval = SystemParameters.MouseHoverTime };
                    _itemOpenTimers.Add(openTimer);
                    openTimer.Tick += (ts, te) =>
                    {
                        openTimer.Stop();
                        _itemOpenTimers.Remove(openTimer);
                        openTimer = null;
                        if (!_disposed) ShellOpen.OpenPath(f.FullName);
                    };
                    openTimer.Start();
                }
                e.Handled = true;
            };
            border.MouseRightButtonUp += (s, e) => { ShowItemMenu(f); e.Handled = true; };
            return border;
        }
        private void ShowItemMenu(FileSystemInfo f)
        {
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("打开", () => ShellOpen.OpenPath(f.FullName)),
                new FlyoutItem("复制", () => FileClipboard.CopyFiles(new[] { f.FullName })),
                new FlyoutItem("剪切", () => FileClipboard.CutFiles(new[] { f.FullName })),
                new FlyoutItem("重命名", () => RenameItem(f)),
                new FlyoutItem("删除", () => DeleteItem(f), danger: true)
            }));
        }
        private void RenameItem(FileSystemInfo f)
        {
            string name = Prompt.Show("重命名", "新的名称:", f.Name);
            if (string.IsNullOrWhiteSpace(name) || name == f.Name) return;
            string extension = f.Extension;
            if ((string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
                && !name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                name += extension;
            if (!IsSafeLeafName(name))
            {
                ConfirmDialog.Notice("重命名失败", "名称不能包含路径分隔符或 ..。");
                return;
            }
            try
            {
                string dest = FilePath.Combine(FilePath.GetDirectoryName(f.FullName) ?? "", name);
                if (File.Exists(dest) || Directory.Exists(dest)) { ConfirmDialog.Notice("重命名失败", "同名项目已存在。"); return; }
                if (f is DirectoryInfo dir) dir.MoveTo(dest);
                else ((FileInfo)f).MoveTo(dest);
                LoadFiles();
            }
            catch (Exception ex) { ConfirmDialog.Notice("重命名失败", ex.Message); }
        }
        private void DeleteItem(FileSystemInfo f)
        {
            if (!ConfirmDialog.Show("删除", "确定要把“" + f.Name + "”移到回收站吗？", "删除")) return;
            if (NativeMethods.DeleteToRecycleBin(new[] { f.FullName })) LoadFiles();
            else ConfirmDialog.Notice("删除失败", "无法把“" + f.Name + "”移到回收站。");
        }
        private void EnableDragDrop()
        {
            Pill.AllowDrop = true;
            PanelWindow.AllowDrop = true;
            foreach (var el in new UIElement[] { Pill, PanelWindow })
            {
                el.DragOver += (s, e) =>
                {
                    if (!e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.None;
                    else e.Effects = (e.AllowedEffects & DragDropEffects.Move) != 0 ? DragDropEffects.Move : DragDropEffects.Copy;
                    e.Handled = true;
                };
                el.Drop += (s, e) =>
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                        MoveInto((string[])e.Data.GetData(DataFormats.FileDrop));
                    e.Handled = true;
                };
            }
        }
        // 拖入采用移动语义：源文件从原位置移走；与目标重名时弹窗选择替换或保留为副本。
        private void MoveInto(string[] files)
        {
            if (string.IsNullOrEmpty(Dir)) return;
            try
            {
                Directory.CreateDirectory(Dir);
                string destRoot = FilePath.GetFullPath(Dir).TrimEnd('\\', '/');
                ConflictChoice? choice = null;
                bool applyToAll = false;
                foreach (var f in files ?? Array.Empty<string>())
                {
                    if (string.IsNullOrEmpty(f)) continue;
                    string full = FilePath.GetFullPath(f).TrimEnd('\\', '/');
                    // 跳过：源就是本目录、或把文件夹拖进它自己的子目录。
                    if (string.Equals(full, destRoot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (destRoot.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    string parent = FilePath.GetDirectoryName(full);
                    if (string.Equals(parent?.TrimEnd('\\', '/'), destRoot, StringComparison.OrdinalIgnoreCase)) continue;

                    string dest = FilePath.Combine(destRoot, FilePath.GetFileName(full));
                    if (File.Exists(dest) || Directory.Exists(dest))
                    {
                        if (!applyToAll)
                        {
                            choice = ConfirmDialog.ShowConflict("存在同名项目",
                                "“" + FilePath.GetFileName(dest) + "”已在此文件夹中。要替换它，还是把拖入的项保留为副本？",
                                out applyToAll);
                            if (choice == null) break; // 取消：中止剩余项
                        }
                        if (choice == ConflictChoice.Replace)
                        {
                            // 旧项目先移入回收站（可撤销），腾出原名后按原名移动；失败则跳过该项避免误留副本。
                            if (!NativeMethods.DeleteToRecycleBin(new[] { dest }))
                            {
                                App.Log("替换失败，旧项目无法移入回收站: " + dest);
                                continue;
                            }
                        }
                    }
                    FileClipboard.TryPlace(f, Dir, move: true);
                }
                LoadFiles();
            }
            catch (Exception ex) { App.Log("拖入失败: " + ex.Message); }
        }
        public override void Dispose()
        {
            _disposed = true;
            _fileLoadVersion++;
            _debounce.Stop();
            foreach (var timer in _itemOpenTimers) timer.Stop();
            _itemOpenTimers.Clear();
            try
            {
                if (_watcher != null) _watcher.EnableRaisingEvents = false;
                _watcher?.Dispose();
            }
            catch (Exception ex) { App.Log("关闭小文件夹监听失败: " + ex.Message); }
            base.Dispose();
        }
    }

    /// <summary>回收站模块：点击展开（悬停不展开），单击项目还原到原位置，右键胶囊清空回收站。</summary>
    public class RecycleBinModule : ModuleBase
    {
        private readonly WrapPanel _wrap = new WrapPanel();
        private readonly ScrollViewer _scroll;
        private readonly TextBlock _emptyHint;
        private bool _disposed;
        private int _loadVersion;

        public RecycleBinModule(TabStyle st) : base(st)
        {
            Pill.Width = 38 * Ui.Scale;
            Pill.Padding = new Thickness(0);
            Pill.ToolTip = "回收站 — 单击展开，双击在资源管理器打开，右键清空";
            Pill.Child = CenterIcon(MakeTrashGlyph(20.5));
            Pill.MouseLeftButtonUp += (s, e) => ShowPanel();
            Pill.MouseRightButtonUp += (s, e) => { ShowClearMenu(); e.Handled = true; };
            // 双击胶囊:直接打开回收站视图(SHOpenFolderAndSelectItems 会打开"父文件夹"即桌面,
            // 对虚拟根文件夹必须用 shell 命名空间路径导航进去;CLSID 是固定常量)。
            Pill.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount != 2) return;
                e.Handled = true;
                NativeMethods.ShellExecuteOpen("shell:::{645FF040-5081-101B-9F08-00AA002F954E}");
            };

            _emptyHint = new TextBlock
            {
                Text = "回收站是空的",
                FontSize = 12 * Ui.Scale,
                Foreground = Ui.Brush(ThemeService.Text, 0.65),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(12 * Ui.Scale, 16 * Ui.Scale, 12 * Ui.Scale, 10 * Ui.Scale),
                Visibility = Visibility.Collapsed
            };
            _wrap.HorizontalAlignment = HorizontalAlignment.Center;
            var host = new StackPanel();
            host.Children.Add(_emptyHint);
            host.Children.Add(_wrap);
            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = SystemParameters.PrimaryScreenHeight * 0.5,
                Content = host
            };
            Panel.Child = _scroll;
            EnableDragDrop();
        }

        protected override bool HoverOpensPanel => false;
        protected override void OnPanelShown() => LoadItems();

        // 拖入回收站 = 把文件移入系统回收站（SHFileOperation 带 undo）。
        private void EnableDragDrop()
        {
            Pill.AllowDrop = true; PanelWindow.AllowDrop = true;
            foreach (var el in new UIElement[] { Pill, PanelWindow })
            {
                el.DragOver += (s, e) =>
                {
                    e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Move : DragDropEffects.None;
                    e.Handled = true;
                };
                el.Drop += (s, e) =>
                {
                    if (e.Data.GetDataPresent(DataFormats.FileDrop))
                    {
                        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                        if (files != null && files.Length > 0 && NativeMethods.DeleteToRecycleBin(files))
                            App.Log("已把 " + files.Length + " 项移入回收站");
                        LoadItems();
                    }
                };
            }
        }

        private async void LoadItems()
        {
            int version = ++_loadVersion;
            try
            {
                var items = await Task.Run(RecycleBin.Enumerate);
                if (_disposed || !Open || version != _loadVersion) return;

                _wrap.Children.Clear();
                bool empty = items.Count == 0;
                _emptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                _wrap.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                int cols = Math.Min(4, Math.Max(1, items.Count));
                double cell = 76 * Ui.Scale;
                _wrap.Width = cols * cell;
                Panel.Width = empty ? 190 * Ui.Scale : (cols * 76 + 26) * Ui.Scale;

                const int batchSize = 8;
                for (int index = 0; index < items.Count; index += batchSize)
                {
                    if (_disposed || !Open || version != _loadVersion) return;
                    int end = Math.Min(items.Count, index + batchSize);
                    for (int i = index; i < end; i++) _wrap.Children.Add(MakeRecycleItem(items[i]));
                    if (end < items.Count)
                        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
                }
            }
            catch (InvalidOperationException) { }
        }

        private FrameworkElement MakeRecycleItem(RecycleItem item)
        {
            double scale = Ui.Scale;
            string ext = FilePath.GetExtension(item.DataPath);
            var img = new Image
            {
                Width = 40 * scale,
                Height = 40 * scale,
                Source = IconCache.GetFallback(item.IsDirectory, ext),
                HorizontalAlignment = HorizontalAlignment.Center,
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            IconCache.LoadAsync(item.DataPath, item.IsDirectory, ext, icon =>
            {
                if (!_disposed) img.Source = icon;
            });
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var name = new TextBlock
            {
                Text = item.Name,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 11 * scale,
                Foreground = Ui.Brush(ThemeService.Text, 0.9),
                MaxHeight = 28 * scale,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 4 * scale, 0, 0)
            };
            var sp = new StackPanel { Width = 68 * scale, Margin = new Thickness(3 * scale, 8 * scale, 3 * scale, 8 * scale) };
            sp.Children.Add(img);
            sp.Children.Add(name);
            var border = new Border
            {
                CornerRadius = new CornerRadius(8 * scale),
                Background = Brushes.Transparent,
                Child = sp,
                Cursor = Cursors.Hand,
                ToolTip = item.OriginalPath + "\n删除于 " + item.DeletedAt.ToString("yyyy-MM-dd HH:mm") + " · 单击还原",
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            var transform = (ScaleTransform)border.RenderTransform;
            // 拖出 = 把内容移出回收站：落点接受 Move 后删除 $R 内容与 $I 元数据。
            Point? dragStart = null;
            bool dragTaken = false;
            border.MouseEnter += (s, e) => border.Background = Ui.Brush(ThemeService.Text, 0.10);
            border.MouseLeave += (s, e) => { border.Background = Brushes.Transparent; AnimateItemBack(transform); };
            border.PreviewMouseLeftButtonDown += (s, e) =>
            {
                dragStart = e.GetPosition(border);
                transform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleX = 0.93;
                transform.ScaleY = 0.93;
                border.Background = Ui.Brush(ThemeService.Text, 0.18);
            };
            border.PreviewMouseMove += (s, e) =>
            {
                if (dragStart == null || dragTaken || e.LeftButton != MouseButtonState.Pressed) return;
                var pos = e.GetPosition(border);
                if (Math.Abs(pos.X - dragStart.Value.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(pos.Y - dragStart.Value.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                dragTaken = true;
                // 文件拖出用"原文件名的硬链接"作为拖拽源，落点文件名才是原始名字
                // （$R 文件名是随机的）；同目录硬链接同卷瞬时创建，结束后清理。
                string dragPath = item.DataPath;
                string tempLink = null;
                if (!item.IsDirectory)
                {
                    try
                    {
                        string linkDir = FilePath.GetDirectoryName(item.DataPath) ?? "";
                        tempLink = FileClipboard.UniqueDest(item.Name, linkDir);
                        NativeMethods.CreateHardLink(tempLink, item.DataPath);
                        dragPath = tempLink;
                    }
                    catch
                    {
                        if (tempLink != null && File.Exists(tempLink)) { try { File.Delete(tempLink); } catch { } }
                        tempLink = null;
                        dragPath = item.DataPath;
                    }
                }
                var data = new DataObject(DataFormats.FileDrop, new[] { dragPath });
                data.SetData("Preferred DropEffect", FileClipboard.EffectStream(FileClipboard.MoveEffect));
                var effect = DragDrop.DoDragDrop(border, data, DragDropEffects.Move);
                try { if (tempLink != null && File.Exists(tempLink)) File.Delete(tempLink); } catch { }
                if ((effect & DragDropEffects.Move) != 0)
                {
                    // 若拖到了我们的其他面板，内容已被移动走；这里只做兜底删除和元数据清理。
                    try
                    {
                        if (item.IsDirectory) Directory.Delete(item.DataPath, true);
                        else File.Delete(item.DataPath);
                    }
                    catch (Exception ex) { App.Log("移出回收站内容失败: " + ex.Message); }
                    try { File.Delete(item.MetaFile); } catch { }
                    LoadItems();
                }
                dragStart = null;
            };
            border.MouseLeftButtonUp += (s, e) =>
            {
                AnimateItemBack(transform);
                border.Background = Ui.Brush(ThemeService.Text, 0.10);
                if (dragTaken) { dragTaken = false; e.Handled = true; return; }
                if (RecycleBin.Restore(item)) LoadItems();
                else ConfirmDialog.Notice("还原失败", "还原“" + item.Name + "”失败，请检查原始位置是否可用。");
                e.Handled = true;
            };
            border.MouseRightButtonUp += (s, e) => { ShowItemMenu(item); e.Handled = true; };
            return border;
        }

        private void ShowItemMenu(RecycleItem item)
        {
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("还原", () =>
                {
                    if (RecycleBin.Restore(item)) LoadItems();
                    else ConfirmDialog.Notice("还原失败", "还原“" + item.Name + "”失败，请检查原始位置是否可用。");
                }),
                new FlyoutItem("复制", () => FileClipboard.CopyFiles(new[] { item.DataPath })),
                new FlyoutItem("永久删除", () =>
                {
                    if (!ConfirmDialog.Show("永久删除", "“" + item.Name + "”将被永久删除且无法恢复。\n确定要继续吗？", "永久删除")) return;
                    try
                    {
                        if (item.IsDirectory) Directory.Delete(item.DataPath, true);
                        else File.Delete(item.DataPath);
                        File.Delete(item.MetaFile);
                    }
                    catch (Exception ex) { App.Log("永久删除失败: " + ex.Message); }
                    LoadItems();
                }, danger: true)
            }));
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _loadVersion++;
            base.Dispose();
        }

        private static void AnimateItemBack(ScaleTransform transform)
        {
            transform.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 } });
            transform.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.08 } });
        }

        // ---------- 右键菜单：清空回收站 ----------

        private void ShowClearMenu()
        {
            // 菜单打开不为数量同步扫描所有磁盘；展开页会在后台刷新准确内容。
            TrackFlyout(FlyoutMenu.Show(new List<FlyoutItem>
            {
                new FlyoutItem("清空回收站", EmptyBin, danger: true)
            }, "回收站"));
        }

        private void EmptyBin()
        {
            if (!ConfirmDialog.Show("清空回收站", "所有项目将被永久删除，此操作不可恢复。\n确定要继续吗？", "清空回收站"))
                return;
            int hr = NativeMethods.EmptyRecycleBin();
            App.Log("清空回收站 hr=0x" + hr.ToString("X8"));
            LoadItems();
        }

    }

    public static class IconCache
    {
        private sealed class IconRequest
        {
            public string Path;
            public bool IsDirectory;
            public int PixelSize;
            public string Key;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BitmapSource> Cache = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, BitmapSource> Fallbacks = new Dictionary<string, BitmapSource>();
        private static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>();
        private static readonly Dictionary<string, List<Action<BitmapSource>>> Callbacks = new Dictionary<string, List<Action<BitmapSource>>>();
        private static readonly BlockingCollection<IconRequest> Queue = new BlockingCollection<IconRequest>(new ConcurrentQueue<IconRequest>());
        private static Thread _worker;

        public static BitmapSource GetFallback(bool isDir, string ext)
        {
            int pixelSize = IconPixelSize();
            string key = (isDir ? "dir:" : "file:") + pixelSize;
            lock (Gate)
            {
                if (Fallbacks.TryGetValue(key, out var icon)) return icon;
                // 占位也用 Windows 系统图标（imageres.dll 的文件夹/通用文件），
                // 保证条目从出现到换上真实图标全程都是 Explorer 同款样式。
                icon = NativeMethods.GetSystemTypeIcon(isDir, pixelSize) ?? TypeIcon(isDir, ext);
                Fallbacks[key] = icon;
                return icon;
            }
        }

        private static int IconPixelSize()
            => Math.Max(48, (int)Math.Ceiling(48 * Ui.Scale * 1.5));

        public static void LoadAsync(string fullPath, bool isDir, string ext, Action<BitmapSource> apply)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || apply == null) return;
            int pixelSize = IconPixelSize();
            string key = (isDir ? "dir:" : "file:") + fullPath.ToLowerInvariant() + "|" + pixelSize;
            BitmapSource cached;
            lock (Gate) Cache.TryGetValue(key, out cached);
            if (cached != null)
            {
                apply(cached);
                return;
            }

            lock (Gate)
            {
                if (!Callbacks.TryGetValue(key, out var callbacks))
                    Callbacks[key] = callbacks = new List<Action<BitmapSource>>();
                callbacks.Add(apply);
            }
            if (!Pending.TryAdd(key, 0)) return;
            EnsureWorker();
            Queue.Add(new IconRequest { Path = fullPath, IsDirectory = isDir, PixelSize = pixelSize, Key = key });
        }

        private static void EnsureWorker()
        {
            lock (Gate)
            {
                if (_worker != null) return;
                _worker = new Thread(ProcessQueue)
                {
                    IsBackground = true,
                    Name = "ShellIconLoader"
                };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
        }

        private static void ProcessQueue()
        {
            foreach (var request in Queue.GetConsumingEnumerable())
            {
                BitmapSource icon = null;
                try
                {
                    icon = NativeMethods.GetFileIcon(request.Path, request.IsDirectory, request.PixelSize)
                        ?? GetFallback(request.IsDirectory, FilePath.GetExtension(request.Path));
                    lock (Gate) Cache[request.Key] = icon;
                }
                catch (Exception ex)
                {
                    App.Log("提取文件图标失败: " + ex.Message);
                    icon = GetFallback(request.IsDirectory, FilePath.GetExtension(request.Path));
                }
                finally
                {
                    List<Action<BitmapSource>> callbacks = null;
                    lock (Gate)
                    {
                        if (Callbacks.TryGetValue(request.Key, out callbacks)) Callbacks.Remove(request.Key);
                    }
                    Pending.TryRemove(request.Key, out _);
                    if (callbacks != null && icon != null)
                    {
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                            {
                                foreach (var callback in callbacks) callback(icon);
                            }));
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
        }

        private static BitmapSource TypeIcon(bool isDir, string ext)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var color = isDir ? Color.FromRgb(245, 190, 64) : ExtensionColor(ext);
                dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(3, 6, 42, 36), 7, 7);
                if (isDir)
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(255, 214, 92)), null, new Rect(6, 3, 21, 10), 4, 4);
                else
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), 2);
                    dc.DrawLine(pen, new Point(12, 17), new Point(36, 17));
                    dc.DrawLine(pen, new Point(12, 24), new Point(32, 24));
                    dc.DrawLine(pen, new Point(12, 31), new Point(28, 31));
                }
            }
            var bmp = new RenderTargetBitmap(48, 48, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static Color ExtensionColor(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".doc": case ".docx": case ".rtf": return Color.FromRgb(54, 111, 220);
                case ".xls": case ".xlsx": case ".csv": return Color.FromRgb(48, 164, 104);
                case ".ppt": case ".pptx": return Color.FromRgb(225, 102, 55);
                case ".pdf": return Color.FromRgb(208, 66, 74);
                case ".zip": case ".rar": case ".7z": return Color.FromRgb(163, 104, 212);
                case ".jpg": case ".jpeg": case ".png": case ".gif": return Color.FromRgb(42, 168, 186);
                case ".mp3": case ".wav": case ".flac": return Color.FromRgb(219, 93, 160);
                case ".bat": case ".cmd": case ".ps1": return Color.FromRgb(89, 104, 122);
                case ".txt": case ".md": return Color.FromRgb(92, 137, 196);
                default: return Color.FromRgb(112, 122, 143);
            }
        }
    }

    /// <summary>音乐胶囊左侧的实时频谱:WASAPI 环回采集 + FFT 对数频段柱状显示,无展开页。</summary>
    public class SpectrumModule : ModuleBase
    {
        private readonly SpectrumSurface _surface;
        private readonly float[] _current = new float[SpectrumService.BarCount];
        private readonly double _maxHeight, _minHeight;
        private bool _captureActive;
        private bool _renderSubscribed;
        private bool _disposed;
        private TimeSpan _lastRenderTime = TimeSpan.MinValue;

        public SpectrumModule(TabStyle st) : base(st)
        {
            // 胶囊固定长度(比内容宽,两侧留白),16 根柱子居中分布,不随内容变化。
            double s = Ui.Scale;
            int n = SpectrumService.BarCount;
            double barW = 3.2 * s, gap = 2.8 * s;
            double inner = n * barW + (n - 1) * gap;
            double pillW = 125 * s;
            _maxHeight = 20 * s;
            _minHeight = 1.6 * s;

            _surface = new SpectrumSurface(
                n,
                barW,
                gap,
                _maxHeight,
                _minHeight,
                1.5 * s,
                Ui.Brush(ThemeService.Text, 1));

            Pill.Width = pillW;
            Pill.Padding = new Thickness(0);
            Pill.Child = _surface;
            Pill.ToolTip = "实时音频频谱";

            ThemeService.Changed += OnBarThemeChanged;
            IsVisibleChanged += (s2, e2) => SetSpectrumActive(IsVisible);
            Loaded += (s2, e2) => SetSpectrumActive(IsVisible);
            Unloaded += (s2, e2) => SetSpectrumActive(false);
        }

        protected override bool HasPanel => false;
        protected override bool HoverOpensPanel => false;

        public void SetSpectrumActive(bool active)
        {
            if (_disposed) return;
            active = active && IsVisible && IsLoaded;
            if (active == _captureActive) return;

            _captureActive = active;
            if (active)
            {
                SpectrumService.Instance.AddRef();
                _lastRenderTime = TimeSpan.MinValue;
                if (!_renderSubscribed)
                {
                    CompositionTarget.Rendering += OnRendering;
                    _renderSubscribed = true;
                }
                return;
            }

            if (_renderSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderSubscribed = false;
            }
            SpectrumService.Instance.ReleaseRef();
        }

        private const int IdleAfterMs = 20000; // 持续无跳动多久后进入空闲动画
        private bool _idleAnim;
        private double _idleHead;
        private long _idleLastTick;
        private long _lastActiveTick = Environment.TickCount64;

        private void OnBarThemeChanged()
        {
            if (Dispatcher.CheckAccess()) ApplyBarTheme();
            else Dispatcher.BeginInvoke(new Action(ApplyBarTheme));
        }

        private void ApplyBarTheme()
        {
            _surface.SetBrush(Ui.Brush(ThemeService.Text, 1));
        }

        private void OnRendering(object sender, EventArgs args)
        {
            if (!(args is RenderingEventArgs renderingArgs)) return;
            TimeSpan now = renderingArgs.RenderingTime;
            if (_lastRenderTime != TimeSpan.MinValue
                && now - _lastRenderTime < TimeSpan.FromMilliseconds(16)) return;

            double elapsedMs = _lastRenderTime == TimeSpan.MinValue
                ? 33
                : Math.Min(150, Math.Max(1, (now - _lastRenderTime).TotalMilliseconds));
            _lastRenderTime = now;
            Tick(elapsedMs);
        }

        private void Tick(double elapsedMs)
        {
            if (_disposed || !_captureActive || Visibility != Visibility.Visible) return;

            var target = SpectrumService.Instance.Bands;
            // 采集线程重连等中断期间让柱子自然落回底端。
            bool stale = Environment.TickCount64 - SpectrumService.Instance.LastDataTick > 600;

            // 任一频段有真实跳动就刷新活跃时间;持续 20 秒无跳动则进入逐根跳起动画。
            bool active = false;
            if (!stale && target != null)
                foreach (var v in target)
                    if (v > 0.12f) { active = true; break; }
            if (active) _lastActiveTick = Environment.TickCount64;
            bool idle = Environment.TickCount64 - _lastActiveTick >= IdleAfterMs;

            if (idle)
            {
                if (!_idleAnim) { _idleAnim = true; _idleHead = -3; _idleLastTick = Environment.TickCount64; }
                long now = Environment.TickCount64;
                // 每 170ms 行进一根;首尾各留 3 根缓冲,扫过一圈后循环。
                _idleHead += (now - _idleLastTick) / 170.0;
                _idleLastTick = now;
                if (_idleHead > _current.Length + 3) _idleHead = -3;
            }
            else _idleAnim = false;

            for (int i = 0; i < _current.Length; i++)
            {
                float t;
                if (idle)
                {
                    // 行进波头:波头到达时该柱跳满,随波头继续前进平滑回落。
                    float d = (float)(_idleHead - i);
                    t = d >= 0 && d <= 2.5f ? 1f - d / 2.5f : 0f;
                }
                else
                {
                    t = !stale && target != null && i < target.Length ? target[i] : 0f;
                }
                float c = _current[i];
                // 上升跟随快、下落衰减慢；按真实帧间隔插值，交互繁忙跳帧后不会积压变慢。
                double baseBlend = t > c ? 0.55 : 0.28;
                double blend = 1 - Math.Pow(1 - baseBlend, elapsedMs / 33.0);
                c += (t - c) * (float)blend;
                _current[i] = Math.Clamp(c, 0f, 1f);
            }

            _surface.SetValues(_current);
        }

        private sealed class SpectrumSurface : FrameworkElement
        {
            private readonly float[] _values;
            private readonly double _barWidth;
            private readonly double _gap;
            private readonly double _maxHeight;
            private readonly double _minHeight;
            private readonly double _radius;
            private Brush _brush;

            public SpectrumSurface(
                int count,
                double barWidth,
                double gap,
                double maxHeight,
                double minHeight,
                double radius,
                Brush brush)
            {
                _values = new float[count];
                _barWidth = barWidth;
                _gap = gap;
                _maxHeight = maxHeight;
                _minHeight = minHeight;
                _radius = radius;
                _brush = brush;
                Width = count * barWidth + (count - 1) * gap;
                Height = maxHeight;
                HorizontalAlignment = HorizontalAlignment.Center;
                VerticalAlignment = VerticalAlignment.Center;
            }

            public void SetValues(float[] values)
            {
                bool changed = false;
                for (int i = 0; i < _values.Length; i++)
                {
                    if (Math.Abs(_values[i] - values[i]) <= 0.002f) continue;
                    _values[i] = values[i];
                    changed = true;
                }
                if (changed) InvalidateVisual();
            }

            public void SetBrush(Brush brush)
            {
                _brush = brush;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);
                for (int i = 0; i < _values.Length; i++)
                {
                    float value = Math.Clamp(_values[i], 0f, 1f);
                    double height = _minHeight + (_maxHeight - _minHeight) * value;
                    double x = i * (_barWidth + _gap);
                    double y = _maxHeight - height;
                    drawingContext.PushOpacity(0.4 + 0.6 * value);
                    drawingContext.DrawRoundedRectangle(
                        _brush,
                        null,
                        new Rect(x, y, _barWidth, height),
                        _radius,
                        _radius);
                    drawingContext.Pop();
                }
            }
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_renderSubscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _renderSubscribed = false;
            }
            ThemeService.Changed -= OnBarThemeChanged;
            if (_captureActive)
            {
                _captureActive = false;
                SpectrumService.Instance.ReleaseRef();
            }
            base.Dispose();
        }
    }

    /// <summary>性能监测胶囊指标。</summary>
    public enum PerfMetric { Cpu, Gpu, Memory, Network }

    /// <summary>性能监测胶囊:无展开页,数值由 PerfService 每秒采样刷新;四个指标各自独立显隐。</summary>
    public class PerfModule : ModuleBase
    {
        private readonly PerfMetric _metric;
        private readonly TextBlock _value;
        private bool _active;
        private bool _disposed;

        public PerfModule(PerfMetric metric, TabStyle st) : base(st)
        {
            _metric = metric;
            double s = Ui.Scale;
            var sp = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (metric != PerfMetric.Network)
            {
                // 网络胶囊不带图标:上行/下行各自有箭头标识,省出宽度。
                sp.Children.Add(metric switch
                {
                    PerfMetric.Cpu => MakeCpuGlyph(17),
                    PerfMetric.Gpu => MakeGpuGlyph(18),
                    _ => MakeMemoryGlyph(18)
                });
            }
            _value = MakeText("--", -1);
            _value.Margin = new Thickness(metric == PerfMetric.Network ? 0 : 4 * s, 0, 0, 0);
            sp.Children.Add(_value);
            Pill.Child = sp;
            Pill.Padding = new Thickness(0);
            // 固定宽度:数值在 "0%" 与 "100%" 间变化时不让胶囊抖动。
            Pill.Width = (metric == PerfMetric.Network ? 88 : 58) * s;
            Pill.ToolTip = Caption;

            IsVisibleChanged += (s2, e2) => SetActive(IsVisible);
            Loaded += (s2, e2) => SetActive(IsVisible);
            Unloaded += (s2, e2) => SetActive(false);
        }

        private string Caption => _metric switch
        {
            PerfMetric.Cpu => "CPU 占用率",
            PerfMetric.Gpu => "GPU 占用率",
            PerfMetric.Memory => "内存占用",
            _ => "网络上下行"
        };

        protected override bool HasPanel => false;
        protected override bool HoverOpensPanel => false;

        private void SetActive(bool active)
        {
            if (_disposed) return;
            active = active && IsVisible && IsLoaded;
            if (active == _active) return;
            _active = active;
            var service = PerfService.Instance;
            if (active)
            {
                service.AddRef();
                service.Updated += OnSample;
                var latest = service.Latest;
                if (latest != null) Apply(latest);
                return;
            }
            service.Updated -= OnSample;
            service.ReleaseRef();
        }

        private void OnSample(PerfSample sample)
        {
            if (_disposed) return;
            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_disposed) Apply(sample);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void Apply(PerfSample sample)
        {
            switch (_metric)
            {
                case PerfMetric.Cpu:
                    _value.Text = FmtPercent(sample.CpuPercent);
                    Pill.ToolTip = Caption + (sample.CpuPercent >= 0
                        ? " — " + Math.Round(sample.CpuPercent) + "%"
                        : "");
                    break;
                case PerfMetric.Gpu:
                    var gpu = PickGpu(sample.Gpus);
                    _value.Text = gpu == null ? "--" : FmtPercent(gpu.UtilPercent);
                    Pill.ToolTip = gpu == null
                        ? Caption
                        : gpu.Name + (gpu.UtilPercent >= 0 ? " — " + Math.Round(gpu.UtilPercent) + "%" : "");
                    break;
                case PerfMetric.Memory:
                    if (sample.MemTotalBytes <= 0)
                    {
                        _value.Text = "--";
                        Pill.ToolTip = Caption;
                    }
                    else
                    {
                        double percent = 100.0 * sample.MemUsedBytes / sample.MemTotalBytes;
                        _value.Text = FmtPercent(percent);
                        Pill.ToolTip = "内存 已用 " + FmtGb(sample.MemUsedBytes) + " / "
                            + FmtGb(sample.MemTotalBytes) + " (" + Math.Round(percent) + "%)";
                    }
                    break;
                default:
                    if (sample.NetUpBps < 0 && sample.NetDownBps < 0)
                    {
                        _value.Text = "--";
                        Pill.ToolTip = Caption;
                    }
                    else
                    {
                        _value.Text = "↓" + FmtSpeed(sample.NetDownBps) + " ↑" + FmtSpeed(sample.NetUpBps);
                        Pill.ToolTip = "网络 下行 " + FmtSpeedLong(sample.NetDownBps) + "/s,上行 "
                            + FmtSpeedLong(sample.NetUpBps) + "/s";
                    }
                    break;
            }
        }

        private static GpuAdapterInfo PickGpu(List<GpuAdapterInfo> gpus)
        {
            if (gpus == null || gpus.Count == 0) return null;
            string want = App.Settings?.MonitorGpuLuid;
            if (!string.IsNullOrEmpty(want))
                foreach (var g in gpus)
                    if (string.Equals(g.Luid, want, StringComparison.OrdinalIgnoreCase)) return g;
            return gpus[0];
        }

        private static string FmtPercent(double v)
            => v < 0 ? "--" : Math.Round(v).ToString() + "%";

        private static string FmtGb(ulong bytes)
            => (bytes / 1073741824.0).ToString("0.0");

        private static string FmtSpeed(double bps)
        {
            if (bps < 0) return "--";
            if (bps >= 1073741824) return (bps / 1073741824).ToString("0.0") + "G";
            if (bps >= 1048576) return (bps / 1048576).ToString(bps >= 10485760 ? "0" : "0.0") + "M";
            return (bps / 1024).ToString(bps >= 10240 ? "0" : "0.0") + "K";
        }

        private static string FmtSpeedLong(double bps)
        {
            if (bps < 0) return "未知";
            if (bps >= 1073741824) return (bps / 1073741824).ToString("0.0") + " MB";
            if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB";
            if (bps >= 1024) return (bps / 1024).ToString("0.0") + " KB";
            return Math.Round(bps) + " B";
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SetActive(false);
            base.Dispose();
        }
    }
}
