namespace TelPhone.Core;

public static class PhoneNumber
{
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var number = string.Concat(input.Where(character =>
            !char.IsWhiteSpace(character) && character is not ('-' or '(' or ')')));
        var digits = number.AsSpan(number.StartsWith('+') ? 1 : 0);
        return digits.Length is > 0 and <= 15 && digits.IndexOfAnyExceptInRange('0', '9') < 0
            ? number
            : null;
    }
}
