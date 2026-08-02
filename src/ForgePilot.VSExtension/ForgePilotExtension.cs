using Microsoft.VisualStudio.Extensibility;

namespace ForgePilot.VSExtension;

[VisualStudioContribution]
internal class ForgePilotExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };
}
