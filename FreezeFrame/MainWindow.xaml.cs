using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using FreezeFrame.Properties;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
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
using Windows.UI.Core;
using Windows.Win32.Foundation;
using WinRT.Interop;
using static Windows.Win32.PInvoke;
using Icon = System.Drawing.Icon;

namespace FreezeFrame;

public sealed partial class MainWindow : Window
{
    static readonly Guid VideoRotationProperty = new("c380465d-2271-428c-9b83-ecea3b4a85c1");

    // TODO: Query codecs
    static readonly HashSet<string> _knownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".3g2", ".3gp", ".3gp2", ".3gpp", ".asf", ".avi", ".dvr-ms", ".m2t", ".m2ts", ".m4v", ".mkv", ".mod", ".mov", ".mp2v", ".mp4", ".mp4v", ".mpa", ".mpeg", ".mpg", ".mts", ".tod", ".tts", ".uvu", ".vob", ".webm", ".wm", ".wmv" };

    string? _dir;
    string? _fileName;
    DateTimeOffset _dateTaken;
    Geopoint? _geotag;
    float _width;
    float _height;
    uint _orientation;

    double _framesPerSecond;
    double _finalFrame;
    TimeSpan _previousPosition = TimeSpan.MinValue;
    double _currentPosition;
    CanvasRenderTarget? _currentFrame;
    MediaPlayer? _player;

    double _dragHorizontalOffset;
    double _dragVerticalOffset;
    Point _dragStartPosition;

    bool _rendering;
    readonly Stopwatch _rateLimitTimer = Stopwatch.StartNew();

    public MainWindow()
    {
        InitializeComponent();

        Title = "Freeze Frame";

        var handle = (HWND)WindowNative.GetWindowHandle(this);

        // Set icon
        using var stream = new MemoryStream(Resources.FreezeFrameIcon);
        var smallIcon = new Icon(stream, 16, 16);
        SendMessage(handle, WM_SETICON, ICON_SMALL, smallIcon.Handle);
        stream.Seek(0, SeekOrigin.Begin);
        var bigIcon = new Icon(stream, 32, 32);
        SendMessage(handle, WM_SETICON, ICON_BIG, bigIcon.Handle);
    }

    async void HandleOpen(object sender, RoutedEventArgs e)
        => await Open();

    async Task Open()
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

    async Task Open(StorageFile file)
    {
        _dir = Path.GetDirectoryName(file.Path);
        _fileName = Path.GetFileNameWithoutExtension(file.Path);

        var basicProperties = await file.GetBasicPropertiesAsync();
        _dateTaken = basicProperties.ItemDate;
        _geotag = await GeotagHelper.GetGeotagAsync(file);

        var source = MediaSource.CreateFromStorageFile(file);
        await source.OpenAsync();

        Title = _fileName + " - Freeze Frame";
        _welcome.Visibility = Visibility.Collapsed;

        var playbackItem = new MediaPlaybackItem(source);

        var videoProperties = playbackItem.VideoTracks[0].GetEncodingProperties();

        var orientation = (uint)videoProperties.Properties[VideoRotationProperty];
        (_canvasControl.Width, _canvasControl.Height) = orientation == 0u || orientation == 180u
            ? (videoProperties.Width, videoProperties.Height)
            : (videoProperties.Height, videoProperties.Width);
        _width = (float)_canvasControl.Width;
        _height = (float)_canvasControl.Height;
        _scrollViewer.ZoomToFactor(_scrollViewer.MinZoomFactor);
        UpdateMinZoomFactor();

        _framesPerSecond = (double)videoProperties.FrameRate.Numerator / videoProperties.FrameRate.Denominator;
        _finalFrame = Math.Round(source.Duration!.Value.TotalSeconds * _framesPerSecond) - 1.0;
        _slider.Maximum = _finalFrame;
        _slider.ThumbToolTipValueConverter = new TimeSpanFormatter(_framesPerSecond);

        _orientation = 0u;

        _currentFrame?.Dispose();
        _currentFrame = null;

        if (_player is null)
        {
            _player = new MediaPlayer
            {
                IsVideoFrameServerEnabled = true,
                IsMuted = true
            };
            _player.CurrentStateChanged += (sender, args) =>
            {
                if (_player.CurrentState == MediaPlayerState.Playing)
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
                if (_rendering)
                {
                    Debug.WriteLine("Too fast!");

                    return;
                }

                _rendering = true;
                try
                {
                    // HACK: Why isn't this up to date?
                    while (_player.Position == _previousPosition)
                    {
                        await Task.Yield();
                    }
                    _previousPosition = _player.Position;

                    var canvasDevice = CanvasDevice.GetSharedDevice();
                    var position = _player.Position;

                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        _currentFrame ??= new CanvasRenderTarget(canvasDevice, _width, _height, 96f);

                        _player.CopyFrameToVideoSurface(_currentFrame);

                        _currentPosition = Math.Round(position.TotalSeconds * _framesPerSecond);

                        _canvasControl.Invalidate();
                    });
                }
                finally
                {
                    _rendering = false;
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
            return;

        if (_player!.CurrentState != MediaPlayerState.Playing)
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

        if (_slider.Value != _currentPosition)
        {
            _slider.ValueChanged -= HandleSeek;
            _slider.Value = _currentPosition;
            _slider.ValueChanged += HandleSeek;
        }

        var ds = args.DrawingSession;

        if (_orientation == 0u)
        {
            ds.DrawImage(_currentFrame);

            return;
        }

        var transform = new Transform2DEffect
        {
            Source = _currentFrame,
            TransformMatrix = Matrix3x2.CreateRotation(MathF.PI * _orientation / 180f)
        };

        var bounds = transform.GetBounds(ds);
        ds.DrawImage(transform, new Rect(0, 0, bounds.Width, bounds.Height), bounds);
    }

    void HandleSeek(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_currentFrame is null)
            return;

        _player!.Pause();
        _player.Position = TimeSpan.FromSeconds(e.NewValue / _framesPerSecond);
    }

    async void HandlePhoto(object sender, RoutedEventArgs e)
        => await SavePhotoAsync();

    async Task SavePhotoAsync()
    {
        if (_currentFrame is null)
            return;

        var path = Path.Combine(_dir!, _fileName + "." + _currentPosition + ".jpg");
        await _currentFrame.SaveAsync(path, CanvasBitmapFileFormat.Auto, 0.95f);

        using (var stream = await FileRandomAccessStream.OpenAsync(path, FileAccessMode.ReadWrite))
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var encoder = await BitmapEncoder.CreateForTranscodingAsync(stream, decoder);
            await encoder.BitmapProperties.SetPropertiesAsync(
                new Dictionary<string, BitmapTypedValue>
                {
                    [SystemProperties.Photo.DateTaken] = new BitmapTypedValue(
                        _dateTaken + TimeSpan.FromSeconds(_currentPosition / _framesPerSecond),
                        PropertyType.DateTime),
                    [SystemProperties.Photo.Orientation] = new BitmapTypedValue(
                        _orientation switch
                        {
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

    async void HandleTips(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Tips",
            Content = XamlReader.Load(@"
              <TextBlock xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
                • Step through the video using <Bold>Left</Bold> and <Bold>Right</Bold><LineBreak />
                • Zoom using <Bold>Ctrl</Bold> and the mouse wheel<LineBreak/>
                • Click and drag to pan<LineBreak/>
                • Pictures are saved next to the video file<LineBreak />
                • Go to a specific frame using <Bold>Ctrl+G</Bold>
              </TextBlock>
            "),
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close
        };

        await dialog.ShowAsync();
    }

    void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateMinZoomFactor();

    async void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_currentFrame is null)
            return;

        switch (e.Key)
        {
            case VirtualKey.Left:
                if (!IsRateLimited())
                    _player!.StepBackwardOneFrame();
                break;

            case VirtualKey.Right:
                if (!IsRateLimited())
                    _player!.StepForwardOneFrame();
                break;

            case VirtualKey.G when IsControlDown():
                _player!.Pause();
                var frameNumberBox = new NumberBox
                {
                    Header = "Frame number",
                    Value = _currentPosition,
                    Minimum = 0.0,
                    Maximum = _finalFrame
                };
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot,
                    Title = "Go to frame",
                    Content = frameNumberBox,
                    PrimaryButtonText = "Go to",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary
                };
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    _player.Position = TimeSpan.FromSeconds(frameNumberBox.Value / _framesPerSecond);
                }
                break;

            case VirtualKey.O when IsControlDown():
                _player!.Pause();
                await Open();
                break;

            case VirtualKey.S when IsControlDown():
                await SavePhotoAsync();
                break;
        }

        static bool IsControlDown()
            => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
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
        {
            _canvasControl.PointerMoved -= HandlePointerMoved;

            return;
        }

        var currentPoint = e.GetCurrentPoint(_scrollViewer);
        _scrollViewer.ScrollToHorizontalOffset(_dragHorizontalOffset - (currentPoint.Position.X - _dragStartPosition.X));
        _scrollViewer.ScrollToVerticalOffset(_dragVerticalOffset - (currentPoint.Position.Y - _dragStartPosition.Y));
    }

    void UpdateMinZoomFactor()
    {
        if (double.IsNaN(_canvasControl.Width))
            return;

        var previousMinZoomFactor = _scrollViewer.MinZoomFactor;


        _scrollViewer.MinZoomFactor = (float)Math.Max(
            Math.Min(
                Math.Min(
                    _scrollViewer.ViewportWidth / _canvasControl.Width,
                    _scrollViewer.ViewportHeight / _canvasControl.Height),
                1.0),
            0.1);

        if (_scrollViewer.ZoomFactor == previousMinZoomFactor)
            _scrollViewer.ZoomToFactor(_scrollViewer.MinZoomFactor);
    }

    bool IsRateLimited()
    {
        if (_rateLimitTimer.Elapsed.TotalSeconds < 1.0 / _framesPerSecond)
        {
            return true;
        }

        _rateLimitTimer.Restart();

        return false;
    }

    partial class TimeSpanFormatter : IValueConverter
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
