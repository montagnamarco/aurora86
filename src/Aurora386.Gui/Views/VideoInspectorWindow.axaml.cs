using System;
using Avalonia.Controls;
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
        Redraw();
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
        image.Width = model.PictureWidth;
        image.Height = model.PictureHeight;
    }
}
