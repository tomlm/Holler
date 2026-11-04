using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// Holler over SignalR to the server's hub (the auth cookie is sent automatically).
builder.Services.AddHollerClient();

await builder.Build().RunAsync();
