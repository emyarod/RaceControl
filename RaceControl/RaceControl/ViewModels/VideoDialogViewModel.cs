﻿using NLog;
using Prism.Commands;
using Prism.Events;
using Prism.Services.Dialogs;
using RaceControl.Common.Enums;
using RaceControl.Common.Interfaces;
using RaceControl.Core.Helpers;
using RaceControl.Core.Mvvm;
using RaceControl.Core.Settings;
using RaceControl.Core.Streamlink;
using RaceControl.Events;
using RaceControl.Extensions;
using RaceControl.Services.Interfaces.F1TV;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Input;

namespace RaceControl.ViewModels
{
    public class VideoDialogViewModel : DialogViewModelBase
    {
        private const int MouseWheelDelta = 12;

        private readonly IEventAggregator _eventAggregator;
        private readonly IApiService _apiService;
        private readonly IStreamlinkLauncher _streamlinkLauncher;
        private readonly ISettings _settings;
        private readonly IVideoDialogLayout _videoDialogLayout;
        private readonly object _showControlsTimerLock = new object();

        private ICommand _mouseDownVideoCommand;
        private ICommand _mouseMoveVideoCommand;
        private ICommand _mouseEnterVideoCommand;
        private ICommand _mouseLeaveVideoCommand;
        private ICommand _mouseWheelVideoCommand;
        private ICommand _mouseMoveControlBarCommand;
        private ICommand _mouseEnterControlBarCommand;
        private ICommand _mouseLeaveControlBarCommand;
        private ICommand _mouseWheelControlBarCommand;
        private ICommand _closeAllWindowsCommand;
        private ICommand _togglePauseCommand;
        private ICommand _togglePauseAllCommand;
        private ICommand _toggleMuteCommand;
        private ICommand _fastForwardCommand;
        private ICommand _syncSessionCommand;
        private ICommand _toggleRecordingCommand;
        private ICommand _toggleFullScreenCommand;
        private ICommand _moveToCornerCommand;
        private ICommand _scanChromecastCommand;
        private ICommand _startCastVideoCommand;
        private ICommand _stopCastVideoCommand;

        private Process _streamlinkProcess;
        private Process _streamlinkRecordingProcess;
        private string _token;
        private long _identifier;
        private IPlayableContent _playableContent;
        private VideoDialogSettings _dialogSettings;
        private WindowStartupLocation _startupLocation = WindowStartupLocation.CenterOwner;
        private bool _isStreamlink;
        private bool _isRecording;
        private bool _showControls = true;
        private IMediaRenderer _selectedMediaRenderer;
        private Timer _showControlsTimer;
        private string _carImageUrl;
        private string _headshotImageUrl;

        public VideoDialogViewModel(
            ILogger logger,
            IEventAggregator eventAggregator,
            IApiService apiService,
            IStreamlinkLauncher streamlinkLauncher,
            ISettings settings,
            IVideoDialogLayout videoDialogLayout,
            IMediaPlayer mediaPlayer)
            : base(logger)
        {
            _eventAggregator = eventAggregator;
            _apiService = apiService;
            _streamlinkLauncher = streamlinkLauncher;
            _settings = settings;
            _videoDialogLayout = videoDialogLayout;
            MediaPlayer = mediaPlayer;
        }

        public override string Title => $"{_identifier}. {PlayableContent?.Title}";

        public ICommand MouseDownVideoCommand => _mouseDownVideoCommand ??= new DelegateCommand<MouseButtonEventArgs>(MouseDownVideoExecute);
        public ICommand MouseMoveVideoCommand => _mouseMoveVideoCommand ??= new DelegateCommand(MouseEnterOrLeaveOrMoveVideoExecute);
        public ICommand MouseEnterVideoCommand => _mouseEnterVideoCommand ??= new DelegateCommand(MouseEnterOrLeaveOrMoveVideoExecute);
        public ICommand MouseLeaveVideoCommand => _mouseLeaveVideoCommand ??= new DelegateCommand(MouseEnterOrLeaveOrMoveVideoExecute);
        public ICommand MouseWheelVideoCommand => _mouseWheelVideoCommand ??= new DelegateCommand<MouseWheelEventArgs>(MouseWheelVideoExecute);
        public ICommand MouseMoveControlBarCommand => _mouseMoveControlBarCommand ??= new DelegateCommand(MouseMoveControlBarExecute);
        public ICommand MouseEnterControlBarCommand => _mouseEnterControlBarCommand ??= new DelegateCommand(MouseEnterControlBarExecute);
        public ICommand MouseLeaveControlBarCommand => _mouseLeaveControlBarCommand ??= new DelegateCommand(MouseLeaveControlBarExecute);
        public ICommand MouseWheelControlBarCommand => _mouseWheelControlBarCommand ??= new DelegateCommand<MouseWheelEventArgs>(MouseWheelControlBarExecute);
        public ICommand CloseAllWindowsCommand => _closeAllWindowsCommand ??= new DelegateCommand(CloseAllWindowsExecute);
        public ICommand TogglePauseCommand => _togglePauseCommand ??= new DelegateCommand(TogglePauseExecute).ObservesCanExecute(() => CanClose);
        public ICommand TogglePauseAllCommand => _togglePauseAllCommand ??= new DelegateCommand(TogglePauseAllExecute).ObservesCanExecute(() => CanClose);
        public ICommand ToggleMuteCommand => _toggleMuteCommand ??= new DelegateCommand<bool?>(ToggleMuteExecute).ObservesCanExecute(() => CanClose);
        public ICommand FastForwardCommand => _fastForwardCommand ??= new DelegateCommand<int?>(FastForwardExecute, CanFastForwardExecute).ObservesProperty(() => CanClose).ObservesProperty(() => PlayableContent);
        public ICommand SyncSessionCommand => _syncSessionCommand ??= new DelegateCommand(SyncSessionExecute, CanSyncSessionExecute).ObservesProperty(() => CanClose).ObservesProperty(() => PlayableContent);
        public ICommand ToggleRecordingCommand => _toggleRecordingCommand ??= new DelegateCommand(ToggleRecordingExecute, CanToggleRecordingExecute).ObservesProperty(() => CanClose).ObservesProperty(() => PlayableContent).ObservesProperty(() => IsRecording).ObservesProperty(() => MediaPlayer.IsPaused);
        public ICommand ToggleFullScreenCommand => _toggleFullScreenCommand ??= new DelegateCommand<long?>(ToggleFullScreenExecute);
        public ICommand MoveToCornerCommand => _moveToCornerCommand ??= new DelegateCommand<WindowLocation?>(MoveToCornerExecute, CanMoveToCornerExecute).ObservesProperty(() => DialogSettings.WindowState);
        public ICommand ScanChromecastCommand => _scanChromecastCommand ??= new DelegateCommand(ScanChromecastExecute, CanScanChromecastExecute).ObservesProperty(() => CanClose).ObservesProperty(() => MediaPlayer.IsScanning);
        public ICommand StartCastVideoCommand => _startCastVideoCommand ??= new DelegateCommand(StartCastVideoExecute, CanStartCastVideoExecute).ObservesProperty(() => CanClose).ObservesProperty(() => SelectedMediaRenderer);
        public ICommand StopCastVideoCommand => _stopCastVideoCommand ??= new DelegateCommand(StopCastVideoExecute, CanStopCastVideoExecute).ObservesProperty(() => MediaPlayer.IsCasting);

        public IMediaPlayer MediaPlayer { get; }

        public IPlayableContent PlayableContent
        {
            get => _playableContent;
            set => SetProperty(ref _playableContent, value);
        }

        public VideoDialogSettings DialogSettings
        {
            get => _dialogSettings ??= VideoDialogSettings.GetDefaultSettings();
            set => SetProperty(ref _dialogSettings, value);
        }

        public WindowStartupLocation StartupLocation
        {
            get => _startupLocation;
            set => SetProperty(ref _startupLocation, value);
        }

        public bool IsStreamlink
        {
            get => _isStreamlink;
            set => SetProperty(ref _isStreamlink, value);
        }

        public bool IsRecording
        {
            get => _isRecording;
            set => SetProperty(ref _isRecording, value);
        }

        public bool ShowControls
        {
            get => _showControls;
            set => SetProperty(ref _showControls, value);
        }

        public IMediaRenderer SelectedMediaRenderer
        {
            get => _selectedMediaRenderer;
            set => SetProperty(ref _selectedMediaRenderer, value);
        }

        public string CarImageUrl
        {
            get => _carImageUrl;
            set => SetProperty(ref _carImageUrl, value);
        }

        public string HeadshotImageUrl
        {
            get => _headshotImageUrl;
            set => SetProperty(ref _headshotImageUrl, value);
        }

        public override void OnDialogOpened(IDialogParameters parameters)
        {
            _token = parameters.GetValue<string>(ParameterNames.TOKEN);
            _identifier = parameters.GetValue<long>(ParameterNames.IDENTIFIER);
            PlayableContent = parameters.GetValue<IPlayableContent>(ParameterNames.CONTENT);
            IsStreamlink = PlayableContent.IsLive && !_settings.DisableStreamlink;

            var dialogSettings = parameters.GetValue<VideoDialogSettings>(ParameterNames.SETTINGS);

            if (dialogSettings != null)
            {
                StartupLocation = WindowStartupLocation.Manual;
                LoadDialogSettings(dialogSettings);
            }
            else
            {
                StartupLocation = WindowStartupLocation.CenterScreen;
            }

            InitializeAsync().Await(InitializeCompleted, InitializeError);
        }

        public override void OnDialogClosed()
        {
            MediaPlayer.StopPlayback();
            MediaPlayer.Dispose();
            RemoveShowControlsTimer();
            UnsubscribeEvents();
            CleanupProcess(_streamlinkProcess);
            CleanupProcess(_streamlinkRecordingProcess);

            base.OnDialogClosed();
        }

        private void ShowControlsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            ShowControls = false;

            Application.Current.Dispatcher.Invoke(() =>
            {
                Mouse.OverrideCursor = Cursors.None;
            });
        }

        private void MouseDownVideoExecute(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            switch (e.ClickCount)
            {
                case 1:
                    if (e.Source is DependencyObject dependencyObject)
                    {
                        Window.GetWindow(dependencyObject)?.Owner?.DragMove();
                    }

                    break;

                case 2:
                    ToggleFullScreenCommand.TryExecute();
                    break;
            }
        }

        private void MouseEnterOrLeaveOrMoveVideoExecute()
        {
            ShowControlsAndResetTimer();
        }

        private void MouseWheelVideoExecute(MouseWheelEventArgs e)
        {
            MediaPlayer.Volume += e.Delta / MouseWheelDelta;
            ShowControlsAndResetTimer();
        }

        private static void MouseMoveControlBarExecute()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Mouse.OverrideCursor = null;
            });
        }

        private void MouseEnterControlBarExecute()
        {
            lock (_showControlsTimerLock)
            {
                _showControlsTimer?.Stop();
            }
        }

        private void MouseLeaveControlBarExecute()
        {
            lock (_showControlsTimerLock)
            {
                _showControlsTimer?.Start();
            }
        }

        private void MouseWheelControlBarExecute(MouseWheelEventArgs e)
        {
            MediaPlayer.Volume += e.Delta / MouseWheelDelta;
        }

        private void CloseAllWindowsExecute()
        {
            _eventAggregator.GetEvent<CloseAllEvent>().Publish(null);
        }

        private void TogglePauseExecute()
        {
            Logger.Info("Toggling pause...");
            MediaPlayer.TogglePause();
        }

        private void TogglePauseAllExecute()
        {
            Logger.Info("Toggling pause for all video players...");
            _eventAggregator.GetEvent<PauseAllEvent>().Publish();
        }

        private void ToggleMuteExecute(bool? mute)
        {
            Logger.Info("Toggling mute...");
            MediaPlayer.ToggleMute(mute);
        }

        private bool CanFastForwardExecute(int? seconds)
        {
            return CanClose && !PlayableContent.IsLive;
        }

        private void FastForwardExecute(int? seconds)
        {
            if (seconds.HasValue)
            {
                Logger.Info($"Fast forwarding stream {seconds.Value} seconds...");
                MediaPlayer.Time += seconds.Value * 1000;
            }
        }

        private bool CanSyncSessionExecute()
        {
            return CanClose && !PlayableContent.IsLive;
        }

        private void SyncSessionExecute()
        {
            var payload = new SyncStreamsEventPayload(PlayableContent.SyncUID, MediaPlayer.Time);
            Logger.Info($"Syncing streams with sync-UID '{payload.SyncUID}' to timestamp '{payload.Time}'...");
            _eventAggregator.GetEvent<SyncStreamsEvent>().Publish(payload);
        }

        private bool CanToggleRecordingExecute()
        {
            return CanClose && PlayableContent.IsLive && (IsRecording || !MediaPlayer.IsPaused);
        }

        private void ToggleRecordingExecute()
        {
            if (!IsRecording)
            {
                StartRecordingAsync().Await(() => IsRecording = true, HandleNonCriticalError);
            }
            else
            {
                StopRecording();
                IsRecording = false;
            }
        }

        private void OnSyncStreams(SyncStreamsEventPayload payload)
        {
            if (CanClose && PlayableContent.SyncUID == payload.SyncUID && !PlayableContent.IsLive)
            {
                MediaPlayer.Time = payload.Time;
            }
        }

        private void OnPauseAll()
        {
            TogglePauseCommand.TryExecute();
        }

        private void OnMuteAll(long identifier)
        {
            var mute = identifier != _identifier;
            ToggleMuteCommand.TryExecute(mute);
        }

        private void OnCloseAll(ContentType? contentType)
        {
            CloseWindowCommand.TryExecute();
        }

        private void OnSaveLayout(ContentType contentType)
        {
            var dialogSettings = GetDialogSettings();
            _videoDialogLayout.Instances.Add(dialogSettings);
        }

        private void OnToggleFullScreen(long identifier)
        {
            if (ToggleFullScreenCommand.TryExecute() && DialogSettings.WindowState == WindowState.Maximized)
            {
                _eventAggregator.GetEvent<MuteAllEvent>().Publish(identifier);
            }
        }

        private void ToggleFullScreenExecute(long? identifier)
        {
            if (identifier == null)
            {
                if (DialogSettings.WindowState != WindowState.Maximized)
                {
                    SetFullScreen();
                }
                else
                {
                    SetWindowed();
                }
            }
            else
            {
                _eventAggregator.GetEvent<ToggleFullScreenEvent>().Publish(identifier.Value);
            }
        }

        private bool CanMoveToCornerExecute(WindowLocation? location)
        {
            return DialogSettings.WindowState != WindowState.Maximized && location != null;
        }

        private void MoveToCornerExecute(WindowLocation? location)
        {
            var screen = ScreenHelper.GetScreen(DialogSettings);
            var screenScale = ScreenHelper.GetScreenScale();
            var screenTop = screen.WorkingArea.Top / screenScale;
            var screenLeft = screen.WorkingArea.Left / screenScale;
            var windowLocation = location.GetValueOrDefault();
            var windowWidth = windowLocation.GetWindowWidthOrHeight(screen.WorkingArea.Width, screenScale);
            var windowHeight = windowLocation.GetWindowWidthOrHeight(screen.WorkingArea.Height, screenScale);
            windowLocation.GetWindowTopAndLeft(screenTop, screenLeft, windowWidth, windowHeight, out var windowTop, out var windowLeft);

            DialogSettings.ResizeMode = ResizeMode.NoResize;
            DialogSettings.Width = windowWidth;
            DialogSettings.Height = windowHeight;
            DialogSettings.Top = windowTop;
            DialogSettings.Left = windowLeft;
        }

        private bool CanScanChromecastExecute()
        {
            return CanClose && !MediaPlayer.IsScanning;
        }

        private void ScanChromecastExecute()
        {
            Logger.Info("Scanning for Chromecast devices...");
            MediaPlayer.ScanChromecastAsync().Await(HandleNonCriticalError);
        }

        private bool CanStartCastVideoExecute()
        {
            return CanClose && SelectedMediaRenderer != null;
        }

        private void StartCastVideoExecute()
        {
            Logger.Info($"Starting casting of video with renderer '{SelectedMediaRenderer.Name}'...");
            ChangeRendererAsync(SelectedMediaRenderer).Await(HandleNonCriticalError);
        }

        private bool CanStopCastVideoExecute()
        {
            return MediaPlayer.IsCasting;
        }

        private void StopCastVideoExecute()
        {
            Logger.Info("Stopping casting of video...");
            ChangeRendererAsync().Await(HandleNonCriticalError);
        }

        private void LoadDialogSettings(VideoDialogSettings settings)
        {
            // Properties need to be set in this order
            DialogSettings.ResizeMode = settings.ResizeMode;
            DialogSettings.WindowState = settings.WindowState;
            DialogSettings.Topmost = settings.Topmost;
            DialogSettings.Top = settings.Top;
            DialogSettings.Left = settings.Left;

            if (settings.WindowState != WindowState.Maximized)
            {
                DialogSettings.Width = settings.Width;
                DialogSettings.Height = settings.Height;
            }

            DialogSettings.IsMuted = settings.IsMuted;
            DialogSettings.Volume = settings.Volume;
            DialogSettings.AudioDevice = settings.AudioDevice;
        }

        private VideoDialogSettings GetDialogSettings()
        {
            return new VideoDialogSettings
            {
                Top = DialogSettings.Top,
                Left = DialogSettings.Left,
                Width = DialogSettings.Width,
                Height = DialogSettings.Height,
                ResizeMode = DialogSettings.ResizeMode,
                WindowState = DialogSettings.WindowState,
                Topmost = DialogSettings.Topmost,
                IsMuted = MediaPlayer.IsMuted,
                Volume = MediaPlayer.Volume,
                AudioDevice = MediaPlayer.AudioDevice?.Description,
                ChannelName = PlayableContent.Name
            };
        }

        private async Task InitializeAsync()
        {
            await StartStreamAsync();
            await LoadDriverImageUrlsAsync();
            SubscribeEvents();
            CreateShowControlsTimer();
        }

        private void InitializeCompleted()
        {
            base.OnDialogOpened(null);
        }

        private void InitializeError(Exception ex)
        {
            base.OnDialogOpened(null);
            HandleCriticalError(ex);
        }

        private async Task StartStreamAsync()
        {
            var streamUrl = await _apiService.GetTokenisedUrlAsync(_token, PlayableContent);

            if (IsStreamlink)
            {
                var (streamlinkProcess, streamlinkUrl) = await _streamlinkLauncher.StartStreamlinkExternalAsync(streamUrl);
                _streamlinkProcess = streamlinkProcess;
                streamUrl = streamlinkUrl;
            }

            if (!string.IsNullOrWhiteSpace(DialogSettings.AudioDevice))
            {
                var audioDevice = MediaPlayer.AudioDevices.FirstOrDefault(ad => ad.Description == DialogSettings.AudioDevice);

                if (audioDevice != null)
                {
                    MediaPlayer.AudioDevice = audioDevice;
                }
            }

            await MediaPlayer.StartPlaybackAsync(streamUrl);
            MediaPlayer.ToggleMute(DialogSettings.IsMuted);
            MediaPlayer.Volume = DialogSettings.Volume;
        }

        private async Task LoadDriverImageUrlsAsync()
        {
            if (string.IsNullOrWhiteSpace(PlayableContent.DriverUID))
            {
                return;
            }

            var driver = await _apiService.GetDriverAsync(PlayableContent.DriverUID);

            if (driver != null)
            {
                CarImageUrl = driver.CarUrl;
                HeadshotImageUrl = driver.HeadshotUrl;
            }
        }

        private void SubscribeEvents()
        {
            _eventAggregator.GetEvent<SyncStreamsEvent>().Subscribe(OnSyncStreams);
            _eventAggregator.GetEvent<PauseAllEvent>().Subscribe(OnPauseAll);
            _eventAggregator.GetEvent<MuteAllEvent>().Subscribe(OnMuteAll);
            _eventAggregator.GetEvent<CloseAllEvent>().Subscribe(OnCloseAll, contentType => contentType == null || contentType == PlayableContent.ContentType);
            _eventAggregator.GetEvent<SaveLayoutEvent>().Subscribe(OnSaveLayout, contentType => contentType == PlayableContent.ContentType);
            _eventAggregator.GetEvent<ToggleFullScreenEvent>().Subscribe(OnToggleFullScreen, identifier => identifier == _identifier);
        }

        private void UnsubscribeEvents()
        {
            _eventAggregator.GetEvent<SyncStreamsEvent>().Unsubscribe(OnSyncStreams);
            _eventAggregator.GetEvent<PauseAllEvent>().Unsubscribe(OnPauseAll);
            _eventAggregator.GetEvent<MuteAllEvent>().Unsubscribe(OnMuteAll);
            _eventAggregator.GetEvent<CloseAllEvent>().Unsubscribe(OnCloseAll);
            _eventAggregator.GetEvent<SaveLayoutEvent>().Unsubscribe(OnSaveLayout);
            _eventAggregator.GetEvent<ToggleFullScreenEvent>().Unsubscribe(OnToggleFullScreen);
        }

        private void CreateShowControlsTimer()
        {
            RemoveShowControlsTimer();

            lock (_showControlsTimerLock)
            {
                _showControlsTimer = new Timer(2000) { AutoReset = false };
                _showControlsTimer.Elapsed += ShowControlsTimer_Elapsed;
                _showControlsTimer.Start();
            }
        }

        private void RemoveShowControlsTimer()
        {
            lock (_showControlsTimerLock)
            {
                if (_showControlsTimer != null)
                {
                    _showControlsTimer.Stop();
                    _showControlsTimer.Dispose();
                    _showControlsTimer = null;
                }
            }
        }

        private void ShowControlsAndResetTimer()
        {
            lock (_showControlsTimerLock)
            {
                _showControlsTimer?.Stop();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ShowControls = true;
                    Mouse.OverrideCursor = null;
                });

                _showControlsTimer?.Start();
            }
        }

        private async Task ChangeRendererAsync(IMediaRenderer mediaRenderer = null)
        {
            var time = MediaPlayer.Time;

            if (IsStreamlink)
            {
                await MediaPlayer.ChangeRendererAsync(mediaRenderer);
            }
            else
            {
                var streamUrl = await _apiService.GetTokenisedUrlAsync(_token, PlayableContent);
                await MediaPlayer.ChangeRendererAsync(mediaRenderer, streamUrl);
            }

            if (!PlayableContent.IsLive)
            {
                MediaPlayer.Time = time;
            }
        }

        private async Task StartRecordingAsync()
        {
            Logger.Info("Starting recording process...");
            var streamUrl = await _apiService.GetTokenisedUrlAsync(_token, PlayableContent);
            _streamlinkRecordingProcess = _streamlinkLauncher.StartStreamlinkRecording(streamUrl, PlayableContent.Title);
        }

        private void StopRecording()
        {
            Logger.Info("Stopping recording process...");
            CleanupProcess(_streamlinkRecordingProcess);
            _streamlinkRecordingProcess = null;
        }

        private void SetFullScreen()
        {
            Logger.Info("Changing to fullscreen mode...");
            DialogSettings.ResizeMode = ResizeMode.NoResize;
            DialogSettings.WindowState = WindowState.Maximized;
        }

        private void SetWindowed()
        {
            Logger.Info("Changing to windowed mode...");
            DialogSettings.WindowState = WindowState.Normal;
        }

        private void CleanupProcess(Process process)
        {
            if (process != null)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error(ex, $"An error occurred while trying to kill process with id '{process.Id}' and name '{process.ProcessName}'.");
                    }
                }

                process.Dispose();
            }
        }
    }
}