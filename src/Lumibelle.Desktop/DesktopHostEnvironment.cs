using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
namespace Lumibelle.Desktop;

// MAUI's default IHostEnvironment does not implement ContentRootPath. Shared
// .NET libraries such as DataProtection still need a complete host environment.
internal sealed class DesktopHostEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "Lumibelle";
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
