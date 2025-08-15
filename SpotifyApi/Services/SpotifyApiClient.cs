using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace SpotifyApi.Services;

public interface ISpotifyApiClient
{
    Task<JsonElement> GetMeAsync(string accessToken);
    Task<IReadOnlyList<TopTrack>> GetTopTracksAsync(string accessToken, string range = "medium_term", int limit = 10);
    Task<IReadOnlyList<TopArtist>> GetTopArtistsAsync(string accessToken, string range = "medium_term", int limit = 10);
    Task<IReadOnlyList<TopTrack>> GetArtistTopTracksAsync(string accessToken, string artistId, string market = "US", int take = 10);
    Task<IReadOnlyList<TopArtist>> GetRelatedArtistsAsync(string accessToken, string artistId, int take = 10);
    Task<IReadOnlyCollection<string>> GetRecentlyPlayedTrackIdsAsync(string accessToken, int limit = 50);
    Task<IReadOnlyCollection<string>> GetSavedTrackIdsAsync(string accessToken, int max = 300);
}

public sealed class SpotifyApiClient : ISpotifyApiClient
{
    private readonly HttpClient http;
    private readonly ILogger<SpotifyApiClient> logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> retryPolicy;

    public SpotifyApiClient(HttpClient http, ILogger<SpotifyApiClient> logger)
    {
        this.http = http;
        this.logger = logger;
        retryPolicy = Policy<HttpResponseMessage>
            .HandleResult(r => (int)r.StatusCode >= 500 || r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * attempt),
                onRetry: (result, ts, attempt) =>
                {
                    // Honor Retry-After if provided for 429 on next attempt (simple backoff already exponential-ish)
                    logger.LogWarning("Retrying Spotify call attempt {Attempt} after {Delay} due to {Status}", attempt, ts, result.Result?.StatusCode);
                });
    }

    public async Task<JsonElement> GetMeAsync(string accessToken)
    {
        var json = await SendJson(accessToken, "https://api.spotify.com/v1/me");
        return JsonDocument.Parse(json).RootElement;
    }

    public async Task<IReadOnlyList<TopTrack>> GetTopTracksAsync(string accessToken, string range = "medium_term", int limit = 10)
    {
        var json = await SendJson(accessToken, $"https://api.spotify.com/v1/me/top/tracks?time_range={range}&limit={Math.Clamp(limit, 1, 50)}", swallowNotFound: true);
        return ParseTopTracks(json);
    }

    public async Task<IReadOnlyList<TopArtist>> GetTopArtistsAsync(string accessToken, string range = "medium_term", int limit = 10)
    {
        var json = await SendJson(accessToken, $"https://api.spotify.com/v1/me/top/artists?time_range={range}&limit={Math.Clamp(limit,1,50)}", swallowNotFound: true);
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<TopArtist>();
        var list = new List<TopArtist>(items.GetArrayLength());
        foreach (var a in items.EnumerateArray())
        {
            try
            {
                list.Add(new TopArtist(
                    Id: a.GetProperty("id").GetString()!,
                    Name: a.GetProperty("name").GetString()!,
                    Genres: a.TryGetProperty("genres", out var g) && g.ValueKind==JsonValueKind.Array ? g.EnumerateArray().Select(e=>e.GetString()!).Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>(),
                    ImageUrl: a.TryGetProperty("images", out var imgs) && imgs.ValueKind==JsonValueKind.Array ? imgs.EnumerateArray().FirstOrDefault().GetProperty("url").GetString() ?? string.Empty : string.Empty
                ));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse top artist JSON fragment.");
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<TopTrack>> GetArtistTopTracksAsync(string accessToken, string artistId, string market = "US", int take = 10)
    {
        var json = await SendJson(accessToken, $"https://api.spotify.com/v1/artists/{artistId}/top-tracks?market={market}", swallowNotFound: true);
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            return Array.Empty<TopTrack>();
        return tracks.EnumerateArray().Take(take).Select(t => SafeParseTrack(t)).Where(t => t is not null).Cast<TopTrack>().ToList();
    }

    public async Task<IReadOnlyList<TopArtist>> GetRelatedArtistsAsync(string accessToken, string artistId, int take = 10)
    {
        var json = await SendJson(accessToken, $"https://api.spotify.com/v1/artists/{artistId}/related-artists", swallowNotFound: true);
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var arts) || arts.ValueKind != JsonValueKind.Array)
            return Array.Empty<TopArtist>();
        var list = new List<TopArtist>();
        foreach (var a in arts.EnumerateArray().Take(take))
        {
            try
            {
                list.Add(new TopArtist(
                    Id: a.GetProperty("id").GetString()!,
                    Name: a.GetProperty("name").GetString()!,
                    Genres: a.TryGetProperty("genres", out var g) && g.ValueKind==JsonValueKind.Array ? g.EnumerateArray().Select(e=>e.GetString()!).Where(s=>!string.IsNullOrWhiteSpace(s)).ToArray() : Array.Empty<string>(),
                    ImageUrl: a.TryGetProperty("images", out var imgs) && imgs.ValueKind==JsonValueKind.Array ? imgs.EnumerateArray().FirstOrDefault().GetProperty("url").GetString() ?? string.Empty : string.Empty
                ));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to parse related artist");
            }
        }
        return list;
    }

    public async Task<IReadOnlyCollection<string>> GetRecentlyPlayedTrackIdsAsync(string accessToken, int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 50);
        var json = await SendJson(accessToken, $"https://api.spotify.com/v1/me/player/recently-played?limit={limit}", swallowNotFound: true, swallowForbidden: true);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in items.EnumerateArray())
                {
                    var t = it.TryGetProperty("track", out var tr) ? tr : default;
                    if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) set.Add(id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Parse recently played failed");
        }
        return set;
    }

    public async Task<IReadOnlyCollection<string>> GetSavedTrackIdsAsync(string accessToken, int max = 300)
    {
        max = Math.Clamp(max, 50, 1000);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int limit = 50; int offset = 0;
        while (set.Count < max)
        {
            var json = await SendJson(accessToken, $"https://api.spotify.com/v1/me/tracks?limit={limit}&offset={offset}", swallowNotFound: true, swallowForbidden: true);
            try
            {
                var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength()==0)
                    break;
                int addedThisPage = 0;
                foreach (var it in items.EnumerateArray())
                {
                    var t = it.TryGetProperty("track", out var tr) ? tr : default;
                    if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrWhiteSpace(id) && set.Add(id)) addedThisPage++;
                    }
                }
                if (addedThisPage == 0) break; // no more new
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Parse saved tracks page failed");
                break;
            }
            offset += limit;
        }
        return set;
    }

    private IReadOnlyList<TopTrack> ParseTopTracks(string json)
    {
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return Array.Empty<TopTrack>();
        var list = new List<TopTrack>(items.GetArrayLength());
        foreach (var t in items.EnumerateArray())
        {
            var parsed = SafeParseTrack(t);
            if (parsed != null) list.Add(parsed);
        }
        return list;
    }

    private TopTrack? SafeParseTrack(JsonElement t)
    {
        try { return ParseTrack(t); } catch { return null; }
    }

    private TopTrack ParseTrack(JsonElement t) => new(
        Id: t.GetProperty("id").GetString()!,
        Name: t.GetProperty("name").GetString()!,
        Artists: t.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray(),
        AlbumArtUrl: t.GetProperty("album").GetProperty("images").EnumerateArray().FirstOrDefault().GetProperty("url").GetString()!,
        Uri: t.GetProperty("uri").GetString()!,
        PreviewUrl: t.TryGetProperty("preview_url", out var p) ? p.GetString() : null,
        Popularity: t.TryGetProperty("popularity", out var pop) ? pop.GetInt32() : 0
    );

    private HttpRequestMessage Build(string token, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        return req;
    }

    private async Task<string> SendJson(string accessToken, string url, bool swallowNotFound = false, bool swallowForbidden = false)
    {
        using var req = Build(accessToken, url);
        var resp = await retryPolicy.ExecuteAsync(() => http.SendAsync(req));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            if (swallowNotFound && resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogInformation("Spotify 404 on {Url} (treating as empty).", url);
                return "{}";
            }
            if (swallowForbidden && resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogWarning("Spotify 403 (insufficient scope) on optional endpoint {Url}; proceeding without data.", url);
                return "{}";
            }
            logger.LogError("Spotify API error {Status} for {Url}: {Body}", resp.StatusCode, url, text);
            throw new Exception($"Spotify {resp.StatusCode}: {text}");
        }
        return text;
    }
}

public sealed record TopTrack(string Id, string Name, string[] Artists, string AlbumArtUrl, string Uri, string? PreviewUrl, int Popularity);
public sealed record TopArtist(string Id, string Name, string[] Genres, string ImageUrl);
public sealed record RecoTrack(string Id, string Name, string[] Artists, string AlbumArtUrl, string Uri, string? PreviewUrl);
