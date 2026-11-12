namespace Sevencat.Train.Engine;

/// <summary>训练引擎入口标记；M4 迁入 Models / Trainer / Export / Predictor。</summary>
public static class EngineModule
{
    public const string Name = "Train.Engine";

    /// <summary>支持的模型族。</summary>
    public static readonly string[] SupportedModels = ["yolov8", "yolo11", "rfdetr"];
}
