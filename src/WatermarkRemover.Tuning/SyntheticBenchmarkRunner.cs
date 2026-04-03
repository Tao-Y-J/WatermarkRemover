using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WatermarkRemover.App.Models;
using WatermarkRemover.App.Services;

internal static class SyntheticBenchmarkRunner
{
    private static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp"];
    private static readonly HersheyFonts[] WatermarkFonts =
    [
        HersheyFonts.HersheySimplex,
        HersheyFonts.HersheyDuplex,
        HersheyFonts.HersheyTriplex,
        HersheyFonts.HersheyComplex,
    ];

    private static readonly string[] WatermarkTexts =
    [
        "SHOT ON MOBILE",
        "SAMPLE WATERMARK",
        "PHOTO SHARE ONLY",
        "SOCIAL PREVIEW",
        "STORY SNAPSHOT",
        "MOMENT ARCHIVE",
        "RAW PREVIEW",
        "CITY LIGHTS",
        "PORTRAIT MODE",
        "SCENE SAMPLE",
    ];

    public static async Task<int> RunAsync(
        string originalsDirectory,
        string workspaceRoot,
        string modelPath,
        int requestedCount,
        BottomTextWatermarkDetectorOptions detectorOptions,
        WatermarkRepairOptions repairOptions)
    {
        if (!Directory.Exists(originalsDirectory))
        {
            Console.WriteLine($"Originals directory not found: {originalsDirectory}");
            return 1;
        }

        Directory.CreateDirectory(workspaceRoot);

        var originalPaths = Directory.EnumerateFiles(originalsDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, requestedCount))
            .ToList();

        if (originalPaths.Count < requestedCount)
        {
            Console.WriteLine($"Only found {originalPaths.Count} supported images in {originalsDirectory}.");
            return 1;
        }

        var runRoot = Path.Combine(workspaceRoot, "synthetic-benchmark", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var watermarkedDir = Path.Combine(runRoot, "watermarked");
        var maskDir = Path.Combine(runRoot, "ground-truth-mask");
        var detectedMaskDir = Path.Combine(runRoot, "detected-mask");
        var debugDir = Path.Combine(runRoot, "detector-debug");
        var autoDir = Path.Combine(runRoot, "auto-result");
        var oracleDir = Path.Combine(runRoot, "oracle-result");
        var reportDir = Path.Combine(runRoot, "reports");

        Directory.CreateDirectory(watermarkedDir);
        Directory.CreateDirectory(maskDir);
        Directory.CreateDirectory(detectedMaskDir);
        Directory.CreateDirectory(debugDir);
        Directory.CreateDirectory(autoDir);
        Directory.CreateDirectory(oracleDir);
        Directory.CreateDirectory(reportDir);

        var imageFileService = new ImageFileService();
        var detector = new BottomTextWatermarkDetector(detectorOptions);
        var oracleRepairService = new WatermarkRemovalService(repairOptions);
        var repairRoutingLabel = $"AutoRouting(default={repairOptions.PresetName},wide={WatermarkRepairOptions.WideContext.PresetName})";
        const string executionProvider = "CPU";
        Console.WriteLine($"EXECUTION_PROVIDER: {executionProvider}");
        Console.WriteLine($"DETECTOR_PROFILE: {detectorOptions.PresetName}");
        Console.WriteLine($"REPAIR_PRESET: {repairRoutingLabel}");
        var rows = new List<BenchmarkRow>(originalPaths.Count);
        var totalStopwatch = Stopwatch.StartNew();

        for (var index = 0; index < originalPaths.Count; index++)
        {
            var originalPath = originalPaths[index];
            using var original = Cv2.ImRead(originalPath, ImreadModes.Color);
            if (original.Empty())
            {
                continue;
            }

            var caseId = $"{index + 1:D4}-{SanitizeFileName(Path.GetFileNameWithoutExtension(originalPath))}";
            var random = new Random(ComputeStableSeed(originalPath, index));
            using var syntheticCase = BuildSyntheticCase(original, random);

            var watermarkedPath = Path.Combine(watermarkedDir, $"{caseId}.jpg");
            var groundTruthMaskPath = Path.Combine(maskDir, $"{caseId}.png");
            Cv2.ImWrite(watermarkedPath, syntheticCase.Watermarked);
            Cv2.ImWrite(groundTruthMaskPath, syntheticCase.GroundTruthMask);

            var detectionStopwatch = Stopwatch.StartNew();
            var detection = await detector.DetectAsync(watermarkedPath);
            detectionStopwatch.Stop();

            using var detectedMask = CreateBinaryMask(detection.MaskImage, original.Size());
            var detectedMaskPixels = Cv2.CountNonZero(detectedMask);
            var groundTruthPixels = Cv2.CountNonZero(syntheticCase.GroundTruthMask);

            var detectedMaskPath = Path.Combine(detectedMaskDir, $"{caseId}.png");
            var debugPath = Path.Combine(debugDir, $"{caseId}.jpg");
            Cv2.ImWrite(detectedMaskPath, detectedMask);
            SaveBitmapIfPresent(detection.DebugImage, debugPath);
            var (watermarkedPsnr, watermarkedMae) = ComputeMaskedMetrics(original, syntheticCase.Watermarked, syntheticCase.GroundTruthMask);

            string? autoResultPath = null;
            string? autoRepairPreset = null;
            double autoRepairMs = 0d;
            double autoPsnr = double.NaN;
            double autoMae = double.NaN;
            var autoSkippedForLowConfidence = detectedMaskPixels > 0
                && WatermarkDetectionPolicy.ShouldKeepCandidate(detection)
                && !WatermarkDetectionPolicy.ShouldAutoApply(detection);

            var autoRejectedCandidate = detectedMaskPixels > 0
                && !WatermarkDetectionPolicy.ShouldKeepCandidate(detection);

            if (detectedMaskPixels > 0 && detection.MaskImage is not null && !autoSkippedForLowConfidence && !autoRejectedCandidate)
            {
                var autoRepairOptions = WatermarkRepairOptions.ResolveAutoPreset(
                    detection.Confidence,
                    detectedMask,
                    original.Size());
                autoRepairPreset = autoRepairOptions.PresetName;
                var autoRepairService = new WatermarkRemovalService(autoRepairOptions);
                var autoStopwatch = Stopwatch.StartNew();
                var autoResultBitmap = await autoRepairService.RemoveWatermarkAsync(new InpaintingRequest
                {
                    ImagePath = watermarkedPath,
                    ModelPath = modelPath,
                    MaskImage = detection.MaskImage,
                });
                autoStopwatch.Stop();

                autoRepairMs = autoStopwatch.Elapsed.TotalMilliseconds;
                autoResultPath = Path.Combine(autoDir, $"{caseId}.jpg");
                await imageFileService.SaveAsync(autoResultBitmap, autoResultPath);

                using var autoResult = BitmapSourceConverter.ToMat(autoResultBitmap);
                (autoPsnr, autoMae) = ComputeMaskedMetrics(original, autoResult, syntheticCase.GroundTruthMask);
            }
            else if (autoSkippedForLowConfidence)
            {
                autoResultPath = watermarkedPath;
                autoPsnr = watermarkedPsnr;
                autoMae = watermarkedMae;
            }

            var oracleStopwatch = Stopwatch.StartNew();
            var oracleMaskBitmap = BitmapSourceConverter.ToBitmapSource(syntheticCase.GroundTruthMask);
            oracleMaskBitmap.Freeze();
            var oracleResultBitmap = await oracleRepairService.RemoveWatermarkAsync(new InpaintingRequest
            {
                ImagePath = watermarkedPath,
                ModelPath = modelPath,
                MaskImage = oracleMaskBitmap,
            });
            oracleStopwatch.Stop();

            var oracleResultPath = Path.Combine(oracleDir, $"{caseId}.jpg");
            await imageFileService.SaveAsync(oracleResultBitmap, oracleResultPath);

            using var oracleResult = BitmapSourceConverter.ToMat(oracleResultBitmap);
            var (oraclePsnr, oracleMae) = ComputeMaskedMetrics(original, oracleResult, syntheticCase.GroundTruthMask);
            var maskIou = ComputeMaskIou(syntheticCase.GroundTruthMask, detectedMask);
            var category = Categorize(maskIou, detectedMaskPixels, autoPsnr, oraclePsnr, watermarkedPsnr, autoSkippedForLowConfidence, autoRejectedCandidate);

            rows.Add(new BenchmarkRow(
                caseId,
                syntheticCase.Style,
                originalPath,
                watermarkedPath,
                groundTruthMaskPath,
                detectedMaskPath,
                debugPath,
                autoResultPath,
                oracleResultPath,
                autoRepairPreset,
                autoSkippedForLowConfidence,
                detection.Confidence,
                groundTruthPixels,
                detectedMaskPixels,
                maskIou,
                watermarkedPsnr,
                autoPsnr,
                oraclePsnr,
                watermarkedMae,
                autoMae,
                oracleMae,
                autoPsnr - watermarkedPsnr,
                oraclePsnr - watermarkedPsnr,
                detectionStopwatch.Elapsed.TotalMilliseconds,
                autoRepairMs,
                oracleStopwatch.Elapsed.TotalMilliseconds,
                category));

            Console.WriteLine(
                $"[{index + 1:D4}/{originalPaths.Count:D4}] {caseId} " +
                $"conf={detection.Confidence:F2} iou={maskIou:F2} " +
                $"auto+={rows[^1].AutoImprovementDb:F2}dB oracle+={rows[^1].OracleImprovementDb:F2}dB " +
                $"{(string.IsNullOrWhiteSpace(autoRepairPreset) ? string.Empty : $"preset={autoRepairPreset} ")}{category}");
        }

        totalStopwatch.Stop();

        await WriteReportsAsync(
            runRoot,
            requestedCount,
            rows,
            totalStopwatch.Elapsed,
            detectorOptions.PresetName,
            repairRoutingLabel);
        Console.WriteLine("EXECUTION_PROVIDER_FINAL: CPU");
        Console.WriteLine($"RUN_ROOT: {runRoot}");
        return 0;
    }

    private static SyntheticCase BuildSyntheticCase(Mat original, Random random)
    {
        var width = original.Width;
        var height = original.Height;
        var font = WatermarkFonts[random.Next(WatermarkFonts.Length)];
        var lineCount = random.NextDouble() < 0.24 ? 2 : 1;
        var fontScale = Math.Clamp((width / 1280d) * NextDouble(random, 0.72, 1.18), 0.45, 1.25);
        var thickness = random.Next(1, 3);
        var textColorValue = random.Next(214, 251);
        var opacity = NextDouble(random, 0.14, 0.28);
        var blurSigma = NextDouble(random, 0.55, 1.35);
        var horizontalOffset = (int)Math.Round(width * NextDouble(random, -0.08, 0.08));
        var bottomMargin = Math.Max(16, (int)Math.Round(height * NextDouble(random, 0.032, 0.075)));
        var lineGap = Math.Max(12, (int)Math.Round(height * NextDouble(random, 0.020, 0.032)));
        var text = WatermarkTexts[random.Next(WatermarkTexts.Length)];
        var suffix = $"{random.Next(10, 99)}.{random.Next(100, 999)}";
        var secondary = $"{WatermarkTexts[random.Next(WatermarkTexts.Length)]} {suffix}";
        string[] lines = lineCount == 1
            ? [$"{text} {suffix}"]
            : [$"{text} {suffix}", secondary];

        var textMask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var textSize = Cv2.GetTextSize(line, font, fontScale, thickness, out var baseline);
            var x = Math.Max(8, (width - textSize.Width) / 2 + horizontalOffset);
            var y = height - bottomMargin - ((lines.Length - 1 - lineIndex) * (textSize.Height + lineGap));
            y = Math.Clamp(y, textSize.Height + baseline + 4, height - 8);

            Cv2.PutText(textMask, line, new Point(x, y), font, fontScale, Scalar.White, thickness, LineTypes.AntiAlias);
        }

        using var softMask = textMask.Clone();
        Cv2.GaussianBlur(softMask, softMask, new OpenCvSharp.Size(0, 0), blurSigma);
        Cv2.Max(softMask, textMask, softMask);

        if (EstimateVisibility(original, softMask) < 10d)
        {
            opacity = Math.Min(0.34, opacity + 0.05);
        }

        using var alphaMask = new Mat();
        softMask.ConvertTo(alphaMask, MatType.CV_32FC1, opacity / 255d);

        using var alpha3 = new Mat();
        Cv2.CvtColor(alphaMask, alpha3, ColorConversionCodes.GRAY2BGR);

        using var originalFloat = new Mat();
        original.ConvertTo(originalFloat, MatType.CV_32FC3, 1d / 255d);

        using var colorLayer = new Mat(
            original.Size(),
            MatType.CV_32FC3,
            new Scalar(textColorValue / 255d, textColorValue / 255d, textColorValue / 255d));
        using var ones = new Mat(alpha3.Size(), alpha3.Type(), Scalar.All(1.0));
        using var inverseAlpha3 = new Mat();
        using var weightedOriginal = new Mat();
        using var weightedWatermark = new Mat();
        using var blended = new Mat();

        Cv2.Subtract(ones, alpha3, inverseAlpha3);
        Cv2.Multiply(originalFloat, inverseAlpha3, weightedOriginal);
        Cv2.Multiply(colorLayer, alpha3, weightedWatermark);
        Cv2.Add(weightedOriginal, weightedWatermark, blended);

        var watermarked = new Mat();
        blended.ConvertTo(watermarked, MatType.CV_8UC3, 255d);

        var groundTruthMask = new Mat();
        Cv2.Threshold(softMask, groundTruthMask, 12, 255, ThresholdTypes.Binary);

        return new SyntheticCase(
            watermarked,
            groundTruthMask,
            $"font={font};lines={lineCount};opacity={opacity:F2};sigma={blurSigma:F2};gray={textColorValue};offset={horizontalOffset}");
    }

    private static async Task WriteReportsAsync(
        string runRoot,
        int requestedCount,
        IReadOnlyList<BenchmarkRow> rows,
        TimeSpan elapsed,
        string detectorProfile,
        string repairPreset)
    {
        var reportDir = Path.Combine(runRoot, "reports");
        Directory.CreateDirectory(reportDir);

        var summary = BuildSummary(runRoot, requestedCount, rows, elapsed, detectorProfile, repairPreset);
        var summaryJsonPath = Path.Combine(reportDir, "summary.json");
        var summaryCsvPath = Path.Combine(reportDir, "cases.csv");
        var summaryTextPath = Path.Combine(reportDir, "summary.txt");
        var selectionPath = Path.Combine(reportDir, "selected-originals.txt");

        await File.WriteAllTextAsync(
            summaryJsonPath,
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

        await File.WriteAllTextAsync(summaryCsvPath, BuildCsv(rows));
        await File.WriteAllTextAsync(summaryTextPath, BuildSummaryText(summary));
        await File.WriteAllLinesAsync(selectionPath, rows.Select(row => row.OriginalPath));

        Console.WriteLine($"SUMMARY_JSON: {summaryJsonPath}");
        Console.WriteLine($"SUMMARY_CSV: {summaryCsvPath}");
        Console.WriteLine($"SUMMARY_TEXT: {summaryTextPath}");
    }

    private static Summary BuildSummary(
        string runRoot,
        int requestedCount,
        IReadOnlyList<BenchmarkRow> rows,
        TimeSpan elapsed,
        string detectorProfile,
        string repairPreset)
    {
        var validAutoRows = rows.Where(row => !double.IsNaN(row.AutoPsnr)).ToList();
        var autoImprovements = validAutoRows.Select(row => row.AutoImprovementDb).OrderBy(value => value).ToArray();

        return new Summary(
            runRoot,
            detectorProfile,
            repairPreset,
            requestedCount,
            rows.Count,
            elapsed.TotalSeconds,
            rows.Count == 0 ? 0d : rows.Count(row => row.DetectedPixels > 0) / (double)rows.Count,
            rows.Count == 0 ? 0d : rows.Average(row => row.MaskIou),
            rows.Count == 0 ? 0d : rows.Average(row => row.DetectionMs),
            validAutoRows.Count == 0 ? 0d : validAutoRows.Average(row => row.AutoRepairMs),
            rows.Count == 0 ? 0d : rows.Average(row => row.OracleRepairMs),
            rows.Count == 0 ? 0d : rows.Average(row => row.WatermarkedPsnr),
            validAutoRows.Count == 0 ? 0d : validAutoRows.Average(row => row.AutoPsnr),
            rows.Count == 0 ? 0d : rows.Average(row => row.OraclePsnr),
            autoImprovements.Length == 0 ? 0d : autoImprovements.Average(),
            rows.Count == 0 ? 0d : rows.Average(row => row.OracleImprovementDb),
            Percentile(autoImprovements, 0.10),
            Percentile(autoImprovements, 0.50),
            Percentile(autoImprovements, 0.90),
            rows.Where(row => !string.IsNullOrWhiteSpace(row.AutoRepairPreset))
                .GroupBy(row => row.AutoRepairPreset!, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            rows.GroupBy(row => row.Category)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal));
    }

    private static string BuildSummaryText(Summary summary)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Run root: {summary.RunRoot}");
        builder.AppendLine($"Detector profile: {summary.DetectorProfile}");
        builder.AppendLine($"Repair preset: {summary.RepairPreset}");
        builder.AppendLine($"Requested count: {summary.RequestedCount}");
        builder.AppendLine($"Processed count: {summary.ProcessedCount}");
        builder.AppendLine($"Elapsed seconds: {summary.TotalElapsedSeconds:F2}");
        builder.AppendLine($"Detection rate: {summary.DetectionRate:P2}");
        builder.AppendLine($"Avg mask IoU: {summary.AvgMaskIou:F4}");
        builder.AppendLine($"Avg detection ms: {summary.AvgDetectionMs:F2}");
        builder.AppendLine($"Avg auto repair ms: {summary.AvgAutoRepairMs:F2}");
        builder.AppendLine($"Avg oracle repair ms: {summary.AvgOracleRepairMs:F2}");
        builder.AppendLine($"Avg baseline PSNR: {summary.AvgWatermarkedPsnr:F3}");
        builder.AppendLine($"Avg auto PSNR: {summary.AvgAutoPsnr:F3}");
        builder.AppendLine($"Avg oracle PSNR: {summary.AvgOraclePsnr:F3}");
        builder.AppendLine($"Avg auto improvement dB: {summary.AvgAutoImprovementDb:F3}");
        builder.AppendLine($"Avg oracle improvement dB: {summary.AvgOracleImprovementDb:F3}");
        builder.AppendLine($"P10/P50/P90 auto improvement dB: {summary.P10AutoImprovementDb:F3} / {summary.P50AutoImprovementDb:F3} / {summary.P90AutoImprovementDb:F3}");
        if (summary.AutoRepairPresetBreakdown.Count > 0)
        {
            builder.AppendLine("Auto repair preset breakdown:");
            foreach (var (preset, count) in summary.AutoRepairPresetBreakdown)
            {
                builder.AppendLine($"  {preset}: {count}");
            }
        }

        builder.AppendLine("Failure breakdown:");

        foreach (var (category, count) in summary.FailureBreakdown)
        {
            builder.AppendLine($"  {category}: {count}");
        }

        return builder.ToString();
    }

    private static string BuildCsv(IReadOnlyList<BenchmarkRow> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(
            ',',
            [
                "case_id",
                "style",
                "original_path",
                "watermarked_path",
                "ground_truth_mask_path",
                "detected_mask_path",
                "debug_path",
                "auto_result_path",
                "oracle_result_path",
                "auto_repair_preset",
                "auto_skipped_for_low_confidence",
                "detection_confidence",
                "ground_truth_pixels",
                "detected_pixels",
                "mask_iou",
                "watermarked_psnr",
                "auto_psnr",
                "oracle_psnr",
                "watermarked_mae",
                "auto_mae",
                "oracle_mae",
                "auto_improvement_db",
                "oracle_improvement_db",
                "detection_ms",
                "auto_repair_ms",
                "oracle_repair_ms",
                "category",
            ]));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(
                ',',
                [
                    Csv(row.CaseId),
                    Csv(row.Style),
                    Csv(row.OriginalPath),
                    Csv(row.WatermarkedPath),
                    Csv(row.GroundTruthMaskPath),
                    Csv(row.DetectedMaskPath),
                    Csv(row.DebugPath),
                    Csv(row.AutoResultPath ?? string.Empty),
                    Csv(row.OracleResultPath),
                    Csv(row.AutoRepairPreset ?? string.Empty),
                    Csv(row.AutoSkippedForLowConfidence),
                    Csv(row.DetectionConfidence),
                    Csv(row.GroundTruthPixels),
                    Csv(row.DetectedPixels),
                    Csv(row.MaskIou),
                    Csv(row.WatermarkedPsnr),
                    Csv(row.AutoPsnr),
                    Csv(row.OraclePsnr),
                    Csv(row.WatermarkedMae),
                    Csv(row.AutoMae),
                    Csv(row.OracleMae),
                    Csv(row.AutoImprovementDb),
                    Csv(row.OracleImprovementDb),
                    Csv(row.DetectionMs),
                    Csv(row.AutoRepairMs),
                    Csv(row.OracleRepairMs),
                    Csv(row.Category),
                ]));
        }

        return builder.ToString();
    }

    private static (double psnr, double mae) ComputeMaskedMetrics(Mat original, Mat candidate, Mat mask)
    {
        double absoluteError = 0d;
        double squaredError = 0d;
        long channelSamples = 0;

        for (var y = 0; y < mask.Rows; y++)
        {
            for (var x = 0; x < mask.Cols; x++)
            {
                if (mask.At<byte>(y, x) == 0)
                {
                    continue;
                }

                var originalPixel = original.At<Vec3b>(y, x);
                var candidatePixel = candidate.At<Vec3b>(y, x);
                for (var channel = 0; channel < 3; channel++)
                {
                    var delta = originalPixel[channel] - candidatePixel[channel];
                    absoluteError += Math.Abs(delta);
                    squaredError += delta * delta;
                    channelSamples++;
                }
            }
        }

        if (channelSamples == 0)
        {
            return (double.NaN, double.NaN);
        }

        var mae = absoluteError / channelSamples;
        var mse = squaredError / channelSamples;
        var psnr = mse <= double.Epsilon ? 99d : 10d * Math.Log10((255d * 255d) / mse);
        return (psnr, mae);
    }

    private static double ComputeMaskIou(Mat groundTruthMask, Mat detectedMask)
    {
        using var intersection = new Mat();
        using var union = new Mat();
        Cv2.BitwiseAnd(groundTruthMask, detectedMask, intersection);
        Cv2.BitwiseOr(groundTruthMask, detectedMask, union);

        var unionPixels = Cv2.CountNonZero(union);
        if (unionPixels == 0)
        {
            return 0d;
        }

        return Cv2.CountNonZero(intersection) / (double)unionPixels;
    }

    private static string Categorize(
        double maskIou,
        int detectedPixels,
        double autoPsnr,
        double oraclePsnr,
        double watermarkedPsnr,
        bool autoSkippedForLowConfidence,
        bool autoRejectedCandidate)
    {
        if (autoRejectedCandidate)
        {
            return "rejected-candidate";
        }

        if (autoSkippedForLowConfidence)
        {
            return "low-confidence-skip";
        }

        if (detectedPixels <= 0 || double.IsNaN(autoPsnr))
        {
            return "no-detection";
        }

        var autoGain = autoPsnr - watermarkedPsnr;
        var oracleGain = oraclePsnr - watermarkedPsnr;

        if (maskIou < 0.25)
        {
            return oracleGain >= 4d ? "mask-miss" : "hard-case";
        }

        if (autoGain < 2d)
        {
            return oracleGain >= autoGain + 3d ? "repair-limited-by-mask" : "repair-limited";
        }

        if (oracleGain >= autoGain + 3d)
        {
            return "needs-better-mask";
        }

        return "pass";
    }

    private static double EstimateVisibility(Mat source, Mat mask)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var mean = new Mat();
        using var stddev = new Mat();
        Cv2.MeanStdDev(gray, mean, stddev, mask);
        return 255d - mean.At<double>(0);
    }

    private static Mat CreateBinaryMask(BitmapSource? maskBitmap, OpenCvSharp.Size targetSize)
    {
        var mask = new Mat(targetSize.Height, targetSize.Width, MatType.CV_8UC1, Scalar.Black);
        if (maskBitmap is null)
        {
            return mask;
        }

        var grayMask = EnsureGray8Mask(maskBitmap);
        var stride = Math.Max(1, (grayMask.PixelWidth * grayMask.Format.BitsPerPixel + 7) / 8);
        var pixelBuffer = new byte[stride * grayMask.PixelHeight];
        grayMask.CopyPixels(pixelBuffer, stride, 0);

        using var gray = new Mat(grayMask.PixelHeight, grayMask.PixelWidth, MatType.CV_8UC1);
        System.Runtime.InteropServices.Marshal.Copy(pixelBuffer, 0, gray.Data, pixelBuffer.Length);

        using var resized = new Mat();
        if (gray.Size() != targetSize)
        {
            Cv2.Resize(gray, resized, targetSize, 0, 0, InterpolationFlags.Nearest);
        }
        else
        {
            gray.CopyTo(resized);
        }

        Cv2.Threshold(resized, mask, 10, 255, ThresholdTypes.Binary);
        return mask;
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

    private static void SaveBitmapIfPresent(BitmapSource? bitmap, string path)
    {
        if (bitmap is null)
        {
            return;
        }

        using var mat = BitmapSourceConverter.ToMat(bitmap);
        Cv2.ImWrite(path, mat);
    }

    private static string Csv<T>(T value)
    {
        var text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value?.ToString() ?? string.Empty,
        };

        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }

        return text;
    }

    private static double Percentile(double[] values, double percentile)
    {
        if (values.Length == 0)
        {
            return 0d;
        }

        var clampedPercentile = Math.Clamp(percentile, 0d, 1d);
        var position = clampedPercentile * (values.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return values[lower];
        }

        var weight = position - lower;
        return values[lower] + ((values[upper] - values[lower]) * weight);
    }

    private static int ComputeStableSeed(string path, int index)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in path)
            {
                hash = (hash * 31) + character;
            }

            return hash ^ (index * 7919);
        }
    }

    private static double NextDouble(Random random, double minValue, double maxValue)
    {
        return minValue + (random.NextDouble() * (maxValue - minValue));
    }

    private static string SanitizeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.ToString();
    }

    private sealed record BenchmarkRow(
        string CaseId,
        string Style,
        string OriginalPath,
        string WatermarkedPath,
        string GroundTruthMaskPath,
        string DetectedMaskPath,
        string DebugPath,
        string? AutoResultPath,
        string OracleResultPath,
        string? AutoRepairPreset,
        bool AutoSkippedForLowConfidence,
        double DetectionConfidence,
        int GroundTruthPixels,
        int DetectedPixels,
        double MaskIou,
        double WatermarkedPsnr,
        double AutoPsnr,
        double OraclePsnr,
        double WatermarkedMae,
        double AutoMae,
        double OracleMae,
        double AutoImprovementDb,
        double OracleImprovementDb,
        double DetectionMs,
        double AutoRepairMs,
        double OracleRepairMs,
        string Category);

    private sealed record Summary(
        string RunRoot,
        string DetectorProfile,
        string RepairPreset,
        int RequestedCount,
        int ProcessedCount,
        double TotalElapsedSeconds,
        double DetectionRate,
        double AvgMaskIou,
        double AvgDetectionMs,
        double AvgAutoRepairMs,
        double AvgOracleRepairMs,
        double AvgWatermarkedPsnr,
        double AvgAutoPsnr,
        double AvgOraclePsnr,
        double AvgAutoImprovementDb,
        double AvgOracleImprovementDb,
        double P10AutoImprovementDb,
        double P50AutoImprovementDb,
        double P90AutoImprovementDb,
        IReadOnlyDictionary<string, int> AutoRepairPresetBreakdown,
        IReadOnlyDictionary<string, int> FailureBreakdown);

    private sealed class SyntheticCase : IDisposable
    {
        public SyntheticCase(Mat watermarked, Mat groundTruthMask, string style)
        {
            Watermarked = watermarked;
            GroundTruthMask = groundTruthMask;
            Style = style;
        }

        public Mat Watermarked { get; }

        public Mat GroundTruthMask { get; }

        public string Style { get; }

        public void Dispose()
        {
            Watermarked.Dispose();
            GroundTruthMask.Dispose();
        }
    }
}
