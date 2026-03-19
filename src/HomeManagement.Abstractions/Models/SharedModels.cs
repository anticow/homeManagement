namespace HomeManagement.Abstractions.Models;

// ── Shared / Common ──

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize);
