using System.Runtime.CompilerServices;
using Bunit;

namespace Lumibelle.Tests;

internal static class BunitDefaults
{
    // Component tests run alongside the rest of the suite. bUnit's one-second default
    // for WaitFor* is too tight under that load; a passing wait still returns at once.
    // Shared CI runners are slower again, so allow them much longer.
    [ModuleInitializer]
    internal static void Initialize() =>
        BunitContext.DefaultWaitTimeout = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("CI") is null ? 5 : 30);

    // Explicit waits keep their own minimum but never undercut the default.
    internal static TimeSpan WaitTimeout(int seconds) => TimeSpan.FromSeconds(Math.Max(seconds, BunitContext.DefaultWaitTimeout.TotalSeconds));
}
