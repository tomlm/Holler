using Holler.Sample.Components;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

// Demo-only sign-in: you pick a name, it becomes your Holler user id.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => o.LoginPath = "/");
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Holler: signaling hub + server-side HollerClient.
builder.Services.AddHoller(o =>
{
    o.IceServers = [new(["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"])];
    o.MaxRoomSize = 6;
});
// Anything can also come from configuration, e.g. a TURN server for users behind strict NATs:
//   "Holler": { "RingTimeout": "00:00:30", "Turn": { "Urls": [ "turn:turn.example.com:3478" ], "SharedSecret": "..." } }
builder.Services.Configure<Holler.Server.HollerOptions>(builder.Configuration.GetSection("Holler"));

// TURN for users behind strict NATs and firewalls: Cloudflare Realtime TURN, when a key is configured
// (Holler:CloudflareTurn:KeyId and ApiToken, e.g. in user secrets).
if (!string.IsNullOrWhiteSpace(builder.Configuration["Holler:CloudflareTurn:KeyId"]))
    builder.Services.AddHollerCloudflareTurn();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapHollerHub();
app.MapPost("/logout", async (HttpContext http) =>
{
    await http.SignOutAsync();
    return Results.LocalRedirect("~/");
});
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Holler.Sample.Client._Imports).Assembly);

app.Run();
