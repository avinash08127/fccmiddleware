namespace FccDesktopAgent.Core.Security;

/// <summary>
/// Marks a property or field as containing sensitive data (credentials, tokens, TIN).
/// Serilog destructuring policies MUST suppress fields tagged with this attribute.
/// NEVER log sensitive fields — see architecture rule #9.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SensitiveDataAttribute : Attribute { }
