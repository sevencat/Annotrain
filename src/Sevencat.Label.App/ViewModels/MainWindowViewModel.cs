using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Infrastructure.Logging;

namespace Sevencat.Label.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = $"{AnnotrainInfo.ProductName} · 标注";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _logText = string.Empty;

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger, LogBuffer logBuffer)
    {
        logger.LogInformation("主窗口已创建，等待打开数据集");

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }
}
