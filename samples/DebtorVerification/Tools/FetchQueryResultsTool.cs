using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.DebtorVerification.Tools;

[ExcludeFromCodeCoverage]
public sealed class FetchQueryResultsTool(QueryStore store) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "handle": {
              "type": "string",
              "description": "The handle returned by submit_query."
            }
          },
          "required": ["handle"]
        }
        """).RootElement;

    public string Name => "fetch_query_results";
    public string Description => "Poll for the results of a previously submitted query. Returns status 'pending' or 'ready' with rows when complete.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var handle = call.Arguments.TryGetProperty("handle", out var h) ? h.GetString() ?? "" : "";
        var query = store.TryGet(handle);

        if (query is null)
            return Task.FromResult(new ToolResult(call.CallId, "{\"status\":\"error\",\"message\":\"Unknown handle.\"}"));

        var json = query.QueryId switch
        {
            "get_client_by_registered_name" or "get_client_by_registration_number" => ClientLookupResult,
            "get_client_contacts_by_client_id"                                     => ContactsResult,
            _                                                                       => """{"status":"error","message":"Unknown query_id."}"""
        };

        return Task.FromResult(new ToolResult(call.CallId, json));
    }

    // Fixture: Marks and Spencer Group PLC — a real UK public company used here as a demo debtor.
    // Contact details below are illustrative only and do not represent real M&S staff.
    private const string ClientLookupResult =
        """
        {
          "status": "ready",
          "rows": [
            {
              "AGENCY_NAME": "Beacon Recruitment Solutions Ltd",
              "CLIENT_ID": "CLT-7342",
              "CLIENT_REGISTERED_NAME": "Marks and Spencer Group PLC",
              "CLIENT_REGISTRATION_NUMBER": "00214436",
              "CLIENT_COUNTRY_JURISDICTION": "England and Wales",
              "CLIENT_ORGANISATION_TYPE": "Public Limited Company"
            }
          ]
        }
        """;

    private const string ContactsResult =
        """
        {
          "status": "ready",
          "rows": [
            {
              "CLIENT_REGISTERED_NAME": "Marks and Spencer Group PLC",
              "CLIENT_REGISTRATION_NUMBER": "00214436",
              "CLIENT_COUNTRY_JURISDICTION": "England and Wales",
              "CLIENT_ORGANISATION_TYPE": "Public Limited Company",
              "CONTACT_NAME": "Accounts Payable Team",
              "CONTACT_EMAIL": "invoices@marksandspencer.com",
              "CONTACT_TELEPHONE": "020 3148 8000",
              "COMPANY_WEBSITE": "https://www.marksandspencer.com"
            }
          ]
        }
        """;
}
