using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WatermarkRemover.App.Models;

namespace WatermarkRemover.App.Services;

public sealed class WatermarkRemovalService : IWatermarkRemovalService
{
    private static readonly ConcurrentDictionary<string, Lazy<InferenceSession>> SessionCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WatermarkRepairOptions _options;

    public WatermarkRemovalService(WatermarkRepairOptions? options = null)
    {
        _options = options ?? WatermarkRepairOptions.Default;
    }

    public WatermarkRepairOptions Options => _options;

    public Task<BitmapSource> RemoveWatermarkAsync(
        InpaintingRequest request,
        IProgress<WatermarkRemovalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelPath);

        if (!File.Exists(request.ImagePath))
        {
            throw new FileNotFoundException("未找到待处理图片。", request.ImagePath);
        }

        if (!File.Exists(request.ModelPath))
        {
            throw new FileNotFoundException("未找到 ONNX 模型文件。", request.ModelPath);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(progress, "正在读取原图...");

            using var source = Cv2.ImRead(request.ImagePath, ImreadModes.Color);
            if (source.Empty())
            {
                throw new InvalidOperationException("图片加载失败，OpenCvSharp 未能读取输入图像。");
            }

            ReportProgress(progress, "正在生成修复蒙版...");
            using var binaryMask = CreateBinaryMask(request.MaskImage, source.Size(), _options);
            if (Cv2.CountNonZero(binaryMask) == 0)
            {
                throw new InvalidOperationException("当前选区为空，请先框选需要修复的水印区域。");
            }

            ReportProgress(progress, "正在分析水印区域...");
            if (TryRemoveSmallWhiteWatermark(source, binaryMask, out var dewatermarked))
            {
                ReportProgress(progress, "正在应用快速修复结果...");
                var directBitmap = BitmapSourceConverter.ToBitmapSource(dewatermarked);
                directBitmap.Freeze();
                dewatermarked.Dispose();
                ReportProgress(progress, "处理完成");
                return directBitmap;
            }

            ReportProgress(progress, "正在加载推理模型...");
            var session = GetOrCreateSession(request.ModelPath);
            var (imageInputName, maskInputName) = ResolveInputNames(session);
            var outputName = ResolveOutputName(session);
            var targetSize = ResolveTargetSize(session.InputMetadata[imageInputName], source.Size());

            ReportProgress(progress, "正在准备推理区域...");
            var processingRect = CalculateProcessingRect(binaryMask, source.Size(), _options);
            using var sourceRegion = new Mat(source, processingRect);
            using var maskRegion = new Mat(binaryMask, processingRect);

            using var prepared = PrepareInput(sourceRegion, maskRegion, targetSize);
            ReportProgress(progress, "正在生成图像输入...");
            var imageTensor = CreateImageTensor(prepared.Image, cancellationToken);
            ReportProgress(progress, "正在生成蒙版输入...");
            var maskTensor = CreateMaskTensor(prepared.Mask, cancellationToken);

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(imageInputName, imageTensor),
                NamedOnnxValue.CreateFromTensor(maskInputName, maskTensor),
            };

            cancellationToken.ThrowIfCancellationRequested();

            ReportProgress(progress, "正在执行模型推理...");
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs, new[] { outputName });
            var outputTensor = results.First().AsTensor<float>();

            ReportProgress(progress, "正在解码推理结果...");
            using var networkOutput = TensorToBgrMat(outputTensor, cancellationToken);
            using var restoredRegion = RestoreToOriginalSize(
                networkOutput,
                prepared.ContentRect,
                new OpenCvSharp.Size(processingRect.Width, processingRect.Height));

            ReportProgress(progress, "正在合成最终图像...");
            using var final = source.Clone();
            using var targetRegion = new Mat(final, processingRect);
            BlendRestoredRegion(sourceRegion, restoredRegion, maskRegion, targetRegion, _options);

            var bitmap = BitmapSourceConverter.ToBitmapSource(final);
            bitmap.Freeze();
            ReportProgress(progress, "处理完成");
            return bitmap;
        }, cancellationToken);
    }

    private static InferenceSession GetOrCreateSession(string modelPath)
    {
        var normalizedPath = Path.GetFullPath(modelPath);
        var lazySession = SessionCache.GetOrAdd(
            normalizedPath,
            static path => new Lazy<InferenceSession>(
                () => CreateSession(path),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return lazySession.Value;
        }
        catch
        {
            SessionCache.TryRemove(normalizedPath, out _);
            throw;
        }
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
        };

        return new InferenceSession(modelPath, sessionOptions);
    }

    private static (string imageInputName, string maskInputName) ResolveInputNames(InferenceSession session)
    {
        string? imageInput = null;
        string? maskInput = null;

        foreach (var (name, metadata) in session.InputMetadata)
        {
            if (name.Contains("image", StringComparison.OrdinalIgnoreCase))
            {
                imageInput ??= name;
            }

            if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
            {
                maskInput ??= name;
            }

            if (metadata.Dimensions.Length >= 4)
            {
                if (metadata.Dimensions[1] == 3)
                {
                    imageInput ??= name;
                }
                else if (metadata.Dimensions[1] == 1)
                {
                    maskInput ??= name;
                }
            }
        }

        if (imageInput is null || maskInput is null)
        {
            var fallbackNames = session.InputMetadata.Keys.ToArray();
            if (fallbackNames.Length < 2)
            {
                throw new InvalidOperationException("模型输入数量不足，至少需要 image 和 mask 两个输入。");
            }

            imageInput ??= fallbackNames[0];
            maskInput ??= fallbackNames[1];
        }

        return (imageInput, maskInput);
    }

    private static string ResolveOutputName(InferenceSession session)
    {
        foreach (var (name, metadata) in session.OutputMetadata)
        {
            if (metadata.Dimensions.Length >= 4 && metadata.Dimensions[1] == 3)
            {
                return name;
            }
        }

        return session.OutputMetadata.Keys.First();
    }

    private static OpenCvSharp.Size ResolveTargetSize(NodeMetadata metadata, OpenCvSharp.Size sourceSize)
    {
        if (metadata.Dimensions.Length < 4)
        {
            throw new InvalidOperationException("当前模型不是标准 4 维图像输入，无法自动推断尺寸。");
        }

        var targetHeight = metadata.Dimensions[^2] > 0
            ? metadata.Dimensions[^2]
            : AlignToMultiple(sourceSize.Height, 8);

        var targetWidth = metadata.Dimensions[^1] > 0
            ? metadata.Dimensions[^1]
            : AlignToMultiple(sourceSize.Width, 8);

        return new OpenCvSharp.Size(targetWidth, targetHeight);
    }

    private static Mat CreateBinaryMask(BitmapSource maskImage, OpenCvSharp.Size targetSize, WatermarkRepairOptions options)
    {
        var grayMask = EnsureGray8Mask(maskImage);
        var stride = Math.Max(1, (grayMask.PixelWidth * grayMask.Format.BitsPerPixel + 7) / 8);
        var pixelBuffer = new byte[stride * grayMask.PixelHeight];
        grayMask.CopyPixels(pixelBuffer, stride, 0);

        using var grayscale = new Mat(grayMask.PixelHeight, grayMask.PixelWidth, MatType.CV_8UC1);
        Marshal.Copy(pixelBuffer, 0, grayscale.Data, pixelBuffer.Length);

        using var resized = new Mat();
        if (grayscale.Size() != targetSize)
        {
            Cv2.Resize(grayscale, resized, targetSize, 0, 0, InterpolationFlags.Nearest);
        }
        else
        {
            grayscale.CopyTo(resized);
        }

        var binary = new Mat();
        Cv2.Threshold(resized, binary, 10, 255, ThresholdTypes.Binary);

        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 3));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Close, closeKernel);

        if (options.MaskDilateKernelSize > 1 && options.MaskDilateIterations > 0)
        {
            using var kernel = Cv2.GetStructuringElement(
                MorphShapes.Rect,
                new OpenCvSharp.Size(options.MaskDilateKernelSize, options.MaskDilateKernelSize));

            Cv2.Dilate(binary, binary, kernel, iterations: options.MaskDilateIterations);
        }

        return binary;
    }

    private static BitmapSource EnsureGray8Mask(BitmapSource source)
    {
        if (source.Format == PixelFormats.Gray8)
        {
            return source;
        }

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Gray8;
        converted.EndInit();
        converted.Freeze();

        return converted;
    }

    private static bool TryRemoveSmallWhiteWatermark(Mat source, Mat mask, out Mat result)
    {
        result = default!;

        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return false;
        }

        var bounds = Cv2.BoundingRect(contours[0]);
        for (var i = 1; i < contours.Length; i++)
        {
            bounds = Union(bounds, Cv2.BoundingRect(contours[i]));
        }

        var areaRatio = Cv2.CountNonZero(mask) / (double)(source.Rows * source.Cols);
        var thinHeightRatio = bounds.Height / (double)source.Rows;
        var widthRatio = bounds.Width / (double)source.Cols;

        if (areaRatio > 0.02 || thinHeightRatio > 0.08 || widthRatio > 0.75)
        {
            return false;
        }

        using var refinedMask = RefineWhiteWatermarkMask(source, mask, bounds);
        if (Cv2.CountNonZero(refinedMask) == 0)
        {
            return false;
        }

        var refinedAreaRatio = Cv2.CountNonZero(refinedMask) / (double)(source.Rows * source.Cols);
        if (refinedAreaRatio > 0.03)
        {
            return false;
        }

        var (meanBrightness, stdDev) = CalculateMaskedLuminanceStats(source, refinedMask);
        if (meanBrightness > 95 || stdDev > 24)
        {
            return false;
        }

        var (alpha, sigma) = CalculateDeblendStrength(meanBrightness);
        result = RunWhiteWatermarkDeblend(source, refinedMask, alpha, sigma);
        return true;
    }

    private static Mat RefineWhiteWatermarkMask(Mat source, Mat coarseMask, OpenCvSharp.Rect region)
    {
        var fullSize = source.Size();
        var expanded = ExpandRect(region, fullSize, 24, 18);
        using var roi = new Mat(source, expanded);
        using var coarseMaskRegion = new Mat(coarseMask, expanded);
        using var gray = new Mat();
        using var topHat = new Mat();
        using var candidateMask = new Mat();
        using var brightPixels = new Mat();
        using var topHatKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(19, 19));
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 3));
        using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));

        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MorphologyEx(gray, topHat, MorphTypes.TopHat, topHatKernel);
        Cv2.Threshold(topHat, candidateMask, 10, 255, ThresholdTypes.Binary);
        Cv2.Threshold(gray, brightPixels, 145, 255, ThresholdTypes.Binary);
        Cv2.BitwiseAnd(candidateMask, brightPixels, candidateMask);
        Cv2.MorphologyEx(candidateMask, candidateMask, MorphTypes.Close, closeKernel);
        Cv2.Dilate(candidateMask, candidateMask, dilateKernel, iterations: 1);

        Cv2.FindContours(candidateMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var coreBounds = new OpenCvSharp.Rect(
            region.X - expanded.X,
            region.Y - expanded.Y,
            region.Width,
            region.Height);

        var verticalBand = ClampRect(
            new OpenCvSharp.Rect(
                0,
                Math.Max(0, coreBounds.Top - 3),
                roi.Width,
                Math.Min(roi.Height - Math.Max(0, coreBounds.Top - 3), coreBounds.Height + 10)),
            roi.Size());

        var refinedMask = new Mat(fullSize.Height, fullSize.Width, MatType.CV_8UC1, Scalar.Black);
        foreach (var contour in contours)
        {
            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < 8 || bounds.Height < 4)
            {
                continue;
            }

            var centerX = bounds.X + bounds.Width / 2;
            var centerY = bounds.Y + bounds.Height / 2;
            if (!verticalBand.Contains(new Point(centerX, centerY)))
            {
                continue;
            }

            if (bounds.Top < coreBounds.Top - 4 || bounds.Bottom > coreBounds.Bottom + 8)
            {
                continue;
            }

            if (bounds.Height > Math.Max(coreBounds.Height * 1.5, 18))
            {
                continue;
            }

            using var coarseOverlapRegion = new Mat(coarseMaskRegion, ClampRect(bounds, roi.Size()));
            if (Cv2.CountNonZero(coarseOverlapRegion) == 0)
            {
                continue;
            }

            var shifted = contour
                .Select(point => new Point(point.X + expanded.X, point.Y + expanded.Y))
                .ToArray();

            Cv2.DrawContours(refinedMask, [shifted], -1, Scalar.White, -1);
        }

        using var dilateKernel2 = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.Dilate(refinedMask, refinedMask, dilateKernel2, iterations: 1);
        return refinedMask;
    }

    private static (double meanBrightness, double stdDev) CalculateMaskedLuminanceStats(Mat source, Mat refinedMask)
    {
        using var gray = new Mat();
        Cv2.CvtColor(source, gray, ColorConversionCodes.BGR2GRAY);
        using var mean = new Mat();
        using var stddev = new Mat();
        Cv2.MeanStdDev(gray, mean, stddev, refinedMask);
        return (mean.At<double>(0), stddev.At<double>(0));
    }

    private static (double alpha, double sigma) CalculateDeblendStrength(double meanBrightness)
    {
        if (meanBrightness < 80)
        {
            return (0.28, 1.4);
        }

        if (meanBrightness < 120)
        {
            return (0.22, 1.3);
        }

        if (meanBrightness < 180)
        {
            return (0.16, 1.2);
        }

        return (0.10, 1.0);
    }


    private static Mat RunWhiteWatermarkDeblend(Mat source, Mat mask, double alpha, double sigma)
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
        using var safeDenominator = new Mat();
        using var restoredFloat = new Mat();

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

    private static DenseTensor<float> CreateImageTensor(
        Mat image,
        CancellationToken cancellationToken)
    {
        using var rgb = new Mat();
        Cv2.CvtColor(image, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>(new[] { 1, 3, rgb.Rows, rgb.Cols });
        for (var y = 0; y < rgb.Rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < rgb.Cols; x++)
            {
                var pixel = rgb.At<Vec3b>(y, x);
                tensor[0, 0, y, x] = pixel.Item0 / 255f;
                tensor[0, 1, y, x] = pixel.Item1 / 255f;
                tensor[0, 2, y, x] = pixel.Item2 / 255f;
            }
        }

        return tensor;
    }

    private static DenseTensor<float> CreateMaskTensor(
        Mat mask,
        CancellationToken cancellationToken)
    {
        var tensor = new DenseTensor<float>(new[] { 1, 1, mask.Rows, mask.Cols });
        for (var y = 0; y < mask.Rows; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < mask.Cols; x++)
            {
                tensor[0, 0, y, x] = mask.At<byte>(y, x) > 0 ? 1f : 0f;
            }
        }

        return tensor;
    }

    private static Mat TensorToBgrMat(
        Tensor<float> tensor,
        CancellationToken cancellationToken)
    {
        var dimensions = tensor.Dimensions.ToArray();
        if (dimensions.Length < 4 || dimensions[1] != 3)
        {
            throw new InvalidOperationException("模型输出不是标准的 3 通道图像张量。");
        }

        var height = dimensions[^2];
        var width = dimensions[^1];

        var minValue = float.MaxValue;
        var maxValue = float.MinValue;

        foreach (var value in tensor)
        {
            if (value < minValue)
            {
                minValue = value;
            }

            if (value > maxValue)
            {
                maxValue = value;
            }
        }

        var normalizeSigned = minValue < 0f;
        var normalizeByteScale = maxValue > 1.5f;

        using var rgb = new Mat(height, width, MatType.CV_8UC3);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var r = NormalizeOutputValue(tensor[0, 0, y, x], normalizeSigned, normalizeByteScale);
                var g = NormalizeOutputValue(tensor[0, 1, y, x], normalizeSigned, normalizeByteScale);
                var b = NormalizeOutputValue(tensor[0, 2, y, x], normalizeSigned, normalizeByteScale);

                rgb.Set(y, x, new Vec3b(r, g, b));
            }
        }

        var bgr = new Mat();
        Cv2.CvtColor(rgb, bgr, ColorConversionCodes.RGB2BGR);
        return bgr;
    }

    private static PreparedInput PrepareInput(Mat source, Mat mask, OpenCvSharp.Size targetSize)
    {
        var scale = Math.Min(
            1d,
            Math.Min(
                targetSize.Width / (double)source.Cols,
                targetSize.Height / (double)source.Rows));

        var resizedWidth = Math.Max(1, (int)Math.Round(source.Cols * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(source.Rows * scale));
        var offsetX = Math.Max(0, (targetSize.Width - resizedWidth) / 2);
        var offsetY = Math.Max(0, (targetSize.Height - resizedHeight) / 2);
        var contentRect = new OpenCvSharp.Rect(offsetX, offsetY, resizedWidth, resizedHeight);

        var preparedImage = new Mat(targetSize, MatType.CV_8UC3, Scalar.All(0));
        var preparedMask = new Mat(targetSize, MatType.CV_8UC1, Scalar.All(0));

        using var resizedImage = new Mat();
        using var resizedMask = new Mat();

        var interpolation = scale < 1d ? InterpolationFlags.Area : InterpolationFlags.Linear;
        Cv2.Resize(source, resizedImage, new OpenCvSharp.Size(resizedWidth, resizedHeight), 0, 0, interpolation);
        Cv2.Resize(mask, resizedMask, new OpenCvSharp.Size(resizedWidth, resizedHeight), 0, 0, InterpolationFlags.Nearest);
        Cv2.Threshold(resizedMask, resizedMask, 10, 255, ThresholdTypes.Binary);

        using (var imageRoi = new Mat(preparedImage, contentRect))
        {
            resizedImage.CopyTo(imageRoi);
        }

        using (var maskRoi = new Mat(preparedMask, contentRect))
        {
            resizedMask.CopyTo(maskRoi);
        }

        return new PreparedInput(preparedImage, preparedMask, contentRect);
    }

    private static OpenCvSharp.Rect CalculateProcessingRect(Mat mask, OpenCvSharp.Size sourceSize, WatermarkRepairOptions options)
    {
        Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return new OpenCvSharp.Rect(0, 0, sourceSize.Width, sourceSize.Height);
        }

        var bounds = Cv2.BoundingRect(contours[0]);
        for (var i = 1; i < contours.Length; i++)
        {
            bounds = Union(bounds, Cv2.BoundingRect(contours[i]));
        }

        var paddingX = Math.Max(options.MinPaddingX, (int)Math.Round(bounds.Width * options.PaddingXScale));
        var paddingY = Math.Max(options.MinPaddingY, (int)Math.Round(bounds.Height * options.PaddingYScale));

        var left = Math.Max(0, bounds.Left - paddingX);
        var top = Math.Max(0, bounds.Top - paddingY);
        var right = Math.Min(sourceSize.Width, bounds.Right + paddingX);
        var bottom = Math.Min(sourceSize.Height, bounds.Bottom + paddingY);

        return new OpenCvSharp.Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    private static OpenCvSharp.Rect Union(OpenCvSharp.Rect left, OpenCvSharp.Rect right)
    {
        var x = Math.Min(left.Left, right.Left);
        var y = Math.Min(left.Top, right.Top);
        var maxRight = Math.Max(left.Right, right.Right);
        var maxBottom = Math.Max(left.Bottom, right.Bottom);

        return new OpenCvSharp.Rect(x, y, maxRight - x, maxBottom - y);
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

    private static Mat RestoreToOriginalSize(Mat output, OpenCvSharp.Rect contentRect, OpenCvSharp.Size originalSize)
    {
        using var cropped = new Mat(output, contentRect);
        if (cropped.Size() == originalSize)
        {
            return cropped.Clone();
        }

        var restored = new Mat();
        Cv2.Resize(cropped, restored, originalSize, 0, 0, InterpolationFlags.Lanczos4);
        return restored;
    }

    private static void BlendRestoredRegion(
        Mat originalRegion,
        Mat restoredRegion,
        Mat maskRegion,
        Mat targetRegion,
        WatermarkRepairOptions options)
    {
        using var featherMask = maskRegion.Clone();
        Cv2.GaussianBlur(
            featherMask,
            featherMask,
            new OpenCvSharp.Size(0, 0),
            Math.Max(0.1, options.FeatherSigma));

        using var alpha = new Mat();
        featherMask.ConvertTo(alpha, MatType.CV_32FC1, 1d / 255d);

        using var alpha3 = new Mat();
        Cv2.CvtColor(alpha, alpha3, ColorConversionCodes.GRAY2BGR);

        using var ones = new Mat(alpha3.Size(), alpha3.Type(), Scalar.All(1.0));
        using var inverseAlpha3 = new Mat();
        Cv2.Subtract(ones, alpha3, inverseAlpha3);

        using var originalFloat = new Mat();
        using var restoredFloat = new Mat();
        originalRegion.ConvertTo(originalFloat, MatType.CV_32FC3, 1d / 255d);
        restoredRegion.ConvertTo(restoredFloat, MatType.CV_32FC3, 1d / 255d);

        using var weightedOriginal = new Mat();
        using var weightedRestored = new Mat();
        using var blended = new Mat();

        Cv2.Multiply(originalFloat, inverseAlpha3, weightedOriginal);
        Cv2.Multiply(restoredFloat, alpha3, weightedRestored);
        Cv2.Add(weightedOriginal, weightedRestored, blended);
        blended.ConvertTo(targetRegion, MatType.CV_8UC3, 255d);
    }

    private static byte NormalizeOutputValue(float value, bool normalizeSigned, bool normalizeByteScale)
    {
        var normalized = normalizeSigned
            ? (value + 1f) / 2f
            : normalizeByteScale
                ? value / 255f
                : value;

        normalized = Math.Clamp(normalized, 0f, 1f);
        return (byte)Math.Round(normalized * 255f);
    }

    private static int AlignToMultiple(int value, int alignment)
    {
        if (alignment <= 1)
        {
            return value;
        }

        var remainder = value % alignment;
        return remainder == 0 ? value : value + alignment - remainder;
    }

    private static void ReportProgress(
        IProgress<WatermarkRemovalProgress>? progress,
        string message)
    {
        progress?.Report(new WatermarkRemovalProgress(message));
    }

    private sealed class PreparedInput : IDisposable
    {
        public PreparedInput(Mat image, Mat mask, OpenCvSharp.Rect contentRect)
        {
            Image = image;
            Mask = mask;
            ContentRect = contentRect;
        }

        public Mat Image { get; }

        public Mat Mask { get; }

        public OpenCvSharp.Rect ContentRect { get; }

        public void Dispose()
        {
            Image.Dispose();
            Mask.Dispose();
        }
    }
}
