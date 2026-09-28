using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Composition;

internal static class SessionAdminQuery
{
    public static bool MatchesSessionQuery(
        Session session,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(session.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(session.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && session.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && session.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && session.State != state)
            return false;

        if (!string.IsNullOrWhiteSpace(query.OwnerAccountId) &&
            !string.Equals(session.OwnerAccountId, query.OwnerAccountId, StringComparison.Ordinal))
            return false;

        var metadata = metadataById.TryGetValue(session.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = session.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return session.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || session.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(session.StableSessionBinding?.ExternalSessionId) &&
                session.StableSessionBinding.ExternalSessionId.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(session.StableSessionBinding?.Namespace) &&
                session.StableSessionBinding.Namespace.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(session.StableSessionBinding?.OwnerKey) &&
                session.StableSessionBinding.OwnerKey.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesSummaryQuery(
        SessionSummary summary,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById)
    {
        if (!string.IsNullOrWhiteSpace(query.ChannelId) &&
            !string.Equals(summary.ChannelId, query.ChannelId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.IsNullOrWhiteSpace(query.SenderId) &&
            !string.Equals(summary.SenderId, query.SenderId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (query.FromUtc is { } fromUtc && summary.LastActiveAt < fromUtc)
            return false;

        if (query.ToUtc is { } toUtc && summary.LastActiveAt > toUtc)
            return false;

        if (query.State is { } state && summary.State != state)
            return false;

        if (!string.IsNullOrWhiteSpace(query.OwnerAccountId) &&
            !string.Equals(summary.OwnerAccountId, query.OwnerAccountId, StringComparison.Ordinal))
            return false;

        var metadata = metadataById.TryGetValue(summary.Id, out var storedMetadata)
            ? storedMetadata
            : new SessionMetadataSnapshot { SessionId = summary.Id, Starred = false, Tags = [] };

        if (query.Starred is { } starred && metadata.Starred != starred)
            return false;

        if (!string.IsNullOrWhiteSpace(query.Tag) &&
            !metadata.Tags.Contains(query.Tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query.Search))
            return true;

        return summary.Id.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.ChannelId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || summary.SenderId.Contains(query.Search, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(summary.StableSessionId) &&
                summary.StableSessionId.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(summary.StableSessionNamespace) &&
                summary.StableSessionNamespace.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(summary.StableSessionOwnerKey) &&
                summary.StableSessionOwnerKey.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            || metadata.Tags.Any(tag => tag.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SessionAdminPersistedListing
{
    /// <summary>
    /// Starred/tag live in <see cref="SessionMetadataStore"/>, not in <see cref="ISessionAdminStore"/>.
    /// Paginating in the store first then filtering would shrink pages; load all store pages, filter, then page.
    /// </summary>
    public static bool NeedsMetadataAwarePersistedPagination(SessionListQuery query)
        => query.Starred is not null || !string.IsNullOrWhiteSpace(query.Tag);

    public static async ValueTask<PagedSessionList> ListPersistedAsync(
        ISessionAdminStore store,
        int page,
        int pageSize,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById,
        CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);

        if (!NeedsMetadataAwarePersistedPagination(query))
        {
            var persisted = await store.ListSessionsAsync(page, pageSize, query, ct);
            return new PagedSessionList
            {
                Page = persisted.Page,
                PageSize = persisted.PageSize,
                HasMore = persisted.HasMore,
                Items = persisted.Items
                    .Where(item => SessionAdminQuery.MatchesSummaryQuery(item, query, metadataById))
                    .ToArray()
            };
        }

        var matches = await ListAllMatchingSummariesAsync(store, query, metadataById, ct);
        var skip = (page - 1) * pageSize;
        var pageItems = matches.Skip(skip).Take(pageSize).ToList();
        return new PagedSessionList
        {
            Page = page,
            PageSize = pageSize,
            HasMore = matches.Count > skip + pageSize,
            Items = pageItems
        };
    }

    /// <summary>
    /// All persisted summaries that pass <see cref="SessionAdminQuery.MatchesSummaryQuery"/> (store filters + metadata/search).
    /// </summary>
    public static async ValueTask<IReadOnlyList<SessionSummary>> ListAllMatchingSummariesAsync(
        ISessionAdminStore store,
        SessionListQuery query,
        IReadOnlyDictionary<string, SessionMetadataSnapshot> metadataById,
        CancellationToken ct)
    {
        var matches = new List<SessionSummary>();
        var batchPage = 1;
        const int batchSize = 200;
        while (true)
        {
            var batch = await store.ListSessionsAsync(batchPage, batchSize, query, ct);
            foreach (var item in batch.Items)
            {
                if (SessionAdminQuery.MatchesSummaryQuery(item, query, metadataById))
                    matches.Add(item);
            }

            if (!batch.HasMore)
                break;
            batchPage++;
        }

        return matches;
    }
}
