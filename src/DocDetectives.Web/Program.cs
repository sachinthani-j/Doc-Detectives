using DocDetectives.Infrastructure;
using DocDetectives.Web.Components;
using DocDetectives.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Same Azure clients, indexes, skills and orchestrator as the console app.
// Settings come from the "DocDetectives" section of appsettings.json (git-ignored) or environment variables.
builder.Services.AddDocDetectives(builder.Configuration);

builder.Services.AddSingleton<IngestionJobService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IngestionJobService>());

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024); // file upload chunks

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
