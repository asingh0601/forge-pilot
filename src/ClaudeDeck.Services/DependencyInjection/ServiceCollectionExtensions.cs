using ClaudeDeck.Services.Abstractions;
using ClaudeDeck.Services.ClaudeCli;
using ClaudeDeck.Services.ClaudeCli.Permissions;
using ClaudeDeck.Services.ClaudeCli.Questions;
using ClaudeDeck.Services.Configuration;
using ClaudeDeck.Services.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClaudeDeck.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddClaudeDeckServices(
        this IServiceCollection services,
        Action<ClaudeDeckOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<ClaudeDeckOptions>(_ => { });

        services.AddSingleton<ISessionStore, JsonSessionStore>();

        // Brokers — both singletons; UI subscribes to events on construction.
        services.AddSingleton<IPermissionBroker, PermissionBroker>();
        services.AddSingleton<IUserQuestionBroker, UserQuestionBroker>();

        // Long-running CLI process host — singleton, owns the subprocess and
        // the in-process pipe server that the MCP helper exe connects to.
        services.AddSingleton<ClaudeCliProcessHost>();

        services.AddSingleton<IChatService>(sp =>
            new ClaudeCliChatService(
                sp.GetRequiredService<IOptions<ClaudeDeckOptions>>(),
                sp.GetRequiredService<IOutputListener>(),
                sp.GetRequiredService<ClaudeCliProcessHost>(),
                sp.GetRequiredService<ILogger<ClaudeCliChatService>>()));

        return services;
    }
}
