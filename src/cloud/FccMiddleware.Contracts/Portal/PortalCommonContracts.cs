namespace FccMiddleware.Contracts.Portal;

public sealed record PortalPageMeta
{
    public required int PageSize { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
    public int? TotalCount { get; init; }
}

public sealed record PortalPagedResult<T>
{
    public required IReadOnlyList<T> Data { get; init; }
    public required PortalPageMeta Meta { get; init; }
}
