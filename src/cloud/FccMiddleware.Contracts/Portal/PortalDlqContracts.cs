using System.Text.Json;

namespace FccMiddleware.Contracts.Portal;

public record DeadLetterDto
{
    public required Guid Id { get; init; }
    public required string Type { get; init; }
    public required string SiteCode { get; init; }
    public required Guid LegalEntityId { get; init; }
    public string? FccTransactionId { get; init; }
    public string? RawPayloadRef { get; init; }
    public required string FailureReason { get; init; }
    public required string ErrorCode { get; init; }
    public required string ErrorMessage { get; init; }
    public required string Status { get; init; }
    public required int RetryCount { get; init; }
    public DateTimeOffset? LastRetryAt { get; init; }
    public string? DiscardReason { get; init; }
    public string? DiscardedBy { get; init; }
    public DateTimeOffset? DiscardedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

public sealed record RetryHistoryEntryDto
{
    public required int AttemptNumber { get; init; }
    public required DateTimeOffset AttemptedAt { get; init; }
    public required string Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public record DeadLetterDetailDto : DeadLetterDto
{
    public JsonElement? RawPayload { get; init; }
    public required IReadOnlyList<RetryHistoryEntryDto> RetryHistory { get; init; }
}

public sealed record DiscardRequestDto
{
    public required string Reason { get; init; }
}

public sealed record RetryResultDto
{
    public required Guid Id { get; init; }
    public required bool Queued { get; init; }
    public object? Error { get; init; }
}

public sealed record RetryBatchRequestDto
{
    public required IReadOnlyList<Guid> Ids { get; init; }
}

public sealed record BatchRetryResultDto
{
    public required IReadOnlyList<Guid> Succeeded { get; init; }
    public required IReadOnlyList<BatchRetryFailureDto> Failed { get; init; }
}

public sealed record BatchRetryFailureDto
{
    public required Guid Id { get; init; }
    public required string Error { get; init; }
}

public sealed record DiscardBatchRequestDto
{
    public required IReadOnlyList<BatchDiscardItemDto> Items { get; init; }
}

public sealed record BatchDiscardItemDto
{
    public required Guid Id { get; init; }
    public required string Reason { get; init; }
}
