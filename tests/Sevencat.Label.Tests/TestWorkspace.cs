using Sevencat.Core.Dataset.TinyLabel;
using Xunit;

// 当前工作库是进程内唯一实例，测试之间不能并行（否则会互相把 Current 指到别的临时目录）
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Sevencat.Label.Tests;

/// <summary>临时目录夹具：测试结束自动清理。</summary>
public sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "annotrain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string WriteText(string relativePath, string content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        string path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        // 当前工作库是进程内唯一实例，删临时目录前先丢掉它（只有测试会这么做）
        TinyLabelDatabase.Reset();

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
