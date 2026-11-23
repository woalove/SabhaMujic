using System.Text.Json;
using SabhaMujic.Contracts;

namespace SabhaMujic.Core.Tests;

public class ContractSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Playback_state_round_trips_with_web_defaults()
    {
        var state = new PlaybackStateDto(PlaybackStatus.Crossfading, Guid.NewGuid(), Guid.NewGuid(), null,
            12.5, 300, 0.8f, false, LoopMode.SmartLoop, 6, null, 42, DateTimeOffset.UnixEpoch);

        var json = JsonSerializer.Serialize(state, Web);
        Assert.Contains("\"positionSeconds\":12.5", json);
        Assert.Equal(state, JsonSerializer.Deserialize<PlaybackStateDto>(json, Web));
    }

    [Fact]
    public void Queue_and_track_round_trip()
    {
        var queue = new QueueDto([new QueueItemDto(Guid.NewGuid(), Guid.NewGuid())], 0, 3);
        var back = JsonSerializer.Deserialize<QueueDto>(JsonSerializer.Serialize(queue, Web), Web)!;
        Assert.Equal(queue.Items, back.Items);

        var track = new TrackDto(Guid.NewGuid(), "Dhun", null, [Guid.NewGuid(), Guid.NewGuid()], 245.2, AnalysisStatus.Ready,
            new LoopRegionDto(10, 70, 0.2, 0.9, false), DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(track, Web);
        Assert.Contains("\"categoryIds\":[", json);
        var trackBack = JsonSerializer.Deserialize<TrackDto>(json, Web)!;
        Assert.Equal(track.CategoryIds, trackBack.CategoryIds); // lists compare by reference in record equality
        Assert.Equal(track with { CategoryIds = [] }, trackBack with { CategoryIds = [] });
    }

    [Fact]
    public void Allowed_extensions_are_case_insensitive()
    {
        Assert.Contains(".MP3", AdminLimits.AllowedExtensions);
        Assert.DoesNotContain(".exe", AdminLimits.AllowedExtensions);
    }
}
