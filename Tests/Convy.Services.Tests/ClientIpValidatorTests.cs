using System.Net;
using Convy.Services.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Convy.Services.Tests;

public class ClientIpValidatorTests
{
    private static readonly string[] Defaults = IpAccessControlOptions.DefaultAllowedNetworks.ToArray();

    private static ClientIpValidator Build(params string[] networks)
    {
        var options = new IpAccessControlOptions { AllowedNetworks = networks.ToList() };
        return new ClientIpValidator(
            new StaticMonitor<IpAccessControlOptions>(options),
            NullLogger<ClientIpValidator>.Instance);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("172.17.0.2", true)]        // default docker0 bridge
    [InlineData("172.31.255.255", true)]    // top of 172.16.0.0/12
    [InlineData("172.32.0.1", false)]       // just outside /12
    [InlineData("10.0.0.5", false)]
    [InlineData("8.8.8.8", false)]
    public void Matches_default_networks(string ip, bool expected)
    {
        var validator = Build(Defaults);
        Assert.Equal(expected, validator.IsAllowed(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Null_address_is_rejected()
    {
        Assert.False(Build(Defaults).IsAllowed(null));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_is_normalized()
    {
        var validator = Build("192.168.0.0/16");
        var mapped = IPAddress.Parse("192.168.1.50").MapToIPv6(); // ::ffff:192.168.1.50

        Assert.True(mapped.IsIPv4MappedToIPv6);
        Assert.True(validator.IsAllowed(mapped));
    }

    [Fact]
    public void Invalid_entries_are_ignored_but_valid_ones_apply()
    {
        var validator = Build("not-an-ip", "10.0.0.0/8");

        Assert.True(validator.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.False(validator.IsAllowed(IPAddress.Parse("192.168.1.1")));
    }

    private sealed class StaticMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;
        public StaticMonitor(T value) => _value = value;

        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
