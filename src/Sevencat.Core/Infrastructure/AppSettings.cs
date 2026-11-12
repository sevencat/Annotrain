namespace Sevencat.Core.Infrastructure;

/// <summary>用户配置。字段只增不改语义；读旧文件时缺字段走默认值。</summary>
public sealed class AppSettings
{
    /// <summary>Light / Dark / Default。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>最近打开的数据集根目录。</summary>
    public List<string> RecentRoots { get; set; } = [];

    /// <summary>标注 Web 服务地址。</summary>
    public string LabelWebUrl { get; set; } = "http://127.0.0.1:5180";

    /// <summary>训练 Web 服务地址。</summary>
    public string TrainWebUrl { get; set; } = "http://127.0.0.1:5181";

    /// <summary>启动时是否自动拉起内嵌 Web 服务。</summary>
    public bool StartWebOnLaunch { get; set; } = true;

    /// <summary>上次打开的数据目录（含 annotrain.db），启动时自动打开。</summary>
    public string LastDataDirectory { get; set; } = string.Empty;

    /// <summary>AI 标注用的 ONNX 模型路径（YOLO 导出）。</summary>
    public string AutoLabelModelPath { get; set; } = string.Empty;

    /// <summary>AI 标注置信度阈值。</summary>
    public double AutoLabelConfidence { get; set; } = 0.25;
}
