using AssetManager.Web.Components;
using AssetManager.Core.Platform;
using AssetManager.Web.Services;
using AssetManager.Shared;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Platform services the browser cannot provide are stubbed and report "not supported".
builder.Services.AddSingleton<UnsupportedPlatformServices>();
builder.Services.AddSingleton<IClipboardFileService>(sp => sp.GetRequiredService<UnsupportedPlatformServices>());
builder.Services.AddSingleton<IFolderPicker>(sp => sp.GetRequiredService<UnsupportedPlatformServices>());
builder.Services.AddSingleton<IFilePicker>(sp => sp.GetRequiredService<UnsupportedPlatformServices>());
builder.Services.AddSingleton<IShellService>(sp => sp.GetRequiredService<UnsupportedPlatformServices>());
builder.Services.AddSingleton<IDragOutService>(sp => sp.GetRequiredService<UnsupportedPlatformServices>());
// No window frame in a browser tab: the app bar reserves nothing for caption buttons (spec "Browser host unchanged").
builder.Services.AddSingleton<IWindowChrome, NoWindowChrome>();
builder.Services.AddAssetManager();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(
        typeof(AssetManager.Shared._Imports).Assembly);

app.Run();
