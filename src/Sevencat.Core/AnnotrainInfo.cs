namespace Sevencat.Core;

/// <summary>全局常量：产品名、生成器标识、文件后缀约定。</summary>
public static class AnnotrainInfo
{
    /// <summary>产品名，用于窗口标题、日志与元数据。</summary>
    public const string ProductName = "Annotrain";

    /// <summary>写进标注 JSON 的自有命名空间，避免与 labelme 字段冲突。</summary>
    public const string MetadataNamespace = "annotrain";

    /// <summary>配置目录名（位于用户配置目录下）。</summary>
    public const string AppFolderName = "Sevencat.Annotrain";
}
