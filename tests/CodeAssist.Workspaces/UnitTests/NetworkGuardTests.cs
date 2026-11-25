using Shouldly;

namespace CodeAssist.Workspaces.Platform.UnitTests;

public sealed class NetworkGuardTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("192.168.0.5", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void NetworkGuardClassifiesAddresses(string address, bool isPublic) =>
        NetworkGuard.IsPublic(System.Net.IPAddress.Parse(address)).ShouldBe(isPublic);
}