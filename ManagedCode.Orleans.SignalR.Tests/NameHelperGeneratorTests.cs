using ManagedCode.Orleans.SignalR.Core.SignalR;
using Shouldly;
using Xunit;

namespace ManagedCode.Orleans.SignalR.Tests;

public sealed class NameHelperGeneratorTests
{
    [Theory]
    [InlineData("a/b", "a?b")]
    [InlineData("a/b", "a:b")]
    [InlineData("a?b", "a:b")]
    [InlineData("hello world", "hello:world")]
    [InlineData("група/один", "група?один")]
    public void VersionedLeafKeysDoNotCollapseDistinctLogicalIdentities(string first, string second)
    {
        var firstKey = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", first);
        var secondKey = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", second);

        firstKey.ShouldNotBe(secondKey);
        firstKey.ShouldStartWith("v2:");
    }

    [Theory]
    [InlineData("valid-id")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionedLeafKeysAreDeterministicAndBounded(string? logicalIdentity)
    {
        var first = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", logicalIdentity);
        var second = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", logicalIdentity);

        first.ShouldBe(second);
        first.ShouldStartWith("v2:");
        first.Length.ShouldBe(46);
    }

    [Fact]
    public void VersionedLeafKeySeparatesTupleBoundaries()
    {
        var first = NameHelperGenerator.CreateVersionedLeafKey("a/b", "c");
        var second = NameHelperGenerator.CreateVersionedLeafKey("a", "b/c");

        first.ShouldNotBe(second);
        first.Length.ShouldBe(46);
        second.Length.ShouldBe(46);
    }

    [Fact]
    public void VersionedLeafKeysKeepTheirExistingEncodingForShortAndLongUtf8Identities()
    {
        NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", "група/one")
            .ShouldBe("v2:2-NmzKg6Jyf8ivmyQEoreiwtg4uYBs2yN0YI6NMKw5k");
        NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", new string('ж', 1024))
            .ShouldBe("v2:247CDZGhRxHHktjUOB36a6WIuWl80Z7Q4nzKPbvAzJQ");
    }

    [Fact]
    public void VersionedLeafKeySeparatesNullAndEmptyIdentity()
    {
        var nullIdentity = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", null);
        var emptyIdentity = NameHelperGenerator.CreateVersionedLeafKey("Example.Hub", string.Empty);

        nullIdentity.ShouldNotBe(emptyIdentity);
    }
}
