using System.Text;
using Microsoft.Extensions.Logging;
using Sevencat.Core.Model;
using Sevencat.Core.Storage;

namespace Sevencat.Core.Dataset;

/// <summary>
/// 格式探测：只读文件名与文件头（COCO 只读前 8KB、SQLite 只读 16 字节），不解析全量标注。
/// 多个形态同时命中时返回 <see cref="DatasetFormat.Ambiguous"/> 并给出候选，交给用户选。
/// </summary>
public sealed class DatasetProbeService(BlobStoreRegistry registry, ILogger<DatasetProbeService>? logger = null) : IDatasetProbe
{
    private const int CocoProbeBytes = 8192;

    private static readonly string[] YamlExtensions = [".yaml", ".yml"];

    private static readonly string[] SplitNames = ["train", "val", "valid", "test"];

    public async Task<DatasetProbe> DetectAsync(StorageLocation location, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(location);

        IBlobStore store = registry.Get(location);

        var files = new List<string>();
        await foreach (BlobEntry entry in store.ListAsync(new StorageKey(location, string.Empty), ct).ConfigureAwait(false))
        {
            files.Add(entry.RelativePath);
            if (files.Count > 200_000)
            {
                break;
            }
        }

        if (files.Count == 0)
        {
            return Unknown(location, "目录为空或不可读");
        }

        var yolo = new List<string>();
        var coco = new List<string>();
        var voc = new List<string>();
        var db = new List<string>();
        bool flat = false;

        HashSet<string> labelStems = CollectLabelStems(files);

        foreach (string file in files)
        {
            ct.ThrowIfCancellationRequested();

            string extension = Path.GetExtension(file).ToLowerInvariant();
            bool isRootFile = !file.Contains('/');

            if (YamlExtensions.Contains(extension) && isRootFile)
            {
                if (await LooksLikeYoloAsync(store, location, file, ct).ConfigureAwait(false))
                {
                    yolo.Add(file);
                }
            }
            else if (extension == ".db")
            {
                if (await LooksLikeSqliteAsync(store, location, file, ct).ConfigureAwait(false))
                {
                    db.Add(file);
                }
            }
            else if (extension == ".json" && IsCocoCandidate(file))
            {
                if (await LooksLikeCocoAsync(store, location, file, ct).ConfigureAwait(false))
                {
                    coco.Add(file);
                }
            }
            else if (extension == ".xml" && file.StartsWith("Annotations/", StringComparison.OrdinalIgnoreCase))
            {
                voc.Add(file);
            }
            else if (ImageFiles.IsSupported(extension))
            {
                flat |= labelStems.Contains(StemKey(file));
            }
        }

        DatasetProbe probe = Decide(location, yolo, coco, voc, db, flat);
        logger?.LogInformation(
            "数据集探测：{Format}，位置 {Location}，文件 {Count} 个，{Message}",
            probe.Format,
            location,
            files.Count,
            probe.Message);

        return probe;
    }

    private static DatasetProbe Decide(
        StorageLocation location,
        List<string> yolo,
        List<string> coco,
        List<string> voc,
        List<string> db,
        bool flat)
    {
        var candidates = new List<(DatasetFormat Format, string? Descriptor, string Message)>();

        if (yolo.Count > 0)
        {
            candidates.Add((DatasetFormat.Yolo, yolo[0], $"YOLO 布局（{Path.GetFileName(yolo[0])}）"));
        }

        if (coco.Count > 0)
        {
            candidates.Add((DatasetFormat.Coco, coco[0], $"COCO / RF-DETR（{Path.GetFileName(coco[0])}）"));
        }

        if (db.Count > 0)
        {
            candidates.Add((DatasetFormat.TinyLabelDb, db[0], $"目录 + 数据库（{Path.GetFileName(db[0])}）"));
        }

        if (voc.Count > 0)
        {
            candidates.Add((DatasetFormat.Voc, voc[0], $"Pascal VOC（{voc.Count} 个 xml，仅只读）"));
        }

        if (flat)
        {
            candidates.Add((DatasetFormat.Flat, null, "平铺：图片与同名标注文件并存"));
        }

        if (candidates.Count == 0)
        {
            return Unknown(location, "未识别到数据集");
        }

        DatasetCandidate[] all = [.. candidates.Select(c => new DatasetCandidate(c.Format, c.Descriptor, c.Message))];

        if (candidates.Count == 1)
        {
            DatasetCandidate only = all[0];
            return new DatasetProbe(only.Format, location, only.DescriptorPath, [], only.Message)
            {
                Candidates = all,
            };
        }

        DatasetCandidate first = all[0];
        string[] alternatives = [.. all.Skip(1).Select(c => c.DescriptorPath ?? c.Message)];

        return new DatasetProbe(
            DatasetFormat.Ambiguous,
            location,
            first.DescriptorPath,
            alternatives,
            $"同时命中 {candidates.Count} 种格式：{string.Join("、", all.Select(c => c.Format.ToDisplayName()))}，请选择")
        {
            Candidates = all,
        };
    }

    private static DatasetProbe Unknown(StorageLocation location, string reason) => new(
        DatasetFormat.Unknown,
        location,
        null,
        [],
        $"{reason}。期望结构：YOLO(data.yaml + images/labels) / COCO(train/_annotations.coco.json) / " +
        "TinyLabel(*.db + projects) / VOC(Annotations + JPEGImages)");

    private static bool IsCocoCandidate(string path)
    {
        string normalized = path.Replace('\\', '/');
        string fileName = Path.GetFileName(normalized);

        if (!normalized.Contains('/'))
        {
            return true;
        }

        if (normalized.StartsWith("annotations/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string split in SplitNames)
        {
            if (normalized.StartsWith($"{split}/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return fileName.Contains("annotations", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> LooksLikeYoloAsync(
        IBlobStore store, StorageLocation location, string file, CancellationToken ct)
    {
        string text = await ReadTextAsync(store, location, file, 64 * 1024, ct).ConfigureAwait(false);

        return text.Contains("names:", StringComparison.Ordinal)
               && (text.Contains("train:", StringComparison.Ordinal) || text.Contains("val:", StringComparison.Ordinal));
    }

    private static async Task<bool> LooksLikeCocoAsync(
        IBlobStore store, StorageLocation location, string file, CancellationToken ct)
    {
        string head = await ReadTextAsync(store, location, file, CocoProbeBytes, ct).ConfigureAwait(false);

        return head.Contains("\"images\"", StringComparison.Ordinal)
               && head.Contains("\"annotations\"", StringComparison.Ordinal);
    }

    private static async Task<bool> LooksLikeSqliteAsync(
        IBlobStore store, StorageLocation location, string file, CancellationToken ct)
    {
        byte[] header = await ReadBytesAsync(store, location, file, 16, ct).ConfigureAwait(false);
        return header.Length >= 16 && Encoding.ASCII.GetString(header, 0, 16) == "SQLite format 3\0";
    }

    private static async Task<string> ReadTextAsync(
        IBlobStore store, StorageLocation location, string file, int maxBytes, CancellationToken ct)
    {
        byte[] bytes = await ReadBytesAsync(store, location, file, maxBytes, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task<byte[]> ReadBytesAsync(
        IBlobStore store, StorageLocation location, string file, int maxBytes, CancellationToken ct)
    {
        var key = new StorageKey(location, file);

        try
        {
            await using Stream stream = await store.OpenReadRangeAsync(key, 0, maxBytes, ct).ConfigureAwait(false);
            byte[] buffer = new byte[maxBytes];
            int read = 0;
            int chunk;

            while (read < maxBytes
                   && (chunk = await stream.ReadAsync(buffer.AsMemory(read, maxBytes - read), ct).ConfigureAwait(false)) > 0)
            {
                read += chunk;
            }

            return read == maxBytes ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>收集所有 .json / .txt 标注的「目录/文件名」键，供平铺布局做 O(1) 同名判定。</summary>
    private static HashSet<string> CollectLabelStems(List<string> files)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();
            if (extension is ".json" or ".txt")
            {
                stems.Add(StemKey(file));
            }
        }

        return stems;
    }

    private static string StemKey(string file)
    {
        string directory = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? string.Empty;
        return $"{directory}/{Path.GetFileNameWithoutExtension(file)}";
    }
}
