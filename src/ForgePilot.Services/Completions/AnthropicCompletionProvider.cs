using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForgePilot.Services.Completions;

/// <summary>
/// Inline completions from the Anthropic Messages API.
///
/// The Messages API has no fill-in-the-middle endpoint, so FIM is emulated:
/// the prefix and suffix go in one structured user message, and the assistant
/// turn is <em>prefilled</em> with an empty string so the model continues code
/// directly instead of opening with prose or a fenced block. That prefill is
/// what makes the output usable verbatim.
///
/// Billing note: this calls the API and is billed per token. It is NOT covered
/// by the Claude Pro/Max subscription the chat window uses — that pays for the
/// CLI, not for API access.
/// </summary>
public sealed class AnthropicCompletionProvider : ICompletionProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private static readonly HttpClient Http = new()
    {
        // Well beyond the debounce budget; a completion this slow is already
        // useless, and the caller cancels long before.
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly Func<string?> _apiKeyAccessor;
    private readonly CompletionOptions _options;
    private readonly ILogger _logger;

    public AnthropicCompletionProvider(
        Func<string?> apiKeyAccessor,
        CompletionOptions options,
        ILogger<AnthropicCompletionProvider>? logger = null)
    {
        _apiKeyAccessor = apiKeyAccessor;
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKeyAccessor());

    public async Task<string?> CompleteAsync(CompletionContext context, CancellationToken cancellationToken)
    {
        if (!IsConfigured || context.IsTriviallyEmpty) return null;

        var apiKey = _apiKeyAccessor();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Content = new StringContent(BuildRequestJson(context), new UTF8Encoding(false), "application/json");

        try
        {
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Logged, never surfaced: a failed completion should be
                // indistinguishable from having no suggestion. Interrupting
                // someone mid-keystroke with an error dialog is worse than
                // silence.
                _logger.LogWarning(
                    "[Completions] {Status} from Anthropic API: {Body}",
                    (int)response.StatusCode, Truncate(body, 400));
                return null;
            }

            return ExtractCompletion(body);
        }
        catch (OperationCanceledException)
        {
            // Expected on every keystroke. Not an error.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Completions] Request failed");
            return null;
        }
    }

    private string BuildRequestJson(CompletionContext context)
    {
        var language = string.IsNullOrWhiteSpace(context.LanguageId) ? "code" : context.LanguageId;
        var fileName = SafeFileName(context.FilePath);

        var user = new StringBuilder()
            .Append("Continue this ").Append(language).Append(" file at <CURSOR>.\n")
            .Append("Reply with only the code that replaces <CURSOR>. No prose, no fences, no repetition of ")
            .Append("surrounding lines.\n\n")
            .Append("File: ").Append(fileName).Append("\n\n")
            .Append("<code>\n")
            .Append(context.Prefix)
            .Append("<CURSOR>")
            .Append(context.Suffix)
            .Append("\n</code>")
            .ToString();

        var payload = new Dictionary<string, object>
        {
            ["model"] = _options.Model,
            ["max_tokens"] = _options.MaxTokens,
            // Completions should be predictable: the same context twice should
            // not produce two different suggestions.
            ["temperature"] = 0,
            ["messages"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "user", ["content"] = user },
                // Assistant prefill — forces raw continuation rather than a
                // preamble like "Here's the completed function:".
                new Dictionary<string, object> { ["role"] = "assistant", ["content"] = "" }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Pulls the text out of the response and strips the wrappers the model
    /// still occasionally adds despite the prefill.
    /// </summary>
    private static string? ExtractCompletion(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            if (!doc.RootElement.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                return null;

            var sb = new StringBuilder();
            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                    block.TryGetProperty("text", out var text))
                {
                    sb.Append(text.GetString());
                }
            }

            var result = sb.ToString();
            if (string.IsNullOrEmpty(result)) return null;

            result = StripCodeFence(result);

            // All-whitespace is not a suggestion; showing it would render an
            // invisible proposal the user can "accept" to no effect.
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripCodeFence(string text)
    {
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return text;

        // Drop the opening fence line (which may carry a language tag) and any
        // closing fence.
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0) return "";

        var body = trimmed.Substring(firstNewline + 1);
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return closing >= 0 ? body.Substring(0, closing).TrimEnd('\n') : body;
    }

    private static string SafeFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return "untitled";
        try { return System.IO.Path.GetFileName(path); }
        catch { return "untitled"; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "…";
}
