using System.Net;
using Carteiro.Dispatch;

namespace Carteiro.UnitTests;

public class SsrfGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("64:ff9b::a00:1")]
    public void Internal_addresses_are_not_public(string address)
    {
        Assert.False(SsrfGuard.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")] // just below 172.16.0.0/12
    [InlineData("172.32.0.0")]     // just above 172.16.0.0/12
    [InlineData("100.63.255.255")] // just below 100.64.0.0/10
    [InlineData("100.128.0.0")]    // just above 100.64.0.0/10
    [InlineData("192.167.255.255")]
    [InlineData("126.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Routable_addresses_are_public(string address)
    {
        Assert.True(SsrfGuard.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://localhost/hooks")]
    [InlineData("http://LOCALHOST:8080/hooks")]
    [InlineData("http://localhost./hooks")]
    [InlineData("http://api.localhost/hooks")]
    [InlineData("http://127.0.0.1:5432/")]
    [InlineData("http://[::1]/hooks")]
    [InlineData("http://10.0.0.5/hooks")]
    [InlineData("http://192.168.0.1/hooks")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://2130706433/")]  // 127.0.0.1 written as a single decimal number
    [InlineData("http://0x7f.0.0.1/")]  // 127.0.0.1 with a hex octet
    public void Urls_pointing_at_internal_hosts_are_flagged(string url)
    {
        Assert.True(SsrfGuard.IsObviouslyInternal(new Uri(url)));
    }

    [Theory]
    [InlineData("https://example.com/hooks")]
    [InlineData("https://8.8.8.8/hooks")]
    [InlineData("https://notlocalhost.example/hooks")]
    public void Urls_pointing_at_public_hosts_are_not_flagged(string url)
    {
        Assert.False(SsrfGuard.IsObviouslyInternal(new Uri(url)));
    }
}
