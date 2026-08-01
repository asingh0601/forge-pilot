using Microsoft.VisualStudio.Extensibility;

namespace ClaudeDeck.VSExtension;

[VisualStudioContribution]
internal class ClaudeDeckExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };
}
