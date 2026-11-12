namespace Sevencat.Core.Infrastructure;

/// <summary>用户配置。字段只增不改语义；读旧文件时缺字段走默认值。</summary>
public sealed class AppSettings
{
    /// <summary>Light / Dark / Default。</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>界面语言：<c>zh</c> / <c>en</c>；空表示跟随系统。</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>最近打开的数据集根目录。</summary>
    public List<string> RecentRoots { get; set; } = [];

    /// <summary>标注 Web 服务地址。</summary>
    public string LabelWebUrl { get; set; } = "http://127.0.0.1:5180";

    /// <summary>训练 Web 服务地址。</summary>
    public string TrainWebUrl { get; set; } = "http://127.0.0.1:5181";

    /// <summary>启动时是否自动拉起内嵌 Web 服务。</summary>
    public bool StartWebOnLaunch { get; set; } = true;

    /// <summary>标注端上次打开的数据目录（含 annotrain.db），启动时自动打开。</summary>
    public string LastDataDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 训练端上次打开的数据集目录。与标注端的 <see cref="LastDataDirectory"/> 分开，
    /// 否则两边互相覆盖，标注端下次启动会找不到工作区。
    /// </summary>
    public string LastTrainDataDirectory { get; set; } = string.Empty;

    /// <summary>AI 标注用的 ONNX 模型路径（YOLO 导出）。</summary>
    public string AutoLabelModelPath { get; set; } = string.Empty;

    /// <summary>AI 标注置信度阈值。</summary>
    public double AutoLabelConfidence { get; set; } = 0.25;
}
