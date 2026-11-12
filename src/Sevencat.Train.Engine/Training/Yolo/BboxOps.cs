using TorchSharp;
using static TorchSharp.torch;

namespace Sevencat.Train.Engine.Training.Yolo;

/// <summary>检测框的向量化几何运算（全部在设备上，输入输出都是 <c>[N,4]</c> 的 xyxy）。</summary>
internal static class BboxOps
{
    private const float Epsilon = 1e-7f;

    /// <summary>两两 IoU（用于正样本分配；clamp 到 [0,1] 避免 CIoU 的负值参与幂运算）。</summary>
    public static Tensor IoU(Tensor left, Tensor right)
    {
        Tensor leftTopLeft = left.narrow(-1, 0, 2);
        Tensor leftBottomRight = left.narrow(-1, 2, 2);
        Tensor rightTopLeft = right.narrow(-1, 0, 2);
        Tensor rightBottomRight = right.narrow(-1, 2, 2);

        Tensor intersectionSize = (rightBottomRight.minimum(leftBottomRight) - leftTopLeft.maximum(rightTopLeft))
            .clamp(min: 0);
        Tensor intersection = intersectionSize.select(-1, 0) * intersectionSize.select(-1, 1);

        Tensor areaLeft = (leftBottomRight - leftTopLeft).clamp(min: 0).prod(-1);
        Tensor areaRight = (rightBottomRight - rightTopLeft).clamp(min: 0).prod(-1);

        return intersection / (areaLeft + areaRight - intersection + Epsilon);
    }

    /// <summary>CIoU（用于 box 回归损失）。</summary>
    public static Tensor Ciou(Tensor left, Tensor right)
    {
        Tensor iou = IoU(left, right);

        Tensor leftCenter = (left.narrow(-1, 0, 2) + left.narrow(-1, 2, 2)) / 2f;
        Tensor rightCenter = (right.narrow(-1, 0, 2) + right.narrow(-1, 2, 2)) / 2f;
        Tensor centerDistance = (leftCenter - rightCenter).pow(2).sum(-1);

        Tensor leftSize = (left.narrow(-1, 2, 2) - left.narrow(-1, 0, 2)).clamp(min: Epsilon);
        Tensor rightSize = (right.narrow(-1, 2, 2) - right.narrow(-1, 0, 2)).clamp(min: Epsilon);

        Tensor enclosing = (left.narrow(-1, 2, 2).maximum(right.narrow(-1, 2, 2))
            - left.narrow(-1, 0, 2).minimum(right.narrow(-1, 0, 2))).pow(2).sum(-1);

        Tensor leftAngle = (leftSize.select(-1, 0) / leftSize.select(-1, 1)).atan();
        Tensor rightAngle = (rightSize.select(-1, 0) / rightSize.select(-1, 1)).atan();
        Tensor shapeTerm = (leftAngle - rightAngle).pow(2) * (4f / (MathF.PI * MathF.PI));

        Tensor alpha = shapeTerm / (1f - iou + shapeTerm + Epsilon);

        return iou - centerDistance / (enclosing + Epsilon) - alpha * shapeTerm;
    }

    /// <summary>把 <c>ltrb</c> 距离还原成 xyxy 框（与 <see cref="Bbox2Dist"/> 互逆）。</summary>
    public static Tensor Dist2Bbox(Tensor distance, Tensor anchor) => torch.cat(
        new[]
        {
            anchor - distance.narrow(-1, 0, 2),
            anchor + distance.narrow(-1, 2, 2),
        },
        -1);

    /// <summary>把 xyxy 框转成相对锚点的 <c>ltrb</c> 距离，并夹到 <c>[0, regressionMax]</c>。</summary>
    public static Tensor Bbox2Dist(Tensor anchor, Tensor box, float regressionMax) => torch.cat(
        new[]
        {
            anchor - box.narrow(-1, 0, 2),
            box.narrow(-1, 2, 2) - anchor,
        },
        -1).clamp(min: 0, max: regressionMax);
}
