using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SpotifyApi.Services;

public interface IDiscoveryRecommender
{
    Task<(IReadOnlyList<RecoTrack> Recs, string Debug)> GetDiscoveryAsync(string accessToken, IReadOnlyList<TopTrack> topTracks, int desired = 40);
    Task<(IReadOnlyList<DiscoveryResult> Items, string Debug)> GetDiscoveryVerboseAsync(string accessToken, IReadOnlyList<TopTrack> topTracks, int desired = 40);
}

public sealed record DiscoveryResult(RecoTrack Track, double Score, string[] Reasons);

public sealed class DiscoveryRecommender : IDiscoveryRecommender
{
    private readonly ISpotifyApiClient api;
    private readonly ILogger<DiscoveryRecommender> logger;

    public DiscoveryRecommender(ISpotifyApiClient api, ILogger<DiscoveryRecommender> logger)
    {
        this.api = api;
        this.logger = logger;
    }

    public async Task<(IReadOnlyList<RecoTrack> Recs, string Debug)> GetDiscoveryAsync(string accessToken, IReadOnlyList<TopTrack> topTracks, int desired = 40)
    {
        var (items, debug) = await GetDiscoveryVerboseAsync(accessToken, topTracks, desired);
        return (items.Select(i => i.Track).ToList(), debug);
    }

    public async Task<(IReadOnlyList<DiscoveryResult> Items, string Debug)> GetDiscoveryVerboseAsync(string accessToken, IReadOnlyList<TopTrack> topTracks, int desired = 40)
    {
        var debug = new List<string>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pool = new ConcurrentDictionary<string, PoolTrack>();

        // Build known (listened) set
        var known = new HashSet<string>(topTracks.Select(t=>t.Id), StringComparer.OrdinalIgnoreCase);
        var recently = await api.GetRecentlyPlayedTrackIdsAsync(accessToken, 50);
        var saved = await api.GetSavedTrackIdsAsync(accessToken, 300);
        known.UnionWith(recently);
        known.UnionWith(saved);
        debug.Add($"KnownSeed={known.Count} (Top={topTracks.Count} Recent={recently.Count} Saved={saved.Count})");

        // Top artists
        var topArtists = await api.GetTopArtistsAsync(accessToken, range: "medium_term", limit: 15);
        debug.Add($"TopArtists={topArtists.Count}");

        foreach (var t in topTracks)
            pool.TryAdd(t.Id, new PoolTrack(t, new HashSet<string>{"seed-track", known.Contains(t.Id)?"already-played":"new-to-you"}));

        // Expand artists in parallel with throttling
        var artistIds = topArtists.Select(a => a.Id).Distinct().Take(15).ToList();
        var sem = new SemaphoreSlim(4);
        int skippedKnown = 0;
        var artistTasks = artistIds.Select(async id =>
        {
            await sem.WaitAsync();
            try
            {
                var top = await api.GetArtistTopTracksAsync(accessToken, id, take: 5);
                foreach (var tr in top)
                {
                    if (known.Contains(tr.Id)) { Interlocked.Increment(ref skippedKnown); continue; }
                    AddToPool(pool, tr, "artist-top", known);
                }
                var related = await api.GetRelatedArtistsAsync(accessToken, id, take: 5);
                foreach (var ra in related)
                {
                    var raTop = await api.GetArtistTopTracksAsync(accessToken, ra.Id, take: 3);
                    foreach (var tr in raTop)
                    {
                        if (known.Contains(tr.Id)) { Interlocked.Increment(ref skippedKnown); continue; }
                        AddToPool(pool, tr, "related-artist", known);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Artist expansion failed for {Artist}", id);
            }
            finally { sem.Release(); }
        }).ToList();
        await Task.WhenAll(artistTasks);

        // If pool too small, allow known tracks (fallback) by re-adding some penalized
        if (pool.Count < desired / 2)
        {
            foreach (var id in known.Take(100))
            {
                if (!pool.ContainsKey(id))
                {
                    // we don't have full metadata; skip unless original topTracks has it
                    var seed = topTracks.FirstOrDefault(t=>t.Id==id);
                    if (seed!=null)
                        pool.TryAdd(id, new PoolTrack(seed, new HashSet<string>{"fallback-known"}));
                }
            }
        }

        // Genre summary (informational)
        var genrePool = topArtists.SelectMany(a => a.Genres).Where(g => !string.IsNullOrWhiteSpace(g))
            .GroupBy(g => g.ToLowerInvariant()).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key).ToList();
        debug.Add("Genres=" + string.Join(',', genrePool));

        int perArtistCap = 2;
        var rnd = new Random();
        var diversified = pool.Values
            .GroupBy(v => v.Track.Artists.FirstOrDefault() ?? "unknown")
            .SelectMany(g => g.OrderByDescending(x => x.Track.Popularity).ThenBy(x => x.Track.Name).Take(perArtistCap))
            .ToList();

        var ranked = diversified
            .GroupBy(t => t.Track.Id).Select(g => g.First())
            .Select(t => new DiscoveryResult(
                new RecoTrack(t.Track.Id, t.Track.Name, t.Track.Artists, t.Track.AlbumArtUrl, t.Track.Uri, t.Track.PreviewUrl),
                Score: (t.Track.Popularity + (t.Reasons.Contains("new-to-you")?5:0) + rnd.NextDouble()*2) / 100.0,
                Reasons: t.Reasons.ToArray()))
            .OrderByDescending(x => x.Score)
            .Take(Math.Clamp(desired, 1, 100))
            .ToList();

        sw.Stop();
        debug.Add($"Pool={pool.Count} Diversified={diversified.Count} Final={ranked.Count} SkippedKnown={skippedKnown} RuntimeMs={sw.ElapsedMilliseconds}");
        return (ranked, string.Join(" | ", debug));
    }

    private void AddToPool(ConcurrentDictionary<string, PoolTrack> pool, TopTrack track, string reason, HashSet<string> known)
    {
        var reasonSet = new HashSet<string>{reason};
        reasonSet.Add(known.Contains(track.Id)?"already-played":"new-to-you");
        pool.AddOrUpdate(track.Id,
            _ => new PoolTrack(track, reasonSet),
            (_, existing) => { existing.Reasons.UnionWith(reasonSet); return existing; });
    }

    private sealed class PoolTrack
    {
        public TopTrack Track { get; }
        public HashSet<string> Reasons { get; }
        public PoolTrack(TopTrack track, HashSet<string> reasons)
        { Track = track; Reasons = reasons; }
    }
}
