namespace FccDesktopAgent.Core.Adapter.Doms.Protocol;

/// <summary>
/// Thrown when a DOMS protocol-level error occurs (bad response, unexpected state, etc.).
/// </summary>
public sealed class DomsProtocolException : Exception
{
    public DomsProtocolException(string message) : base(message) { }
    public DomsProtocolException(string message, Exception innerException) : base(message, innerException) { }
}
