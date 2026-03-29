using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.Models;

public sealed class InpaintingRequest
{
    public required string ImagePath { get; init; }

    public required string ModelPath { get; init; }

    public required BitmapSource MaskImage { get; init; }
}
