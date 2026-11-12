using Sevencat.Core.Dataset.TinyLabel;
using Sevencat.Core.Localization;
using Sevencat.Core.Storage;
using Xunit;

namespace Sevencat.Label.Tests;

/// <summary>本地化表完整性，以及启动即建库后「新建项目只插一行」的语义。</summary>
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
        var service = new TinyLabelProjectService(workspace.OpenDatabase().Orm);
        StorageLocation root = StorageLocation.FromPath(workspace.Root);
        Localizer loc = Localizer.Instance;
        string original = loc.Language;

        try
        {
            // Core 抛错文案也走 Localizer：切到英文后异常消息不能还是中文
            loc.SetLanguage("en");
            ArgumentException en = await Assert.ThrowsAsync<ArgumentException>(
                () => service.CreateAsync(root, "   "));
            Assert.Contains("Name", en.Message);
            Assert.DoesNotContain("项目", en.Message);

            loc.SetLanguage("zh");
            ArgumentException zh = await Assert.ThrowsAsync<ArgumentException>(
                () => service.CreateAsync(root, "   "));
            Assert.Contains("项目名", zh.Message);
        }
        finally
        {
            loc.SetLanguage(original);
        }
    }

    [Fact]
    public async Task CreateProject_WritesIntoStartedDatabase()
    {
        using var workspace = new TestWorkspace();
        TinyLabelDatabase database = workspace.OpenDatabase();
        var service = new TinyLabelProjectService(database.Orm);
        StorageLocation root = StorageLocation.FromPath(workspace.Root);

        long projectId = await service.CreateAsync(root, "空项目");

        Assert.True(projectId > 0);
        Assert.True(File.Exists(database.Path));
        Assert.True(Directory.Exists(TinyLabelWorkspace.ProjectDirectory(root, projectId)));
    }
}
