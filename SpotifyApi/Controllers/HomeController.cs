using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using SpotifyApi.Models;
using SpotifyApi.Services;
using System.Diagnostics;

namespace SpotifyApi.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ISpotifyApiClient _spotify;

        public HomeController(ILogger<HomeController> logger, ISpotifyApiClient spotify)
        {
            _logger = logger;
            _spotify = spotify;
        }

        /// <summary>
        /// Home page:
        /// - If not authenticated with Spotify: show CTA to /login.
        /// - If authenticated:
        ///    1) Fetch profile (for UX).
        ///    2) Fetch top tracks (short_term = last ~4 weeks).
        ///    3) Build recommendations seeded by up to 5 top tracks,
        ///       falling back to valid Spotify genres if user has no tops.
        /// 
        /// Tweakable recommendation options:
        /// - seed_artists / seed_tracks / seed_genres: up to 5 combined seeds
        /// - target_energy        [0..1]   (how intense)
        /// - target_danceability  [0..1]   (how danceable)
        /// - target_valence       [0..1]   (happiness/positivity)
        /// - min_tempo/max_tempo  (BPM)
        /// 
        /// We set some sane defaults you can surface as sliders later.
        /// </summary>
        public async Task<IActionResult> Index()
        {
            var token = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(token))
            {
                ViewBag.SpotifyProfile = null;
                return View(new RecommendViewModel());
            }

            // 1) Profile (optional, for greeting)
            var profile = await _spotify.GetMeAsync(token);

            // 2) User’s top tracks (short_term = most recent listening; use "medium_term" or "long_term" as needed)
            var top = await _spotify.GetTopTracksAsync(token, range: "short_term", limit: 10);

            // 3) Build seeds:
            //    - Prefer up to 5 top track IDs
            //    - If none, fall back to VALID Spotify genres (we query them, then pick a few popular ones if present)
            var seedTracks = top.Take(5).Select(t => t.Id).ToArray();
            string[] seedGenres = Array.Empty<string>();

            if (seedTracks.Length == 0)
            {
                var available = await _spotify.GetAvailableGenreSeedsAsync(token);
                // Pick some crowd-pleasers only if they exist in the official list
                var preferred = new[] { "pop", "rock", "hip-hop", "electronic", "indie", "r-n-b", "dance", "house" };
                seedGenres = preferred.Where(g => available.Contains(g, StringComparer.OrdinalIgnoreCase))
                                      .Take(5)
                                      .ToArray();
            }

            // 4) Recommendation “vibe” (defaults; later expose as sliders on the UI)
            double? targetEnergy        = 0.6; // 0..1
            double? targetDanceability  = 0.6; // 0..1
            double? targetValence       = null; // null = don’t constrain
            int?     minTempo           = null; // BPM
            int?     maxTempo           = null;

            // 5) Call recs. Spotify allows max 5 seeds combined across artists/tracks/genres.
            var recs = await _spotify.GetRecommendationsAsync(
                token,
                seedArtists: null,
                seedTracks: seedTracks.Length > 0 ? seedTracks : null,
                seedGenres: seedGenres.Length > 0 ? seedGenres : null,
                targetEnergy: targetEnergy,
                targetDanceability: targetDanceability,
                targetValence: targetValence,
                minTempo: minTempo,
                maxTempo: maxTempo,
                limit: 20
            );

            var vm = new RecommendViewModel
            {
                Profile = profile,
                Top     = top,
                Recs    = recs
            };

            return View(vm);
        }


        // GET /home/top
        public async Task<IActionResult> Top(string range = "medium_term", int limit = 10)
        {
            var token = await HttpContext.GetTokenAsync("access_token"); // Use current auth ticket
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            var tracks = await _spotify.GetTopTracksAsync(token, range, limit);
            return Json(tracks);
        }

        // GET /home/reco (quick demo: seedGenres=pop&targetEnergy=0.7&limit=10)
        public async Task<IActionResult> Reco(
            [FromQuery] string[]? seedArtists,
            [FromQuery] string[]? seedTracks,
            [FromQuery] string[]? seedGenres,
            double? targetEnergy, double? targetDanceability, double? targetValence,
            int? minTempo, int? maxTempo, int limit = 20)
        {
            var token = await HttpContext.GetTokenAsync("access_token"); // Use current auth ticket
            if (string.IsNullOrEmpty(token)) return Unauthorized();

            // Spotify allows at most 5 total seeds across all categories
            static string[]? Limit(string[]? xs) => xs is { Length: > 0 } ? xs.Take(5).ToArray() : null;
            seedArtists = Limit(seedArtists);
            seedTracks  = Limit(seedTracks);
            seedGenres  = Limit(seedGenres);

            var rec = await _spotify.GetRecommendationsAsync(
                token, seedArtists, seedTracks, seedGenres,
                targetEnergy, targetDanceability, targetValence,
                minTempo, maxTempo, Math.Clamp(limit, 1, 100));

            return Json(rec);
        }

        public IActionResult Privacy() => View();

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
            => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
