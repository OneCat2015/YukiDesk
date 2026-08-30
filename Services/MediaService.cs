using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DesktopBar.Services
{
    /// <summary>一次媒体状态更新的不可变快照；发布到 UI 后不会再被服务修改。</summary>
    public sealed class MediaInfo
    {
        public long Generation { get; }
        public bool HasSession { get; }
        public string SessionKey { get; }
        public string TrackKey { get; }
        public string ThumbnailKey { get; }
        public string Title { get; }
        public string Artist { get; }
        public bool Playing { get; }
        public TimeSpan Position { get; }
        public TimeSpan Duration { get; }
        public bool SeekSupported { get; }
        public BitmapSource Cover { get; }

        public MediaInfo(
            long generation,
            bool hasSession,
            string sessionKey,
            string trackKey,
            string thumbnailKey,
            string title,
            string artist,
            BitmapSource cover,
            bool playing,
            TimeSpan position,
            TimeSpan duration,
            bool seekSupported)
        {
            Generation = generation;
            HasSession = hasSession;
            SessionKey = sessionKey ?? "";
            TrackKey = trackKey ?? "";
            ThumbnailKey = thumbnailKey ?? "";
            Title = title ?? "";
            Artist = artist ?? "";
            Cover = cover;
            Playing = playing;
            Position = position;
            Duration = duration;
            SeekSupported = seekSupported;
        }
    }

    /// <summary>
    /// 通过系统媒体控制(SMTC)读取后台播放的音乐:QQ音乐/网易云/Spotify/浏览器等均支持。
    /// 控制按钮通过系统媒体键实现。
    /// </summary>
    public class MediaService
    {
        private const ulong MaxCoverBytes = 20_000_000;

        private readonly Dispatcher _dispatcher;
        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private GlobalSystemMediaTransportControlsSession _session;
        private DispatcherTimer _timer;
        private DateTime _posTime = DateTime.MinValue;
        private long _sessionGeneration;
        private long _refreshGeneration;
        private long _publishedGeneration;
        private string _sessionKey = "";
        private MediaInfo _current = new MediaInfo(0, false, "", "", "", "", "", null, false, TimeSpan.Zero, TimeSpan.Zero, false);

        public event Action<MediaInfo> Updated;   // 已在 UI 线程触发
        public event Action SessionLost;          // 已在 UI 线程触发

        public async void Seek(TimeSpan position)
        {
            var session = _session;
            if (session == null) return;
            try { await session.TryChangePlaybackPositionAsync((long)Math.Max(0, position.Ticks)); }
            catch (Exception ex) { App.Log("媒体定位失败: " + ex.Message); }
        }

        public MediaService(Dispatcher dispatcher) { _dispatcher = dispatcher; }

        public async void Start()
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            }
            catch (Exception ex)
            {
                App.Log("SMTC 初始化失败: " + ex);
                _ = _dispatcher.BeginInvoke(new Action(() => Hook(null)));
                return;
            }

            _manager.CurrentSessionChanged += (manager, args) =>
                _dispatcher.BeginInvoke(new Action(() => Hook(manager.GetCurrentSession())));
            _manager.SessionsChanged += (manager, args) =>
                _dispatcher.BeginInvoke(new Action(() => Hook(manager.GetCurrentSession())));

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _timer.Tick += (sender, args) => Push(_session);
            _timer.Start();

            Hook(_manager.GetCurrentSession());
        }

        private void Hook(GlobalSystemMediaTransportControlsSession session)
        {
            if (ReferenceEquals(_session, session)) return;

            _session = session;
            _sessionGeneration++;
            _refreshGeneration++;
            _sessionKey = session?.SourceAppUserModelId ?? "";
            _posTime = DateTime.MinValue;
            PublishEmpty(session != null);

            if (session == null)
            {
                SessionLost?.Invoke();
                return;
            }

            session.MediaPropertiesChanged += (sender, args) =>
                _dispatcher.BeginInvoke(new Action(() => BeginRefresh(session)));
            session.PlaybackInfoChanged += (sender, args) =>
                _dispatcher.BeginInvoke(new Action(() => Push(session)));
            session.TimelinePropertiesChanged += (sender, args) =>
                _dispatcher.BeginInvoke(new Action(() => Push(session)));

            BeginRefresh(session);
            Push(session);
        }

        private void BeginRefresh(GlobalSystemMediaTransportControlsSession session)
        {
            if (!ReferenceEquals(_session, session)) return;
            long sessionGeneration = _sessionGeneration;
            long refreshGeneration = ++_refreshGeneration;
            _ = RefreshPropsAsync(session, sessionGeneration, refreshGeneration);
        }

        private async Task RefreshPropsAsync(
            GlobalSystemMediaTransportControlsSession session,
            long sessionGeneration,
            long refreshGeneration)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var timeline = session.GetTimelineProperties();
                string title = props?.Title ?? "";
                string artist = props?.Artist ?? "";
                string sessionKey = session.SourceAppUserModelId ?? "";
                string trackKey = BuildTrackKey(sessionKey, props, timeline.EndTime);
                bool seekSupported = timeline.MaxSeekTime > timeline.MinSeekTime;
                GetPlayback(session, timeline, out bool playing, out TimeSpan position);

                if (!IsCurrent(session, sessionGeneration, refreshGeneration)) return;

                // 缩略图 API 没有可提前比较的稳定 ID；每次属性刷新都先清图，
                // 这样同名曲目或专辑封面更新时也不会在异步读取期间沿用旧图。
                Publish(new MediaInfo(
                    NextPublishedGeneration(),
                    true,
                    sessionKey,
                    trackKey,
                    "",
                    title,
                    artist,
                    null,
                    playing,
                    position,
                    timeline.EndTime,
                    seekSupported));

                var cover = await LoadCoverAsync(props?.Thumbnail);
                if (!IsCurrent(session, sessionGeneration, refreshGeneration)) return;

                GetPlayback(session, timeline, out playing, out position);
                Publish(new MediaInfo(
                    NextPublishedGeneration(),
                    true,
                    sessionKey,
                    trackKey,
                    cover.Key,
                    title,
                    artist,
                    cover.Image,
                    playing,
                    position,
                    timeline.EndTime,
                    seekSupported));
            }
            catch (Exception ex)
            {
                if (IsCurrent(session, sessionGeneration, refreshGeneration))
                    App.Log("读取媒体信息: " + ex.Message);
            }
        }

        private bool IsCurrent(
            GlobalSystemMediaTransportControlsSession session,
            long sessionGeneration,
            long refreshGeneration)
        {
            return ReferenceEquals(_session, session)
                && _sessionGeneration == sessionGeneration
                && _refreshGeneration == refreshGeneration;
        }

        private static string BuildTrackKey(
            string sessionKey,
            GlobalSystemMediaTransportControlsSessionMediaProperties props,
            TimeSpan duration)
        {
            const string separator = "\u001F";
            return string.Join(separator,
                sessionKey ?? "",
                props?.Title ?? "",
                props?.Artist ?? "",
                props?.AlbumArtist ?? "",
                props?.AlbumTitle ?? "",
                props?.Subtitle ?? "",
                props?.TrackNumber.ToString() ?? "",
                duration.Ticks.ToString());
        }

        private void GetPlayback(
            GlobalSystemMediaTransportControlsSession session,
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline,
            out bool playing,
            out TimeSpan position)
        {
            var playback = session.GetPlaybackInfo();
            playing = playback != null
                && playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            position = timeline.Position;
            if (playing && _posTime != DateTime.MinValue)
            {
                var elapsed = DateTime.Now - _posTime;
                if (elapsed < TimeSpan.FromSeconds(3)) position += elapsed;
            }
            if (timeline.EndTime > TimeSpan.Zero && position > timeline.EndTime)
                position = timeline.EndTime;
        }

        private void Push(GlobalSystemMediaTransportControlsSession session)
        {
            if (session == null || !ReferenceEquals(_session, session) || !_current.HasSession) return;
            try
            {
                var timeline = session.GetTimelineProperties();
                GetPlayback(session, timeline, out bool playing, out TimeSpan position);
                _posTime = DateTime.Now;
                Publish(new MediaInfo(
                    NextPublishedGeneration(),
                    true,
                    _current.SessionKey,
                    _current.TrackKey,
                    _current.ThumbnailKey,
                    _current.Title,
                    _current.Artist,
                    _current.Cover,
                    playing,
                    position,
                    timeline.EndTime,
                    timeline.MaxSeekTime > timeline.MinSeekTime));
            }
            catch { }
        }

        private void PublishEmpty(bool hasSession)
        {
            Publish(new MediaInfo(
                NextPublishedGeneration(),
                hasSession,
                _sessionKey,
                "",
                "",
                "",
                "",
                null,
                false,
                TimeSpan.Zero,
                TimeSpan.Zero,
                false));
        }

        private long NextPublishedGeneration() => ++_publishedGeneration;

        private void Publish(MediaInfo info)
        {
            _current = info;
            Updated?.Invoke(info);
        }

        private sealed class CoverResult
        {
            public BitmapSource Image { get; }
            public string Key { get; }

            public CoverResult(BitmapSource image, string key)
            {
                Image = image;
                Key = key;
            }
        }

        private static async Task<CoverResult> LoadCoverAsync(IRandomAccessStreamReference reference)
        {
            if (reference == null) return new CoverResult(null, "none");
            try
            {
                using var stream = await reference.OpenReadAsync();
                if (stream.Size == 0) return new CoverResult(null, "empty");
                if (stream.Size > MaxCoverBytes) return new CoverResult(null, "oversized:" + stream.Size);

                uint size = (uint)stream.Size;
                var bytes = new byte[size];
                using var reader = new DataReader(stream);
                await reader.LoadAsync(size).AsTask();
                reader.ReadBytes(bytes);

                string key = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes));
                var image = new BitmapImage();
                using var memory = new MemoryStream(bytes);
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = memory;
                image.EndInit();
                image.Freeze();
                return new CoverResult(image, key);
            }
            catch
            {
                return new CoverResult(null, "error");
            }
        }
    }
}
