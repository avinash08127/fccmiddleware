using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FccDesktopAgent.Core.Buffer;
using FccDesktopAgent.Core.Buffer.Entities;
using FccDesktopAgent.Core.Config;
using FccDesktopAgent.Core.Registration;
using FccDesktopAgent.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FccDesktopAgent.Core.Sync;

/// <summary>
/// Uploads warning/error log entries from the local rolling log files to the cloud diagnostic-log endpoint.
/// </summary>
public sealed class DiagnosticLogUploadService : IDiagnosticLogUploadService
{
    private const string DiagnosticLogsPath = "/api/v1/agent/diagnostic-logs";
    private const string DecommissionedCode = "DEVICE_DECOMMISSIONED";
    private const int MaxLogEntriesPerBatch = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<AgentConfiguration> _config;
    private readonly AuthenticatedCloudRequestHandler _authHandler;
    private readonly IRegistrationManager _registrationManager;
    private readonly IConfigManager _configManager;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DiagnosticLogUploadService> _logger;

    public DiagnosticLogUploadService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AgentConfiguration> config,
        AuthenticatedCloudRequestHandler authHandler,
        IRegistrationManager registrationManager,
        IConfigManager configManager,
        IHttpClientFactory httpFactory,
        ILogger<DiagnosticLogUploadService> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _authHandler = authHandler;
        _registrationManager = registrationManager;
        _configManager = configManager;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task<int> UploadPendingAsync(CancellationToken ct)
    {
        if (_registrationManager.IsDecommissioned)
            return 0;

        var siteConfig = _configManager.CurrentSiteConfig;
        if (siteConfig?.Telemetry?.IncludeDiagnosticsLogs != true)
            return 0;

        var config = _config.CurrentValue;
        if (!CloudUrlGuard.IsSecure(config.CloudBaseUrl)
            || string.IsNullOrWhiteSpace(config.SiteId)
            || !Guid.TryParse(config.DeviceId, out var deviceId)
            || !Guid.TryParse(config.LegalEntityId, out var legalEntityId))
        {
            return 0;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgentDbContext>();
        var cursor = await db.DiagnosticLogCursors.FindAsync([1], ct) ?? new DiagnosticLogCursorRecord();

        var batch = ReadNextBatch(cursor.FilePath, cursor.LastProcessedLineNumber);
        if (batch.FilePath is not null
            && (batch.LastProcessedLineNumber != cursor.LastProcessedLineNumber
                || !string.Equals(batch.FilePath, cursor.FilePath, StringComparison.OrdinalIgnoreCase)))
        {
            cursor.FilePath = batch.FilePath;
            cursor.LastProcessedLineNumber = batch.LastProcessedLineNumber;
            cursor.UpdatedAt = DateTimeOffset.UtcNow;

            if (db.Entry(cursor).State == EntityState.Detached)
                db.DiagnosticLogCursors.Add(cursor);
        }

        if (batch.Entries.Count == 0)
        {
            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);
            return 0;
        }

        var request = new DiagnosticLogUploadRequest
        {
            DeviceId = deviceId,
            SiteCode = config.SiteId,
            LegalEntityId = legalEntityId,
            UploadedAtUtc = DateTimeOffset.UtcNow,
            LogEntries = batch.Entries.ToList()
        };

        var result = await _authHandler.ExecuteAsync<int>(
            (token, innerCt) => SendDiagnosticLogsAsync(request, token, config, innerCt),
            "diagnostic log upload",
            ct);

        if (!result.IsSuccess || result.RequiresHalt)
            return 0;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Diagnostic logs upload: {Count} entry(s) uploaded", result.Value);
        return result.Value;
    }

    private DiagnosticLogReadResult ReadNextBatch(string? currentFilePath, int lastProcessedLineNumber)
    {
        var logDirectory = Path.Combine(AgentDataDirectory.Resolve(), "logs");
        if (!Directory.Exists(logDirectory))
            return DiagnosticLogReadResult.Empty(currentFilePath, lastProcessedLineNumber);

        var files = Directory.EnumerateFiles(logDirectory, "agent*.log")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            return DiagnosticLogReadResult.Empty(currentFilePath, lastProcessedLineNumber);

        var startIndex = 0;
        if (!string.IsNullOrWhiteSpace(currentFilePath))
        {
            var existingIndex = files.FindIndex(path =>
                string.Equals(path, currentFilePath, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                startIndex = existingIndex;
        }

        var entries = new List<string>(MaxLogEntriesPerBatch);
        var lastFilePath = files[startIndex];
        var lastLineNumber = currentFilePath != null
            && string.Equals(lastFilePath, currentFilePath, StringComparison.OrdinalIgnoreCase)
                ? lastProcessedLineNumber
                : 0;

        for (var fileIndex = startIndex; fileIndex < files.Count; fileIndex++)
        {
            var filePath = files[fileIndex];
            var skipLineCount = string.Equals(filePath, currentFilePath, StringComparison.OrdinalIgnoreCase)
                ? lastProcessedLineNumber
                : 0;

            var fileResult = ReadEntriesFromFile(filePath, skipLineCount, MaxLogEntriesPerBatch - entries.Count);
            entries.AddRange(fileResult.Entries);
            lastFilePath = filePath;
            lastLineNumber = fileResult.LastProcessedLineNumber;

            if (entries.Count >= MaxLogEntriesPerBatch)
                break;
        }

        return new DiagnosticLogReadResult(entries, lastFilePath, lastLineNumber);
    }

    private static DiagnosticLogReadResult ReadEntriesFromFile(string filePath, int skipLineCount, int remainingCapacity)
    {
        var entries = new List<string>(Math.Max(remainingCapacity, 0));
        var lastProcessedLineNumber = skipLineCount;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lineNumber = 0;
        StringBuilder? currentEntry = null;
        var currentEntryWanted = false;
        var currentEntryEndLine = skipLineCount;

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (line is null)
                break;

            lineNumber++;
            if (lineNumber <= skipLineCount)
                continue;

            if (LooksLikeLogHeader(line))
            {
                if (currentEntry is not null)
                {
                    lastProcessedLineNumber = currentEntryEndLine;
                    if (currentEntryWanted && remainingCapacity > 0)
                    {
                        entries.Add(currentEntry.ToString());
                        remainingCapacity--;
                        if (remainingCapacity == 0)
                            return new DiagnosticLogReadResult(entries, filePath, lastProcessedLineNumber);
                    }
                }

                currentEntry = new StringBuilder(line);
                currentEntryWanted = IsDiagnosticSeverity(line);
                currentEntryEndLine = lineNumber;
                continue;
            }

            if (currentEntry is not null)
            {
                currentEntry.AppendLine().Append(line);
                currentEntryEndLine = lineNumber;
            }
            else
            {
                lastProcessedLineNumber = lineNumber;
            }
        }

        if (currentEntry is not null)
        {
            lastProcessedLineNumber = currentEntryEndLine;
            if (currentEntryWanted && remainingCapacity > 0)
                entries.Add(currentEntry.ToString());
        }

        return new DiagnosticLogReadResult(entries, filePath, lastProcessedLineNumber);
    }

    private async Task<int> SendDiagnosticLogsAsync(
        DiagnosticLogUploadRequest request,
        string token,
        AgentConfiguration config,
        CancellationToken ct)
    {
        var url = $"{config.CloudBaseUrl.TrimEnd('/')}{DiagnosticLogsPath}";
        var http = _httpFactory.CreateClient("cloud");

        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await http.SendAsync(message, ct);

        PeerDirectoryVersionHelper.CheckAndTrigger(response, _configManager, _logger);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException();

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var error = await TryReadErrorAsync(response, ct);
            if (string.Equals(error?.ErrorCode, DecommissionedCode, StringComparison.OrdinalIgnoreCase))
                throw new DeviceDecommissionedException(error?.Message ?? "Device decommissioned");

            throw new HttpRequestException(error?.Message ?? "403 Forbidden");
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadErrorAsync(response, ct);
            throw new HttpRequestException(error?.Message ?? $"Diagnostic log upload failed with {(int)response.StatusCode}");
        }

        return request.LogEntries.Count;
    }

    private static async Task<ErrorResponse?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeLogHeader(string line) =>
        line.Length > 24
        && char.IsDigit(line[0])
        && char.IsDigit(line[1])
        && char.IsDigit(line[2])
        && char.IsDigit(line[3])
        && line[4] == '-'
        && line[7] == '-'
        && line[10] == ' ';

    private static bool IsDiagnosticSeverity(string line) =>
        line.Contains("[WRN]", StringComparison.Ordinal)
        || line.Contains("[ERR]", StringComparison.Ordinal)
        || line.Contains("[FTL]", StringComparison.Ordinal);
}

internal sealed record DiagnosticLogReadResult(
    IReadOnlyList<string> Entries,
    string? FilePath,
    int LastProcessedLineNumber)
{
    public static DiagnosticLogReadResult Empty(string? filePath, int lineNumber) =>
        new([], filePath, lineNumber);
}
