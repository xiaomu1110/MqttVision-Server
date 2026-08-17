namespace MqttVision.Server.Application.Configuration;

internal static class ConfigurationMarkerNormalizer
{
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = string.Concat(text
            .Select(character => character switch
            {
                '／' => '/',
                '–' or '—' or '－' => '-',
                '＇' or '\'' or '’' or '‘' => '/',
                _ => character
            })
            .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

        return CollapseDuplicateSlashes(normalized);
    }

    public static string? NormalizeLoose(string? text)
    {
        var normalized = Normalize(text);
        if (normalized is null)
        {
            return null;
        }

        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }

    private static string CollapseDuplicateSlashes(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var previousWasSlash = false;
        foreach (var character in value)
        {
            if (character == '/')
            {
                if (previousWasSlash)
                {
                    continue;
                }

                previousWasSlash = true;
            }
            else
            {
                previousWasSlash = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
