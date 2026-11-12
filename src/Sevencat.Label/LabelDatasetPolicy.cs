using Sevencat.Core.Dataset;

namespace Sevencat.Label;

/// <summary>
/// 标注端的形态边界：**打开（可编辑）只支持「图片目录 + SQLite」**；
/// YOLO / RF-DETR 除了作为导出目标，还可以**导入**——导入会复制图片与标注，
/// 在目标目录建一个可编辑的项目库，源数据集保持只读。
/// 训练端不受此约束，三种形态都能读（只认 <see cref="IDatasetStoreProvider"/>）。
/// </summary>
public static class LabelDatasetPolicy
{
    /// <summary>标注端唯一可打开为项目的形态。</summary>
    public static DatasetFormat OpenableFormat => DatasetFormat.TinyLabelDb;

    /// <summary>标注端可导出的目标形态。</summary>
    public static IReadOnlyList<DatasetFormat> ExportTargets { get; } = [DatasetFormat.Yolo, DatasetFormat.Coco];

    /// <summary>标注端可导入成项目的来源形态（现成的 YOLO / RF-DETR 数据集）。</summary>
    public static IReadOnlyList<DatasetFormat> ImportSources { get; } = [DatasetFormat.Yolo, DatasetFormat.Coco];

    public static bool CanOpen(DatasetFormat format) => format == OpenableFormat;

    public static bool CanImport(DatasetFormat format) => ImportSources.Contains(format);

    /// <summary>打开前调用：形态不允许时给出明确提示，而不是让用户看到一个空数据集。</summary>
    public static void EnsureOpenable(DatasetFormat format)
    {
        if (!CanOpen(format))
        {
            throw new NotSupportedException(
                $"标注端只打开「{OpenableFormat.ToDisplayName()}」形态；" +
                $"{format.ToDisplayName()} 可先导入成项目，或作为导出目标" +
                $"（{string.Join(" / ", ExportTargets.Select(t => t.ToDisplayName()))}）");
        }
    }

    /// <summary>导入前调用：只有 YOLO / RF-DETR 能作为导入源。</summary>
    public static void EnsureImportable(DatasetFormat format)
    {
        if (!CanImport(format))
        {
            throw new NotSupportedException(
                $"不能从「{format.ToDisplayName()}」导入；支持的来源：" +
                string.Join(" / ", ImportSources.Select(source => source.ToDisplayName())));
        }
    }
}
