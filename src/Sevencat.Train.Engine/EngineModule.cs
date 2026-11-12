namespace Sevencat.Train.Engine;

/// <summary>
/// 训练引擎入口标记。实现来自 OmniTensorSharp：模型（YOLOv8 / YOLO11 / RF-DETR）、
/// 训练循环、检查点、ONNX 导出与数据管线都已迁入本程序集。
/// </summary>
public static class EngineModule
{
    public const string Name = "Train.Engine";

    /// <summary>支持的模型族。</summary>
    public static readonly string[] SupportedModels = ["yolov8", "yolo11", "rfdetr"];
}
