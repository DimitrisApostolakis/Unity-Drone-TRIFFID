using System;
using UnityEngine;

public static class GeoUtils
{
    public static Vector3 GetColmapPosition(double lon, double lat, double alt, TransformConfig transformData)
    {
        if (!IsFinite(lon) || !IsFinite(lat) || !IsFinite(alt) || !IsValidTransformData(transformData))
            return Vector3.zero;

        Vector3 enuPos = Wgs84ToENU(lat, lon, alt,
            transformData.origin_wgs84.lat,
            transformData.origin_wgs84.lon,
            transformData.origin_wgs84.alt);

        float s = transformData.colmap_to_enu.scale;
        float[] r = transformData.colmap_to_enu.R_rowmajor;
        float[] t = transformData.colmap_to_enu.t;

        float e_t = enuPos.x - t[0];
        float n_t = enuPos.y - t[1];
        float u_t = enuPos.z - t[2];
        float colX = (r[0] * e_t + r[3] * n_t + r[6] * u_t) / s;
        float colY = (r[1] * e_t + r[4] * n_t + r[7] * u_t) / s;
        float colZ = (r[2] * e_t + r[5] * n_t + r[8] * u_t) / s;

        return new Vector3(colX, colY, colZ);
    }

    public static void ColmapToWgs84(Vector3 colmapPos, TransformConfig transformData, out double lon, out double lat, out double alt)
    {
        lon = 0.0;
        lat = 0.0;
        alt = 0.0;

        if (!IsFinite(colmapPos.x) || !IsFinite(colmapPos.y) || !IsFinite(colmapPos.z) ||
            !IsValidTransformData(transformData))
            return;

        float s = transformData.colmap_to_enu.scale;
        float[] r = transformData.colmap_to_enu.R_rowmajor;
        float[] t = transformData.colmap_to_enu.t;

        double enuE = s * (r[0] * colmapPos.x + r[1] * colmapPos.y + r[2] * colmapPos.z) + t[0];
        double enuN = s * (r[3] * colmapPos.x + r[4] * colmapPos.y + r[5] * colmapPos.z) + t[1];
        double enuU = s * (r[6] * colmapPos.x + r[7] * colmapPos.y + r[8] * colmapPos.z) + t[2];

        OriginWgs84 origin = transformData.origin_wgs84;
        EnuToEcef(enuE, enuN, enuU, origin.lat, origin.lon, origin.alt, out double x, out double y, out double z);
        EcefToWgs84(x, y, z, out lon, out lat, out alt);
    }

    public static bool IsValidTransformData(TransformConfig transformData)
    {
        if (transformData == null || transformData.origin_wgs84 == null || transformData.colmap_to_enu == null)
            return false;

        OriginWgs84 origin = transformData.origin_wgs84;
        ColmapToEnu transform = transformData.colmap_to_enu;

        if (!IsFinite(origin.lat) || !IsFinite(origin.lon) || !IsFinite(origin.alt))
            return false;

        if (!IsFinite(transform.scale) || transform.scale == 0f)
            return false;

        return HasFiniteValues(transform.R_rowmajor, 9) && HasFiniteValues(transform.t, 3);
    }

    private static bool HasFiniteValues(float[] values, int requiredLength)
    {
        if (values == null || values.Length < requiredLength)
            return false;

        for (int i = 0; i < requiredLength; i++)
        {
            if (!IsFinite(values[i]))
                return false;
        }

        return true;
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static void EnuToEcef(double e, double n, double u, double refLat, double refLon, double refAlt,
        out double x, out double y, out double z)
    {
        double phi = refLat * Math.PI / 180.0;
        double lam = refLon * Math.PI / 180.0;
        double sinPhi = Math.Sin(phi);
        double cosPhi = Math.Cos(phi);
        double sinLam = Math.Sin(lam);
        double cosLam = Math.Cos(lam);

        Wgs84ToEcef(refLat, refLon, refAlt, out double refX, out double refY, out double refZ);

        x = refX - sinLam * e - sinPhi * cosLam * n + cosPhi * cosLam * u;
        y = refY + cosLam * e - sinPhi * sinLam * n + cosPhi * sinLam * u;
        z = refZ + cosPhi * n + sinPhi * u;
    }

    private static void EcefToWgs84(double x, double y, double z, out double lon, out double lat, out double alt)
    {
        double a = 6378137.0;
        double f = 1.0 / 298.257223563;
        double b = a * (1.0 - f);
        double e_sq = 2.0 * f - f * f;
        double ep_sq = (a * a - b * b) / (b * b);

        double p = Math.Sqrt(x * x + y * y);
        double th = Math.Atan2(a * z, b * p);

        lon = Math.Atan2(y, x);
        lat = Math.Atan2(z + ep_sq * b * Math.Pow(Math.Sin(th), 3), p - e_sq * a * Math.Pow(Math.Cos(th), 3));

        double n = a / Math.Sqrt(1.0 - e_sq * Math.Pow(Math.Sin(lat), 2));
        alt = p / Math.Cos(lat) - n;

        lon = lon * 180.0 / Math.PI;
        lat = lat * 180.0 / Math.PI;
    }

    private static Vector3 Wgs84ToENU(double lat, double lon, double alt, double refLat, double refLon, double refAlt)
    {
        Wgs84ToEcef(lat, lon, alt, out double targetX, out double targetY, out double targetZ);
        Wgs84ToEcef(refLat, refLon, refAlt, out double refX, out double refY, out double refZ);

        double dx = targetX - refX;
        double dy = targetY - refY;
        double dz = targetZ - refZ;

        double phi = refLat * Math.PI / 180.0;
        double lam = refLon * Math.PI / 180.0;
        double sinPhi = Math.Sin(phi);
        double cosPhi = Math.Cos(phi);
        double sinLam = Math.Sin(lam);
        double cosLam = Math.Cos(lam);

        double e = -sinLam * dx + cosLam * dy;
        double n = -sinPhi * cosLam * dx - sinPhi * sinLam * dy + cosPhi * dz;
        double u = cosPhi * cosLam * dx + cosPhi * sinLam * dy + sinPhi * dz;

        return new Vector3((float)e, (float)n, (float)u);
    }

    private static void Wgs84ToEcef(double lat, double lon, double alt, out double x, out double y, out double z)
    {
        double a = 6378137.0;
        double e_sq = 0.00669437999014;
        double phi = lat * Math.PI / 180.0;
        double lam = lon * Math.PI / 180.0;
        double sinPhi = Math.Sin(phi);
        double cosPhi = Math.Cos(phi);
        double sinLam = Math.Sin(lam);
        double cosLam = Math.Cos(lam);

        double n = a / Math.Sqrt(1.0 - e_sq * sinPhi * sinPhi);
        x = (n + alt) * cosPhi * cosLam;
        y = (n + alt) * cosPhi * sinLam;
        z = (n * (1.0 - e_sq) + alt) * sinPhi;
    }
}
