using System.IO;
using WatermarkRemover.App.Models;

namespace WatermarkRemover.App.Services;

public sealed class ModelAssetService : IModelAssetService
{
    public Task<ModelAssetResolution> ResolveBundledModelAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modelPath = FindBestBundledModelPath();
        if (modelPath is not null)
        {
            return Task.FromResult(new ModelAssetResolution(modelPath, false));
        }

        throw new FileNotFoundException("未找到程序内置的 ONNX 模型。", "*.onnx");
    }

    private static string? FindBestBundledModelPath()
    {
        var candidateDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Models"),
            Path.Combine(Environment.CurrentDirectory, "src", "WatermarkRemover.App", "Assets", "Models"),
            Path.Combine(Environment.CurrentDirectory, "Assets", "Models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
            Path.Combine(Environment.CurrentDirectory, "models"),
        };

        var candidates = candidateDirectories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly))
            .Where(path => new FileInfo(path).Length > 0)
            .Select(path => new
            {
                Path = path,
                Score = ScoreModel(Path.GetFileNameWithoutExtension(path)),
                Length = new FileInfo(path).Length,
            })
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Length)
            .ToList();

        return candidates.FirstOrDefault()?.Path;
    }

    private static int ScoreModel(string fileName)
    {
        var score = 0;
        var normalizedName = fileName.ToLowerInvariant();

        if (normalizedName.Contains("lama", StringComparison.Ordinal))
        {
            score += 100;
        }

        if (normalizedName.Contains("fp32", StringComparison.Ordinal))
        {
            score += 20;
        }

        if (normalizedName.Contains("large", StringComparison.Ordinal) ||
            normalizedName.Contains("big", StringComparison.Ordinal))
        {
            score += 10;
        }

        if (normalizedName.Contains("tiny", StringComparison.Ordinal) ||
            normalizedName.Contains("small", StringComparison.Ordinal) ||
            normalizedName.Contains("mobile", StringComparison.Ordinal))
        {
            score -= 20;
        }

        return score;
    }
}
