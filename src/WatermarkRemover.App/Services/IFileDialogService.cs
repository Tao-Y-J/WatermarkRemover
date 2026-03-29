namespace WatermarkRemover.App.Services;

public interface IFileDialogService
{
    string? OpenImageFile();

    string? OpenModelFile();

    string? SaveImageFile(string suggestedFileName, string requiredExtension);
}
