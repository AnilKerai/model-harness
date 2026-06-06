using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

public sealed class CheckEmailDomainMatchTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "website_url": {
              "type": "string",
              "description": "The company website URL from the client record (e.g. https://www.company.com)."
            },
            "contact_email": {
              "type": "string",
              "description": "The primary contact email address from the client record."
            }
          },
          "required": ["website_url", "contact_email"]
        }
        """).RootElement;

    public string Name => "check_email_domain_match";
    public string Description => "Deterministic check: extracts the domain from the company website URL and from the contact email address and compares them. Returns a structured verdict — use it directly.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var websiteUrl   = call.Arguments.TryGetProperty("website_url",   out var w) ? w.GetString() ?? "" : "";
        var contactEmail = call.Arguments.TryGetProperty("contact_email", out var e) ? e.GetString() ?? "" : "";

        var websiteDomain = ExtractDomainFromUrl(websiteUrl);
        var emailDomain   = ExtractDomainFromEmail(contactEmail);

        if (string.IsNullOrEmpty(websiteDomain) && string.IsNullOrEmpty(emailDomain))
            return CheckResult.Build(call.CallId, "Inconclusive", "Low", "Could not extract a domain from either the website URL or the email address.");

        if (string.IsNullOrEmpty(websiteDomain))
            return CheckResult.Build(call.CallId, "Inconclusive", "Low", $"Could not extract a domain from website URL '{websiteUrl}'.");

        if (string.IsNullOrEmpty(emailDomain))
            return CheckResult.Build(call.CallId, "Inconclusive", "Low", $"Could not extract a domain from email address '{contactEmail}'.");

        if (websiteDomain.Equals(emailDomain, StringComparison.OrdinalIgnoreCase))
            return CheckResult.Build(call.CallId, "Pass", "High",
                $"Website domain '{websiteDomain}' matches email domain '{emailDomain}' exactly.");

        return CheckResult.Build(call.CallId, "Fail", "High",
            $"Website domain '{websiteDomain}' does not match email domain '{emailDomain}'.");
    }

    private static string ExtractDomainFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "";
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static string ExtractDomainFromEmail(string email)
    {
        var atIdx = email.IndexOf('@');
        return atIdx >= 0 ? email[(atIdx + 1)..].ToLowerInvariant() : "";
    }
}
