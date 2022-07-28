using System;
using System.Linq;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace FreezeFrame;

public sealed partial class MainWindow : Window
{
    float _sourceWidth;
    float _sourceHeight;

    TimeSpan _currentPosition;
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

        _openGrid.Visibility = Visibility.Collapsed;
        _canvasControl.Visibility = Visibility.Visible;

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
        var framesPerSecond = (double)videoProperties.FrameRate.Numerator / videoProperties.FrameRate.Denominator;
        var oneFrame = 1 / framesPerSecond;

        // TODO: Use _slider.ThumbToolTipValueConverter to show a formatted TimeSpan
        _slider.StepFrequency = oneFrame;
        _slider.Maximum = source.Duration.Value.TotalSeconds;

        _currentFrame?.Dispose();
        _currentFrame = new CanvasRenderTarget(_canvasControl, _sourceWidth, _sourceHeight, 96);

        if (_player is null)
        {
            _player = new MediaPlayer
            {
                IsVideoFrameServerEnabled = true,
                IsMuted = true
            };
            _player.VideoFrameAvailable += (sender, args) =>
            {
                _currentPosition = sender.Position;
                sender.CopyFrameToVideoSurface(_currentFrame);
                _canvasControl.Invalidate();
            };
        }

        _player.Source = playbackItem;
        _player.Position = TimeSpan.Zero;
    }

    async void HandlePlay(object sender, RoutedEventArgs e)
    {
        _playButton.Icon = new SymbolIcon(Symbol.Pause);
        _playButton.Label = "Pause";

        _player.Play();
        //_player.StepForwardOneFrame();
        //_player.Position -= TimeSpan.FromSeconds(oneFrame);
    }

    void HandleDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_currentFrame is null)
            return;

        // TODO: Bind to player instead?
        _slider.Value = _currentPosition.TotalSeconds;

        // TODO: Cache target dimmensions
        args.DrawingSession.DrawImage(_currentFrame, new Rect(0, 0, sender.ActualWidth, sender.ActualWidth * _sourceHeight / _sourceWidth));

    }

    async void HandlePhoto(object sender, RoutedEventArgs e)
    {
        // TODO: Save dateTaken, latitude, and longitude
        // TODO: Avoid tearing
        await _currentFrame.SaveAsync(@"C:\Users\brice\OneDrive\Desktop\Test.jpg");
    }
}
