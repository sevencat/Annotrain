using Sevencat.Core.AutoLabel;
using Sevencat.Core.Model;
using Xunit;

namespace Sevencat.Label.Tests;

public class YoloOutputDecoderTests
{
    /// <summary>640×480 的图做 letterbox 到 640×640：缩放到 640×480，上下各垫 80。</summary>
    private static YoloDecodeOptions Options(double confidence = 0.25) =>
        new(confidence, 0.45, Scale: 1.0, PadX: 0, PadY: 80, ImageWidth: 640, ImageHeight: 480);

    [Fact]
    public void Decode_MapsLetterboxBackToImage()
    {
        // 一个 100×50 的框，中心在输入坐标 (320, 320) → 原图 (320, 240)
        float[] output = BuildOutput(channels: 6, count: 1, classScores: [0.9f, 0.1f], box: [320, 320, 100, 50]);

        IReadOnlyList<AutoLabelCandidate> candidates = YoloOutputDecoder.Decode(
            output, 6, 1, channelFirst: true, Options(), classNames: ["cat", "dog"]);

        AutoLabelCandidate candidate = Assert.Single(candidates);
        Assert.Equal("cat", candidate.Label);
        Assert.Equal(270, candidate.Box.X, 3);
        Assert.Equal(215, candidate.Box.Y, 3);
        Assert.Equal(100, candidate.Box.Width, 3);
        Assert.Equal(50, candidate.Box.Height, 3);
    }

    [Fact]
    public void Decode_DropsLowConfidence()
    {
        float[] output = BuildOutput(6, 1, classScores: [0.10f, 0.05f], box: [320, 320, 100, 50]);

        IReadOnlyList<AutoLabelCandidate> candidates = YoloOutputDecoder.Decode(
            output, 6, 1, true, Options(confidence: 0.5));

        Assert.Empty(candidates);
    }

    [Fact]
    public void Decode_SuppressesOverlappingBoxesOfSameClass()
    {
        // 两个几乎重合的框 + 一个远处的框
        float[] output = new float[6 * 3];
        WriteBox(output, channelFirst: true, channels: 6, count: 3, index: 0, box: [320, 320, 100, 50], scores: [0.9f, 0.1f]);
        WriteBox(output, channelFirst: true, channels: 6, count: 3, index: 1, box: [322, 321, 100, 50], scores: [0.8f, 0.1f]);
        WriteBox(output, channelFirst: true, channels: 6, count: 3, index: 2, box: [100, 200, 60, 40], scores: [0.7f, 0.1f]);

        IReadOnlyList<AutoLabelCandidate> candidates = YoloOutputDecoder.Decode(
            output, 6, 3, true, Options(), classNames: ["cat", "dog"]);

        Assert.Equal(2, candidates.Count);
        Assert.Equal(0.9, candidates[0].Score, 3);
    }

    [Fact]
    public void Decode_UsesClassMapOverModelNames()
    {
        float[] output = BuildOutput(5, 1, classScores: [0.8f], box: [320, 320, 100, 50]);

        IReadOnlyList<AutoLabelCandidate> candidates = YoloOutputDecoder.Decode(
            output,
            5,
            1,
            true,
            Options(),
            classNames: ["person"],
            classMap: new Dictionary<int, string> { [0] = "工人" });

        Assert.Equal("工人", Assert.Single(candidates).Label);
    }

    [Fact]
    public void Decode_HandlesTransposedOutput()
    {
        float[] output = new float[1 * 5];
        // [count=1][channels=5]：cx, cy, w, h, score
        output[0] = 320;
        output[1] = 320;
        output[2] = 100;
        output[3] = 50;
        output[4] = 0.9f;

        IReadOnlyList<AutoLabelCandidate> candidates = YoloOutputDecoder.Decode(
            output, channels: 5, count: 1, channelFirst: false, Options());

        Assert.Single(candidates);
    }

    // ── 造 YOLO 输出 ─────────────────────────────────────────────────────────

    private static float[] BuildOutput(
        int channels,
        int count,
        float[] classScores,
        float[] box)
    {
        float[] output = new float[channels * count];
        WriteBox(output, channelFirst: true, channels, count, index: 0, box, classScores);
        return output;
    }

    private static void WriteBox(
        float[] output,
        bool channelFirst,
        int channels,
        int count,
        int index,
        float[] box,
        float[] scores)
    {
        if (channelFirst)
        {
            output[index] = box[0];
            output[count + index] = box[1];
            output[(2 * count) + index] = box[2];
            output[(3 * count) + index] = box[3];

            for (int classId = 0; classId < scores.Length; classId++)
            {
                output[((4 + classId) * count) + index] = scores[classId];
            }

            return;
        }

        int offset = index * channels;
        output[offset] = box[0];
        output[offset + 1] = box[1];
        output[offset + 2] = box[2];
        output[offset + 3] = box[3];

        for (int classId = 0; classId < scores.Length; classId++)
        {
            output[offset + 4 + classId] = scores[classId];
        }
    }
}
