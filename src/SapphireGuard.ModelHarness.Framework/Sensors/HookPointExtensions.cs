namespace SapphireGuard.ModelHarness.Framework.Sensors;

internal static class HookPointExtensions
{
    // The one definition of the split the guides render: an advisory intervention (PreModelCall
    // guidance, PostToolCall flag) blocks nothing, so the response or tool result it refers to
    // stands. Every other hookpoint rejects, challenges or blocks. An allowlist, so a hookpoint
    // added later is treated as blocking until someone decides otherwise.
    internal static bool IsAdvisory(this HookPoint hookPoint) =>
        hookPoint is HookPoint.PreModelCall or HookPoint.PostToolCall;
}
