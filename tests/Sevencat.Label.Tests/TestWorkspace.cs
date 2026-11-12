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
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
