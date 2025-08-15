// ADD:
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpotifyApi.Data;
using SpotifyApi.Services;
using System.Security.Claims;
using System.Text.Json;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHttpClient<ISpotifyApiClient, SpotifyApiClient>();
builder.Services.AddScoped<IDiscoveryRecommender, DiscoveryRecommender>();


builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>();

var spotifyCfg = builder.Configuration.GetSection("Authentication:Spotify");

// Use Identity's defaults, only set the challenge scheme for Spotify
builder.Services
    .AddAuthentication(options =>
    {
        // keep Identity’s cookie scheme as default (Identity sets this already)
        // just tell the pipeline that challenges should go to Spotify
        options.DefaultChallengeScheme = "Spotify";
    })
    .AddOAuth("Spotify", options =>
    {
        // ? Save tokens into the main Identity cookie
        options.SignInScheme = IdentityConstants.ApplicationScheme;

        options.ClientId = spotifyCfg["ClientId"]!;
        options.ClientSecret = spotifyCfg["ClientSecret"]!;
        options.CallbackPath = spotifyCfg["CallbackPath"] ?? "/signin-spotify";
        options.AuthorizationEndpoint = "https://accounts.spotify.com/authorize";
        options.TokenEndpoint = "https://accounts.spotify.com/api/token";
        options.UserInformationEndpoint = "https://api.spotify.com/v1/me";

        // Scopes from config
        foreach (var s in (spotifyCfg["Scopes"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
            options.Scope.Add(s);

        options.SaveTokens = true; // keep access/refresh tokens in auth properties

        // Map a few useful claims
        options.ClaimActions.MapJsonKey(ClaimTypes.NameIdentifier, "id");
        options.ClaimActions.MapJsonKey(ClaimTypes.Name, "display_name");
        options.ClaimActions.MapJsonKey(ClaimTypes.Email, "email");

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async ctx =>
            {
                var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("SpotifyOAuth");

                // Retry fetching the user profile a few times in case Spotify transiently returns 5xx (e.g. 502 Bad Gateway)
                const int maxAttempts = 3;
                JsonDocument? doc = null;
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                        using var resp = await ctx.Backchannel.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ctx.HttpContext.RequestAborted);

                        if (!resp.IsSuccessStatusCode)
                        {
                            // Log and retry on 5xx, fail immediately on 4xx
                            if ((int)resp.StatusCode >= 500 && attempt < maxAttempts)
                            {
                                logger.LogWarning("Spotify user info attempt {Attempt} failed with {Status}. Retrying...", attempt, resp.StatusCode);
                                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ctx.HttpContext.RequestAborted);
                                continue;
                            }
                            resp.EnsureSuccessStatusCode(); // will throw for non-success (4xx or last 5xx)
                        }

                        var payload = await resp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted);
                        doc = JsonDocument.Parse(payload);
                        break; // success
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // Propagate cancellations
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= maxAttempts)
                        {
                            logger.LogError(ex, "Failed to retrieve Spotify user profile after {Attempts} attempts.", maxAttempts);
                        }
                        else
                        {
                            logger.LogWarning(ex, "Error retrieving Spotify profile on attempt {Attempt}/{Max}; retrying...", attempt, maxAttempts);
                            await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ctx.HttpContext.RequestAborted);
                        }
                    }
                }

                if (doc is null)
                {
                    // Do not fail the whole auth if profile fetch failed; proceed without mapped claims.
                    logger.LogWarning("Proceeding without Spotify user profile claims (profile fetch failed).");
                    return; // no claims mapped
                }

                try
                {
                    ctx.RunClaimActions(doc.RootElement);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error mapping Spotify claims.");
                }
            }
        };
    });

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
    // Do NOT use HTTPS redirection in dev
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseHttpsRedirection(); // Only in prod
}
app.UseStaticFiles();

app.UseRouting();

// ADD: Authentication must come before Authorization
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

// ADD: Minimal endpoints to test OAuth + a real Spotify API call
app.MapGet("/login", () =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        authenticationSchemes: new[] { "Spotify" }
    )
);

app.MapGet("/me", async (HttpContext ctx) =>
{
    // FIX: Use GetTokenAsync from Microsoft.AspNetCore.Authentication
    var token = await ctx.GetTokenAsync("access_token");
    if (string.IsNullOrEmpty(token))
        return Results.Unauthorized();

    using var http = new HttpClient();
    http.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var json = await http.GetStringAsync("https://api.spotify.com/v1/me");
    return Results.Content(json, "application/json");
});

app.Run();
