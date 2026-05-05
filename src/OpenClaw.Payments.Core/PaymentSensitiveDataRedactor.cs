using System.Text.RegularExpressions;
using OpenClaw.Core.Security;
using OpenClaw.Payments.Abstractions;

namespace OpenClaw.Payments.Core;

public sealed class PaymentSensitiveDataRedactor : ISensitiveDataRedactor, IPaymentRedactor
{
    private static readonly Regex PanCandidateRegex = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CvvContextRegex = new(
        @"(?i)\b(cvv|cvc|security[-_\s]?code|card[-_\s]?code)\b(\s*[""']?\s*[:=]\s*[""']?)\d{3,4}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PaymentAuthorizationRegex = new(
        @"(?im)\b(Authorization\s*:\s*Payment\s+)[^\s\r\n]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PaymentJsonFieldRegex = new(
        @"(?i)([""']?(?:pan|cardNumber|card_number|cvv|cvc|securityCode|security_code|paymentToken|payment_token|sharedPaymentToken|shared_payment_token|authorizationToken|authorization_token|providerSecret|provider_secret|rawSecret|raw_secret|clientSecret|client_secret)[""']?\s*[:=]\s*[""']?)[^,""'\s}\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PaymentTokenRegex = new(
        @"(?i)\b(?:spt|payment|paytok|machinepay)_[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Name => "payment-secrets";

    public string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var redacted = PanCandidateRegex.Replace(value, match =>
        {
            var digits = StripDigits(match.Value);
            return IsPotentialPan(digits) && PassesLuhn(digits)
                ? "[REDACTED:payment-card]"
                : match.Value;
        });

        redacted = CvvContextRegex.Replace(redacted, "$1$2[REDACTED:payment-cvv]");
        redacted = PaymentAuthorizationRegex.Replace(redacted, "$1[REDACTED:payment-authorization]");
        redacted = PaymentJsonFieldRegex.Replace(redacted, "$1[REDACTED:payment-secret]");
        redacted = PaymentTokenRegex.Replace(redacted, "[REDACTED:payment-token]");
        return redacted;
    }

    private static string StripDigits(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var count = 0;
        foreach (var ch in value)
        {
            if (ch is >= '0' and <= '9')
                buffer[count++] = ch;
        }

        return new string(buffer[..count]);
    }

    private static bool IsPotentialPan(string digits)
        => digits.Length is >= 13 and <= 19;

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }

            sum += n;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }
}
