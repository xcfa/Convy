using Convy.Services.Http;

namespace Convy.Services.Tests;

public class NoProxyListTests
{
    [Theory]
    // Domains: with or without a leading dot or "*.", the domain and every subdomain.
    [InlineData("bakuretsu.org", "https://auth.bakuretsu.org", true)]
    [InlineData("bakuretsu.org", "https://bakuretsu.org", true)]
    [InlineData(".bakuretsu.org", "https://auth.bakuretsu.org", true)]
    [InlineData(".bakuretsu.org", "https://bakuretsu.org", true)]
    [InlineData("*.bakuretsu.org", "https://auth.bakuretsu.org", true)]
    [InlineData("*.bakuretsu.org", "https://a.b.bakuretsu.org", true)]
    [InlineData("*.bakuretsu.org", "https://bakuretsu.org", true)]
    [InlineData("*.bakuretsu.org", "https://notbakuretsu.org", false)]
    [InlineData("bakuretsu.org", "https://bakuretsu.org.evil.com", false)]
    [InlineData("prowlarr", "http://prowlarr:9696", true)]
    [InlineData("prowlarr", "http://slskd:5030", false)]
    [InlineData("Auth.Bakuretsu.ORG.", "https://auth.bakuretsu.org", true)]
    // Addresses and ranges; names are not resolved to match a range.
    [InlineData("172.16.0.0/12", "http://172.20.0.5:9091", true)]
    [InlineData("172.16.0.0/12", "http://172.32.0.1", false)]
    [InlineData("172.16.0.0/12", "http://prowlarr:9696", false)]
    [InlineData("10.0.0.0/8", "http://10.200.3.4", true)]
    [InlineData("192.168.1.5/24", "http://192.168.1.77", true)]
    [InlineData("0.0.0.0/0", "http://8.8.8.8", true)]
    [InlineData("10.0.0.5", "http://10.0.0.5:8080", true)]
    [InlineData("10.0.0.5", "http://10.0.0.6", false)]
    [InlineData("fd00::/8", "http://[fd12::1]:8080", true)]
    [InlineData("fd00::/8", "http://[fe80::1]", false)]
    [InlineData("fd00::5", "http://[fd00::5]", true)]
    [InlineData("10.0.0.0/8", "http://[fd00::5]", false)]
    // Ports.
    [InlineData("example.org:8080", "http://example.org:8080", true)]
    [InlineData("example.org:8080", "http://example.org", false)]
    [InlineData("10.0.0.5:443", "https://10.0.0.5", true)]
    [InlineData("[fd00::5]:8080", "http://[fd00::5]:8080", true)]
    [InlineData("[fd00::5]:8080", "http://[fd00::5]:9090", false)]
    // Everything.
    [InlineData("*", "https://flibusta.site", true)]
    [InlineData("", "https://flibusta.site", false)]
    public void MatchesTheCommonForms(string noProxy, string url, bool direct) =>
        Assert.Equal(direct, NoProxyList.Parse(noProxy).Matches(new Uri(url)));

    [Fact]
    public void ReadsAListSeparatedByCommasAndSpaces()
    {
        var list = NoProxyList.Parse("localhost, 127.0.0.1 ,.bakuretsu.org\t172.16.0.0/12,,prowlarr");

        Assert.Empty(list.Invalid);
        Assert.True(list.Matches(new Uri("http://prowlarr:9696")));
        Assert.True(list.Matches(new Uri("https://auth.bakuretsu.org")));
        Assert.True(list.Matches(new Uri("http://172.18.0.2")));
        Assert.False(list.Matches(new Uri("https://flibusta.site")));
    }

    [Fact]
    public void SetsAsideWhatItCannotRead()
    {
        var list = NoProxyList.Parse("10.0.0.0/40, 1.2.3.4/x, example.org:http, *foo, ok.org");

        Assert.Equal(["10.0.0.0/40", "1.2.3.4/x", "example.org:http", "*foo"], list.Invalid);
        Assert.True(list.Matches(new Uri("https://www.ok.org")));
    }
}

public class EnvironmentProxyTests
{
    private static EnvironmentProxy? Proxy(params (string Name, string Value)[] variables) =>
        Proxy(out _, variables);

    private static EnvironmentProxy? Proxy(out IReadOnlyList<string> problems, params (string Name, string Value)[] variables)
    {
        var values = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return EnvironmentProxy.FromEnvironment(name => values.GetValueOrDefault(name), out problems);
    }

    [Fact]
    public void NoProxyVariablesMeanNoProxy()
    {
        Assert.Null(Proxy());
        Assert.Null(Proxy(("NO_PROXY", "localhost")));
    }

    [Fact]
    public void HttpsAddressesUseHttpsProxyAndHttpOnesHttpProxy()
    {
        var proxy = Proxy(("HTTP_PROXY", "http://plain:3128"), ("HTTPS_PROXY", "http://secure:3128"))!;

        Assert.Equal(new Uri("http://secure:3128"), proxy.GetProxy(new Uri("https://flibusta.site/opds")));
        Assert.Equal(new Uri("http://plain:3128"), proxy.GetProxy(new Uri("http://flibusta.is/opds")));
    }

    [Fact]
    public void WithoutHttpsProxyHttpsAddressesGoDirect()
    {
        var proxy = Proxy(("HTTP_PROXY", "http://plain:3128"))!;

        Assert.True(proxy.IsBypassed(new Uri("https://flibusta.site")));
        Assert.Null(proxy.GetProxy(new Uri("https://flibusta.site")));
    }

    [Fact]
    public void AllProxyCoversWhatHasNoProxyOfItsOwn()
    {
        var proxy = Proxy(("ALL_PROXY", "socks5h://tor:9050"), ("HTTP_PROXY", "http://plain:3128"))!;

        Assert.Equal(new Uri("socks5://tor:9050"), proxy.GetProxy(new Uri("https://flibusta.site")));
        Assert.Equal(new Uri("http://plain:3128"), proxy.GetProxy(new Uri("http://flibusta.is")));
    }

    [Fact]
    public void LowerCaseWinsAndSchemeDefaultsToHttp()
    {
        var proxy = Proxy(("https_proxy", "mihomo:7890"), ("HTTPS_PROXY", "http://other:1"))!;

        Assert.Equal(new Uri("http://mihomo:7890"), proxy.HttpsProxy);
    }

    [Fact]
    public void NoProxyAndLoopbackGoDirect()
    {
        var proxy = Proxy(("HTTPS_PROXY", "http://mihomo:7890"), ("HTTP_PROXY", "http://mihomo:7890"),
            ("NO_PROXY", "*.bakuretsu.org,172.16.0.0/12,prowlarr"))!;

        Assert.True(proxy.IsBypassed(new Uri("https://auth.bakuretsu.org")));
        Assert.True(proxy.IsBypassed(new Uri("http://172.20.0.5:8080")));
        Assert.True(proxy.IsBypassed(new Uri("http://prowlarr:9696")));
        Assert.True(proxy.IsBypassed(new Uri("http://localhost:8080")));
        Assert.True(proxy.IsBypassed(new Uri("http://127.0.0.1:8080")));
        Assert.True(proxy.IsBypassed(new Uri("http://[::1]:8080")));
        Assert.False(proxy.IsBypassed(new Uri("https://flibusta.site")));
    }

    [Fact]
    public void CredentialsGoToTheProxyAndStayOutOfItsAddress()
    {
        var proxy = Proxy(("HTTPS_PROXY", "http://user:p%40ss@mihomo:7890"))!;

        Assert.Equal(new Uri("http://mihomo:7890"), proxy.GetProxy(new Uri("https://flibusta.site")));
        var credential = proxy.Credentials!.GetCredential(new Uri("http://mihomo:7890"), "Basic");
        Assert.Equal(("user", "p@ss"), (credential!.UserName, credential.Password));
        Assert.Null(proxy.Credentials.GetCredential(new Uri("http://other:7890"), "Basic"));
    }

    [Fact]
    public void ReportsWhatItCannotRead()
    {
        var proxy = Proxy(out var problems, ("HTTPS_PROXY", "ftp://mihomo:21"), ("HTTP_PROXY", "http://plain:3128"),
            ("NO_PROXY", "ok.org,10.0.0.0/33"));

        Assert.NotNull(proxy);
        Assert.Null(proxy.HttpsProxy);
        Assert.Equal(2, problems.Count);
        Assert.Contains("HTTPS_PROXY", problems[0]);
        Assert.Contains("10.0.0.0/33", problems[1]);
    }
}
