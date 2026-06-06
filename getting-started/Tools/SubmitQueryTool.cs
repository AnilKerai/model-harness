using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

[ExcludeFromCodeCoverage]
public sealed class SubmitQueryTool(QueryStore store) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query_id": {
              "type": "string",
              "description": "The query to execute. One of: get_client_by_registered_name, get_client_by_registration_number, get_client_contacts_by_client_id."
            },
            "client_registered_name": {
              "type": "string",
              "description": "Required when query_id is get_client_by_registered_name."
            },
            "client_registration_number": {
              "type": "string",
              "description": "Required when query_id is get_client_by_registration_number."
            },
            "client_id": {
              "type": "string",
              "description": "Required when query_id is get_client_contacts_by_client_id."
            }
          },
          "required": ["query_id"]
        }
        """).RootElement;

    public string Name => "submit_query";
    public string Description => "Submit an async query against the internal client database. Returns a handle to poll with fetch_query_results.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var args = call.Arguments;
        var queryId = args.TryGetProperty("query_id", out var q) ? q.GetString() ?? "" : "";

        var param = queryId switch
        {
            "get_client_by_registered_name"     => args.TryGetProperty("client_registered_name", out var n) ? n.GetString() : null,
            "get_client_by_registration_number" => args.TryGetProperty("client_registration_number", out var r) ? r.GetString() : null,
            "get_client_contacts_by_client_id"  => args.TryGetProperty("client_id", out var c) ? c.GetString() : null,
            _                                   => null
        };

        var handle = store.Submit(queryId, param);
        return Task.FromResult(new ToolResult(call.CallId, $"{{\"handle\":\"{handle}\"}}"));
    }
}
