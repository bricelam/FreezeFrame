using System;
using System.IO;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FreezeFrame;

public sealed partial class MainWindow : Window
{
    string _dir;
    string _fileName;

    float _sourceWidth;
    float _sourceHeight;
    double _targetWidth;
    double _targetHeight;

    double _framesPerSecond;
    double _currentPosition;
    CanvasRenderTarget _currentFrame;
    MediaPlayer _player;

    public MainWindow()
        => InitializeComponent();

    async void HandleOpen(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,

            // TODO: Query codecs
            FileTypeFilter = { ".avi", ".mov", ".mp4" }
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        _dir = Path.GetDirectoryName(file.Path);
        _fileName = Path.GetFileNameWithoutExtension(file.Path);

        var imageProperties = await file.Properties.GetImagePropertiesAsync();
        var dateTaken = imageProperties.DateTaken;
        var latitude = imageProperties.Latitude;
        var longitude = imageProperties.Longitude;

        var source = MediaSource.CreateFromStorageFile(file);
        await source.OpenAsync();

        var playbackItem = new MediaPlaybackItem(source);

        var videoProperties = playbackItem.VideoTracks.First().GetEncodingProperties();

        _sourceWidth = videoProperties.Width;
        _sourceHeight = videoProperties.Height;
        UpdateTargetSize();

        _framesPerSecond = (double)videoProperties.FrameRate.Numerator / videoProperties.FrameRate.Denominator;
        _slider.Maximum = source.Duration.Value.TotalSeconds * _framesPerSecond;
        _slider.ThumbToolTipValueConverter = new TimeSpanFormatter(_framesPerSecond);

        _currentFrame?.Dispose();
        _currentFrame = new CanvasRenderTarget(_canvasControl, _sourceWidth, _sourceHeight, 96);

        if (_player is null)
        {
            _player = new MediaPlayer
            {
                IsVideoFrameServerEnabled = true,
                IsMuted = true
            };
            _player.CurrentStateChanged += (sender, args) =>
            {
                if (sender.CurrentState == MediaPlayerState.Playing)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _playButton.Icon = new SymbolIcon(Symbol.Pause);
                        _playButton.Label = "Pause";
                    });
                }
                else
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _playButton.Icon = new SymbolIcon(Symbol.Play);
                        _playButton.Label = "Play";
                    });
                }
            };
            _player.VideoFrameAvailable += (sender, args) =>
            {
                _currentPosition = sender.Position.TotalSeconds * _framesPerSecond;
                sender.CopyFrameToVideoSurface(_currentFrame);
                _canvasControl.Invalidate();
            };
        }

        _player.Source = playbackItem;
        _player.Position = TimeSpan.Zero;
        //_player.StepForwardOneFrame();
        //_player.Position -= TimeSpan.FromSeconds(oneFrame);
    }

    void HandlePlay(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }
        if (_player.CurrentState != MediaPlayerState.Playing)
        {
            _player.Play();
        }
        else
        {
            _player.Pause();
        }
    }

    void HandleDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_currentFrame is null)
            return;

        // TODO: Bind to player instead?
        _slider.Value = _currentPosition;

        args.DrawingSession.DrawImage(_currentFrame, new Rect(0, 0, _targetWidth, _targetHeight));
    }

    async void HandlePhoto(object sender, RoutedEventArgs e)
    {
        // TODO: Save dateTaken, latitude, and longitude
        // TODO: Avoid tearing
        await _currentFrame.SaveAsync(Path.Combine(_dir, _fileName + "." + Math.Floor(_currentPosition) + ".jpg"));
    }

    void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTargetSize();

    void UpdateTargetSize()
    {
        var scale = Math.Min(
            _canvasControl.ActualWidth / _sourceWidth,
            _canvasControl.ActualHeight / _sourceHeight);
        _targetWidth = _sourceWidth * scale;
        _targetHeight = _sourceHeight * scale;
    }

    class TimeSpanFormatter : IValueConverter
    {
        readonly double _framesPerSecond;

        public TimeSpanFormatter(double framesPerSecond)
            => _framesPerSecond = framesPerSecond;

        public object Convert(object value, Type targetType, object parameter, string language)
            => TimeSpan.FromSeconds((double)value / _framesPerSecond).ToString(@"hh\:mm\:ss");

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
