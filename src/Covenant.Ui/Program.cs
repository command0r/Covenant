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
// One admin HttpClient. Console inference (POST /admin/playground/requests) runs on it too — with the admin token,
// never an application key (ADR-0012) — so its timeout covers a long buffered local generation.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = origin, Timeout = TimeSpan.FromMinutes(5) });

// In Blazor WebAssembly a scoped service lives for the page: these are the console's singletons.
builder.Services.AddScoped<Interop>();
builder.Services.AddScoped<AppState>();
builder.Services.AddScoped<AdminApi>();
builder.Services.AddScoped<StatusFeed>();
builder.Services.AddScoped<ConsoleSession>();
builder.Services.AddScoped<CatalogState>();
builder.Services.AddScoped<EvidenceTracker>();
builder.Services.AddScoped<ConsoleInferenceApi>();
builder.Services.AddScoped<TraceClient>();
builder.Services.AddScoped<WorkspaceConfig>();
builder.Services.AddScoped<PlaygroundState>();
builder.Services.AddScoped<RequestsViewState>();

await builder.Build().RunAsync();
