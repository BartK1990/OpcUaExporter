using System.Globalization;
using OpcUaExporter.UI;
using Xunit;

namespace OpcUaExporter.Tests;

public class ValueFormatterTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("pl-PL")]
    public void DateTime_UsesFixed24HourFormat_RegardlessOfCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var value = new DateTime(2026, 10, 14, 17, 45, 30, DateTimeKind.Utc);

            Assert.Equal("2026-10-14 17:45:30", ValueFormatter.Format(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DateTime_DropsFractionalSeconds()
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc);

        Assert.Equal("2026-01-02 03:04:05", ValueFormatter.Format(value));
    }

    [Fact]
    public void DateTimeOffset_IsShownInUtc()
    {
        var value = new DateTimeOffset(2026, 10, 14, 19, 45, 30, TimeSpan.FromHours(2));

        Assert.Equal("2026-10-14 17:45:30", ValueFormatter.Format(value));
    }

    [Fact]
    public void Null_UsesTheGivenPlaceholder()
    {
        Assert.Equal("–", ValueFormatter.Format(null, "–"));
        Assert.Equal(string.Empty, ValueFormatter.Format(null));
    }

    [Fact]
    public void OtherValues_UseTheirOwnText()
    {
        Assert.Equal("42", ValueFormatter.Format(42));
        Assert.Equal("hello", ValueFormatter.Format("hello"));
    }
}
