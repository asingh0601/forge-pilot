using System.Diagnostics;
using ForgePilot.Services.Completions;

namespace ForgePilot.Console;

/// <summary>
/// Exercises the inline-completion path from the command line.
///
/// The point is latency. A completion that arrives after the user has typed
/// past it is worse than no completion, so the round trip has to be measured
/// before wiring anything into the editor — and it cannot be measured from
/// inside the editor, where the debounce and the cache hide the real number.
///
/// Usage:
///   ForgePilot.Console --complete &lt;file&gt; &lt;line&gt; &lt;column&gt; [runs]
/// Line and column are 1-based, matching what an editor shows.
/// </summary>
internal static class CompletionHarness
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            System.Console.Error.WriteLine(
                "usage: ForgePilot.Console --complete <file> <line> <column> [runs]");
            return 2;
        }

        var path = Path.GetFullPath(args[0]);
        if (!File.Exists(path))
        {
            System.Console.Error.WriteLine($"File not found: {path}");
            return 2;
        }

        if (!int.TryParse(args[1], out var line) || !int.TryParse(args[2], out var column))
        {
            System.Console.Error.WriteLine("Line and column must be integers (1-based).");
            return 2;
        }

        var runs = args.Length > 3 && int.TryParse(args[3], out var r) ? Math.Max(1, r) : 3;

        var text = File.ReadAllText(path);
        var offset = ToOffset(text, line, column);
        var context = CompletionContext.FromDocument(text, offset, path, GuessLanguage(path));

        var options = new CompletionOptions { Enabled = true };
        using var provider = new ClaudeCliCompletionProvider(
            new ForgePilot.Services.Configuration.ForgePilotOptions
            {
                WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            },
            options);

        System.Console.WriteLine($"file    : {path}");
        System.Console.WriteLine($"caret   : line {line}, col {column} (offset {offset})");
        System.Console.WriteLine($"model   : {options.Model}");
        System.Console.WriteLine($"context : {context.Prefix.Length} chars before, {context.Suffix.Length} after");
        System.Console.WriteLine();

        var timings = new List<double>();
        string? last = null;

        for (var i = 1; i <= runs; i++)
        {
            // Deliberately calls the provider directly, not the caching wrapper:
            // a cache hit would report ~0ms and tell us nothing about the CLI.
            //
            // Run 1 also pays for starting the CLI process, so it is reported
            // separately below rather than folded into the median — it is a
            // once-per-session cost, not a per-completion one.
            var sw = Stopwatch.StartNew();
            last = await provider.CompleteAsync(context, CancellationToken.None);
            sw.Stop();

            timings.Add(sw.Elapsed.TotalMilliseconds);
            System.Console.WriteLine(
                $"run {i}: {sw.Elapsed.TotalMilliseconds,7:N0} ms  {(last is null ? "<no suggestion>" : $"{last.Length} chars")}");
        }

        var startup = timings[0];
        var steady = timings.Count > 1 ? timings.GetRange(1, timings.Count - 1) : timings;
        steady.Sort();
        var median = steady[steady.Count / 2];

        System.Console.WriteLine();
        System.Console.WriteLine($"startup : {startup:N0} ms   (run 1 — spawns the CLI, paid once per session)");
        System.Console.WriteLine($"median  : {median:N0} ms   (steady state, excluding run 1)");
        System.Console.WriteLine($"min/max : {steady[0]:N0} / {steady[steady.Count - 1]:N0} ms");

        if (last is not null)
        {
            System.Console.WriteLine();
            System.Console.WriteLine("--- suggestion ---");
            System.Console.WriteLine(last);
            System.Console.WriteLine("------------------");
        }

        // Non-zero when too slow to be useful, so this can gate a check.
        return median <= 500 ? 0 : 1;
    }

    /// <summary>Converts a 1-based line/column to a character offset.</summary>
    private static int ToOffset(string text, int line, int column)
    {
        var offset = 0;
        var currentLine = 1;

        while (currentLine < line && offset < text.Length)
        {
            var next = text.IndexOf('\n', offset);
            if (next < 0) break;
            offset = next + 1;
            currentLine++;
        }

        return Math.Min(offset + Math.Max(0, column - 1), text.Length);
    }

    private static string GuessLanguage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "CSharp",
            ".ts" or ".tsx" => "TypeScript",
            ".js" or ".jsx" => "JavaScript",
            ".py" => "Python",
            ".xaml" or ".xml" => "XML",
            ".json" => "JSON",
            ".sql" => "SQL",
            _ => "code",
        };
}
