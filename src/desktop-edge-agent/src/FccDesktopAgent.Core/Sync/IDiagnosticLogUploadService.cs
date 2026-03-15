namespace FccDesktopAgent.Core.Sync;

public interface IDiagnosticLogUploadService
{
    Task<int> UploadPendingAsync(CancellationToken ct);
}
