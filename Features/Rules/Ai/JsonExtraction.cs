namespace NtfyDesktop.Features.Rules.Ai;

/// <summary>Pulls the first balanced JSON object out of a model response that may be
/// wrapped in markdown fences or surrounded by prose.</summary>
public static class JsonExtraction
{
    public static string? ExtractObject(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    if (--depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }
}
