using BARS_Client_V2.Domain;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BARS_Client_V2.Application;

public interface IAirportRepository
{
    Task<(IReadOnlyList<Airport> Items, int TotalCount)> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default);
}
