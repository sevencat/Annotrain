namespace Sevencat.Core.Model;

public static class IdGenerator
{
    /// <summary>短 Id：足够本地唯一，写入标注文件时比 GUID 好看。</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
