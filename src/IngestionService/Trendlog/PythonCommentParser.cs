using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace IngestionService.Trendlog;

/// <summary>
/// Resultatet af at parse et Trendlog <c>comment</c>-felt på formatet
/// <c>{'category': 'Fault', 'comment': 'Product jam'}</c>.
/// Hverken <see cref="Category"/> eller <see cref="Reason"/> er garanteret
/// non-null; en operatør kan have udfyldt kun det ene felt.
/// </summary>
/// <param name="Category">Den parsed kategori (fx <c>Fault</c>) eller <c>null</c>.</param>
/// <param name="Reason">Operatørens fri-tekst-årsag (Trendlog-feltet <c>comment</c>) eller <c>null</c>.</param>
/// <param name="ParseSucceeded">
/// <c>true</c> hvis mindst ét af felterne <c>category</c> eller <c>comment</c>
/// blev udtrukket. <c>false</c> ved tom string, malformeret input eller
/// rå-string der ikke ligner en Python-dict.
/// </param>
public readonly record struct PythonCommentResult(string? Category, string? Reason, bool ParseSucceeded)
{
    public static PythonCommentResult Empty { get; } = new(null, null, false);
}

/// <summary>
/// Parser for Trendlog's <c>comment</c>-felt, der leveres som en
/// stringificering af et Python-dict — <em>ikke</em> som gyldig JSON.
/// Eksempel på input:
/// <code>{'category': 'Fault', 'comment': 'Product jam'}</code>
/// <para>
/// Parseren er bevidst defensiv. Den må ikke kaste exceptions for malformeret
/// input, da Trendlog ikke garanterer en konsistent skemafølgning på fri-tekst-
/// kommentarer. Strategien er regex-baseret matchning af nøgle-/værdipar:
/// </para>
/// <list type="bullet">
/// <item><description>Begge værdi-citationsmønstre understøttes (single og double),
/// hvilket dækker både den canoniske Python <c>repr</c> og det tilfælde hvor
/// en apostrof i værdien får Python til at switche til double-quotes.</description></item>
/// <item><description>Escapede citationstegn (<c>\'</c>, <c>\"</c>) un-escapes,
/// således at en operatørs <c>don't</c> bevares som <c>don't</c>.</description></item>
/// <item><description>Ukendte nøgler ignoreres, så fremtidige Trendlog-felter
/// (fx <c>severity</c>) ikke bryder pipelinen.</description></item>
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
