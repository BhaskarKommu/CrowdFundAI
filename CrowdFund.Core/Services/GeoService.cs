namespace CrowdFund.Core.Services;

public static class GeoService
{
    /// <summary>Haversine distance in kilometers.</summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        static double Rad(double d) => d * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }

    public static string? BuildLabel(string? city, string? region, string? country)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(city)) parts.Add(city.Trim());
        if (!string.IsNullOrWhiteSpace(region)) parts.Add(region.Trim());
        if (!string.IsNullOrWhiteSpace(country)) parts.Add(country.Trim().ToUpperInvariant());
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public static bool ValidCoords(double? lat, double? lon) =>
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
}
