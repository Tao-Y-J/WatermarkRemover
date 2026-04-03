using System.Windows;
using WatermarkRemover.App.Models;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var repairOptions = WatermarkRepairOptions.Default;

        var viewModel = new MainWindowViewModel(
            new FileDialogService(),
            new ImageFileService(),
            new WatermarkRemovalService(repairOptions),
            new ModelAssetService());

        var window = new MainWindow
        {
            DataContext = viewModel,
        };

        window.Loaded += async (_, _) => await viewModel.InitializeAsync();

        MainWindow = window;
        window.Show();
    }
}
