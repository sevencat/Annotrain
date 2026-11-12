using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Sevencat.Core;
using Sevencat.Core.Infrastructure.Logging;

namespace Sevencat.Train.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = $"{AnnotrainInfo.ProductName} · 训练";

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private string _logText = string.Empty;

    public MainWindowViewModel(ILogger<MainWindowViewModel> logger, LogBuffer logBuffer)
    {
        logger.LogInformation("训练主窗口已创建，等待选择数据集");

        LogText = string.Join(Environment.NewLine, logBuffer.Snapshot().Select(entry => entry.ToString()));
    }
}
