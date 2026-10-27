using System.Globalization;
using System.Text.RegularExpressions;

namespace VrcTwitchOscBridge.Services;

/// <summary>
/// Reads a tip amount out of a chat message posted by a whitelisted bot account.
///
/// Some tipping services announce payments in Twitch chat through their own bot
/// (Ko-fi does this natively). Because Crystal Relay already reads chat over EventSub,
/// a whitelisted bot's announcement can drive the normal cash payment rules without any
/// webhook, relay or tunnel. See README.md.
///
/// This class is pure and has no dependencies so it can be unit tested directly.
/// </summary>
public static class ChatTipBotParser
{
    private const string DefaultCurrencyCode = "USD";
    public const string DefaultTipperName = "Chat supporter";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private const string NumberPattern =
        @"\d{1,3}(?:[,.   ]\d{3})+(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?";

    // "$5", "$5.00", "£10", "€7,50", "¥500"
    private static readonly Regex SymbolThenNumber = new(
        @"(?<sym>[$£€¥₹])\s*(?<num>" + NumberPattern + ")",
        RegexOptions.CultureInvariant, RegexTimeout);

    // "5 USD", "7,50 EUR"
    private static readonly Regex NumberThenCode = new(
        @"(?<num>" + NumberPattern + @")\s*(?<code>[A-Za-z]{3})\b",
        RegexOptions.CultureInvariant, RegexTimeout);

    // "USD 5", "EUR 7,50"
    private static readonly Regex CodeThenNumber = new(
        @"\b(?<code>[A-Za-z]{3})\s*(?<num>" + NumberPattern + ")",
        RegexOptions.CultureInvariant, RegexTimeout);

    // "SomeViewer just tipped ...", "SomeViewer donated ..."
    private static readonly Regex LeadingTipperName = new(
        @"^(?<user>[^\s].{0,39}?)\s+(?:just\s+|has\s+)?(?:tipped|donated|dropped|sent|gifted|supported|bought)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);

    private static readonly Dictionary<char, string> SymbolCurrencyCodes = new()
    {
        ['$'] = "USD",
        ['£'] = "GBP",
        ['€'] = "EUR",
        ['¥'] = "JPY",
        ['₹'] = "INR"
    };

    /// <summary>
    /// Attempts to read a tip out of a bot chat message.
    /// </summary>
    /// <param name="messageText">The full chat message as posted by the bot.</param>
    /// <param name="customPattern">
    /// Optional user-supplied regex. Named groups are honoured: <c>amount</c> (required),
    /// <c>currency</c> and <c>user</c> (both optional). When supplied this replaces the
    /// built-in detection entirely, so an unusual bot format can be handled without a
    /// code change.
    /// </param>
    /// <param name="fallbackCurrencyCode">Currency to assume when the message carries none.</param>
    public static bool TryParseTip(
        string? messageText,
        string? customPattern,
        string? fallbackCurrencyCode,
        out decimal amount,
        out string currencyCode,
        out string tipperName)
    {
        amount = 0m;
        currencyCode = NormalizeCurrencyCode(fallbackCurrencyCode);
        tipperName = DefaultTipperName;

        var text = messageText?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(customPattern))
            {
                return TryParseWithCustomPattern(text, customPattern, ref amount, ref currencyCode, ref tipperName);
            }

            if (!TryParseAmountAndCurrency(text, out amount, out var detectedCurrency))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(detectedCurrency))
            {
                currencyCode = detectedCurrency;
            }

            tipperName = ExtractTipperName(text);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            amount = 0m;
            return false;
        }
        catch (ArgumentException)
        {
            // An invalid custom pattern must never take the app down.
            amount = 0m;
            return false;
        }
    }

    private static bool TryParseWithCustomPattern(
        string text,
        string customPattern,
        ref decimal amount,
        ref string currencyCode,
        ref string tipperName)
    {
        var regex = new Regex(
            customPattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            RegexTimeout);

        var match = regex.Match(text);
        if (!match.Success)
        {
            return false;
        }

        var amountGroup = match.Groups["amount"];
        if (!amountGroup.Success || !TryNormalizeAmount(amountGroup.Value, out amount))
        {
            return false;
        }

        var currencyGroup = match.Groups["currency"];
        if (currencyGroup.Success)
        {
            var normalized = NormalizeDetectedCurrency(currencyGroup.Value);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                currencyCode = normalized;
            }
        }

        var userGroup = match.Groups["user"];
        tipperName = userGroup.Success && !string.IsNullOrWhiteSpace(userGroup.Value)
            ? CleanTipperName(userGroup.Value)
            : ExtractTipperName(text);

        return true;
    }

    private static bool TryParseAmountAndCurrency(string text, out decimal amount, out string currencyCode)
    {
        amount = 0m;
        currencyCode = string.Empty;

        var symbolMatch = SymbolThenNumber.Match(text);
        if (symbolMatch.Success && TryNormalizeAmount(symbolMatch.Groups["num"].Value, out amount))
        {
            var symbol = symbolMatch.Groups["sym"].Value[0];
            currencyCode = SymbolCurrencyCodes.TryGetValue(symbol, out var mapped) ? mapped : string.Empty;
            return true;
        }

        var codeAfterMatch = NumberThenCode.Match(text);
        if (codeAfterMatch.Success && TryNormalizeAmount(codeAfterMatch.Groups["num"].Value, out amount))
        {
            currencyCode = NormalizeDetectedCurrency(codeAfterMatch.Groups["code"].Value);
            return true;
        }

        var codeFirstMatch = CodeThenNumber.Match(text);
        if (codeFirstMatch.Success && TryNormalizeAmount(codeFirstMatch.Groups["num"].Value, out amount))
        {
            currencyCode = NormalizeDetectedCurrency(codeFirstMatch.Groups["code"].Value);
            return true;
        }

        amount = 0m;
        return false;
    }

    /// <summary>
    /// Converts a localized number into a decimal without guessing wrongly about
    /// separators. When both "." and "," appear, the rightmost one is the decimal mark
    /// and the other is grouping. When only one appears it is a decimal mark only if it
    /// is followed by one or two digits; otherwise it is grouping.
    /// </summary>
    private static bool TryNormalizeAmount(string rawValue, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var value = rawValue
            .Replace(" ", string.Empty)
            .Replace(" ", string.Empty)
            .Replace(" ", string.Empty);

        var lastDot = value.LastIndexOf('.');
        var lastComma = value.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            if (lastDot > lastComma)
            {
                value = value.Replace(",", string.Empty);
            }
            else
            {
                value = value.Replace(".", string.Empty).Replace(',', '.');
            }
        }
        else if (lastComma >= 0)
        {
            value = IsDecimalSeparator(value, lastComma)
                ? value.Replace(',', '.')
                : value.Replace(",", string.Empty);
        }
        else if (lastDot >= 0 && !IsDecimalSeparator(value, lastDot))
        {
            value = value.Replace(".", string.Empty);
        }

        return decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out amount)
            && amount > 0m;
    }

    private static bool IsDecimalSeparator(string value, int separatorIndex)
    {
        var trailingDigits = value.Length - separatorIndex - 1;
        return trailingDigits is 1 or 2;
    }

    private static string ExtractTipperName(string text)
    {
        var match = LeadingTipperName.Match(text);
        return match.Success ? CleanTipperName(match.Groups["user"].Value) : DefaultTipperName;
    }

    private static string CleanTipperName(string rawName)
    {
        var cleaned = rawName.Trim().Trim('@', ',', ':', '-', '"', '\'');
        if (cleaned.Length > 40)
        {
            cleaned = cleaned[..40].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? DefaultTipperName : cleaned;
    }

    private static string NormalizeDetectedCurrency(string rawCode)
    {
        var trimmed = rawCode?.Trim() ?? string.Empty;
        if (trimmed.Length == 1 && SymbolCurrencyCodes.TryGetValue(trimmed[0], out var mapped))
        {
            return mapped;
        }

        return trimmed.Length == 3 && trimmed.All(char.IsLetter)
            ? trimmed.ToUpperInvariant()
            : string.Empty;
    }

    private static string NormalizeCurrencyCode(string? rawCode)
    {
        var normalized = NormalizeDetectedCurrency(rawCode ?? string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? DefaultCurrencyCode : normalized;
    }
}
