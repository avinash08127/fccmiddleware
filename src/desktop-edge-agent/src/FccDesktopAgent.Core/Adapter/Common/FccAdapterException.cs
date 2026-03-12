namespace FccDesktopAgent.Core.Adapter.Common;

/// <summary>
/// Thrown when an FCC adapter operation fails with a classifiable error.
/// <para>Recoverable errors (network, timeout, 5xx, 408, 429) indicate transient failures — callers may retry.</para>
/// <para>Non-recoverable errors (401, 403, parse failure) indicate misconfiguration or protocol violation — callers should not retry without operator intervention.</para>
/// </summary>
public sealed class FccAdapterException : Exception
{
    public bool IsRecoverable { get; }
    public int? HttpStatusCode { get; }

    public FccAdapterException(string message, bool isRecoverable, int? httpStatusCode = null)
        : base(message)
    {
        IsRecoverable = isRecoverable;
        HttpStatusCode = httpStatusCode;
    }

    public FccAdapterException(string message, bool isRecoverable, Exception innerException, int? httpStatusCode = null)
        : base(message, innerException)
    {
        IsRecoverable = isRecoverable;
        HttpStatusCode = httpStatusCode;
    }
}
