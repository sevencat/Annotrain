using Sevencat.Core.Dataset.TinyLabel;
using Xunit;

// 语言、数据目录这些还是进程级静态状态，测试之间别并行（否则会互相改掉）
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Sevencat.Label.Tests;

/// <summary>临时目录夹具：测试结束自动清理。</summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly List<TinyLabelDatabase> _databases = [];

    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "annotrain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>在临时目录里开一个库：测试自己用，夹具销毁时统一关掉。</summary>
    public TinyLabelDatabase OpenDatabase(string fileName = "annotrain.db")
        => OpenDatabaseAt(Path.Combine(Root, fileName));

    /// <summary>开指定路径的库（比如临时目录下某个子目录里的数据集），同样由夹具统一释放。</summary>
    public TinyLabelDatabase OpenDatabaseAt(string path)
    {
        TinyLabelDatabase database = TinyLabelDatabase.Open(path);
        _databases.Add(database);
        return database;
    }

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
        // 先关库再删目录（SQLite 连接池不关的话文件可能被占着）
        foreach (TinyLabelDatabase database in _databases)
        {
            database.Dispose();
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
