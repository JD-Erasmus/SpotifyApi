using System.Net.Http.Headers;
using System.Text.Json;

namespace SpotifyApi.Services;

public interface ISpotifyApiClient
{
    Task<JsonElement> GetMeAsync(string accessToken);
    Task<IReadOnlyList<TopTrack>> GetTopTracksAsync(string accessToken, string range = "medium_term", int limit = 10);
    Task<IReadOnlyList<RecoTrack>> GetRecommendationsAsync(
        string accessToken,
        string[]? seedArtists = null, string[]? seedTracks = null, string[]? seedGenres = null,
        double? targetEnergy = null, double? targetDanceability = null, double? targetValence = null,
        int? minTempo = null, int? maxTempo = null, int limit = 20);
    Task<string[]> GetAvailableGenreSeedsAsync(string accessToken);
}

public sealed class SpotifyApiClient(HttpClient http) : ISpotifyApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<JsonElement> GetMeAsync(string accessToken)
    {
        using var req = Build(accessToken, "https://api.spotify.com/v1/me");
        var json = await Send(req);
        return JsonDocument.Parse(json).RootElement;
    }

    public async Task<IReadOnlyList<TopTrack>> GetTopTracksAsync(string accessToken, string range = "medium_term", int limit = 10)
    {
        using var req = Build(accessToken, $"https://api.spotify.com/v1/me/top/tracks?time_range={range}&limit={Math.Clamp(limit, 1, 50)}");
        var json = await Send(req);
        var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");
        var list = new List<TopTrack>(items.GetArrayLength());
        foreach (var t in items.EnumerateArray())
        {
            list.Add(new TopTrack(
                Id: t.GetProperty("id").GetString()!,
                Name: t.GetProperty("name").GetString()!,
                Artists: t.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray(),
                AlbumArtUrl: t.GetProperty("album").GetProperty("images").EnumerateArray().FirstOrDefault().GetProperty("url").GetString()!,
                Uri: t.GetProperty("uri").GetString()!,
                PreviewUrl: t.TryGetProperty("preview_url", out var p) ? p.GetString() : null
            ));
        }
        return list;
    }

    public async Task<IReadOnlyList<RecoTrack>> GetRecommendationsAsync(
        string accessToken,
        string[]? seedArtists = null, string[]? seedTracks = null, string[]? seedGenres = null,
        double? targetEnergy = null, double? targetDanceability = null, double? targetValence = null,
        int? minTempo = null, int? maxTempo = null, int limit = 20)
    {
        var q = new List<string> { $"limit={Math.Clamp(limit, 1, 100)}" };
        if (seedArtists is { Length: > 0 }) q.Add($"seed_artists={string.Join(",", seedArtists)}");
        if (seedTracks is { Length: > 0 }) q.Add($"seed_tracks={string.Join(",", seedTracks)}");
        if (seedGenres is { Length: > 0 }) q.Add($"seed_genres={string.Join(",", seedGenres)}");
        void AddNum(string k, double? v) { if (v is not null) q.Add($"{k}={v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"); }
        void AddInt(string k, int? v) { if (v is not null) q.Add($"{k}={v}"); }
        AddNum("target_energy", targetEnergy);
        AddNum("target_danceability", targetDanceability);
        AddNum("target_valence", targetValence);
        AddInt("min_tempo", minTempo);
        AddInt("max_tempo", maxTempo);

        using var req = Build(accessToken, $"https://api.spotify.com/v1/recommendations?{string.Join("&", q)}");
        var json = await Send(req);
        var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("tracks");
        var list = new List<RecoTrack>(items.GetArrayLength());
        foreach (var t in items.EnumerateArray())
        {
            list.Add(new RecoTrack(
                Id: t.GetProperty("id").GetString()!,
                Name: t.GetProperty("name").GetString()!,
                Artists: t.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray(),
                AlbumArtUrl: t.GetProperty("album").GetProperty("images").EnumerateArray().FirstOrDefault().GetProperty("url").GetString()!,
                Uri: t.GetProperty("uri").GetString()!,
                PreviewUrl: t.TryGetProperty("preview_url", out var p) ? p.GetString() : null
            ));
        }
        return list;
    }

    public async Task<string[]> GetAvailableGenreSeedsAsync(string accessToken)
    {
        using var req = Build(accessToken, "https://api.spotify.com/v1/recommendations/available-genre-seeds");
        var json = await Send(req);
        var doc = JsonDocument.Parse(json);
        var arr = doc.RootElement.GetProperty("genres").EnumerateArray().Select(e => e.GetString()!).ToArray();
        return arr;
    }

    private static HttpRequestMessage Build(string token, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private async Task<string> Send(HttpRequestMessage req)
    {
        using var resp = await http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            // Log the error and return an empty result instead of throwing
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Optionally log or handle 404 specifically
                return "{\"items\":[],\"tracks\":[]}"; // Return empty structure for top/reco
            }
            // For other errors, you may want to log and return empty or partial data
            return "{\"items\":[],\"tracks\":[]}";
        }
        return text;
    }
}

public sealed record TopTrack(string Id, string Name, string[] Artists, string AlbumArtUrl, string Uri, string? PreviewUrl);
public sealed record RecoTrack(string Id, string Name, string[] Artists, string AlbumArtUrl, string Uri, string? PreviewUrl);
