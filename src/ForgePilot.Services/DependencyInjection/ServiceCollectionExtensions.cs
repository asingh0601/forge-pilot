using ForgePilot.Services.Abstractions;
using ForgePilot.Services.ClaudeCli;
using ForgePilot.Services.ClaudeCli.Permissions;
using ForgePilot.Services.ClaudeCli.Questions;
using ForgePilot.Services.Configuration;
using ForgePilot.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ForgePilot.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddForgePilotServices(
        this IServiceCollection services,
        Action<ForgePilotOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<ForgePilotOptions>(_ => { });

        services.AddSingleton<ISessionStore, JsonSessionStore>();

        // Discovers the commands / skills / plugins / MCP connectors the CLI
        // will load for this workspace, so the UI can list and invoke them.
        services.AddSingleton<IClaudeAssetService>(sp =>
            new ClaudeAssetService(
                sp.GetRequiredService<IOptions<ForgePilotOptions>>().Value.WorkingDirectory,
                sp.GetService<ILogger<ClaudeAssetService>>()));

        // Brokers — both singletons; UI subscribes to events on construction.
        services.AddSingleton<IPermissionBroker, PermissionBroker>();
        services.AddSingleton<IUserQuestionBroker, UserQuestionBroker>();

        // Long-running CLI process host — singleton, owns the subprocess and
        // the in-process pipe server that the MCP helper exe connects to.
        services.AddSingleton<ClaudeCliProcessHost>();

        services.AddSingleton<IChatService>(sp =>
            new ClaudeCliChatService(
                sp.GetRequiredService<IOptions<ForgePilotOptions>>(),
                sp.GetRequiredService<IOutputListener>(),
                sp.GetRequiredService<ClaudeCliProcessHost>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatService>>()));

        return services;
    }
}
