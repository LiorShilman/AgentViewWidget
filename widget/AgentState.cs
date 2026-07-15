using System.Text.Json.Serialization;

namespace AgentLiveWidget;

/// <summary>Multi-project snapshot broadcast by the local state server.</summary>
public sealed class WidgetSnapshot
{
    [JsonPropertyName("projects")]
    public List<AgentState> Projects { get; set; } = [];

    [JsonPropertyName("activeProjectKey")]
    public string? ActiveProjectKey { get; set; }
}

/// <summary>Per-project state model — mirrors the JSON broadcast by the local state server.</summary>
public sealed class AgentState
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "offline";

    [JsonPropertyName("currentTool")]
    public CurrentTool? CurrentTool { get; set; }

    [JsonPropertyName("lastPrompt")]
    public string? LastPrompt { get; set; }

    [JsonPropertyName("lastResponse")]
    public string? LastResponse { get; set; }

    [JsonPropertyName("sessionStartedAt")]
    public long? SessionStartedAt { get; set; }

    [JsonPropertyName("memoryPressure")]
    public string MemoryPressure { get; set; } = "normal";

    [JsonPropertyName("recentEvents")]
    public List<RecentEvent> RecentEvents { get; set; } = [];

    [JsonPropertyName("filesTouched")]
    public List<string> FilesTouched { get; set; } = [];

    [JsonPropertyName("projectPath")]
    public string? ProjectPath { get; set; }

    [JsonPropertyName("lastMemorySaveAt")]
    public long? LastMemorySaveAt { get; set; }

    [JsonPropertyName("git")]
    public GitInfo? Git { get; set; }
}

public sealed class GitInfo
{
    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "";

    [JsonPropertyName("changedCount")]
    public int ChangedCount { get; set; }
}

public sealed class CurrentTool
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Tool";

    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("startedAt")]
    public long StartedAt { get; set; }
}

public sealed class RecentEvent
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
