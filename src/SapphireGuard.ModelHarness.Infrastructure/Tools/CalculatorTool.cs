using System.Globalization;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

/// <summary>
/// Evaluates a single arithmetic expression over two operands. Deliberately
/// minimal — the goal is to exercise the loop, not to be a real calculator.
/// </summary>
public sealed class CalculatorTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "op":  { "type": "string", "enum": ["add", "sub", "mul", "div"] },
            "lhs": { "type": "number" },
            "rhs": { "type": "number" }
          },
          "required": ["op", "lhs", "rhs"]
        }
        """).RootElement;

    public string Name => "calculator";

    public string Description => "Performs basic arithmetic: add | sub | mul | div over two numbers.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var args = call.Arguments;
        var op = args.GetProperty("op").GetString();
        var lhs = args.GetProperty("lhs").GetDouble();
        var rhs = args.GetProperty("rhs").GetDouble();

        double? value = op switch
        {
            "add" => lhs + rhs,
            "sub" => lhs - rhs,
            "mul" => lhs * rhs,
            "div" when rhs != 0 => lhs / rhs,
            _ => null
        };

        if (value is null)
        {
            return Task.FromResult(new ToolResult(call.CallId,
                $"Invalid op '{op}' or division by zero.", IsError: true));
        }

        return Task.FromResult(new ToolResult(call.CallId, value.Value.ToString("G", CultureInfo.InvariantCulture)));
    }
}
