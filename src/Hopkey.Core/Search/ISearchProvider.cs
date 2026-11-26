namespace Hopkey.Core.Search;

public interface ISearchProvider
{
    string Name { get; }
    bool CanHandle(SearchQuery q);
    /// <summary>Return candidate results (unsorted). Must honor cancellation; called on a thread-pool thread.</summary>
    IReadOnlyList<SearchResult> Search(SearchQuery q, int maxResults, CancellationToken ct);
}
