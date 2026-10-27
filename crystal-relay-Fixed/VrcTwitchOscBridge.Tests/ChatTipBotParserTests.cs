using Xunit;
using VrcTwitchOscBridge.Services;

namespace VrcTwitchOscBridge.Tests;

public sealed class ChatTipBotParserTests
{
    [Theory]
    [InlineData("SomeViewer just tipped $5.00! Thanks!", 5.00, "USD", "SomeViewer")]
    [InlineData("Kaylee donated $10 - have a great stream", 10, "USD", "Kaylee")]
    [InlineData("Alex tipped £7.50", 7.50, "GBP", "Alex")]
    [InlineData("Noa donated €7,50", 7.50, "EUR", "Noa")]
    [InlineData("Sam sent 25 USD", 25, "USD", "Sam")]
    [InlineData("Rae tipped USD 3.20", 3.20, "USD", "Rae")]
    [InlineData("Whale dropped $1,234.56", 1234.56, "USD", "Whale")]
    public void TryParseTip_ReadsCommonBotFormats(
        string message,
        double expectedAmount,
        string expectedCurrency,
        string expectedTipper)
    {
        var parsed = ChatTipBotParser.TryParseTip(message, null, null, out var amount, out var currency, out var tipper);

        Assert.True(parsed);
        Assert.Equal((decimal)expectedAmount, amount);
        Assert.Equal(expectedCurrency, currency);
        Assert.Equal(expectedTipper, tipper);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Follow the channel for updates!")]
    [InlineData("Next raid in a bit")]
    [InlineData("!kofi")]
    public void TryParseTip_IgnoresMessagesWithoutAnAmount(string message)
    {
        Assert.False(ChatTipBotParser.TryParseTip(message, null, null, out var amount, out _, out _));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void TryParseTip_FallsBackToConfiguredCurrencyWhenMessageHasNone()
    {
        var parsed = ChatTipBotParser.TryParseTip("Jo tipped 12", null, "AUD", out var amount, out var currency, out _);

        Assert.True(parsed);
        Assert.Equal(12m, amount);
        Assert.Equal("AUD", currency);
    }

    [Fact]
    public void TryParseTip_DefaultsCurrencyToUsd()
    {
        Assert.True(ChatTipBotParser.TryParseTip("Jo tipped 12", null, null, out _, out var currency, out _));
        Assert.Equal("USD", currency);
    }

    [Fact]
    public void TryParseTip_UsesDefaultTipperNameWhenNoNameIsPresent()
    {
        Assert.True(ChatTipBotParser.TryParseTip("A tip of $4 arrived", null, null, out _, out _, out var tipper));
        Assert.Equal(ChatTipBotParser.DefaultTipperName, tipper);
    }

    [Fact]
    public void TryParseTip_HonoursCustomPatternGroups()
    {
        const string pattern = @"(?<user>\w+) gave (?<amount>[\d.]+) (?<currency>[A-Z]{3})";
        var parsed = ChatTipBotParser.TryParseTip(
            "Mira gave 9.99 CAD to the stream",
            pattern,
            null,
            out var amount,
            out var currency,
            out var tipper);

        Assert.True(parsed);
        Assert.Equal(9.99m, amount);
        Assert.Equal("CAD", currency);
        Assert.Equal("Mira", tipper);
    }

    [Fact]
    public void TryParseTip_InvalidCustomPatternDoesNotThrow()
    {
        Assert.False(ChatTipBotParser.TryParseTip("Mira tipped $5", "(?<amount>[", null, out var amount, out _, out _));
        Assert.Equal(0m, amount);
    }

    [Fact]
    public void TryParseTip_RejectsZeroAndNegativeAmounts()
    {
        Assert.False(ChatTipBotParser.TryParseTip("Jo tipped $0.00", null, null, out _, out _, out _));
        Assert.False(ChatTipBotParser.TryParseTip("Balance is -$5", null, null, out var amount, out _, out _));
        Assert.True(amount >= 0m);
    }

    [Fact]
    public void ParseLines_KeepsWholeLoginsAndSkipsNoise()
    {
        var parsed = ChatTipBotWhitelist.ParseLines(
        [
            "# bots that announce tips",
            "streamelements",
            "  @StreamLabs  ",
            string.Empty,
            "nightbot # keep",
            "not a login"
        ]);

        Assert.Equal(3, parsed.Count);
        Assert.Contains("streamelements", parsed);
        Assert.Contains("streamlabs", parsed);
        Assert.Contains("nightbot", parsed);
        Assert.DoesNotContain("not a login", parsed);
    }

    [Fact]
    public void ParseLines_MatchesWholeNameOnlyWithNoWildcard()
    {
        var parsed = ChatTipBotWhitelist.ParseLines(["streamelements"]);

        Assert.Contains("StreamElements", parsed);
        Assert.DoesNotContain("streamelements_alt", parsed);
        Assert.DoesNotContain("notstreamelements", parsed);
    }
}
