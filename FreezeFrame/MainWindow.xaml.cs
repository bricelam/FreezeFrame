﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
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
    string _dir;
    string _fileName;
    DateTimeOffset _dateTaken;
    Geopoint _geotag;

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
        await source.OpenAsync();

        var playbackItem = new MediaPlaybackItem(source);

        var videoProperties = playbackItem.VideoTracks.First().GetEncodingProperties();

        _sourceWidth = videoProperties.Width;
        _sourceHeight = videoProperties.Height;
        UpdateTargetSize();

        _framesPerSecond = (double)videoProperties.FrameRate.Numerator / videoProperties.FrameRate.Denominator;
        _slider.Maximum = Math.Round(source.Duration.Value.TotalSeconds * _framesPerSecond) - 1.0;
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
            _player.VideoFrameAvailable += async (sender, args) =>
            {
                // TODO: Is there a better way to ensure position is up to date?
                await Task.Yield();

                if (Monitor.TryEnter(_currentFrame))
                {
                    try
                    {
                        _currentPosition = Math.Round(sender.Position.TotalSeconds * _framesPerSecond);
                        sender.CopyFrameToVideoSurface(_currentFrame);
                    }
                    finally
                    {
                        Monitor.Exit(_currentFrame);
                    }

                    _canvasControl.Invalidate();
                }
            };
        }

        _player.Source = playbackItem;
        _player.Position = TimeSpan.Zero;
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
        string path;
        Monitor.Enter(_currentFrame);
        try
        {
            path = Path.Combine(_dir, _fileName + "." + _currentPosition + ".jpg");
            await _currentFrame.SaveAsync(path);
        }
        finally
        {
            Monitor.Exit(_currentFrame);
        }

        var file = await StorageFile.GetFileFromPathAsync(path);
        var imageProperties = await file.Properties.GetImagePropertiesAsync();
        // TODO: Offset using current position?
        imageProperties.DateTaken = _dateTaken;
        await imageProperties.SavePropertiesAsync();
        if (_geotag is not null)
            await GeotagHelper.SetGeotagAsync(file, _geotag);
    }

    void HandleSizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTargetSize();

    void HandleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_player is null)
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
