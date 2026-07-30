using DuplicateVault.Core;

namespace DuplicateVault.Core.Tests;

public sealed class CoreTests
{
    [Theory]
    [InlineData("1MiB", 1048576)]
    [InlineData("2 gb", 2147483648)]
    [InlineData("512", 512)]
    public void SizeParser_ParsesExpectedUnits(string value, long expected)
    {
        Assert.Equal(expected, SizeParser.Parse(value));
    }

    [Fact]
    public void ExclusionEngine_AppliesPriorityAndWildcard()
    {
        var engine = new ExclusionEngine([
            new("Temporary", "wildcard", "*.tmp", true, true, false, false, 10)
        ]);

        Assert.Equal("Temporary", engine.GetExclusionReason(@"D:\data\file.tmp", false));
        Assert.Null(engine.GetExclusionReason(@"D:\data\file.bin", false));
    }
}
