namespace ModelHarness.Framework.Sensors;

/// <summary>Lifecycle points at which sensors can observe and intervene.</summary>
public enum HookPoint
{
    PreModelCall,
    PostModelCall,
    PreToolCall,
    PostToolCall,
    PreReturn
}
