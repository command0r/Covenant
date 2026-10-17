using Covenant.Ui;
using Covenant.Ui.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The console is served from /ui/ but talks to the Host's root (/admin/*), so the HttpClient is rooted at the
// origin, never at the base href. Same origin only — CSP connect-src 'self' enforces it in the browser too.
var origin = new Uri(new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority));
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = origin });

// In Blazor WebAssembly a scoped service lives for the page: these are the console's singletons.
builder.Services.AddScoped<Interop>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<AdminApi>();
builder.Services.AddScoped<StatusFeed>();
builder.Services.AddScoped<ConsoleSession>();
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<EvidenceTracker>();
// Governed inference uses its OWN HttpClient (same origin) so the admin client's headers can never leak into
// an application call, and vice versa. Only the application key is ever attached here.
builder.Services.AddScoped(sp => new InferenceApi(new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromMinutes(5) }, sp.GetRequiredService<AppState>()));
builder.Services.AddScoped<PlaygroundState>();
builder.Services.AddScoped<RequestsViewState>();

await builder.Build().RunAsync();
