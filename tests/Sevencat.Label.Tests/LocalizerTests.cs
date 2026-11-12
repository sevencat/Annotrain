using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

/// <summary>本地化表完整性与「新建项目不建库」的语义。</summary>
public class LocalizerTests
{
    [Fact]
    public void EveryKey_HasBothChineseAndEnglish()
    {
        Assert.NotEmpty(Localizer.Keys);

        foreach (string key in Localizer.Keys)
        {
            string zh = Localizer.Get(key, "zh");
            string en = Localizer.Get(key, "en");

            Assert.False(string.IsNullOrWhiteSpace(zh), $"中文缺失：{key}");
            Assert.False(string.IsNullOrWhiteSpace(en), $"英文缺失：{key}");
            Assert.NotEqual(key, zh);
            Assert.NotEqual(key, en);
        }
    }

    [Fact]
    public void SwitchLanguage_ChangesTextAndRestores()
    {
        Localizer loc = Localizer.Instance;
        string original = loc.Language;

        try
        {
            loc.SetLanguage("zh");
            Assert.Equal("新建项目", loc.Get("Label.NewProject"));
            Assert.Equal("共 21 张", loc.Format("Label.TotalImages", 21));

            loc.SetLanguage("en");
            Assert.Equal("New project", loc.Get("Label.NewProject"));
            Assert.Equal("21 images", loc.Format("Label.TotalImages", 21));
        }
        finally
        {
            loc.SetLanguage(original);
        }
    }

    [Fact]
    public async Task ExceptionMessages_FollowCurrentLanguage()
    {
        using var workspace = new TestWorkspace();
        var service = new TinyLabelProjectService();
        StorageLocation root = StorageLocation.FromPath(workspace.Root);
        Localizer loc = Localizer.Instance;
        string original = loc.Language;

        try
        {
            // Core 抛错文案也走 Localizer：切到英文后异常消息不能还是中文
            loc.SetLanguage("en");
            FileNotFoundException en = await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.CreateAsync(root, "demo"));
            Assert.Contains("annotrain.db", en.Message);
            Assert.DoesNotContain("数据目录", en.Message);

            loc.SetLanguage("zh");
            FileNotFoundException zh = await Assert.ThrowsAsync<FileNotFoundException>(
                () => service.CreateAsync(root, "demo"));
            Assert.Contains("数据目录", zh.Message);
        }
        finally
        {
            loc.SetLanguage(original);
        }
    }

    [Fact]
    public async Task CreateProject_RequiresExistingDatabase()
    {
        using var workspace = new TestWorkspace();
        var service = new TinyLabelProjectService();
        StorageLocation root = StorageLocation.FromPath(workspace.Root);

        // 数据目录还没初始化：新建项目应报错，且不会偷偷建库
        await Assert.ThrowsAsync<FileNotFoundException>(() => service.CreateAsync(root, "空项目"));
        Assert.False(File.Exists(Path.Combine(workspace.Root, TinyLabelProjectCatalog.DefaultDatabaseFile)));

        // 打开数据目录（初始化空库）后，新建项目只插一行
        TinyLabelWorkspace.EnsureDatabase(root);
        long projectId = await service.CreateAsync(root, "空项目");

        Assert.True(projectId > 0);
        Assert.True(File.Exists(Path.Combine(workspace.Root, TinyLabelProjectCatalog.DefaultDatabaseFile)));
    }
}
