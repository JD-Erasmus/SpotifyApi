using SpotifyApi.Services;
using System.Text.Json;

namespace SpotifyApi.Models;

public sealed class RecommendViewModel
{
    public JsonElement? Profile { get; init; }
    public IReadOnlyList<TopTrack> Top { get; init; } = Array.Empty<TopTrack>();
    public IReadOnlyList<RecoTrack> Recs { get; init; } = Array.Empty<RecoTrack>();
}
