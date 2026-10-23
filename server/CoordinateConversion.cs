namespace StreetEcoTool;

internal static class CoordinateConversion
{
    // Imported AMap coordinates are GCJ-02; store geographic WGS84 coordinates.
    internal static double[] ToWgs84(double[] point)
    {
        var lon = point[0]; var lat = point[1];
        if (lon < 72.004 || lon > 137.8347 || lat < 0.8293 || lat > 55.8271) return [lon, lat];
        var x = lon; var y = lat;
        for (var i = 0; i < 6; i++) { var shifted = ToGcj02(x, y); x -= shifted[0] - lon; y -= shifted[1] - lat; }
        return [Math.Round(x, 8), Math.Round(y, 8)];
    }
    private static double[] ToGcj02(double lon, double lat)
    {
        const double a = 6378245.0, ee = 0.00669342162296594323;
        var x = lon - 105; var y = lat - 35; var pi = Math.PI;
        var dlat = -100 + 2*x + 3*y + 0.2*y*y + 0.1*x*y + 0.2*Math.Sqrt(Math.Abs(x));
        dlat += (20*Math.Sin(6*x*pi)+20*Math.Sin(2*x*pi))*2/3;
        dlat += (20*Math.Sin(y*pi)+40*Math.Sin(y/3*pi))*2/3;
        dlat += (160*Math.Sin(y/12*pi)+320*Math.Sin(y*pi/30))*2/3;
        var dlon = 300+x+2*y+0.1*x*x+0.1*x*y+0.1*Math.Sqrt(Math.Abs(x));
        dlon += (20*Math.Sin(6*x*pi)+20*Math.Sin(2*x*pi))*2/3;
        dlon += (20*Math.Sin(x*pi)+40*Math.Sin(x/3*pi))*2/3;
        dlon += (150*Math.Sin(x/12*pi)+300*Math.Sin(x/30*pi))*2/3;
        var rad = lat/180*pi; var magic = 1-ee*Math.Sin(rad)*Math.Sin(rad); var sqrt = Math.Sqrt(magic);
        return [lon+dlon*180/(a/sqrt*Math.Cos(rad)*pi),lat+dlat*180/(a*(1-ee)/(magic*sqrt)*pi)];
    }
}
