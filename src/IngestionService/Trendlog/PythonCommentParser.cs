using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IngestionService.Trendlog;

/// <summary>
/// Resultat af at parse et Trendlog <c>comment</c>-felt på Python-dict-format.
/// Hverken <see cref="Category"/> eller <see cref="Reason"/> er garanteret non-null.
/// </summary>

public readonly record struct PythonCommentResult(string? Category, string? Reason, bool ParseSucceeded)
{
    public static PythonCommentResult Empty { get; } = new(null, null, false);
}

/// <summary>
/// Parser Trendlogs <c>comment</c>-felt, der leveres som en Python-dict-stringificering — ikke gyldig JSON.
/// Eksempel: <code>{'category': 'Fault', 'comment': 'Product jam'}</code>
/// <list type="bullet">
///   <item><description>Understøtter single- og double-quote værdier.</description></item>
///   <item><description>Un-escaper <c>\'</c> og <c>\"</c> i værdier.</description></item>
///   <item><description>Ukendte nøgler ignoreres.</description></item>
/// </list>
/// </summary>
public static class PythonCommentParser
{
    // Mønstret matcher: '<key>': '<v1>'  ELLER  '<key>': "<v2>"
    // Begge værdi-grupper tillader escapede citationer via \'/\".
    private static readonly Regex KeyValuePattern = new(
        @"'(?<key>[^']+)'\s*:\s*(?:'(?<v1>(?:[^'\\]|\\.)*)'|""(?<v2>(?:[^""\\]|\\.)*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// Parser et rå-comment-felt fra Trendlog. Returnerer
    /// <see cref="PythonCommentResult.Empty"/> for tomme inputs eller for
    /// strenge der ikke kan tolkes som en Python-dict.
    /// </summary>
    public static PythonCommentResult Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return PythonCommentResult.Empty;
        }

        var trimmed = raw.Trim();

        // En gyldig Python-dict-stringificering omsluttes af krøllede paranteser.
        // Hvis disse mangler, har Trendlog returneret enten en almindelig tekst
        // eller et malformeret felt; i begge tilfælde er der ingen struktureret
        // information at udtrække.
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return PythonCommentResult.Empty;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (Match match in KeyValuePattern.Matches(trimmed))
            {
                if (!match.Success)
                {
                    continue;
                }

                var key = match.Groups["key"].Value;
                var rawValue = match.Groups["v1"].Success
                    ? match.Groups["v1"].Value
                    : match.Groups["v2"].Value;

                var value = Unescape(rawValue).Trim();
                if (!fields.ContainsKey(key))
                {
                    fields[key] = value;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathologisk input som overstiger 1s tidsbudget afvises stille;
            // pipelinen skal ikke afbrydes af et enkelt operatør-comment.
            return PythonCommentResult.Empty;
        }

        var hasCategory = fields.TryGetValue("category", out var category)
                          && !string.IsNullOrWhiteSpace(category);
        var hasReason = fields.TryGetValue("comment", out var comment)
                         && !string.IsNullOrWhiteSpace(comment);

        if (!hasCategory && !hasReason)
        {
            return PythonCommentResult.Empty;
        }

        return new PythonCommentResult(
            hasCategory ? category : null,
            hasReason ? comment : null,
            ParseSucceeded: true);
    }

    /// <summary>
    /// Konstruerer en sammensat tekstuel stop-årsag på basis af de parsed felter.
    /// <list type="bullet">
    ///   <item><description>Begge felter til stede: <c>"{Category}: {Reason}"</c>.</description></item>
    ///   <item><description>Kun et felt: feltets indhold uændret.</description></item>
    ///   <item><description>Ingen felter: tom string.</description></item>
    /// </list>
    /// </summary>
    public static string ComposeReason(in PythonCommentResult parsed)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Category)
            && !string.IsNullOrWhiteSpace(parsed.Reason))
        {
            return $"{parsed.Category}: {parsed.Reason}";
        }

        if (!string.IsNullOrWhiteSpace(parsed.Reason))
        {
            return parsed.Reason!;
        }

        if (!string.IsNullOrWhiteSpace(parsed.Category))
        {
            return parsed.Category!;
        }

        return string.Empty;
    }

    private static string Unescape(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // Standard Python-string-escapes der observeres i Trendlogs comment-felt.
        // Rækkefølgen er signifikant: \\ skal konverteres SIDST for at undgå at
        // \\\' fejlfortolkes som \' efter \\ er konverteret til \.
        return raw
            .Replace("\\'", "'", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
