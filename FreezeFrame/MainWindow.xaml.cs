using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace FreezeFrame;

public sealed partial class MainWindow : Window
{
    static readonly Guid VideoRotationProperty = new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1");

    string _dir;
    string _fileName;
    DateTimeOffset _dateTaken;
    Geopoint _geotag;
    uint _orientation;

    double _framesPerSecond;
    TimeSpan _previousPosition = TimeSpan.MinValue;
    double _currentPosition;
    CanvasRenderTarget _currentFrame;
    MediaPlayer _player;

    double _dragHorizontalOffset;
    double _dragVerticalOffset;
    Point _dragStartPosition;

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
        if (file is null)
            return;

        await Open(file);
    }

    async Task Open(StorageFile file)
    {
        _dir = Path.GetDirectoryName(file.Path);
        _fileName = Path.GetFileNameWithoutExtension(file.Path);

        var imageProperties = await file.Properties.GetImagePropertiesAsync();
        _dateTaken = imageProperties.DateTaken;
        _geotag = imageProperties.Latitude.HasValue
            ? new Geopoint(new BasicGeoposition(imageProperties.Latitude.Value, imageProperties.Longitude.Value, 0.0))
            : null;

        var source = MediaSource.CreateFromStorageFile(file);
        try
        {
            await source.OpenAsync();
        }
        catch (Exception ex)
        {
            Debug.Fail(ex.ToString());

            // TODO: Show error
            throw;
        }

        var playbackItem = new MediaPlaybackItem(source);

        var videoProperties = playbackItem.VideoTracks.First().GetEncodingProperties();

        var orientation = (uint)videoProperties.Properties[VideoRotationProperty];
        (_canvasControl.Width, _canvasControl.Height) = orientation == 0u || orientation == 180u
            ? (videoProperties.Width, videoProperties.Height)
            : (videoProperties.Height, videoProperties.Width);
        _scrollViewer.ZoomToFactor(_scrollViewer.MinZoomFactor);
        UpdateMinZoomFactor();

        _framesPerSecond = (double)videoProperties.FrameRate.Numerator / videoProperties.FrameRate.Denominator;
        _slider.Maximum = Math.Round(source.Duration.Value.TotalSeconds * _framesPerSecond) - 1.0;
        _slider.ThumbToolTipValueConverter = new TimeSpanFormatter(_framesPerSecond);

        _orientation = 0u;

        _currentFrame?.Dispose();
        _currentFrame = new CanvasRenderTarget(_canvasControl, (float)_canvasControl.Width, (float)_canvasControl.Height, 96f);

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
            _player.VideoFrameAvailable += async (sender, args) =>
            {
                // HACK: Why isn't this up to date?
                var timer = Stopwatch.StartNew();
                while (sender.Position == _previousPosition
                    && timer.ElapsedMilliseconds < 1000 / _framesPerSecond)
                {
                    await Task.Yield();
                }
                _previousPosition = sender.Position;

                var currentFrame = _currentFrame;
                if (Monitor.TryEnter(currentFrame))
                {
                    try
                    {
                        _currentPosition = Math.Round(sender.Position.TotalSeconds * _framesPerSecond);
                        sender.CopyFrameToVideoSurface(currentFrame);
                    }
                    finally
                    {
                        Monitor.Exit(currentFrame);
                    }

                    _canvasControl.Invalidate();
                }
            };
        }

        _player.Source = playbackItem;
        _previousPosition = TimeSpan.MinValue;
        _player.Position = TimeSpan.Zero;
    }

    void HandlePlay(object sender, RoutedEventArgs e)
    {
        if (_currentFrame is null)
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

        var transform = new Transform2DEffect
        {
            Source = _currentFrame,
            TransformMatrix = Matrix3x2.CreateRotation(MathF.PI * _orientation / 180f)
        };
        var bounds = transform.GetBounds(sender);
        args.DrawingSession.DrawImage(transform, new Rect(0, 0, bounds.Width, bounds.Height), bounds);
    }

    async void HandlePhoto(object sender, RoutedEventArgs e)
    {
        string path;
        var currentFrame = _currentFrame;
        Monitor.Enter(currentFrame);
        try
        {
            path = Path.Combine(_dir, _fileName + "." + _currentPosition + ".jpg");
            await currentFrame.SaveAsync(path, CanvasBitmapFileFormat.Auto, 0.95f);
        }
        finally
        {
            Monitor.Exit(currentFrame);
        }

        // TODO: Save orientation
        var file = await StorageFile.GetFileFromPathAsync(path);
        var imageProperties = await file.Properties.GetImagePropertiesAsync();
        // TODO: Offset using current position?
        imageProperties.DateTaken = _dateTaken;
        await imageProperties.SavePropertiesAsync();
        if (_geotag is not null)
            await GeotagHelper.SetGeotagAsync(file, _geotag);
    }

    void HandleRotate(object sender, RoutedEventArgs e)
    {
        if (_currentFrame is null)
            return;

        _orientation += 90u;
        if (_orientation == 360u)
            _orientation = 0u;

        (_canvasControl.Width, _canvasControl.Height) = (_canvasControl.Height, _canvasControl.Width);
        UpdateMinZoomFactor();
    }

    void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMinZoomFactor();

    void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_currentFrame is null)
            return;

        switch (e.Key)
        {
            case VirtualKey.Left:
                // NB: StepBackwardOneFrame ignores FPS
                _player.Position = TimeSpan.FromSeconds((_currentPosition - 1.0) / _framesPerSecond);
                break;

            case VirtualKey.Right:
                _player.StepForwardOneFrame();
                break;
        }
    }

    void HandleDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.IsGlyphVisible = false;
        e.DragUIOverride.Caption = "Open";
    }

    async void HandleDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        var storageItems = await e.DataView.GetStorageItemsAsync();

        await Open((StorageFile)storageItems[0]);
    }

    void HandlePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            _scrollViewer.ScrollToHorizontalOffset(_scrollViewer.HorizontalOffset - e.GetCurrentPoint(_scrollViewer).Properties.MouseWheelDelta);
            e.Handled = true;
        }
    }

    void HandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(_scrollViewer);
        if (currentPoint.Properties.IsLeftButtonPressed)
        {
            _dragStartPosition = currentPoint.Position;
            _dragHorizontalOffset = _scrollViewer.HorizontalOffset;
            _dragVerticalOffset = _scrollViewer.VerticalOffset;
            _canvasControl.PointerMoved += HandlePointerMoved;
        }
    }

    void HandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var currentPoint = e.GetCurrentPoint(_scrollViewer);
        _scrollViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - (currentPoint.Position.X - _dragStartPosition.X));
        _scrollViewer.ScrollToVerticalOffset(_dragVerticalOffset - (currentPoint.Position.Y - _dragStartPosition.Y));
    }

    void HandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_scrollViewer).Properties.IsLeftButtonPressed)
            _canvasControl.PointerMoved -= HandlePointerMoved;
    }

    void UpdateMinZoomFactor()
    {
        if (double.IsNaN(_canvasControl.Width))
            return;

        var previousMinZoomFactor = _scrollViewer.MinZoomFactor;

        _scrollViewer.MinZoomFactor = (float)Math.Min(
            Math.Min(
                _scrollViewer.ViewportWidth / _canvasControl.Width,
                _scrollViewer.ViewportHeight / _canvasControl.Height),
            1.0);

        if (_scrollViewer.ZoomFactor == previousMinZoomFactor)
            _scrollViewer.ZoomToFactor(_scrollViewer.MinZoomFactor);
    }

    class TimeSpanFormatter : IValueConverter
    {
        readonly double _framesPerSecond;

        public TimeSpanFormatter(double framesPerSecond)
            => _framesPerSecond = framesPerSecond;

        public object Convert(object value, Type targetType, object parameter, string language)
            => "Frame " + (double)value + " (" + TimeSpan.FromSeconds((double)value / _framesPerSecond).ToString(@"hh\:mm\:ss\.fff") + ")";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
