using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DotNext.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;
using static Windows.Win32.PInvoke;

namespace FreezeFrame;

public sealed partial class MainWindow : Window
{
    static readonly Guid VideoRotationProperty = new Guid("c380465d-2271-428c-9b83-ecea3b4a85c1");

    // TODO: Query codecs
    static readonly HashSet<string> _knownExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".3g2", ".3gp", ".3gp2", ".3gpp", ".asf", ".avi", ".dvr-ms", ".m2t", ".m2ts", ".m4v", ".mkv", ".mod", ".mov", ".mp2v", ".mp4", ".mp4v", ".mpa", ".mpeg", ".mpg", ".mts", ".tod", ".tts", ".uvu", ".vob", ".webm", ".wm", ".wmv" };

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

    AsyncReaderWriterLock _lock = new AsyncReaderWriterLock();

    bool _ignoreSeek;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Freeze Frame";

        var handle = (HWND)WindowNative.GetWindowHandle(this);

        // Remove icon
        var styleEx = GetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        SetWindowLong(handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, styleEx | (int)WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME);
        SendMessage(handle, WM_SETICON, ICON_SMALL, IntPtr.Zero);
        SendMessage(handle, WM_SETICON, ICON_BIG, IntPtr.Zero);
    }

    async Task Open(StorageFile file)
    {
        _dir = Path.GetDirectoryName(file.Path);
        _fileName = Path.GetFileNameWithoutExtension(file.Path);

        var imageProperties = await file.Properties.GetImagePropertiesAsync();
        _dateTaken = imageProperties.DateTaken;
        _geotag = await GeotagHelper.GetGeotagAsync(file);

        var source = MediaSource.CreateFromStorageFile(file);
        await source.OpenAsync();

        Title = _fileName + " - Freeze Frame";

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
                var timer = Stopwatch.StartNew();

                using (await _lock.AcquireWriteLockAsync(CancellationToken.None))
                {
                    // HACK: Why isn't this up to date?
                    while (sender.Position == _previousPosition
                        && timer.ElapsedMilliseconds < 1000 / _framesPerSecond)
                    {
                        await Task.Yield();
                    }
                    _previousPosition = sender.Position;

                    _currentPosition = Math.Round(sender.Position.TotalSeconds * _framesPerSecond);
                    sender.CopyFrameToVideoSurface(_currentFrame);
                }

                _canvasControl.Invalidate();
            };
        }

        _player.Source = playbackItem;
        _previousPosition = TimeSpan.MinValue;
        _player.Position = TimeSpan.Zero;
    }

    async void HandlePlay(object sender, RoutedEventArgs e)
    {
        if (_currentFrame is null)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };

            foreach (var knownExtension in _knownExtensions)
                picker.FileTypeFilter.Add(knownExtension);

            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await Open(file);
        }
        else if (_player.CurrentState != MediaPlayerState.Playing)
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

        _ignoreSeek = true;
        _slider.Value = _currentPosition;

        var transform = new Transform2DEffect
        {
            Source = _currentFrame,
            TransformMatrix = Matrix3x2.CreateRotation(MathF.PI * _orientation / 180f)
        };

        using (_lock.AcquireReadLock())
        {
            var bounds = transform.GetBounds(sender);
            args.DrawingSession.DrawImage(transform, new Rect(0, 0, bounds.Width, bounds.Height), bounds);
        }
    }

    void HandleSeek(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_currentFrame is null
            || _ignoreSeek)
        {
            _ignoreSeek = false;

            return;
        }

        _player.Pause();
        _player.Position = TimeSpan.FromSeconds(e.NewValue / _framesPerSecond);
    }

    async void HandlePhoto(object sender, RoutedEventArgs e)
    {
        if (_currentFrame is null)
            return;

        string path;
        using (await _lock.AcquireReadLockAsync(CancellationToken.None))
        {
            path = Path.Combine(_dir, _fileName + "." + _currentPosition + ".jpg");
            await _currentFrame.SaveAsync(path, CanvasBitmapFileFormat.Auto, 0.95f);
        }

        using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite))
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(stream, decoder);
            await encoder.BitmapProperties.SetPropertiesAsync(
                new Dictionary<string, BitmapTypedValue>
                {
                    [SystemProperties.Photo.DateTaken] = new BitmapTypedValue(_dateTaken, PropertyType.DateTime),
                    [SystemProperties.Photo.Orientation] = new BitmapTypedValue(
                        _orientation switch
                        {
                            // TODO: Is this wrong? Account for the existing value?
                            // NB: Values don't align
                            0u => PhotoOrientation.Normal,
                            90u => PhotoOrientation.Rotate270,
                            180u => PhotoOrientation.Rotate180,
                            270u => PhotoOrientation.Rotate90,
                            _ => throw new Exception("Inconceivable!")
                        },
                        PropertyType.UInt16)
                });
            await encoder.FlushAsync();
        }

        if (_geotag is not null)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await GeotagHelper.SetGeotagAsync(file, _geotag);
        }
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
                _player.Pause();
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
        var storageItem = (StorageFile)storageItems[0];
        if (!_knownExtensions.Contains(Path.GetExtension(storageItem.Path)))
            return;

        await Open(storageItem);
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
        if (!e.GetCurrentPoint(_scrollViewer).Properties.IsLeftButtonPressed)
            _canvasControl.PointerMoved -= HandlePointerMoved;

        var currentPoint = e.GetCurrentPoint(_scrollViewer);
        _scrollViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - (currentPoint.Position.X - _dragStartPosition.X));
        _scrollViewer.ScrollToVerticalOffset(_dragVerticalOffset - (currentPoint.Position.Y - _dragStartPosition.Y));
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
            => value + " (" + TimeSpan.FromSeconds((double)value / _framesPerSecond).ToString(@"hh\:mm\:ss") + ")";

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
