using Microsoft.VisualStudio.Extensibility;

namespace ForgePilot.VSExtension;

/// <summary>
/// Extension entry point for the VisualStudio.Extensibility host. Required —
/// the build refuses a project that contributes none (VSEXT0004) — and that
/// build is what produces the .vsix. <c>RequiresInProcessHosting</c> keeps
/// everything in the devenv process, where the VSSDK package lives.
/// </summary>
[VisualStudioContribution]
internal class ForgePilotExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };
}
