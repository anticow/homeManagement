namespace HomeManagement.AI.Abstractions.Contracts;

public interface ISummarizer
{
    Task<string> SummarizeAsync(string content, CancellationToken ct = default);
}
