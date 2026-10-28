using System.Text.Json;
namespace StreetEcoTool;
public sealed partial class Store
{
    private static GeoShape? ReadShape(string? json) => json is null ? null : JsonSerializer.Deserialize(json, AppJsonContext.Default.GeoShape);
    private static string? WriteShape(GeoShape? shape) => shape is null ? null : JsonSerializer.Serialize(shape, AppJsonContext.Default.GeoShape);
    private static GeoShape? NormalizeShape(GeoShape? shape, double? longitude = null, double? latitude = null)
    {
        if (shape is null)
        {
            var (lon, lat) = ValidateCoordinates(longitude, latitude);
            if (lon is null || lat is null) return null;
            shape = new("Point", [[lon.Value, lat.Value]]);
        }
        ValidateShape(shape);
        return shape;
    }
    public static void ValidateShape(GeoShape shape)
    {
        if (shape is null || shape.Type is not ("Point" or "Polygon") || shape.Coordinates is null || shape.Coordinates.Length > 1000) throw new ApiException(400, "地图标记格式不正确。");
        if (shape.Coordinates.Any(x => x is null || x.Length != 2 || !double.IsFinite(x[0]) || !double.IsFinite(x[1]) || x[0] < -180 || x[0] > 180 || x[1] < -85 || x[1] > 85)) throw new ApiException(400, "经纬度超出范围。");
        if (shape.Type == "Point" && shape.Coordinates.Length != 1) throw new ApiException(400, "请标记一个点。");
        if (shape.Type == "Polygon")
        {
            var ring = shape.Coordinates;
            if (ring.Length < 4 || !ring[0].SequenceEqual(ring[^1])) throw new ApiException(400, "区域至少需要三个点，并闭合边界。");
            var area = 0.0;
            for (var i = 0; i < ring.Length - 1; i++) area += ring[i][0] * ring[i + 1][1] - ring[i + 1][0] * ring[i][1];
            if (Math.Abs(area) < 1e-12) throw new ApiException(400, "区域不能只有一条线。");
            for (var i = 0; i < ring.Length - 1; i++)
            {
                if (ring[i].SequenceEqual(ring[i + 1])) throw new ApiException(400, "区域边界有重复点，请移除重复点。");
                for (var j = i + 2; j < ring.Length - 1; j++)
                {
                    if (i == 0 && j == ring.Length - 2) continue;
                    if (SegmentsIntersect(ring[i], ring[i + 1], ring[j], ring[j + 1])) throw new ApiException(400, "区域边界互相交叉，请调整交叉位置。");
                }
            }
        }
    }

    private static bool SegmentsIntersect(double[] a, double[] b, double[] c, double[] d)
    {
        static double Cross(double[] p, double[] q, double[] r) => (q[0]-p[0])*(r[1]-p[1])-(q[1]-p[1])*(r[0]-p[0]);
        static bool On(double[] p, double[] q, double[] r) => r[0]>=Math.Min(p[0],q[0]) && r[0]<=Math.Max(p[0],q[0]) && r[1]>=Math.Min(p[1],q[1]) && r[1]<=Math.Max(p[1],q[1]);
        var abC=Cross(a,b,c); var abD=Cross(a,b,d); var cdA=Cross(c,d,a); var cdB=Cross(c,d,b);
        return (abC*abD<0 && cdA*cdB<0) || (abC==0 && On(a,b,c)) || (abD==0 && On(a,b,d)) || (cdA==0 && On(c,d,a)) || (cdB==0 && On(c,d,b));
    }

}
