namespace RagNet.Mcp.Analyzers.Common;

internal static class TokenBudget
{
    public const int DefaultMaxEstimatedTokens = 384;

    public static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var tokens = 0;
        var inWord = false;

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                if (!inWord)
                {
                    tokens++;
                    inWord = true;
                }

                continue;
            }

            inWord = false;
            if (!char.IsWhiteSpace(character))
            {
                tokens++;
            }
        }

        return Math.Max(tokens, (text.Length + 3) / 4);
    }
}
