using AssetManager.Core.Platform;
using AssetManager.Core.Settings;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.FileProviders;

namespace AssetManager;

/// <summary>
/// The app's web view. The portable package ships its web content as <c>wwwroot.zip</c> beside the exe (design D2):
/// when that file is there, pages are served from it; otherwise (Debug, development runs) from the usual folder.
/// </summary>
public sealed class AppBlazorWebView : BlazorWebView
{
    public const string WebContentZip = "wwwroot.zip";

    public override IFileProvider CreateFileProvider(string contentRootDir)
    {
        var zip = Path.Combine(AppPaths.InstallFolder, WebContentZip);
        return File.Exists(zip) ? new ZipFileProvider(zip) : base.CreateFileProvider(contentRootDir);
    }
}
