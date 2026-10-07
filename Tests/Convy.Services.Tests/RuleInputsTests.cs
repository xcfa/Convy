using Convy.Services.Downloads;
using Convy.Services.Rules;

namespace Convy.Services.Tests;

public class RuleInputsTests
{
    private static DownloadItem Item(string provider, Dictionary<string, object?> properties) => new()
    {
        Provider = provider,
        ItemRef = "ref",
        Name = "name",
        SavePath = "/dl",
        State = DownloadState.Completed,
        Properties = properties,
    };

    [Fact]
    public void ProviderIsAlwaysPresent()
    {
        var input = RuleInputs.For(Item("slskd", new Dictionary<string, object?>()));

        Assert.Equal("slskd", input["Provider"]);
        Assert.False(input.ContainsKey("Category"));
    }

    [Fact]
    public void JobCategoryOverridesTheClientCategory()
    {
        var item = Item("qbittorrent", new Dictionary<string, object?> { ["Category"] = "Manual" });

        Assert.Equal("Manual", RuleInputs.For(item)["Category"]);
        Assert.Equal("Music", RuleInputs.For(item, "Music")["category"]);
    }

    [Fact]
    public void DownloaderPropertiesAreNotModified()
    {
        var properties = new Dictionary<string, object?> { ["Category"] = "Manual" };
        RuleInputs.For(Item("qbittorrent", properties), "Music");

        Assert.Equal("Manual", properties["Category"]);
        Assert.False(properties.ContainsKey("Provider"));
    }
}
