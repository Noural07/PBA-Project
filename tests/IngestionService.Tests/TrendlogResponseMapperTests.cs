using System;
using System.Text.Json;
using FluentAssertions;
using IngestionService.Trendlog;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IngestionService.Tests;

/// <summary>
/// Enhedstest for <see cref="TrendlogResponseMapper"/>. Mapperen verificeres
/// mod en realistisk fragmenteret Trendlog-respons, så det demonstreres at:
/// <list type="bullet">
///   <item><description>Strenge værdier konverteres til numeriske typer.</description></item>
///   <item><description>Naive tidsstempler tolkes som UTC.</description></item>
///   <item><description>Operatør-kommentarer på Python-dict-form parses korrekt.</description></item>
///   <item><description>Manglende eller tomme feeds giver tom payload uden exception.</description></item>
/// </list>
/// </summary>
public sealed class TrendlogResponseMapperTests
{
    private static TrendlogResponseMapper CreateSut()
        => new(NullLogger<TrendlogResponseMapper>.Instance);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact(DisplayName = "Stoptime-feed mappes med parsed comment og sekunder→minutter")]
    public void Map_StoptimeFeed_ParsesCommentAndConvertsDuration()
    {
        const string json = """
        {
          "channel": { "channel_id": 20, "name": "Plug&Log Production Unit" },
          "feeds": [
            {
              "feed_id": 30119,
              "name": "XYZ01_stoptime",
              "points": [
                {
                  "pointid": "2687278",
                  "comment": "{'category': 'Fault', 'comment': 'Product jam'}",
                  "timestamp": "2026-05-04 00:05:04.000",
                  "value": "180"
                },
                {
                  "pointid": "2687279",
                  "comment": "{'category': 'Reload', 'comment': 'New roll'}",
                  "timestamp": "2026-05-04 00:30:00.000",
                  "value": "45"
                }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(
            Parse(json),
            channelId: 20,
            windowStart: new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
            windowEnd: new DateTimeOffset(2026, 5, 4, 23, 59, 59, TimeSpan.Zero));

        payload.ChannelId.Should().Be(20);
        payload.Feeds.Stoptime.Should().HaveCount(2);

        var first = payload.Feeds.Stoptime[0];
        first.Reason.Should().Be("Fault: Product jam");
        first.Category.Should().Be("Fault");
        first.DurationMinutes.Should().Be(3); // 180 sekunder afrundes op til 3 min
        first.SourcePointId.Should().Be("2687278");
        first.Timestamp.UtcDateTime.Year.Should().Be(2026);

        var second = payload.Feeds.Stoptime[1];
        second.Reason.Should().Be("Reload: New roll");
        second.DurationMinutes.Should().Be(1); // 45s afrundes op til 1 min (Math.Ceiling)
    }

    [Fact(DisplayName = "Stoptime-points med value=0 ignoreres")]
    public void Map_StoptimePointsWithZeroValue_AreSkipped()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            {
              "name": "XYZ01_stoptime",
              "points": [
                { "pointid": "1", "comment": "", "timestamp": "2026-05-04 00:00:00.000", "value": "0" },
                { "pointid": "2", "comment": "{'category': 'Fault', 'comment': 'Real stop'}", "timestamp": "2026-05-04 00:05:00.000", "value": "120" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Stoptime.Should().HaveCount(1);
        payload.Feeds.Stoptime[0].SourcePointId.Should().Be("2");
    }

    [Fact(DisplayName = "Cnt- og runtime-feeds mappes som diff-entries")]
    public void Map_DiffFeeds_ParseStringValuesAsInts()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            {
              "name": "XYZ01_cnt",
              "points": [
                { "timestamp": "2026-05-04 06:00:00.000", "value": "120" },
                { "timestamp": "2026-05-04 07:00:00.000", "value": "98" }
              ]
            },
            {
              "name": "XYZ01_runtime",
              "points": [
                { "timestamp": "2026-05-04 06:00:00.000", "value": "1700" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Count.Should().HaveCount(2);
        payload.Feeds.Count[0].Diff.Should().Be(120);
        payload.Feeds.Count[1].Diff.Should().Be(98);

        payload.Feeds.Runtime.Should().HaveCount(1);
        payload.Feeds.Runtime[0].Diff.Should().Be(1700);
    }

    [Fact(DisplayName = "Negative diff-værdier bevares (videreføres til normaliseringen)")]
    public void Map_NegativeDiffs_ArePreservedForDownstreamAnomalyDetection()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            {
              "name": "XYZ01_cnt",
              "points": [
                { "timestamp": "2026-05-04 06:00:00.000", "value": "-260" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Count.Should().HaveCount(1);
        payload.Feeds.Count[0].Diff.Should().Be(-260);
    }

    [Fact(DisplayName = "Tomt feeds-array giver tom payload uden exception")]
    public void Map_EmptyFeedsArray_ReturnsEmptyPayload()
    {
        const string json = """
        { "channel": { "channel_id": 20 }, "feeds": [] }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.ChannelId.Should().Be(20);
        payload.Feeds.Stoptime.Should().BeEmpty();
        payload.Feeds.Count.Should().BeEmpty();
        payload.Feeds.Runtime.Should().BeEmpty();
    }

    [Fact(DisplayName = "Feed med tomt points-array springes over uden exception")]
    public void Map_FeedWithEmptyPoints_IsSkipped()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            { "name": "XYZ01_cnt", "points": [] },
            { "name": "XYZ01_stoptime", "points": [] }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Count.Should().BeEmpty();
        payload.Feeds.Stoptime.Should().BeEmpty();
    }

    [Fact(DisplayName = "Manglende feeds-property returnerer tom payload med fallback-channel")]
    public void Map_MissingFeedsProperty_ReturnsEmptyWithFallbackChannel()
    {
        const string json = """
        { "channel": { "channel_id": 20 } }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 99, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        // Channel-id fra payload tager forrang frem for fallback.
        payload.ChannelId.Should().Be(20);
        payload.Feeds.Stoptime.Should().BeEmpty();
    }

    [Fact(DisplayName = "Manglende channel-objekt anvender fallback-id")]
    public void Map_MissingChannelObject_FallsBackToProvidedId()
    {
        const string json = """
        { "feeds": [] }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 42, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.ChannelId.Should().Be(42);
    }

    [Fact(DisplayName = "Ukendt feed-navn (XYZ01_run) ignoreres uden exception")]
    public void Map_UnknownFeedName_IsIgnoredGracefully()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            {
              "name": "XYZ01_run",
              "points": [
                { "timestamp": "2026-05-04 06:00:00.000", "value": "1" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        // run-feedet eksisterer ikke som target-slot — skal ignoreres uden exception.
        payload.Feeds.Stoptime.Should().BeEmpty();
        payload.Feeds.Count.Should().BeEmpty();
        payload.Feeds.Runtime.Should().BeEmpty();
    }

    [Fact(DisplayName = "Trendlog-live-struktur (channel.feeds[]) mappes korrekt")]
    public void Map_LiveTrendlogShape_ReadsFeedsNestedInChannel()
    {
        // Faktisk observeret Trendlog-respons (jf. fase B-diagnose 2026-05-06):
        // feeds-arrayet ligger INDE i channel-objektet, ikke på top-niveau.
        const string json = """
        {
          "channel": {
            "channel_id": "20",
            "name": "Plug&Log Production Unit",
            "feeds": [
              {
                "feed_id": "30119",
                "name": "XYZ01_stoptime",
                "label": "XYZ01 stop",
                "points": [
                  {
                    "pointid": "2687278",
                    "comment": "{'category': 'Fault', 'comment': 'Product jam'}",
                    "timestamp": "2026-05-04 00:05:04.000",
                    "value": "341"
                  },
                  {
                    "pointid": "2687349",
                    "timestamp": "2026-05-04 00:52:08.000",
                    "value": "81"
                  }
                ]
              },
              {
                "feed_id": "30120",
                "name": "XYZ01_cnt",
                "points": [
                  { "timestamp": "2026-05-04 06:00:00.000", "value": "120" }
                ]
              }
            ]
          }
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow);

        payload.ChannelId.Should().Be(20);
        payload.Feeds.Stoptime.Should().HaveCount(2);
        payload.Feeds.Stoptime[0].Reason.Should().Be("Fault: Product jam");
        payload.Feeds.Stoptime[0].DurationMinutes.Should().Be(6); // 341s -> 6 min (Math.Ceiling)
        payload.Feeds.Stoptime[1].Reason.Should().BeEmpty();      // ingen kommentar på dette point
        payload.Feeds.Stoptime[1].DurationMinutes.Should().Be(2); // 81s  -> 2 min
        payload.Feeds.Count.Should().HaveCount(1);
        payload.Feeds.Count[0].Diff.Should().Be(120);
    }

    [Fact(DisplayName = "Feed med 'feedid' (Trendlog-live-form) genkendes korrekt")]
    public void Map_FeedWithFeedidProperty_IsRecognized()
    {
        // Trendlogs faktiske respons bruger 'feedid' frem for 'name' (afviger fra
        // den i Fase A dokumenterede struktur). Mapperen skal genkende begge.
        const string json = """
        {
          "channel": { "channel_id": "20" },
          "feeds": [
            {
              "feedid": "XYZ01_cnt",
              "method": "diff",
              "points": [
                { "timestamp": "2026-05-04 06:00:00.000", "value": "120" },
                { "timestamp": "2026-05-04 07:00:00.000", "value": "98" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Count.Should().HaveCount(2);
        payload.Feeds.Count[0].Diff.Should().Be(120);
        payload.Feeds.Count[1].Diff.Should().Be(98);
    }

    [Fact(DisplayName = "Stoptime-point uden tolkbar comment giver tom Reason og udelades senere")]
    public void Map_StoptimeWithUnparseableComment_StillEmitsEntryWithEmptyReason()
    {
        const string json = """
        {
          "channel": { "channel_id": 20 },
          "feeds": [
            {
              "name": "XYZ01_stoptime",
              "points": [
                { "pointid": "9", "comment": "this is not a python dict", "timestamp": "2026-05-04 00:05:04.000", "value": "120" }
              ]
            }
          ]
        }
        """;

        var sut = CreateSut();
        var payload = sut.Map(Parse(json), 20, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        payload.Feeds.Stoptime.Should().HaveCount(1);
        payload.Feeds.Stoptime[0].Reason.Should().BeEmpty();
        payload.Feeds.Stoptime[0].Category.Should().BeNull();
    }
}
