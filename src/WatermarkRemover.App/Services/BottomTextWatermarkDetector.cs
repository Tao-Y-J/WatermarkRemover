using System.IO;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace WatermarkRemover.App.Services;

public sealed class BottomTextWatermarkDetector : IBottomTextWatermarkDetector
{
    private readonly BottomTextWatermarkDetectorOptions _options;

    public BottomTextWatermarkDetector(BottomTextWatermarkDetectorOptions? options = null)
    {
        _options = options ?? BottomTextWatermarkDetectorOptions.Default;
    }

    public BottomTextWatermarkDetectorOptions Options => _options;

    public Task<BottomTextWatermarkDetectionResult> DetectAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException("未找到待检测图片。", imagePath);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
            if (source.Empty())
            {
                throw new InvalidOperationException("图片加载失败，无法执行底部文字水印检测。");
            }

            using var artifacts = DetectArtifacts(source, _options, cancellationToken);
            return new BottomTextWatermarkDetectionResult(
                CreateBitmapSource(artifacts.Mask),
                CreateBitmapSource(artifacts.DebugOverlay),
                artifacts.Confidence);
        }, cancellationToken);
    }

    private static DetectionArtifacts DetectArtifacts(
        Mat source,
        BottomTextWatermarkDetectorOptions options,
        CancellationToken cancellationToken)
    {
        var searchRegion = BuildSearchRegion(source.Size());
        using var roi = new Mat(source, searchRegion);
        using var gray = new Mat();
        using var hsv = new Mat();
        using var saturation = new Mat();
        using var value = new Mat();
        using var topHat = new Mat();
        using var topHatMask = new Mat();
        using var brightMask = new Mat();
        using var lowSaturationMask = new Mat();
        using var brightTextMask = new Mat();
        using var textLikeMask = new Mat();
        using var gradientX16 = new Mat();
        using var gradientX = new Mat();
        using var strokeMask = new Mat();
        using var candidateMask = new Mat();
        using var cleanedMask = new Mat();
        using var filteredMask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        using var mergedLineMask = new Mat();
        using var topHatKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(17, 17));
        using var cleanKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        using var componentCloseKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(9, 3));
        using var lineCloseKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new OpenCvSharp.Size(Math.Max(31, EnsureOdd(roi.Width / 7)), 5));
        using var lineDilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));

        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.ExtractChannel(hsv, saturation, 1);
        Cv2.ExtractChannel(hsv, value, 2);

        Cv2.MorphologyEx(gray, topHat, MorphTypes.TopHat, topHatKernel);
        Cv2.Sobel(gray, gradientX16, MatType.CV_16S, 1, 0, 3);
        Cv2.ConvertScaleAbs(gradientX16, gradientX);

        Cv2.Threshold(value, brightMask, 132, 255, ThresholdTypes.Binary);
        Cv2.Threshold(saturation, lowSaturationMask, 118, 255, ThresholdTypes.BinaryInv);
        Cv2.BitwiseAnd(brightMask, lowSaturationMask, brightTextMask);
        Cv2.Threshold(topHat, topHatMask, 10, 255, ThresholdTypes.Binary);
        Cv2.Threshold(gradientX, strokeMask, 18, 255, ThresholdTypes.Binary);

        Cv2.BitwiseOr(topHatMask, strokeMask, candidateMask);
        Cv2.BitwiseAnd(candidateMask, lowSaturationMask, textLikeMask);
        textLikeMask.CopyTo(candidateMask);
        Cv2.MorphologyEx(candidateMask, cleanedMask, MorphTypes.Open, cleanKernel);
        Cv2.MorphologyEx(cleanedMask, cleanedMask, MorphTypes.Close, componentCloseKernel);

        cancellationToken.ThrowIfCancellationRequested();

        Cv2.FindContours(cleanedMask, out var componentContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var componentBounds = new List<OpenCvSharp.Rect>();
        foreach (var contour in componentContours)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bounds = Cv2.BoundingRect(contour);
            if (!IsValidComponent(contour, bounds, roi.Size(), value, saturation))
            {
                continue;
            }

            Cv2.DrawContours(filteredMask, [contour], -1, Scalar.White, -1);
            componentBounds.Add(bounds);
        }

        Cv2.MorphologyEx(filteredMask, mergedLineMask, MorphTypes.Close, lineCloseKernel);
        Cv2.Dilate(mergedLineMask, mergedLineMask, lineDilateKernel, iterations: 1);

        Cv2.FindContours(mergedLineMask, out var lineContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        LineCandidate? bestCandidate = null;

        foreach (var contour in lineContours)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bounds = Cv2.BoundingRect(contour);
            var componentCount = componentBounds.Count(component => HasIntersection(component, bounds));
            if (!IsValidLine(bounds, componentCount, roi.Size(), source.Size(), searchRegion))
            {
                continue;
            }

            var expandedBounds = ExpandRect(
                bounds,
                roi.Size(),
                Math.Max(options.MinExpandX, (int)Math.Round(bounds.Width * options.ExpandWidthScale)),
                Math.Max(options.MinExpandY, (int)Math.Round(bounds.Height * options.ExpandHeightScale)));

            var score = ScoreLine(bounds, componentCount, source.Size(), searchRegion);
            var confidence = CalculateConfidence(bounds, componentCount, source.Size(), searchRegion);
            var candidate = new LineCandidate(bounds, expandedBounds, componentCount, score, confidence, IsFallback: false);

            if (bestCandidate is null || candidate.Score > bestCandidate.Score)
            {
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            bestCandidate = TryCreateFallbackCandidate(cleanedMask, roi.Size(), options);
        }

        using var lineMask = new Mat(roi.Height, roi.Width, MatType.CV_8UC1, Scalar.Black);
        if (bestCandidate is not null)
        {
            var sourceMask = Cv2.CountNonZero(filteredMask) > 0 ? filteredMask : cleanedMask;
            if (bestCandidate.IsFallback)
            {
                PopulateFallbackLineMask(
                    sourceMask,
                    brightTextMask,
                    strokeMask,
                    bestCandidate.ExpandedBounds,
                    lineMask);
            }
            else
            {
                using var filteredRegion = new Mat(sourceMask, bestCandidate.ExpandedBounds);
                using var lineRegion = new Mat(lineMask, bestCandidate.ExpandedBounds);
                filteredRegion.CopyTo(lineRegion);

                if (options.IncludeBrightTextUnion)
                {
                    using var brightTextRegion = new Mat(brightTextMask, bestCandidate.ExpandedBounds);
                    Cv2.BitwiseOr(lineRegion, brightTextRegion, lineRegion);
                }
            }

            if (Cv2.CountNonZero(lineMask) == 0)
            {
                using var boundsRegion = new Mat(lineMask, bestCandidate.Bounds);
                boundsRegion.SetTo(Scalar.White);
            }

            RefineLineMask(lineMask, bestCandidate.Bounds, options);
        }

        var fullMask = CreateFullMask(source.Size(), searchRegion, lineMask);
        var debugOverlay = BuildDebugOverlay(source, searchRegion, candidateMask, filteredMask, lineMask, bestCandidate);
        return new DetectionArtifacts(fullMask, debugOverlay, bestCandidate?.Confidence ?? 0d);
    }

    private static BitmapSource? CreateBitmapSource(Mat? mat)
    {
        if (mat is null || mat.Empty())
        {
            return null;
        }

        var bitmap = BitmapSourceConverter.ToBitmapSource(mat);
        bitmap.Freeze();
        return bitmap;
    }

    private static Mat? CreateFullMask(OpenCvSharp.Size sourceSize, OpenCvSharp.Rect searchRegion, Mat lineMask)
    {
        if (lineMask.Empty() || Cv2.CountNonZero(lineMask) == 0)
        {
            return null;
        }

        var fullMask = new Mat(sourceSize.Height, sourceSize.Width, MatType.CV_8UC1, Scalar.Black);
        using (var target = new Mat(fullMask, searchRegion))
        {
            lineMask.CopyTo(target);
        }

        return Cv2.CountNonZero(fullMask) == 0 ? null : fullMask;
    }

    private static Mat BuildDebugOverlay(
        Mat source,
        OpenCvSharp.Rect searchRegion,
        Mat candidateMask,
        Mat filteredMask,
        Mat lineMask,
        LineCandidate? bestCandidate)
    {
        var debug = source.Clone();

        OverlayMask(debug, searchRegion, candidateMask, new Scalar(90, 200, 90), 0.18);
        OverlayMask(debug, searchRegion, filteredMask, new Scalar(0, 215, 255), 0.30);
        OverlayMask(debug, searchRegion, lineMask, new Scalar(30, 120, 255), 0.48);

        Cv2.Rectangle(debug, searchRegion, new Scalar(255, 200, 0), 2, LineTypes.AntiAlias);

        if (bestCandidate is not null)
        {
            var lineBounds = OffsetRect(bestCandidate.Bounds, searchRegion.X, searchRegion.Y);
            var expandedBounds = OffsetRect(bestCandidate.ExpandedBounds, searchRegion.X, searchRegion.Y);

            Cv2.Rectangle(debug, expandedBounds, new Scalar(255, 170, 0), 2, LineTypes.AntiAlias);
            Cv2.Rectangle(debug, lineBounds, new Scalar(0, 255, 255), 2, LineTypes.AntiAlias);

            var labelPoint = new Point(
                Math.Max(8, expandedBounds.X),
                Math.Max(24, expandedBounds.Y - 10));

            Cv2.PutText(
                debug,
                $"conf {bestCandidate.Confidence:P0} comps {bestCandidate.ComponentCount} {(bestCandidate.IsFallback ? "fb" : "line")}",
                labelPoint,
                HersheyFonts.HersheySimplex,
                0.55,
                new Scalar(255, 255, 255),
                2,
                LineTypes.AntiAlias);
        }
        else
        {
            var labelPoint = new Point(Math.Max(8, searchRegion.X), Math.Max(24, searchRegion.Y - 10));
            Cv2.PutText(
                debug,
                "no bottom text watermark detected",
                labelPoint,
                HersheyFonts.HersheySimplex,
                0.55,
                new Scalar(255, 255, 255),
                2,
                LineTypes.AntiAlias);
        }

        return debug;
    }

    private static void OverlayMask(Mat destination, OpenCvSharp.Rect region, Mat mask, Scalar color, double alpha)
    {
        if (mask.Empty() || Cv2.CountNonZero(mask) == 0)
        {
            return;
        }

        using var destinationRegion = new Mat(destination, region);
        using var colorLayer = new Mat(destinationRegion.Size(), destinationRegion.Type(), color);
        using var maskedColor = new Mat();
        Cv2.BitwiseAnd(colorLayer, colorLayer, maskedColor, mask);
        Cv2.AddWeighted(destinationRegion, 1.0, maskedColor, alpha, 0, destinationRegion);
    }

    private static OpenCvSharp.Rect OffsetRect(OpenCvSharp.Rect rect, int offsetX, int offsetY)
    {
        return new OpenCvSharp.Rect(rect.X + offsetX, rect.Y + offsetY, rect.Width, rect.Height);
    }

    private static OpenCvSharp.Rect BuildSearchRegion(OpenCvSharp.Size sourceSize)
    {
        var rect = new OpenCvSharp.Rect(
            (int)Math.Round(sourceSize.Width * 0.05),
            (int)Math.Round(sourceSize.Height * 0.68),
            Math.Max(1, (int)Math.Round(sourceSize.Width * 0.90)),
            Math.Max(1, (int)Math.Round(sourceSize.Height * 0.30)));

        return ClampRect(rect, sourceSize);
    }

    private static bool IsValidComponent(
        Point[] contour,
        OpenCvSharp.Rect bounds,
        OpenCvSharp.Size roiSize,
        Mat value,
        Mat saturation)
    {
        if (bounds.Width < 4 || bounds.Height < 4)
        {
            return false;
        }

        if (bounds.Width > roiSize.Width * 0.35 || bounds.Height > roiSize.Height * 0.38)
        {
            return false;
        }

        var centerY = bounds.Y + bounds.Height / 2d;
        if (centerY < roiSize.Height * 0.20 || centerY > roiSize.Height * 0.98)
        {
            return false;
        }

        var area = Cv2.ContourArea(contour);
        if (area < 18)
        {
            return false;
        }

        var fillRatio = area / Math.Max(1d, bounds.Width * bounds.Height);
        if (fillRatio < 0.08 || fillRatio > 0.95)
        {
            return false;
        }

        using var valueRegion = new Mat(value, bounds);
        using var saturationRegion = new Mat(saturation, bounds);
        var meanBrightness = Cv2.Mean(valueRegion).Val0;
        var meanSaturation = Cv2.Mean(saturationRegion).Val0;

        return meanBrightness >= 95 && meanSaturation <= 150;
    }

    private static bool IsValidLine(
        OpenCvSharp.Rect bounds,
        int componentCount,
        OpenCvSharp.Size roiSize,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Rect searchRegion)
    {
        if (bounds.Width < Math.Max(72, roiSize.Width / 7))
        {
            return false;
        }

        if (bounds.Height < 8 || bounds.Height > Math.Max(42, roiSize.Height * 0.48))
        {
            return false;
        }

        var fullBottom = searchRegion.Y + bounds.Bottom;
        if (fullBottom < sourceSize.Height * 0.82)
        {
            return false;
        }

        var widthRatio = bounds.Width / (double)sourceSize.Width;
        var heightRatio = bounds.Height / (double)sourceSize.Height;
        if (widthRatio > 0.82 || heightRatio > 0.12)
        {
            return false;
        }

        return componentCount >= 2 || widthRatio >= 0.24;
    }

    private static double ScoreLine(
        OpenCvSharp.Rect bounds,
        int componentCount,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Rect searchRegion)
    {
        var widthRatio = bounds.Width / (double)sourceSize.Width;
        var heightRatio = bounds.Height / (double)sourceSize.Height;
        var bottomRatio = (searchRegion.Y + bounds.Bottom) / (double)sourceSize.Height;
        var centerScore = CalculateHorizontalCenterScore(bounds, sourceSize, searchRegion);

        return componentCount * 14d
               + widthRatio * 110d
               + bottomRatio * 18d
               + centerScore * 28d
               - heightRatio * 155d;
    }

    private static double CalculateConfidence(
        OpenCvSharp.Rect bounds,
        int componentCount,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Rect searchRegion)
    {
        var widthRatio = bounds.Width / (double)sourceSize.Width;
        var heightRatio = bounds.Height / (double)sourceSize.Height;
        var bottomRatio = (searchRegion.Y + bounds.Bottom) / (double)sourceSize.Height;
        var centerScore = CalculateHorizontalCenterScore(bounds, sourceSize, searchRegion);
        var lineCenterX = searchRegion.X + bounds.X + (bounds.Width / 2d);
        var centerDistanceRatio = Math.Abs(lineCenterX - (sourceSize.Width / 2d)) / sourceSize.Width;

        var widthScore = Math.Clamp((widthRatio - 0.18) / 0.22, 0d, 1d);
        var bottomScore = Math.Clamp((bottomRatio - 0.82) / 0.14, 0d, 1d);
        var componentScore = Math.Clamp((componentCount - 1d) / 5d, 0d, 1d);
        var compactScore = 1d - Math.Clamp((heightRatio - 0.012) / 0.045, 0d, 1d);
        var confidence = (widthScore * 0.28)
                         + (bottomScore * 0.15)
                         + (componentScore * 0.22)
                         + (compactScore * 0.15)
                         + (centerScore * 0.20);

        if (componentCount <= 2 && widthRatio < 0.18)
        {
            confidence *= 0.70;
        }

        if (centerDistanceRatio > 0.20 && widthRatio < 0.30)
        {
            confidence *= 0.55;
        }

        return Math.Clamp(confidence, 0d, 1d);
    }

    private static double CalculateHorizontalCenterScore(
        OpenCvSharp.Rect bounds,
        OpenCvSharp.Size sourceSize,
        OpenCvSharp.Rect searchRegion)
    {
        var lineCenterX = searchRegion.X + bounds.X + (bounds.Width / 2d);
        var centerDistanceRatio = Math.Abs(lineCenterX - (sourceSize.Width / 2d)) / sourceSize.Width;
        return 1d - Math.Clamp((centerDistanceRatio - 0.06) / 0.18, 0d, 1d);
    }

    private static LineCandidate? TryCreateFallbackCandidate(
        Mat cleanedMask,
        OpenCvSharp.Size roiSize,
        BottomTextWatermarkDetectorOptions options)
    {
        var fallbackBounds = ClampRect(
            new OpenCvSharp.Rect(
                (int)Math.Round(roiSize.Width * 0.25),
                (int)Math.Round(roiSize.Height * 0.66),
                Math.Max(1, (int)Math.Round(roiSize.Width * 0.50)),
                Math.Max(1, (int)Math.Round(roiSize.Height * 0.20))),
            roiSize);

        using var fallbackRegion = new Mat(cleanedMask, fallbackBounds);
        var activePixels = Cv2.CountNonZero(fallbackRegion);
        if (activePixels < 72)
        {
            return null;
        }

        var density = activePixels / (double)(fallbackBounds.Width * fallbackBounds.Height);
        if (density < 0.015)
        {
            return null;
        }

        var confidence = Math.Clamp(0.06 + (density * 4.0), 0.10, options.FallbackConfidenceCap);
        return new LineCandidate(
            fallbackBounds,
            fallbackBounds,
            Math.Max(1, activePixels / 40),
            density * 10d,
            confidence,
            IsFallback: true);
    }

    private static void PopulateFallbackLineMask(
        Mat sourceMask,
        Mat brightTextMask,
        Mat strokeMask,
        OpenCvSharp.Rect expandedBounds,
        Mat lineMask)
    {
        using var sourceRegion = new Mat(sourceMask, expandedBounds);
        using var brightRegion = new Mat(brightTextMask, expandedBounds);
        using var strokeRegion = new Mat(strokeMask, expandedBounds);
        using var lineRegion = new Mat(lineMask, expandedBounds);
        using var composite = new Mat();

        Cv2.BitwiseAnd(brightRegion, strokeRegion, composite);

        if (Cv2.CountNonZero(composite) < 24)
        {
            Cv2.BitwiseAnd(sourceRegion, brightRegion, composite);
        }

        if (Cv2.CountNonZero(composite) < 24)
        {
            sourceRegion.CopyTo(composite);
        }

        var rowBand = FindDominantRowBand(composite);
        if (rowBand is null)
        {
            composite.CopyTo(lineRegion);
            return;
        }

        using var compositeBand = new Mat(composite, rowBand.Value);
        using var lineBand = new Mat(lineRegion, rowBand.Value);
        compositeBand.CopyTo(lineBand);

        using var filteredBand = FilterFallbackComponents(lineBand);
        if (Cv2.CountNonZero(filteredBand) > 0)
        {
            filteredBand.CopyTo(lineBand);
        }
    }

    private static OpenCvSharp.Rect? FindDominantRowBand(Mat mask)
    {
        if (mask.Empty() || Cv2.CountNonZero(mask) == 0)
        {
            return null;
        }

        var rowCounts = new int[mask.Rows];
        var peakRow = -1;
        var peakCount = 0;

        for (var y = 0; y < mask.Rows; y++)
        {
            var rowCount = 0;
            for (var x = 0; x < mask.Cols; x++)
            {
                if (mask.At<byte>(y, x) > 0)
                {
                    rowCount++;
                }
            }

            rowCounts[y] = rowCount;
            if (rowCount > peakCount)
            {
                peakCount = rowCount;
                peakRow = y;
            }
        }

        if (peakRow < 0 || peakCount < Math.Max(4, mask.Cols / 24))
        {
            return null;
        }

        var threshold = Math.Max(3, (int)Math.Round(peakCount * 0.25));
        var top = peakRow;
        var bottom = peakRow;

        while (top > 0 && rowCounts[top - 1] >= threshold)
        {
            top--;
        }

        while (bottom < mask.Rows - 1 && rowCounts[bottom + 1] >= threshold)
        {
            bottom++;
        }

        top = Math.Max(0, top - 1);
        bottom = Math.Min(mask.Rows - 1, bottom + 1);
        return new OpenCvSharp.Rect(0, top, mask.Cols, Math.Max(1, bottom - top + 1));
    }

    private static Mat FilterFallbackComponents(Mat bandMask)
    {
        var filtered = new Mat(bandMask.Rows, bandMask.Cols, MatType.CV_8UC1, Scalar.Black);
        Cv2.FindContours(bandMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        foreach (var contour in contours)
        {
            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < 4 || bounds.Height < 2)
            {
                continue;
            }

            if (bounds.Height > Math.Max(16, bandMask.Rows))
            {
                continue;
            }

            if (bounds.Width > bandMask.Cols * 0.90)
            {
                continue;
            }

            Cv2.DrawContours(filtered, [contour], -1, Scalar.White, -1);
        }

        return filtered;
    }

    private static void RefineLineMask(
        Mat lineMask,
        OpenCvSharp.Rect bounds,
        BottomTextWatermarkDetectorOptions options)
    {
        using var closeKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new OpenCvSharp.Size(options.InitialCloseKernelWidth, options.InitialCloseKernelHeight));
        using var dilateKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new OpenCvSharp.Size(options.DilateKernelWidth, options.DilateKernelHeight));

        Cv2.MorphologyEx(lineMask, lineMask, MorphTypes.Close, closeKernel);
        Cv2.Dilate(lineMask, lineMask, dilateKernel, iterations: 1);

        if (options.AdaptiveHorizontalCloseDivisor <= 0)
        {
            return;
        }

        var horizontalWidth = Math.Min(
            EnsureOdd(Math.Max(3, bounds.Width / options.AdaptiveHorizontalCloseDivisor)),
            options.AdaptiveHorizontalCloseMaxWidth);
        using var adaptiveKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new OpenCvSharp.Size(Math.Max(3, horizontalWidth), 3));
        Cv2.MorphologyEx(lineMask, lineMask, MorphTypes.Close, adaptiveKernel);
    }

    private static bool HasIntersection(OpenCvSharp.Rect left, OpenCvSharp.Rect right)
    {
        return left.Left < right.Right
               && left.Right > right.Left
               && left.Top < right.Bottom
               && left.Bottom > right.Top;
    }

    private static OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, OpenCvSharp.Size boundary, int expandX, int expandY)
    {
        var left = Math.Max(0, rect.Left - expandX);
        var top = Math.Max(0, rect.Top - expandY);
        var right = Math.Min(boundary.Width, rect.Right + expandX);
        var bottom = Math.Min(boundary.Height, rect.Bottom + expandY);

        return new OpenCvSharp.Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static OpenCvSharp.Rect ClampRect(OpenCvSharp.Rect rect, OpenCvSharp.Size boundary)
    {
        var left = Math.Max(0, rect.Left);
        var top = Math.Max(0, rect.Top);
        var right = Math.Min(boundary.Width, rect.Right);
        var bottom = Math.Min(boundary.Height, rect.Bottom);

        return new OpenCvSharp.Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static int EnsureOdd(int value)
    {
        var clamped = Math.Max(3, value);
        return clamped % 2 == 0 ? clamped + 1 : clamped;
    }

    private sealed class DetectionArtifacts : IDisposable
    {
        public DetectionArtifacts(Mat? mask, Mat debugOverlay, double confidence)
        {
            Mask = mask;
            DebugOverlay = debugOverlay;
            Confidence = confidence;
        }

        public Mat? Mask { get; }

        public Mat DebugOverlay { get; }

        public double Confidence { get; }

        public void Dispose()
        {
            Mask?.Dispose();
            DebugOverlay.Dispose();
        }
    }

    private sealed record LineCandidate(
        OpenCvSharp.Rect Bounds,
        OpenCvSharp.Rect ExpandedBounds,
        int ComponentCount,
        double Score,
        double Confidence,
        bool IsFallback);
}
