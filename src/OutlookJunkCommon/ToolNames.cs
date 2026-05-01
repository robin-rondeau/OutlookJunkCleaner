namespace OutlookJunkCommon;

public static class ToolNames
{
    public const string ListJunk = "list_junk";
    public const string GetMessage = "get_message";
    public const string MarkAsRead = "mark_as_read";
    public const string MoveToTriage = "move_to_triage";
    public const string ListTriage = "list_triage";
    public const string GetStatus = "get_status";
    public const string DeleteFromJunk = "delete_from_junk";
    public const string LookupClassificationStatus = "lookup_classification_status";
}

public static class FolderNames
{
    public const string Junk = "JunkEmail";
    public const string DefaultTriage = "Triage";
    public const string DeletedItems = "DeletedItems";
}

public static class EnvVars
{
    public const string ClientId = "OUTLOOK_JUNK_MCP_CLIENT_ID";
    public const string TriageFolder = "OUTLOOK_JUNK_MCP_TRIAGE_FOLDER";
    public const string AllowDelete = "OUTLOOK_JUNK_MCP_ALLOW_DELETE";

    public const string AgentProvider = "OUTLOOK_JUNK_AGENT_PROVIDER";
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string AnthropicModel = "OUTLOOK_JUNK_AGENT_MODEL";
    public const string McpServerPath = "OUTLOOK_JUNK_AGENT_MCP_SERVER";

    public const string OllamaBaseUrl = "OUTLOOK_JUNK_OLLAMA_BASE_URL";
    public const string OllamaModel = "OUTLOOK_JUNK_OLLAMA_MODEL";
}
