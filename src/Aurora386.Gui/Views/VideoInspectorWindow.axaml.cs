using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Aurora386.Gui.ViewModels;

namespace Aurora386.Gui.Views;

/// <summary>
/// The video memory window. Everything about what to show is decided in the
/// view model; this puts the pixels it produced into a bitmap.
///
/// The bitmap is rebuilt rather than updated in place because the picture
/// changes size whenever the stride does, and a stride that can be typed is
/// most of the point: a picture drawn at the wrong one shears diagonally, and
/// trying another value is how the right one is found.
/// </summary>
public partial class VideoInspectorWindow : Window
{
    private WriteableBitmap? _bitmap;

    public VideoInspectorWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private VideoInspectorViewModel? ViewModel => DataContext as VideoInspectorViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is not { } model) return;

        model.PictureChanged += Redraw;

        // Magnifying does not need the picture rebuilt, only stretched, so it
        // has its own event: at ×16 a redraw of two megabytes for every step
        // of the wheel would be felt.
        model.ZoomChanged += ApplyZoom;

        Redraw();
    }

    /// <summary>
    /// Stretches the bitmap to the current magnification.
    ///
    /// The stretch is done by sizing the control rather than by scaling the
    /// bitmap, and the interpolation mode set in the markup is None: this
    /// window exists to show what individual bytes hold, and a smoothed
    /// magnification invents pixels that are not in memory.
    /// </summary>
    private void ApplyZoom()
    {
        if (ViewModel is not { } model) return;
        if (this.FindControl<Image>("MemoryView") is not { } image) return;
        if (model.PictureWidth <= 0) return;

        image.Width = model.PictureWidth * model.Zoom;
        image.Height = model.PictureHeight * model.Zoom;
    }

    private void Redraw()
    {
        if (ViewModel is not { } model) return;
        if (model.PictureWidth <= 0 || model.PictureHeight <= 0) return;

        var image = this.FindControl<Image>("MemoryView");
        if (image is null) return;

        // A new bitmap whenever the shape changes. Reusing one of the wrong
        // size would silently draw the picture into the wrong rows, which is
        // exactly the class of bug this window exists to find.
        if (_bitmap is null
            || _bitmap.PixelSize.Width != model.PictureWidth
            || _bitmap.PixelSize.Height != model.PictureHeight)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new Avalonia.PixelSize(model.PictureWidth, model.PictureHeight),
                new Avalonia.Vector(96, 96),
                PixelFormat.Rgba8888,
                AlphaFormat.Opaque);
        }

        using (var buffer = _bitmap.Lock())
        {
            unsafe
            {
                var destination = (uint*)buffer.Address;
                uint[] source = model.Picture;

                for (int y = 0; y < model.PictureHeight; y++)
                {
                    int rowStart = y * model.PictureWidth;
                    uint* row = destination + y * (buffer.RowBytes / 4);

                    for (int x = 0; x < model.PictureWidth; x++)
                        row[x] = source[rowStart + x];
                }
            }
        }

        image.Source = null;
        image.Source = _bitmap;
        ApplyZoom();
    }

    /// <summary>
    /// The wheel magnifies, with the modifier key every other viewer uses --
    /// plain scrolling still scrolls, because a picture taller than the
    /// window is the normal case here.
    /// </summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (ViewModel is { } model
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Delta.Y > 0) model.ZoomInCommand.Execute(null);
            else if (e.Delta.Y < 0) model.ZoomOutCommand.Execute(null);
            e.Handled = true;
            return;
        }

        base.OnPointerWheelChanged(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (ViewModel is { } model)
        {
            switch (e.Key)
            {
                case Key.OemPlus or Key.Add:
                    model.ZoomInCommand.Execute(null); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract:
                    model.ZoomOutCommand.Execute(null); e.Handled = true; return;
                case Key.D0 or Key.NumPad0:
                    model.ZoomResetCommand.Execute(null); e.Handled = true; return;
                case Key.F5:
                    model.RefreshCommand.Execute(null); e.Handled = true; return;
            }
        }

        base.OnKeyDown(e);
    }
}
