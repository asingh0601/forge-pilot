namespace ForgePilot.Services.Abstractions;

public enum OutputItemStatus
{
    Pending,
    Success,
    Error,
    Info
}

public enum OutputBodyMode
{
    Markdown,
    Html
}

public record OutputItem
{
    public required string Id { get; init; }
    public required string ToolName { get; init; }
    public required string Title { get; set; }

    /// <summary>
    /// One-line summary of the tool's arguments — the file path, the command,
    /// the search pattern — for the collapsed tool card header.
    ///
    /// This is set when the tool starts and deliberately never cleared: the
    /// tool_result overwrites <see cref="Body"/> with the result, so without a
    /// separate field the arguments are gone the moment the tool completes and
    /// the header can only say "Read" instead of "Read src/Foo.cs".
    /// </summary>
    public string? ToolArgs { get; set; }
    public OutputItemStatus Status { get; set; } = OutputItemStatus.Pending;
    public OutputBodyMode BodyMode { get; set; } = OutputBodyMode.Markdown;
    public string? Body { get; set; }
    public string? Delta { get; set; }
}

public interface IOutputListener
{
    /// <summary>
    /// Called when a new step begins. The item starts with Pending status.
    /// </summary>
    void OnStepStarted(OutputItem item);

    /// <summary>
    /// Called when a step's body or status is updated (e.g. streaming text).
    /// </summary>
    void OnStepUpdated(OutputItem item);

    /// <summary>
    /// Called when a step completes (status should be Success or Error).
    /// </summary>
    void OnStepCompleted(OutputItem item);
}
