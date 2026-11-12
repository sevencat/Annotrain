using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Sevencat.Train.App.ViewModels;

namespace Sevencat.Train.App.Views;

/// <summary>运行日志：顶栏「日志」按钮弹出，跟着 <see cref="MainWindowViewModel.LogText"/> 实时刷新。</summary>
public partial class LogWindow : Window
{
    private readonly MainWindowViewModel? _viewModel;

    /// <summary>XAML 预览用。</summary>
    public LogWindow() => InitializeComponent();

    public LogWindow(MainWindowViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;

        Opened += (_, _) =>
        {
            LogScroll.ScrollToEnd();
            viewModel.PropertyChanged += OnViewModelChanged;
        };

        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.LogText))
        {
            LogScroll.ScrollToEnd();
        }
    }

    private void OnClear(object? sender, RoutedEventArgs e) => _viewModel?.ClearLog();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
