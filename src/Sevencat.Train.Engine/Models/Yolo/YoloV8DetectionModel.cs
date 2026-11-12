using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// YOLOv8 检测模型（detect 任务）：<c>model.0</c> … <c>model.21</c> 为主干 + 颈部，
/// <c>model.22</c> 为检测头，层级与 ultralytics <c>yolov8*.pt</c> 的 <c>state_dict</c> 一一对应。
/// <para>具体的前向 / 形状逻辑在基类 <see cref="YoloDetectionModel"/> 里，两个家族共用。</para>
/// </summary>
public sealed class YoloV8DetectionModel : YoloDetectionModel
{
    internal YoloV8DetectionModel(
        string name,
        YoloModuleChain model,
        DetectHeadModule head,
        YoloScale scale,
        int classCount)
        : base(name, YoloModelFamily.V8, scale, classCount, model, head)
    {
    }
}
