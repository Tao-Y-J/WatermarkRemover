using System.Windows;
using System.Windows.Input;

namespace WatermarkRemover.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        UpdateWindowStateGlyph();
        UpdateChromeForState();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_OnStateChanged(object sender, EventArgs e)
    {
        UpdateWindowStateGlyph();
        UpdateChromeForState();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowStateGlyph()
    {
        if (MaximizeGlyphText is null)
        {
            return;
        }

        MaximizeGlyphText.Text = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void UpdateChromeForState()
    {
        if (RootChromeBorder is null)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            RootChromeBorder.Margin = new Thickness(0);
            RootChromeBorder.CornerRadius = new CornerRadius(0);
            RootChromeBorder.BorderThickness = new Thickness(0);
            return;
        }

        RootChromeBorder.Margin = new Thickness(10);
        RootChromeBorder.CornerRadius = new CornerRadius(26);
        RootChromeBorder.BorderThickness = new Thickness(1);
    }
}
