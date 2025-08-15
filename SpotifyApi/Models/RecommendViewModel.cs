using SpotifyApi.Services;
using System.Text.Json;

namespace SpotifyApi.Models;

public sealed class RecommendViewModel
{
    public JsonElement? Profile { get; init; }
    public IReadOnlyList<TopTrack> Top { get; init; } = Array.Empty<TopTrack>();
    public IReadOnlyList<RecoTrack> Recs { get; init; } = Array.Empty<RecoTrack>();
    public string DebugInfo { get; init; } = string.Empty;
}

public sealed class DiscoveryViewModel
{
    public JsonElement? Profile { get; init; }
    public IReadOnlyList<TopTrack> Top { get; init; } = Array.Empty<TopTrack>();
    public IReadOnlyList<DiscoveryResult> Items { get; init; } = Array.Empty<DiscoveryResult>();
    public string Debug { get; init; } = string.Empty;
}
