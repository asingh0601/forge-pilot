namespace ForgePilot.Services.Models;

public class PersistedMessage
{
    public int Ordinal { get; set; }
    public string ItemType { get; set; } = "";
    public string Content { get; set; } = "";
    public string? ToolName { get; set; }

    /// <summary>
    /// Added after the original schema. Sessions written before this field
    /// existed deserialize it as null, and the renderer falls back to showing
    /// the tool name alone — so old sessions still load.
    /// </summary>
    public string? ToolArgs { get; set; }

    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? BodyMode { get; set; }
    public string? ExpanderTitle { get; set; }
    public string StatusText { get; set; } = "Success";
    public DateTime CreatedUtc { get; set; }
}
