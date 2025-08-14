// ADD:
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpotifyApi.Data;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddHttpClient<SpotifyApi.Services.ISpotifyApiClient, SpotifyApi.Services.SpotifyApiClient>();


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
                using var req = new HttpRequestMessage(HttpMethod.Get, options.UserInformationEndpoint);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                using var resp = await ctx.Backchannel.SendAsync(req);
                resp.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                ctx.RunClaimActions(doc.RootElement);
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
