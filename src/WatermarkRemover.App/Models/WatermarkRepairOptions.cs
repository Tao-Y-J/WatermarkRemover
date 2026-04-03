using System.Windows.Media.Imaging;
using OpenCvSharp;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App.Models;

public sealed record WatermarkRepairOptions(
    int MinPaddingX,
    int MinPaddingY,
    double PaddingXScale,
    double PaddingYScale,
    int MaskDilateKernelSize,
    int MaskDilateIterations,
    double FeatherSigma)
{
    public string PresetName { get; init; } = "Custom";

    public static WatermarkRepairOptions Balanced { get; } = new(
        MinPaddingX: 56,
        MinPaddingY: 72,
        PaddingXScale: 1.2,
        PaddingYScale: 1.8,
        MaskDilateKernelSize: 3,
        MaskDilateIterations: 1,
        FeatherSigma: 2.4)
    {
        PresetName = "Balanced"
    };

    public static WatermarkRepairOptions CoverageBoost { get; } = new(
        MinPaddingX: 72,
        MinPaddingY: 96,
        PaddingXScale: 1.4,
        PaddingYScale: 2.0,
        MaskDilateKernelSize: 5,
        MaskDilateIterations: 1,
        FeatherSigma: 2.8)
    {
        PresetName = "CoverageBoost"
    };

    public static WatermarkRepairOptions Conservative { get; } = new(
        MinPaddingX: 60,
        MinPaddingY: 84,
        PaddingXScale: 1.15,
        PaddingYScale: 2.1,
        MaskDilateKernelSize: 3,
        MaskDilateIterations: 1,
        FeatherSigma: 1.9)
    {
        PresetName = "Conservative"
    };

    public static WatermarkRepairOptions NarrowReview { get; } = new(
        MinPaddingX: 44,
        MinPaddingY: 64,
        PaddingXScale: 0.95,
        PaddingYScale: 1.35,
        MaskDilateKernelSize: 3,
        MaskDilateIterations: 1,
        FeatherSigma: 1.6)
    {
        PresetName = "NarrowReview"
    };

    public static WatermarkRepairOptions WideContext { get; } = new(
        MinPaddingX: 92,
        MinPaddingY: 128,
        PaddingXScale: 1.8,
        PaddingYScale: 2.7,
        MaskDilateKernelSize: 5,
        MaskDilateIterations: 1,
        FeatherSigma: 3.3)
    {
        PresetName = "WideContext"
    };

    public static WatermarkRepairOptions Default { get; } = Balanced;

    public static WatermarkRepairOptions ResolveAutoPreset(double confidence)
    {
        return ResolveAutoPreset(confidence, profile: null);
    }

    public static WatermarkRepairOptions ResolveAutoPreset(
        double confidence,
        BitmapSource? maskImage,
        int sourceWidth,
        int sourceHeight)
    {
        return ResolveAutoPreset(confidence, BuildAutoRoutingProfile(maskImage, sourceWidth, sourceHeight));
    }

    public static WatermarkRepairOptions ResolveAutoPreset(
        double confidence,
        Mat mask,
        OpenCvSharp.Size sourceSize)
    {
        return ResolveAutoPreset(confidence, BuildAutoRoutingProfile(mask, sourceSize));
    }

    public static WatermarkRepairOptions ResolveAutoPreset(
        double confidence,
        AutoRoutingProfile? profile)
    {
        if (confidence < WatermarkDetectionPolicy.MinimumAutoApplyConfidence)
        {
            return NarrowReview;
        }

        if (profile is not null
            && confidence >= 0.55
            && profile.AreaRatio <= 0.0090
            && profile.WidthRatio <= 0.52
            && profile.HeightRatio <= 0.045
            && profile.AspectRatio >= 4.40
            && profile.FillRatio <= 0.42
            && profile.BottomInsetRatio <= 0.16)
        {
            return WideContext;
        }

        return Balanced;
    }

    public static AutoRoutingProfile? BuildAutoRoutingProfile(
        BitmapSource? maskImage,
        int sourceWidth,
        int sourceHeight)
    {
        if (maskImage is null || sourceWidth <= 0 || sourceHeight <= 0)
        {
            return null;
        }

        using var mask = CreateBinaryMask(maskImage, new OpenCvSharp.Size(sourceWidth, sourceHeight));
        return BuildAutoRoutingProfile(mask, new OpenCvSharp.Size(sourceWidth, sourceHeight));
    }

    public static AutoRoutingProfile? BuildAutoRoutingProfile(Mat mask, OpenCvSharp.Size sourceSize)
    {
        if (mask.Empty() || sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            return null;
        }

        using var normalizedMask = NormalizeMask(mask, sourceSize);
        var maskPixelCount = Cv2.CountNonZero(normalizedMask);
        if (maskPixelCount == 0)
        {
            return null;
        }

        Cv2.FindContours(
            normalizedMask,
            out var contours,
            out _,
            RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        OpenCvSharp.Rect? bounds = null;
        foreach (var contour in contours)
        {
            var contourBounds = Cv2.BoundingRect(contour);
            bounds = bounds is null ? contourBounds : Union(bounds.Value, contourBounds);
        }

        if (bounds is null)
        {
            return null;
        }

        return new AutoRoutingProfile(
            sourceSize.Width,
            sourceSize.Height,
            maskPixelCount,
            bounds.Value.Left,
            bounds.Value.Top,
            bounds.Value.Width,
            bounds.Value.Height);
    }

    private static Mat NormalizeMask(Mat mask, OpenCvSharp.Size sourceSize)
    {
        using var gray = mask.Channels() switch
        {
            1 => mask.Clone(),
            3 => new Mat(),
            4 => new Mat(),
            _ => throw new InvalidOperationException("Unsupported mask format."),
        };

        if (mask.Channels() == 3)
        {
            Cv2.CvtColor(mask, gray, ColorConversionCodes.BGR2GRAY);
        }
        else if (mask.Channels() == 4)
        {
            Cv2.CvtColor(mask, gray, ColorConversionCodes.BGRA2GRAY);
        }

        using var resized = new Mat();
        if (gray.Size() != sourceSize)
        {
            Cv2.Resize(gray, resized, sourceSize, 0, 0, InterpolationFlags.Nearest);
        }
        else
        {
            gray.CopyTo(resized);
        }

        var normalized = new Mat();
        Cv2.Threshold(resized, normalized, 32, 255, ThresholdTypes.Binary);
        return normalized;
    }

    private static Mat CreateBinaryMask(BitmapSource maskImage, OpenCvSharp.Size targetSize)
    {
        var grayMask = EnsureGray8Mask(maskImage);
        var stride = Math.Max(1, (grayMask.PixelWidth * grayMask.Format.BitsPerPixel + 7) / 8);
        var pixelBuffer = new byte[stride * grayMask.PixelHeight];
        grayMask.CopyPixels(pixelBuffer, stride, 0);

        using var sourceMask = new Mat(grayMask.PixelHeight, grayMask.PixelWidth, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, sourceMask.Data, pixelBuffer.Length);

        return NormalizeMask(sourceMask, targetSize);
    }

    private static BitmapSource EnsureGray8Mask(BitmapSource source)
    {
        if (source.Format == System.Windows.Media.PixelFormats.Gray8)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = System.Windows.Media.PixelFormats.Gray8;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    private static OpenCvSharp.Rect Union(OpenCvSharp.Rect left, OpenCvSharp.Rect right)
    {
        var x = Math.Min(left.Left, right.Left);
        var y = Math.Min(left.Top, right.Top);
        var rightEdge = Math.Max(left.Right, right.Right);
        var bottomEdge = Math.Max(left.Bottom, right.Bottom);
        return new OpenCvSharp.Rect(x, y, Math.Max(1, rightEdge - x), Math.Max(1, bottomEdge - y));
    }

    public sealed record AutoRoutingProfile(
        int SourceWidth,
        int SourceHeight,
        int MaskPixelCount,
        int BoundsLeft,
        int BoundsTop,
        int BoundsWidth,
        int BoundsHeight)
    {
        public double AreaRatio => MaskPixelCount / (double)Math.Max(1, SourceWidth * SourceHeight);

        public double WidthRatio => BoundsWidth / (double)Math.Max(1, SourceWidth);

        public double HeightRatio => BoundsHeight / (double)Math.Max(1, SourceHeight);

        public double AspectRatio => BoundsWidth / (double)Math.Max(1, BoundsHeight);

        public double FillRatio => MaskPixelCount / (double)Math.Max(1, BoundsWidth * BoundsHeight);

        public double BottomInsetRatio => (SourceHeight - (BoundsTop + BoundsHeight)) / (double)Math.Max(1, SourceHeight);
    }
}
