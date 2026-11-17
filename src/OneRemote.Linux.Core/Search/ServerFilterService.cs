using OneRemote.Linux.Core.Models;

namespace OneRemote.Linux.Core.Search;

public static class ServerFilterService
{
    public static List<ProtocolBase> FilterAndSort(
        IEnumerable<ProtocolBase> servers,
        string? searchQuery,
        string? protocolFilter = null,
        string? tagFilter = null)
    {
        var query = servers;

        if (!string.IsNullOrWhiteSpace(protocolFilter) && !string.Equals(protocolFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(s => string.Equals(s.Protocol, protocolFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tagFilter) && !string.Equals(tagFilter, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(s =>
                s.Tags.Any(t => string.Equals(t, tagFilter, StringComparison.OrdinalIgnoreCase)) ||
                s.TreeNodes.Any(t => string.Equals(t, tagFilter, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var tokens = searchQuery.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            query = query.Where(s => MatchesAllTokens(s, tokens));
        }

        return query
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s is ProtocolBaseWithAddressPort p ? p.Address : s.SubTitle, NaturalIpComparer.Instance)
            .ToList();
    }

    private static bool MatchesAllTokens(ProtocolBase server, string[] tokens)
    {
        var addr = (server as ProtocolBaseWithAddressPort)?.Address ?? string.Empty;
        var port = (server as ProtocolBaseWithAddressPort)?.Port ?? string.Empty;
        var user = (server as ProtocolBaseWithAddressPortUserPwd)?.UserName ?? string.Empty;

        foreach (var token in tokens)
        {
            var hit =
                server.DisplayName.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                addr.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                port.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                user.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                server.Protocol.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                server.Note.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                server.DataSourceBadge.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                server.Tags.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                server.TreeNodes.Any(t => t.Contains(token, StringComparison.OrdinalIgnoreCase));

            if (!hit)
                return false;
        }
        return true;
    }
}

public sealed class NaturalIpComparer : IComparer<string?>
{
    public static readonly NaturalIpComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var xParts = x.Split('.');
        var yParts = y.Split('.');
        if (xParts.Length == 4 && yParts.Length == 4 &&
            xParts.All(p => int.TryParse(p, out _)) &&
            yParts.All(p => int.TryParse(p, out _)))
        {
            for (var i = 0; i < 4; i++)
            {
                var cmp = int.Parse(xParts[i]).CompareTo(int.Parse(yParts[i]));
                if (cmp != 0) return cmp;
            }
            return 0;
        }

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
