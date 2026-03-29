using System.IO;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WatermarkRemover.App.Models;
using WatermarkRemover.App.Services;

var inputPath = args.Length > 0
    ? args[0]
    : @"C:\Users\admin\Pictures\微信图片_20260317214019_164_51.jpg";

var modelService = new ModelAssetService();
var imageFileService = new ImageFileService();
var model = await modelService.ResolveBundledModelAsync();

if (Directory.Exists(inputPath))
{
    await RunDirectoryPressureTestAsync(inputPath, model.ModelPath, imageFileService);
    return 0;
}

var baselinePath = args.Length > 1
    ? args[1]
    : Path.Combine(
        Path.GetDirectoryName(inputPath) ?? string.Empty,
        $"{Path.GetFileNameWithoutExtension(inputPath)}-clean{Path.GetExtension(inputPath)}");

if (!File.Exists(inputPath))
{
    Console.WriteLine($"Input not found: {inputPath}");
    return 1;
}

await RunSingleImagePressureTestAsync(inputPath, baselinePath, model.ModelPath, imageFileService);
return 0;

static async Task RunDirectoryPressureTestAsync(string directoryPath, string modelPath, ImageFileService imageFileService)
{
    var imagePaths = Directory.EnumerateFiles(directoryPath, "*.*", SearchOption.TopDirectoryOnly)
        .Where(path =>
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp";
        })
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (imagePaths.Count == 0)
    {
        Console.WriteLine($"No supported images found in: {directoryPath}");
        return;
    }

    var batchRoot = Path.Combine(directoryPath, "pressure-test-batch", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(batchRoot);

    var summaryItems = new List<(string Label, string OriginalPath, string ResultPath)>();
    var zoomSummaryItems = new List<(string Label, string ZoomPath)>();

    foreach (var imagePath in imagePaths)
    {
        Console.WriteLine($"PROCESS: {Path.GetFileName(imagePath)}");

        var outputDir = Path.Combine(batchRoot, Path.GetFileNameWithoutExtension(imagePath));
        Directory.CreateDirectory(outputDir);

        using var source = Cv2.ImRead(imagePath, ImreadModes.Color);
        using var detectedMask = DetectWatermarkMask(source);
        using var maskOverlay = CreateMaskOverlay(source, detectedMask);
        var zoomRect = CalculateZoomRect(source.Size(), detectedMask);

        var maskPath = Path.Combine(outputDir, "detected-mask.png");
        var overlayPath = Path.Combine(outputDir, "mask-overlay.jpg");
        Cv2.ImWrite(maskPath, detectedMask);
        Cv2.ImWrite(overlayPath, maskOverlay);

        var maskBitmap = BitmapSourceConverter.ToBitmapSource(detectedMask);
        maskBitmap.Freeze();

        var service = new WatermarkRemovalService();
        var result = await service.RemoveWatermarkAsync(new InpaintingRequest
        {
            ImagePath = imagePath,
            ModelPath = modelPath,
            MaskImage = maskBitmap,
        });

        var resultPath = Path.Combine(
            outputDir,
            $"{Path.GetFileNameWithoutExtension(imagePath)}-default{Path.GetExtension(imagePath)}");

        await imageFileService.SaveAsync(result, resultPath);
        summaryItems.Add((Path.GetFileNameWithoutExtension(imagePath), imagePath, resultPath));

        var zoomComparePath = Path.Combine(outputDir, "zoom-compare.jpg");
        using (var zoomCompare = BuildZoomComparison(imagePath, resultPath, zoomRect))
        {
            Cv2.ImWrite(zoomComparePath, zoomCompare);
        }

        zoomSummaryItems.Add((Path.GetFileNameWithoutExtension(imagePath), zoomComparePath));

        Console.WriteLine($"RESULT: {resultPath}");
    }

    var summaryPath = Path.Combine(batchRoot, "batch-summary.jpg");
    using (var summary = BuildBatchSummary(summaryItems))
    {
        Cv2.ImWrite(summaryPath, summary);
    }

    var zoomSummaryPath = Path.Combine(batchRoot, "batch-zoom-summary.jpg");
    using (var zoomSummary = BuildZoomSummary(zoomSummaryItems))
    {
        Cv2.ImWrite(zoomSummaryPath, zoomSummary);
    }

    Console.WriteLine($"BATCH_DIR: {batchRoot}");
    Console.WriteLine($"SUMMARY: {summaryPath}");
    Console.WriteLine($"ZOOM_SUMMARY: {zoomSummaryPath}");
}

static async Task RunSingleImagePressureTestAsync(
    string inputPath,
    string baselinePath,
    string modelPath,
    ImageFileService imageFileService)
{
    var outputRoot = Path.Combine(
        Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory,
        "pressure-test",
        DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    Directory.CreateDirectory(outputRoot);

    using var source = Cv2.ImRead(inputPath, ImreadModes.Color);
    using var detectedMask = DetectWatermarkMask(source);
    using var maskOverlay = CreateMaskOverlay(source, detectedMask);

    var maskPath = Path.Combine(outputRoot, "detected-mask.png");
    var maskOverlayPath = Path.Combine(outputRoot, "mask-overlay.jpg");
    Cv2.ImWrite(maskPath, detectedMask);
    Cv2.ImWrite(maskOverlayPath, maskOverlay);

    var maskBitmap = BitmapSourceConverter.ToBitmapSource(detectedMask);
    maskBitmap.Freeze();

    using var rectTightMask = BuildCenteredMask(source.Size(), 0.34, 0.915, 0.32, 0.022);
    using var rectMediumMask = BuildCenteredMask(source.Size(), 0.30, 0.910, 0.40, 0.028);
    using var rectWideMask = BuildCenteredMask(source.Size(), 0.27, 0.907, 0.46, 0.034);
    using var rectLowTightMask = BuildCenteredMask(source.Size(), 0.29, 0.936, 0.42, 0.018);
    using var rectLowMediumMask = BuildCenteredMask(source.Size(), 0.27, 0.934, 0.46, 0.022);

    Cv2.ImWrite(Path.Combine(outputRoot, "rect-tight-mask.png"), rectTightMask);
    Cv2.ImWrite(Path.Combine(outputRoot, "rect-medium-mask.png"), rectMediumMask);
    Cv2.ImWrite(Path.Combine(outputRoot, "rect-wide-mask.png"), rectWideMask);
    Cv2.ImWrite(Path.Combine(outputRoot, "rect-low-tight-mask.png"), rectLowTightMask);
    Cv2.ImWrite(Path.Combine(outputRoot, "rect-low-medium-mask.png"), rectLowMediumMask);

    var candidates = new[]
    {
        new Candidate("baseline", null, baselinePath),
        new Candidate("c1-tight-soft", new WatermarkRepairOptions(40, 56, 1.0, 1.4, 3, 1, 1.8), null),
        new Candidate("c2-balanced", new WatermarkRepairOptions(56, 72, 1.2, 1.8, 3, 1, 2.4), null),
        new Candidate("c3-current+", new WatermarkRepairOptions(72, 96, 1.4, 2.0, 5, 1, 2.8), null),
        new Candidate("c4-context", new WatermarkRepairOptions(96, 128, 1.8, 2.6, 5, 1, 3.2), null),
        new Candidate("c5-strong-fill", new WatermarkRepairOptions(112, 144, 2.1, 3.0, 7, 2, 3.8), null),
    };

    var generated = new List<(string Label, string Path)>
    {
        ("original", inputPath),
    };

    foreach (var candidate in candidates)
    {
        if (candidate.ExistingPath is not null)
        {
            if (File.Exists(candidate.ExistingPath))
            {
                generated.Add((candidate.Name, candidate.ExistingPath));
            }

            continue;
        }

        var service = new WatermarkRemovalService(candidate.Options);
        var result = await service.RemoveWatermarkAsync(new InpaintingRequest
        {
            ImagePath = inputPath,
            ModelPath = modelPath,
            MaskImage = maskBitmap,
        });

        var outputPath = Path.Combine(outputRoot, $"{candidate.Name}.jpg");
        await imageFileService.SaveAsync(result, outputPath);
        generated.Add((candidate.Name, outputPath));
        Console.WriteLine($"{candidate.Name}: {outputPath}");
    }

    var aiRectMediumService = new WatermarkRemovalService(new WatermarkRepairOptions(56, 72, 1.2, 1.8, 3, 1, 2.4));
    var aiRectMediumMaskBitmap = BitmapSourceConverter.ToBitmapSource(rectMediumMask);
    aiRectMediumMaskBitmap.Freeze();
    var aiRectMediumResult = await aiRectMediumService.RemoveWatermarkAsync(new InpaintingRequest
    {
        ImagePath = inputPath,
        ModelPath = modelPath,
        MaskImage = aiRectMediumMaskBitmap,
    });
    var aiRectMediumPath = Path.Combine(outputRoot, "ai-rect-medium.jpg");
    await imageFileService.SaveAsync(aiRectMediumResult, aiRectMediumPath);
    generated.Add(("ai-rect-medium", aiRectMediumPath));

    var aiRectLowTightMaskBitmap = BitmapSourceConverter.ToBitmapSource(rectLowTightMask);
    aiRectLowTightMaskBitmap.Freeze();
    var aiRectLowTightResult = await aiRectMediumService.RemoveWatermarkAsync(new InpaintingRequest
    {
        ImagePath = inputPath,
        ModelPath = modelPath,
        MaskImage = aiRectLowTightMaskBitmap,
    });
    var aiRectLowTightPath = Path.Combine(outputRoot, "ai-rect-low-tight.jpg");
    await imageFileService.SaveAsync(aiRectLowTightResult, aiRectLowTightPath);
    generated.Add(("ai-rect-low-tight", aiRectLowTightPath));

    var aiRectLowMediumMaskBitmap = BitmapSourceConverter.ToBitmapSource(rectLowMediumMask);
    aiRectLowMediumMaskBitmap.Freeze();
    var aiRectLowMediumResult = await aiRectMediumService.RemoveWatermarkAsync(new InpaintingRequest
    {
        ImagePath = inputPath,
        ModelPath = modelPath,
        MaskImage = aiRectLowMediumMaskBitmap,
    });
    var aiRectLowMediumPath = Path.Combine(outputRoot, "ai-rect-low-medium.jpg");
    await imageFileService.SaveAsync(aiRectLowMediumResult, aiRectLowMediumPath);
    generated.Add(("ai-rect-low-medium", aiRectLowMediumPath));

    var teleaTightPath = Path.Combine(outputRoot, "telea-tight.jpg");
    var teleaMediumPath = Path.Combine(outputRoot, "telea-medium.jpg");
    var teleaWidePath = Path.Combine(outputRoot, "telea-wide.jpg");
    var nsMediumPath = Path.Combine(outputRoot, "ns-medium.jpg");

    using (var teleaTight = RunOpenCvInpaint(source, rectTightMask, InpaintTypes.Telea, 3))
    using (var teleaMedium = RunOpenCvInpaint(source, rectMediumMask, InpaintTypes.Telea, 3))
    using (var teleaWide = RunOpenCvInpaint(source, rectWideMask, InpaintTypes.Telea, 4))
    using (var nsMedium = RunOpenCvInpaint(source, rectMediumMask, InpaintTypes.NS, 3))
    {
        Cv2.ImWrite(teleaTightPath, teleaTight);
        Cv2.ImWrite(teleaMediumPath, teleaMedium);
        Cv2.ImWrite(teleaWidePath, teleaWide);
        Cv2.ImWrite(nsMediumPath, nsMedium);
    }

    generated.Add(("telea-tight", teleaTightPath));
    generated.Add(("telea-medium", teleaMediumPath));
    generated.Add(("telea-wide", teleaWidePath));
    generated.Add(("ns-medium", nsMediumPath));

    var cloneRect = ClampRect(new Rect(
        (int)Math.Round(source.Width * 0.27),
        (int)Math.Round(source.Height * 0.928),
        (int)Math.Round(source.Width * 0.46),
        (int)Math.Round(source.Height * 0.022)), source.Size());

    var cloneCandidates = new[]
    {
        new { Name = "clone-up-80", OffsetY = 80, OffsetX = 0, Mode = SeamlessCloneFlags.NormalClone },
        new { Name = "clone-up-120", OffsetY = 120, OffsetX = 0, Mode = SeamlessCloneFlags.NormalClone },
        new { Name = "clone-up-80-mixed", OffsetY = 80, OffsetX = 0, Mode = SeamlessCloneFlags.MixedClone },
        new { Name = "clone-up-120-mixed", OffsetY = 120, OffsetX = 0, Mode = SeamlessCloneFlags.MixedClone },
        new { Name = "clone-up-left", OffsetY = 90, OffsetX = -40, Mode = SeamlessCloneFlags.MixedClone },
        new { Name = "clone-up-right", OffsetY = 90, OffsetX = 40, Mode = SeamlessCloneFlags.MixedClone },
    };

    foreach (var candidate in cloneCandidates)
    {
        using var cloned = RunPatchClone(source, cloneRect, candidate.OffsetX, candidate.OffsetY, candidate.Mode);
        var clonePath = Path.Combine(outputRoot, $"{candidate.Name}.jpg");
        Cv2.ImWrite(clonePath, cloned);
        generated.Add((candidate.Name, clonePath));
    }

    var deblendCandidates = new[]
    {
        new { Name = "deblend-018", Alpha = 0.18, Sigma = 1.2, Mask = rectLowTightMask },
        new { Name = "deblend-024", Alpha = 0.24, Sigma = 1.4, Mask = rectLowTightMask },
        new { Name = "deblend-030", Alpha = 0.30, Sigma = 1.6, Mask = rectLowTightMask },
        new { Name = "deblend-detected-010", Alpha = 0.10, Sigma = 1.1, Mask = detectedMask },
        new { Name = "deblend-detected-014", Alpha = 0.14, Sigma = 1.2, Mask = detectedMask },
        new { Name = "deblend-detected-018", Alpha = 0.18, Sigma = 1.3, Mask = detectedMask },
        new { Name = "deblend-detected-024", Alpha = 0.24, Sigma = 1.4, Mask = detectedMask },
    };

    foreach (var candidate in deblendCandidates)
    {
        using var deblended = RunWhiteWatermarkDeblend(source, candidate.Mask, candidate.Alpha, candidate.Sigma);
        var deblendPath = Path.Combine(outputRoot, $"{candidate.Name}.jpg");
        Cv2.ImWrite(deblendPath, deblended);
        generated.Add((candidate.Name, deblendPath));
    }

    var contactSheetPath = Path.Combine(outputRoot, "contact-sheet.jpg");
    using (var contactSheet = BuildContactSheet(generated))
    {
        Cv2.ImWrite(contactSheetPath, contactSheet);
    }

    Console.WriteLine($"MASK: {maskPath}");
    Console.WriteLine($"OVERLAY: {maskOverlayPath}");
    Console.WriteLine($"CONTACT: {contactSheetPath}");
    Console.WriteLine($"OUTDIR: {outputRoot}");
}

static Mat DetectWatermarkMask(Mat source)
{
    var mask = new Mat(source.Rows, source.Cols, MatType.CV_8UC1, Scalar.Black);

    var roiRect = new Rect(
        (int)(source.Cols * 0.08),
        (int)(source.Rows * 0.84),
        (int)(source.Cols * 0.84),
        (int)(source.Rows * 0.12));

    roiRect = ClampRect(roiRect, source.Size());

    using var roi = new Mat(source, roiRect);
    using var gray = new Mat();
    using var hsv = new Mat();
    using var brightPixels = new Mat();
    using var lowSaturation = new Mat();
    using var topHat = new Mat();
    using var topHatMask = new Mat();
    using var candidateMask = new Mat();
    using var mergedLineMask = new Mat();
    using var topHatKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(15, 15));
    using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(Math.Max(31, roi.Width / 10), 5));
    using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));

    Cv2.CvtColor(roi, hsv, ColorConversionCodes.BGR2HSV);
    Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
    Cv2.MorphologyEx(gray, topHat, MorphTypes.TopHat, topHatKernel);
    Cv2.Threshold(gray, brightPixels, 150, 255, ThresholdTypes.Binary);
    Cv2.InRange(hsv, new Scalar(0, 0, 120), new Scalar(180, 90, 255), lowSaturation);
    Cv2.Threshold(topHat, topHatMask, 8, 255, ThresholdTypes.Binary);

    Cv2.BitwiseAnd(brightPixels, lowSaturation, candidateMask);
    Cv2.BitwiseOr(candidateMask, topHatMask, candidateMask);
    Cv2.MorphologyEx(candidateMask, mergedLineMask, MorphTypes.Close, closeKernel);
    Cv2.Dilate(mergedLineMask, mergedLineMask, dilateKernel, iterations: 1);

    Cv2.FindContours(mergedLineMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

    var validBounds = contours
        .Select(Cv2.BoundingRect)
        .Where(bounds => bounds.Width >= Math.Max(roi.Width / 5, 40) && bounds.Height >= 8)
        .ToList();

    if (validBounds.Count == 0)
    {
        var fallbackRect = new Rect(
            (int)(source.Cols * 0.24),
            (int)(source.Rows * 0.915),
            (int)(source.Cols * 0.52),
            (int)(source.Rows * 0.045));

        fallbackRect = ClampRect(fallbackRect, source.Size());
        using var fallbackRoi = new Mat(mask, fallbackRect);
        fallbackRoi.SetTo(Scalar.White);
        return mask;
    }

    var unionBounds = validBounds[0];
    for (var index = 1; index < validBounds.Count; index++)
    {
        unionBounds = Union(unionBounds, validBounds[index]);
    }

    unionBounds = ExpandRect(unionBounds, roi.Size(), 20, 10);
    var fullRect = new Rect(
        unionBounds.X + roiRect.X,
        unionBounds.Y + roiRect.Y,
        unionBounds.Width,
        unionBounds.Height);

    fullRect = ClampRect(fullRect, source.Size());
    using (var target = new Mat(mask, fullRect))
    {
        target.SetTo(Scalar.White);
    }

    return mask;
}

static Mat CreateMaskOverlay(Mat source, Mat mask)
{
    var overlay = source.Clone();
    using var colorMask = new Mat();
    Cv2.CvtColor(mask, colorMask, ColorConversionCodes.GRAY2BGR);
    using var tinted = new Mat(colorMask.Size(), colorMask.Type(), new Scalar(30, 120, 255));
    Cv2.BitwiseAnd(tinted, colorMask, tinted);
    Cv2.AddWeighted(source, 1.0, tinted, 0.45, 0, overlay);
    return overlay;
}

static Mat BuildCenteredMask(OpenCvSharp.Size size, double leftRatio, double topRatio, double widthRatio, double heightRatio)
{
    var mask = new Mat(size.Height, size.Width, MatType.CV_8UC1, Scalar.Black);
    var rect = new Rect(
        (int)Math.Round(size.Width * leftRatio),
        (int)Math.Round(size.Height * topRatio),
        Math.Max(1, (int)Math.Round(size.Width * widthRatio)),
        Math.Max(1, (int)Math.Round(size.Height * heightRatio)));

    rect = ClampRect(rect, size);
    using var roi = new Mat(mask, rect);
    roi.SetTo(Scalar.White);
    return mask;
}

static Mat RunOpenCvInpaint(Mat source, Mat mask, InpaintTypes method, double radius)
{
    var result = new Mat();
    Cv2.Inpaint(source, mask, result, radius, method);
    return result;
}

static Mat RunPatchClone(Mat source, Rect targetRect, int offsetX, int offsetY, SeamlessCloneFlags mode)
{
    var sourceRect = new Rect(
        Math.Clamp(targetRect.X + offsetX, 0, Math.Max(0, source.Width - targetRect.Width)),
        Math.Clamp(targetRect.Y - offsetY, 0, Math.Max(0, source.Height - targetRect.Height)),
        targetRect.Width,
        targetRect.Height);

    using var sourcePatch = new Mat(source, sourceRect);
    using var cloneMask = new Mat(targetRect.Height, targetRect.Width, MatType.CV_8UC1, Scalar.White);
    var result = source.Clone();

    var center = new Point(targetRect.X + targetRect.Width / 2, targetRect.Y + targetRect.Height / 2);
    Cv2.SeamlessClone(sourcePatch, result, cloneMask, center, result, mode);
    return result;
}

static Mat RunWhiteWatermarkDeblend(Mat source, Mat mask, double alpha, double sigma)
{
    using var featherMask = mask.Clone();
    Cv2.GaussianBlur(featherMask, featherMask, new OpenCvSharp.Size(0, 0), sigma);

    using var alphaMask = new Mat();
    featherMask.ConvertTo(alphaMask, MatType.CV_32FC1, alpha / 255d);

    using var alpha3 = new Mat();
    Cv2.CvtColor(alphaMask, alpha3, ColorConversionCodes.GRAY2BGR);

    using var sourceFloat = new Mat();
    source.ConvertTo(sourceFloat, MatType.CV_32FC3);

    using var whiteLayer = new Mat(source.Size(), MatType.CV_32FC3, new Scalar(255, 255, 255));
    using var whiteContribution = new Mat();
    using var numerator = new Mat();
    using var denominator = new Mat();
    using var restoredFloat = new Mat();
    using var safeDenominator = new Mat();

    Cv2.Multiply(whiteLayer, alpha3, whiteContribution);
    Cv2.Subtract(sourceFloat, whiteContribution, numerator);

    using var ones = new Mat(alpha3.Size(), alpha3.Type(), Scalar.All(1.0));
    Cv2.Subtract(ones, alpha3, denominator);
    Cv2.Max(denominator, new Scalar(0.05, 0.05, 0.05), safeDenominator);
    Cv2.Divide(numerator, safeDenominator, restoredFloat);

    var restored = new Mat();
    restoredFloat.ConvertTo(restored, MatType.CV_8UC3);
    return restored;
}

static Mat BuildContactSheet(IReadOnlyList<(string Label, string Path)> images)
{
    const int columns = 3;
    const int cellWidth = 260;
    const int imageHeight = 520;
    const int labelHeight = 42;
    const int padding = 16;

    var rows = (int)Math.Ceiling(images.Count / (double)columns);
    var canvasWidth = padding + columns * (cellWidth + padding);
    var canvasHeight = padding + rows * (imageHeight + labelHeight + padding);
    var canvas = new Mat(canvasHeight, canvasWidth, MatType.CV_8UC3, new Scalar(245, 245, 245));

    for (var index = 0; index < images.Count; index++)
    {
        var (label, path) = images[index];
        using var image = Cv2.ImRead(path, ImreadModes.Color);
        if (image.Empty())
        {
            continue;
        }

        var row = index / columns;
        var column = index % columns;

        var x = padding + column * (cellWidth + padding);
        var y = padding + row * (imageHeight + labelHeight + padding);

        using var resized = ResizeToFit(image, cellWidth, imageHeight);
        var imageRect = new Rect(x + (cellWidth - resized.Width) / 2, y, resized.Width, resized.Height);
        using (var roi = new Mat(canvas, imageRect))
        {
            resized.CopyTo(roi);
        }

        Cv2.Rectangle(canvas, new Point(x, y), new Point(x + cellWidth, y + imageHeight), new Scalar(210, 210, 210), 1);
        Cv2.PutText(
            canvas,
            label,
            new Point(x + 8, y + imageHeight + 28),
            HersheyFonts.HersheySimplex,
            0.65,
            new Scalar(40, 40, 40),
            2,
            LineTypes.AntiAlias);
    }

    return canvas;
}

static Mat BuildBatchSummary(IReadOnlyList<(string Label, string OriginalPath, string ResultPath)> items)
{
    const int rowHeight = 220;
    const int imageWidth = 170;
    const int labelHeight = 36;
    const int padding = 16;

    var canvasWidth = padding + imageWidth + padding + imageWidth + padding;
    var canvasHeight = padding + items.Count * (rowHeight + labelHeight + padding);
    var canvas = new Mat(canvasHeight, canvasWidth, MatType.CV_8UC3, new Scalar(245, 245, 245));

    for (var index = 0; index < items.Count; index++)
    {
        var (label, originalPath, resultPath) = items[index];
        var y = padding + index * (rowHeight + labelHeight + padding);

        using var original = Cv2.ImRead(originalPath, ImreadModes.Color);
        using var result = Cv2.ImRead(resultPath, ImreadModes.Color);
        using var resizedOriginal = ResizeToFit(original, imageWidth, rowHeight);
        using var resizedResult = ResizeToFit(result, imageWidth, rowHeight);

        var originalRect = new Rect(
            padding + (imageWidth - resizedOriginal.Width) / 2,
            y,
            resizedOriginal.Width,
            resizedOriginal.Height);

        var resultRect = new Rect(
            padding + imageWidth + padding + (imageWidth - resizedResult.Width) / 2,
            y,
            resizedResult.Width,
            resizedResult.Height);

        using (var roiOriginal = new Mat(canvas, originalRect))
        {
            resizedOriginal.CopyTo(roiOriginal);
        }

        using (var roiResult = new Mat(canvas, resultRect))
        {
            resizedResult.CopyTo(roiResult);
        }

        Cv2.Rectangle(canvas, new Point(padding, y), new Point(padding + imageWidth, y + rowHeight), new Scalar(210, 210, 210), 1);
        Cv2.Rectangle(canvas, new Point(padding + imageWidth + padding, y), new Point(padding + imageWidth + padding + imageWidth, y + rowHeight), new Scalar(210, 210, 210), 1);

        Cv2.PutText(canvas, label, new Point(padding, y + rowHeight + 24), HersheyFonts.HersheySimplex, 0.65, new Scalar(40, 40, 40), 2, LineTypes.AntiAlias);
        Cv2.PutText(canvas, "orig", new Point(padding + 6, y + 22), HersheyFonts.HersheySimplex, 0.55, new Scalar(255, 255, 255), 2, LineTypes.AntiAlias);
        Cv2.PutText(canvas, "result", new Point(padding + imageWidth + padding + 6, y + 22), HersheyFonts.HersheySimplex, 0.55, new Scalar(255, 255, 255), 2, LineTypes.AntiAlias);
    }

    return canvas;
}

static OpenCvSharp.Rect CalculateZoomRect(OpenCvSharp.Size sourceSize, Mat mask)
{
    Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
    if (contours.Length == 0)
    {
        return new OpenCvSharp.Rect(
            (int)Math.Round(sourceSize.Width * 0.18),
            (int)Math.Round(sourceSize.Height * 0.89),
            Math.Max(1, (int)Math.Round(sourceSize.Width * 0.64)),
            Math.Max(1, (int)Math.Round(sourceSize.Height * 0.08)));
    }

    var bounds = Cv2.BoundingRect(contours[0]);
    for (var i = 1; i < contours.Length; i++)
    {
        bounds = Union(bounds, Cv2.BoundingRect(contours[i]));
    }

    var expanded = ExpandRect(bounds, sourceSize, 140, 80);
    return ClampRect(expanded, sourceSize);
}

static Mat BuildZoomComparison(string originalPath, string resultPath, OpenCvSharp.Rect zoomRect)
{
    using var original = Cv2.ImRead(originalPath, ImreadModes.Color);
    using var result = Cv2.ImRead(resultPath, ImreadModes.Color);
    using var originalCrop = new Mat(original, zoomRect);
    using var resultCrop = new Mat(result, zoomRect);
    using var originalZoom = ResizeToFit(originalCrop, 900, 260);
    using var resultZoom = ResizeToFit(resultCrop, 900, 260);

    var canvas = new Mat(16 + 260 + 40 + 16, 16 + (900 + 16) * 2, MatType.CV_8UC3, new Scalar(245, 245, 245));

    var leftRect = new OpenCvSharp.Rect(16, 16, originalZoom.Width, originalZoom.Height);
    var rightRect = new OpenCvSharp.Rect(16 + 900 + 16, 16, resultZoom.Width, resultZoom.Height);

    using (var roi = new Mat(canvas, leftRect))
    {
        originalZoom.CopyTo(roi);
    }

    using (var roi = new Mat(canvas, rightRect))
    {
        resultZoom.CopyTo(roi);
    }

    Cv2.Rectangle(canvas, new Point(16, 16), new Point(16 + 900, 16 + 260), new Scalar(210, 210, 210), 1);
    Cv2.Rectangle(canvas, new Point(16 + 900 + 16, 16), new Point(16 + 900 + 16 + 900, 16 + 260), new Scalar(210, 210, 210), 1);

    Cv2.PutText(canvas, "orig", new Point(24, 16 + 260 + 26), HersheyFonts.HersheySimplex, 0.8, new Scalar(40, 40, 40), 2, LineTypes.AntiAlias);
    Cv2.PutText(canvas, "result", new Point(16 + 900 + 24, 16 + 260 + 26), HersheyFonts.HersheySimplex, 0.8, new Scalar(40, 40, 40), 2, LineTypes.AntiAlias);

    return canvas;
}

static Mat BuildZoomSummary(IReadOnlyList<(string Label, string ZoomPath)> items)
{
    const int padding = 16;
    const int thumbWidth = 340;
    const int thumbHeight = 110;
    const int labelHeight = 34;
    const int columns = 2;

    var rows = (int)Math.Ceiling(items.Count / (double)columns);
    var canvasWidth = padding + columns * (thumbWidth + padding);
    var canvasHeight = padding + rows * (thumbHeight + labelHeight + padding);
    var canvas = new Mat(canvasHeight, canvasWidth, MatType.CV_8UC3, new Scalar(245, 245, 245));

    for (var index = 0; index < items.Count; index++)
    {
        var (label, zoomPath) = items[index];
        using var zoom = Cv2.ImRead(zoomPath, ImreadModes.Color);
        if (zoom.Empty())
        {
            continue;
        }

        using var resized = ResizeToFit(zoom, thumbWidth, thumbHeight);
        var row = index / columns;
        var column = index % columns;
        var x = padding + column * (thumbWidth + padding);
        var y = padding + row * (thumbHeight + labelHeight + padding);

        var imageRect = new OpenCvSharp.Rect(x + (thumbWidth - resized.Width) / 2, y, resized.Width, resized.Height);
        using (var roi = new Mat(canvas, imageRect))
        {
            resized.CopyTo(roi);
        }

        Cv2.Rectangle(canvas, new Point(x, y), new Point(x + thumbWidth, y + thumbHeight), new Scalar(210, 210, 210), 1);
        Cv2.PutText(canvas, label, new Point(x + 6, y + thumbHeight + 24), HersheyFonts.HersheySimplex, 0.65, new Scalar(40, 40, 40), 2, LineTypes.AntiAlias);
    }

    return canvas;
}

static OpenCvSharp.Rect ExpandRect(OpenCvSharp.Rect rect, OpenCvSharp.Size boundary, int expandX, int expandY)
{
    var left = Math.Max(0, rect.Left - expandX);
    var top = Math.Max(0, rect.Top - expandY);
    var right = Math.Min(boundary.Width, rect.Right + expandX);
    var bottom = Math.Min(boundary.Height, rect.Bottom + expandY);

    return new OpenCvSharp.Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
}

static OpenCvSharp.Rect Union(OpenCvSharp.Rect left, OpenCvSharp.Rect right)
{
    var x = Math.Min(left.Left, right.Left);
    var y = Math.Min(left.Top, right.Top);
    var maxRight = Math.Max(left.Right, right.Right);
    var maxBottom = Math.Max(left.Bottom, right.Bottom);

    return new OpenCvSharp.Rect(x, y, maxRight - x, maxBottom - y);
}

static Mat ResizeToFit(Mat source, int maxWidth, int maxHeight)
{
    var scale = Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height);
    var width = Math.Max(1, (int)Math.Round(source.Width * scale));
    var height = Math.Max(1, (int)Math.Round(source.Height * scale));

    var resized = new Mat();
    Cv2.Resize(source, resized, new OpenCvSharp.Size(width, height), 0, 0, InterpolationFlags.Area);
    return resized;
}

static Rect ClampRect(Rect rect, OpenCvSharp.Size boundary)
{
    var left = Math.Max(0, rect.Left);
    var top = Math.Max(0, rect.Top);
    var right = Math.Min(boundary.Width, rect.Right);
    var bottom = Math.Min(boundary.Height, rect.Bottom);

    return new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
}

file sealed record Candidate(string Name, WatermarkRepairOptions? Options, string? ExistingPath);
