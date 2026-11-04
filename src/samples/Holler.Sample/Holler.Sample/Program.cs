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

// Holler: signaling hub + server-side HollerClient. Add a TURN server here for users behind strict NATs.
builder.Services.AddHoller(o =>
{
    o.IceServers = [new(["stun:stun.l.google.com:19302", "stun:stun1.l.google.com:19302"])];
    o.MaxRoomSize = 6;
});

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
