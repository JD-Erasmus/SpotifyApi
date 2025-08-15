using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using SpotifyApi.Models;
using SpotifyApi.Services;

namespace SpotifyApi.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ISpotifyApiClient _spotify;
        private readonly IDiscoveryRecommender _recommender;

        public HomeController(ILogger<HomeController> logger, ISpotifyApiClient spotify, IDiscoveryRecommender recommender)
        {
            _logger = logger;
            _spotify = spotify;
            _recommender = recommender;
        }

        public async Task<IActionResult> Index()
        {
            var token = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(token))
                return View(new RecommendViewModel());

            var profile = await _spotify.GetMeAsync(token);

            var top = await _spotify.GetTopTracksAsync(token, range: "short_term", limit: 10);
            if (top.Count == 0)
                top = await _spotify.GetTopTracksAsync(token, range: "medium_term", limit: 10);
            if (top.Count == 0)
                top = await _spotify.GetTopTracksAsync(token, range: "long_term", limit: 10);

            var (recs, debug) = await _recommender.GetDiscoveryAsync(token, top);

            var vm = new RecommendViewModel
            {
                Profile = profile,
                Top = top,
                Recs = recs,
                DebugInfo = debug
            };
            return View(vm);
        }

        public async Task<IActionResult> Discovery()
        {
            var token = await HttpContext.GetTokenAsync("access_token");
            if (string.IsNullOrEmpty(token)) return RedirectToAction(nameof(Index));
            var profile = await _spotify.GetMeAsync(token);
            var top = await _spotify.GetTopTracksAsync(token, range: "short_term", limit: 10);
            if (top.Count == 0) top = await _spotify.GetTopTracksAsync(token, range: "medium_term", limit: 10);
            if (top.Count == 0) top = await _spotify.GetTopTracksAsync(token, range: "long_term", limit: 10);
            var (items, debug) = await _recommender.GetDiscoveryVerboseAsync(token, top);
            var model = new DiscoveryViewModel
            {
                Profile = profile,
                Top = top,
                Items = items,
                Debug = debug
            };
            return View(model);
        }
    }
}
