using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LTAI.Tools.GIS;

public sealed class SpatialCache
{
    private readonly string _dbPath;
    private readonly ILogger<SpatialCache> _logger;
    private readonly ConcurrentDictionary<string, object?> _memoryCache = new();

    public SpatialCache(string? dbPath = null, ILogger<SpatialCache>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SpatialCache>.Instance;
        _dbPath = dbPath ?? Path.Combine(AppContext.BaseDirectory, ".livingtree", "spatial_cache.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS places (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                lat REAL, lng REAL,
                category TEXT,
                address TEXT,
                rating REAL,
                data TEXT,
                cached_at TEXT DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS routes (
                id TEXT PRIMARY KEY,
                origin TEXT, destination TEXT,
                mode TEXT DEFAULT 'driving',
                distance_m REAL, duration_s REAL,
                polyline TEXT, steps TEXT,
                data TEXT,
                cached_at TEXT DEFAULT (datetime('now'))
            );
            CREATE TABLE IF NOT EXISTS nearby (
                id TEXT PRIMARY KEY,
                reference_place TEXT,
                category TEXT,
                radius_m REAL,
                places TEXT,
                cached_at TEXT DEFAULT (datetime('now'))
            );
            CREATE INDEX IF NOT EXISTS idx_places_name ON places(name);
            CREATE INDEX IF NOT EXISTS idx_routes_od ON routes(origin, destination);
        """;
        cmd.ExecuteNonQuery();
        _logger.LogInformation("SpatialCache initialized at {Path}", _dbPath);
    }

    public SpatialPlace? GetPlace(string name)
    {
        var key = $"place:{name.ToLowerInvariant()}";
        if (_memoryCache.TryGetValue(key, out var cached) && cached is SpatialPlace p)
            return p;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM places WHERE name = @name COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("@name", name);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var place = new SpatialPlace
            {
                Name = reader.GetString(1),
                Lat = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                Lng = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                Category = reader.IsDBNull(4) ? null : reader.GetString(4),
                Address = reader.IsDBNull(5) ? null : reader.GetString(5),
                Rating = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                RawData = reader.IsDBNull(7) ? null : reader.GetString(7)
            };
            _memoryCache[key] = place;
            return place;
        }
        return null;
    }

    public void CachePlace(SpatialPlace place)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """INSERT OR REPLACE INTO places (id, name, lat, lng, category, address, rating, data) VALUES (@id, @name, @lat, @lng, @cat, @addr, @rating, @data)""";
        cmd.Parameters.AddWithValue("@id", $"place_{place.Name.ToLowerInvariant()}");
        cmd.Parameters.AddWithValue("@name", place.Name);
        cmd.Parameters.AddWithValue("@lat", place.Lat as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lng", place.Lng as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat", place.Category as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@addr", place.Address as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rating", place.Rating as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@data", place.RawData as object ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        _memoryCache[$"place:{place.Name.ToLowerInvariant()}"] = place;
    }

    public SpatialRoute? GetRoute(string origin, string destination, string mode = "driving")
    {
        var key = $"route:{origin}:{destination}:{mode}".ToLowerInvariant();
        if (_memoryCache.TryGetValue(key, out var cached) && cached is SpatialRoute r)
            return r;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM routes WHERE origin = @o AND destination = @d AND mode = @m LIMIT 1";
        cmd.Parameters.AddWithValue("@o", origin);
        cmd.Parameters.AddWithValue("@d", destination);
        cmd.Parameters.AddWithValue("@m", mode);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var route = new SpatialRoute
            {
                Origin = reader.GetString(1),
                Destination = reader.GetString(2),
                Mode = reader.GetString(3),
                DistanceMeters = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                DurationSeconds = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                Polyline = reader.IsDBNull(6) ? null : reader.GetString(6),
                Steps = reader.IsDBNull(7) ? null : reader.GetString(7)
            };
            _memoryCache[key] = route;
            return route;
        }
        return null;
    }

    public void CacheRoute(SpatialRoute route)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """INSERT OR REPLACE INTO routes (id, origin, destination, mode, distance_m, duration_s, polyline, steps) VALUES (@id, @o, @d, @m, @dist, @dur, @poly, @steps)""";
        var id = $"route_{route.Origin}_{route.Destination}_{route.Mode}".ToLowerInvariant();
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@o", route.Origin);
        cmd.Parameters.AddWithValue("@d", route.Destination);
        cmd.Parameters.AddWithValue("@m", route.Mode);
        cmd.Parameters.AddWithValue("@dist", route.DistanceMeters as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dur", route.DurationSeconds as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@poly", route.Polyline as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@steps", route.Steps as object ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        _memoryCache[$"route:{route.Origin}:{route.Destination}:{route.Mode}".ToLowerInvariant()] = route;
    }

    public List<string>? GetNearby(string referencePlace, string? category = null, double? radiusMeters = null)
    {
        var key = $"nearby:{referencePlace}:{category}:{radiusMeters}".ToLowerInvariant();
        if (_memoryCache.TryGetValue(key, out var cached) && cached is List<string> list)
            return list;

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT places FROM nearby WHERE reference_place = @ref AND (category = @cat OR (@cat IS NULL AND category IS NULL)) AND (radius_m = @rad OR (@rad IS NULL AND radius_m IS NULL)) LIMIT 1";
        cmd.Parameters.AddWithValue("@ref", referencePlace);
        cmd.Parameters.AddWithValue("@cat", category as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rad", radiusMeters as object ?? DBNull.Value);
        var result = cmd.ExecuteScalar() as string;
        if (result != null)
        {
            var places = JsonSerializer.Deserialize<List<string>>(result);
            _memoryCache[key] = places;
            return places;
        }
        return null;
    }

    public void CacheNearby(string referencePlace, List<string> places, string? category = null, double? radiusMeters = null)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """INSERT OR REPLACE INTO nearby (id, reference_place, category, radius_m, places) VALUES (@id, @ref, @cat, @rad, @places)""";
        var id = $"nearby_{referencePlace}_{category}_{radiusMeters}".ToLowerInvariant();
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@ref", referencePlace);
        cmd.Parameters.AddWithValue("@cat", category as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rad", radiusMeters as object ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@places", JsonSerializer.Serialize(places));
        cmd.ExecuteNonQuery();
        _memoryCache[$"nearby:{referencePlace}:{category}:{radiusMeters}".ToLowerInvariant()] = places;
    }

    public SpatialCacheStats GetStats()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT COUNT(*) FROM places), (SELECT COUNT(*) FROM routes), (SELECT COUNT(*) FROM nearby)";
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return new SpatialCacheStats(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }
}

public sealed record SpatialPlace
{
    public string Name { get; init; } = "";
    public double? Lat { get; init; }
    public double? Lng { get; init; }
    public string? Category { get; init; }
    public string? Address { get; init; }
    public double? Rating { get; init; }
    public string? RawData { get; init; }
}

public sealed record SpatialRoute
{
    public string Origin { get; init; } = "";
    public string Destination { get; init; } = "";
    public string Mode { get; init; } = "driving";
    public double? DistanceMeters { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Polyline { get; init; }
    public string? Steps { get; init; }
}

public sealed record SpatialCacheStats(int Places, int Routes, int Nearby);
