using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VexPN.Desktop;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(42) };
    private readonly List<BitmapSource> _spinnerFrames = [];
    private int _spinnerFrameIndex;

    public SplashWindow()
    {
        InitializeComponent();
        LoadLogo();
        LoadSpinner();
        _spinnerTimer.Tick += (_, _) => AdvanceSpinnerFrame();
        _spinnerTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _spinnerTimer.Stop();
        base.OnClosed(e);
    }

    private void LoadLogo()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "IMG_8971.PNG");
            if (!File.Exists(path))
                return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 340;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            SplashLogo.Source = bmp;
        }
        catch
        {
            // ignore
        }
    }

    private void LoadSpinner()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Lightness_rotate_36f_cw.gif");
            if (!File.Exists(path))
                return;

            using var fs = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            foreach (var frame in decoder.Frames)
            {
                frame.Freeze();
                _spinnerFrames.Add(frame);
            }

            if (_spinnerFrames.Count > 0)
                LoadingSpinner.Source = _spinnerFrames[0];
        }
        catch
        {
            // ignore
        }
    }

    private void AdvanceSpinnerFrame()
    {
        if (_spinnerFrames.Count == 0)
            return;

        _spinnerFrameIndex = (_spinnerFrameIndex + 1) % _spinnerFrames.Count;
        LoadingSpinner.Source = _spinnerFrames[_spinnerFrameIndex];
    }
}
