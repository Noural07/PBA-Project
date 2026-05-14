using FluentAssertions;
using IngestionService.Trendlog;
using Xunit;

namespace IngestionService.Tests;

/// <summary>
/// Enhedstest for <see cref="PythonCommentParser"/>. Parseren udsættes for
/// fri-tekst-kommentarer leveret af Trendlog som stringificerede Python-dicts;
/// testene dækker både den canoniske form og typiske afvigelser observeret
/// i live-data, jf. fase B-rapportens afsnit B.3.
/// </summary>
public sealed class PythonCommentParserTests
{
    [Fact(DisplayName = "Canonisk Python-dict parses til både category og reason")]
    public void Parse_CanonicalDict_ExtractsCategoryAndReason()
    {
        const string raw = "{'category': 'Fault', 'comment': 'Product jam'}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Category.Should().Be("Fault");
        result.Reason.Should().Be("Product jam");
    }

    [Fact(DisplayName = "Apostrof i værdi kvoteret med double-quotes håndteres korrekt")]
    public void Parse_ApostropheInDoubleQuotedValue_PreservesApostrophe()
    {
        // Python's repr skifter til double-quotes når strengen indeholder apostrof.
        const string raw = "{'category': 'Fault', 'comment': \"Operator said don't\"}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Category.Should().Be("Fault");
        result.Reason.Should().Be("Operator said don't");
    }

    [Fact(DisplayName = "Escaped apostrof i single-quoted værdi un-escapes")]
    public void Parse_EscapedApostropheInSingleQuotedValue_Unescapes()
    {
        const string raw = @"{'category': 'Reload', 'comment': 'Don\'t restart'}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Reason.Should().Be("Don't restart");
    }

    [Theory(DisplayName = "Tom eller whitespace-input giver Empty-resultat")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsEmpty(string? raw)
    {
        var result = PythonCommentParser.Parse(raw);

        result.Should().Be(PythonCommentResult.Empty);
        result.ParseSucceeded.Should().BeFalse();
    }

    [Theory(DisplayName = "Malformeret input afvises uden exception")]
    [InlineData("category: Fault, comment: jam")]
    [InlineData("not even close to a dict")]
    [InlineData("{ broken without quotes }")]
    [InlineData("{'unterminated': 'value")]
    public void Parse_Malformed_ReturnsEmpty(string raw)
    {
        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeFalse();
    }

    [Fact(DisplayName = "Ukendte felter ignoreres")]
    public void Parse_UnknownFields_AreIgnored()
    {
        const string raw =
            "{'category': 'Fault', 'comment': 'Belt slip', 'severity': 'high', 'crew': 'A'}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Category.Should().Be("Fault");
        result.Reason.Should().Be("Belt slip");
    }

    [Fact(DisplayName = "Kun comment uden category udfylder kun Reason")]
    public void Parse_OnlyComment_PopulatesOnlyReason()
    {
        const string raw = "{'comment': 'Routine maintenance'}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Category.Should().BeNull();
        result.Reason.Should().Be("Routine maintenance");
    }

    [Fact(DisplayName = "Kun category uden comment udfylder kun Category")]
    public void Parse_OnlyCategory_PopulatesOnlyCategory()
    {
        const string raw = "{'category': 'Reload'}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeTrue();
        result.Category.Should().Be("Reload");
        result.Reason.Should().BeNull();
    }

    [Fact(DisplayName = "Tomme værdier behandles som ikke-tilstede")]
    public void Parse_EmptyValues_TreatedAsAbsent()
    {
        const string raw = "{'category': '', 'comment': ''}";

        var result = PythonCommentParser.Parse(raw);

        result.ParseSucceeded.Should().BeFalse();
        result.Category.Should().BeNull();
        result.Reason.Should().BeNull();
    }

    [Fact(DisplayName = "ComposeReason kombinerer category og comment")]
    public void ComposeReason_BothFields_ConcatenatesWithColon()
    {
        var parsed = new PythonCommentResult("Fault", "Product jam", true);

        var composed = PythonCommentParser.ComposeReason(parsed);

        composed.Should().Be("Fault: Product jam");
    }

    [Fact(DisplayName = "ComposeReason falder tilbage til kun comment hvis category mangler")]
    public void ComposeReason_OnlyComment_ReturnsComment()
    {
        var parsed = new PythonCommentResult(null, "Routine maintenance", true);

        PythonCommentParser.ComposeReason(parsed).Should().Be("Routine maintenance");
    }

    [Fact(DisplayName = "ComposeReason returnerer empty for Empty-resultat")]
    public void ComposeReason_Empty_ReturnsEmptyString()
    {
        PythonCommentParser.ComposeReason(PythonCommentResult.Empty).Should().BeEmpty();
    }
}
