using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BARS_Client_V2.Application;
using BARS_Client_V2.Domain;

namespace BARS_Client_V2.Infrastructure.InMemory;

/// <summary>
/// Temporary in-memory airport repository until real backend integration.
/// </summary>
internal sealed class InMemoryAirportRepository : IAirportRepository
{
    private readonly List<Airport> _airports;

    public InMemoryAirportRepository()
    {
        _airports = new List<Airport>
        {
            new("KJFK", new []{ new SceneryPackage("Asobo Default"), new SceneryPackage("Drzewiecki Design KJFK"), }),
            new("EGLL", new []{ new SceneryPackage("Asobo Default"), new SceneryPackage("IniBuilds EGLL"), }),
            new("KLAX", new []{ new SceneryPackage("Asobo Default"), new SceneryPackage("IniBuilds KLAX"), }),
            new("KSEA", new []{ new SceneryPackage("Asobo Default") }),
            new("LFPG", new []{ new SceneryPackage("Asobo Default") }),
            new("EDDF", new []{ new SceneryPackage("Aerosoft EDDF") }),
            new("EDDM", new []{ new SceneryPackage("SimWings EDDM") }),
            new("CYYZ", new []{ new SceneryPackage("FSimStudios CYYZ") }),
            new("YSSY", new []{ new SceneryPackage("FlyTampa YSSY") }),
            new("KSFO", new []{ new SceneryPackage("FlightBeam KSFO"), new SceneryPackage("Asobo Default") }),
        };
    }

    public Task<(IReadOnlyList<Airport> Items, int TotalCount)> SearchAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        IEnumerable<Airport> q = _airports;
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim().ToUpperInvariant();
            q = q.Where(a => a.ICAO.Contains(search, StringComparison.OrdinalIgnoreCase) || a.SceneryPackages.Any(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }
        var total = q.Count();
        var items = q
            .OrderBy(a => a.ICAO)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();
        return Task.FromResult(((IReadOnlyList<Airport>)items, total));
    }
}
