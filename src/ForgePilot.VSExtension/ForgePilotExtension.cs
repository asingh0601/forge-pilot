using Microsoft.VisualStudio.Extensibility;

namespace ForgePilot.VSExtension;

/// <summary>
/// Required by the VisualStudio.Extensibility build, which refuses to compile a
/// project that contributes no <see cref="Extension"/> (VSEXT0004) — and that
/// build is currently the only thing that produces a .vsix under
/// <c>dotnet build</c>, since the VSSDK BuildTools targets are never imported
/// there.
///
/// It contributes nothing else. The menu command moved to a classic .vsct
/// (see ForgePilotPackage.vsct and Commands/OpenChatSessionCommand.cs), so this
/// class exists purely to satisfy packaging. <c>RequiresInProcessHosting</c>
/// keeps everything in the devenv process, where the VSSDK package lives.
/// </summary>
[VisualStudioContribution]
internal class ForgePilotExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        RequiresInProcessHosting = true,
    };
}
