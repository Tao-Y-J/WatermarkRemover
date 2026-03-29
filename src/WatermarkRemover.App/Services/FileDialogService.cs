using System.IO;
using Microsoft.Win32;

namespace WatermarkRemover.App.Services;

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenImageFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*",
            Multiselect = false,
            Title = "选择待处理图片",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? OpenModelFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ONNX 模型|*.onnx|所有文件|*.*",
            Multiselect = false,
            Title = "选择 LaMa ONNX 模型",
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveImageFile(string suggestedFileName, string requiredExtension)
    {
        var normalizedExtension = NormalizeExtension(requiredExtension);
        var dialog = new SaveFileDialog
        {
            Filter = BuildFilter(normalizedExtension),
            FileName = EnsureExtension(suggestedFileName, normalizedExtension),
            DefaultExt = normalizedExtension.TrimStart('.'),
            Title = "保存处理后的图片",
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true
            ? EnsureExtension(dialog.FileName, normalizedExtension)
            : null;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".png";
        }

        return extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";
    }

    private static string BuildFilter(string extension)
    {
        return extension switch
        {
            ".jpg" or ".jpeg" => "JPEG 图片|*.jpg;*.jpeg",
            ".bmp" => "BMP 图片|*.bmp",
            ".webp" => "WebP 图片|*.webp",
            _ => "PNG 图片|*.png",
        };
    }

    private static string EnsureExtension(string path, string extension)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        return Path.Combine(directory, $"{fileNameWithoutExtension}{extension}");
    }
}
