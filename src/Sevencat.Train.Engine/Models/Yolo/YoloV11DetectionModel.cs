using TorchSharp;
using static TorchSharp.torch;
using static TorchSharp.torch.nn;

namespace Sevencat.Train.Engine.Models.Yolo;

/// <summary>
/// YOLOv11 检测模型（detect 任务）：<c>model.0</c> … <c>model.22</c> 为主干 + 颈部，<c>model.23</c> 为检测头。
/// <para>层数与键名对齐 ultralytics <c>yolo11*.pt</c> 的 <c>state_dict</c>；前向逻辑在基类。</para>
/// </summary>
public sealed class YoloV11DetectionModel : YoloDetectionModel
{
    internal YoloV11DetectionModel(
        string name,
        YoloModuleChain model,
        DetectHeadModule head,
        YoloScale scale,
        int classCount)
        : base(name, YoloModelFamily.V11, scale, classCount, model, head)
    {
    }
}
