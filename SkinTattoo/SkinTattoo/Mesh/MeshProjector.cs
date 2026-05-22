using System;
using System.Numerics;

namespace SkinTattoo.Mesh;

/// <summary>
/// Per-texel callback for <see cref="MeshProjector.Rasterize"/>.
/// (lu, lv) are decal-local coordinates in [-0.5, 0.5].
/// </summary>
public delegate void ProjectorTexelPainter(int px, int py, float lu, float lv);

/// <summary>
/// Rasterizes a 3D oriented-box projector onto a texture by walking mesh triangles
/// and projecting their UV-space coverage into the projector's local frame.
///
/// This is the mesh-aware replacement for the UV-quad scan used by legacy decals.
/// A single 3D placement naturally scatters across all UV islands that cover the
/// projector's 3D footprint, including mirrored / stitched layouts.
/// </summary>
public static class MeshProjector
{
    /// <summary>
    /// Iterate every texel touched by the projector and invoke <paramref name="paint"/>
    /// with the decal-local coords. Triangle filtering:
    ///   1. World-AABB intersection (projector OBB AABB vs triangle AABB).
    ///   2. Front-face test: averaged vertex normal must face the projector normal.
    ///   3. UV-tile sanity (skip triangles that span multiple UDIM tiles after fract).
    /// Inside each candidate triangle we scan its UV bbox, do a half-plane test, and
    /// barycentric-interpolate the 3D position to reconstruct the texel's world point.
    /// </summary>
    public static void Rasterize(
        MeshData mesh, int texW, int texH,
        Vector3 projOrigin, Vector3 projNormal, Vector3 projTangent,
        Vector2 projSize, float projDepth, float rotRad,
        ProjectorTexelPainter paint,
        float backfaceCutoff = 0.2f,
        Action<int>? perTriangleLod = null,
        float decalRefWidth = 0f, float decalRefHeight = 0f, int maxLod = 6)
    {
        if (mesh == null || mesh.TriangleCount == 0) return;
        if (texW <= 0 || texH <= 0) return;
        if (projSize.X <= 1e-6f || projSize.Y <= 1e-6f) return;
        if (projDepth <= 0f) return;

        if (!BuildFrame(projNormal, projTangent, rotRad, out var n, out var t, out var b)) return;

        float halfX = projSize.X * 0.5f;
        float halfY = projSize.Y * 0.5f;
        float halfZ = projDepth;

        // World AABB of the oriented box (for cheap triangle reject)
        float ex = MathF.Abs(t.X) * halfX + MathF.Abs(b.X) * halfY + MathF.Abs(n.X) * halfZ;
        float ey = MathF.Abs(t.Y) * halfX + MathF.Abs(b.Y) * halfY + MathF.Abs(n.Y) * halfZ;
        float ez = MathF.Abs(t.Z) * halfX + MathF.Abs(b.Z) * halfY + MathF.Abs(n.Z) * halfZ;
        var boxMin = projOrigin - new Vector3(ex, ey, ez);
        var boxMax = projOrigin + new Vector3(ex, ey, ez);

        var verts = mesh.Vertices;
        var indices = mesh.Indices;
        int triCount = indices.Length / 3;
        float invSizeX = 1f / projSize.X;
        float invSizeY = 1f / projSize.Y;

        for (int tIdx = 0; tIdx < triCount; tIdx++)
        {
            int i0 = indices[tIdx * 3];
            int i1 = indices[tIdx * 3 + 1];
            int i2 = indices[tIdx * 3 + 2];
            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;

            var p0 = verts[i0].Position;
            var p1 = verts[i1].Position;
            var p2 = verts[i2].Position;

            // Triangle AABB vs projector AABB
            float triMinX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X));
            float triMaxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
            if (triMaxX < boxMin.X || triMinX > boxMax.X) continue;
            float triMinY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y));
            float triMaxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
            if (triMaxY < boxMin.Y || triMinY > boxMax.Y) continue;
            float triMinZ = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z));
            float triMaxZ = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
            if (triMaxZ < boxMin.Z || triMinZ > boxMax.Z) continue;

            // Backface test via averaged vertex normal. Vertex normals point outward
            // from the surface in FFXIV models; a triangle facing the projector has
            // an outward normal roughly parallel to projector normal.
            var avgN = verts[i0].Normal + verts[i1].Normal + verts[i2].Normal;
            // Reject grazing-angle triangles (cosine of angle vs projector normal
            // below the layer's configured cutoff). Higher cutoff = tighter alignment
            // required = fewer UV-overlap ghost artifacts but less wrap on curves.
            if (Vector3.Dot(avgN, n) < backfaceCutoff) continue;

            // Per-vertex UV fract (UDIM normalization). Skip triangles that straddle
            // a tile boundary  -- fract would tear them.
            var uv0 = Fract(verts[i0].UV);
            var uv1 = Fract(verts[i1].UV);
            var uv2 = Fract(verts[i2].UV);
            float uvMaxX = MathF.Max(uv0.X, MathF.Max(uv1.X, uv2.X));
            float uvMinX = MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X));
            if (uvMaxX - uvMinX > 0.5f) continue;
            float uvMaxY = MathF.Max(uv0.Y, MathF.Max(uv1.Y, uv2.Y));
            float uvMinY = MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y));
            if (uvMaxY - uvMinY > 0.5f) continue;

            // Expand bbox by 1 texel so conservative rasterization can include texels
            // whose center is just outside the triangle but whose footprint overlaps.
            int pxMin = Math.Max(0, (int)MathF.Floor(uvMinX * texW) - 1);
            int pxMax = Math.Min(texW - 1, (int)MathF.Ceiling(uvMaxX * texW) + 1);
            int pyMin = Math.Max(0, (int)MathF.Floor(uvMinY * texH) - 1);
            int pyMax = Math.Min(texH - 1, (int)MathF.Ceiling(uvMaxY * texH) + 1);
            if (pxMax < pxMin || pyMax < pyMin) continue;

            // Barycentric basis in UV space: solve (u,v) = uv0 + w1*(uv1-uv0) + w2*(uv2-uv0)
            var e1 = uv1 - uv0;
            var e2 = uv2 - uv0;
            float det = e1.X * e2.Y - e1.Y * e2.X;
            if (MathF.Abs(det) < 1e-14f) continue;
            float invDet = 1f / det;

            float invW = 1f / texW;
            float invH = 1f / texH;

            // Conservative-rasterization tolerance: per-axis barycentric gradient
            // magnitude * 0.5 texel. Accepting w_i in [-tol_i, ...] catches texels
            // whose corner touches the triangle even when the center is outside,
            // closing single-texel pinholes at UV island borders.
            float gw1X = e2.Y * invDet * invW;
            float gw1Y = -e2.X * invDet * invH;
            float gw2X = -e1.Y * invDet * invW;
            float gw2Y = e1.X * invDet * invH;
            float tolW1 = 0.5f * (MathF.Abs(gw1X) + MathF.Abs(gw1Y));
            float tolW2 = 0.5f * (MathF.Abs(gw2X) + MathF.Abs(gw2Y));
            float tolW0 = 0.5f * (MathF.Abs(gw1X + gw2X) + MathF.Abs(gw1Y + gw2Y));

            // Per-triangle LOD: compute d(lu,lv)/d(texel) via chain rule and pick
            // mip based on max-axis decal-pixels-per-texel. Replaces per-layer LOD
            // estimates so each triangle uses the right mip even when UV stretch
            // varies sharply at seams.
            if (perTriangleLod != null && (decalRefWidth > 0f || decalRefHeight > 0f))
            {
                var pe1 = p1 - p0;
                var pe2 = p2 - p0;
                float A = t.X * pe1.X + t.Y * pe1.Y + t.Z * pe1.Z;
                float B = t.X * pe2.X + t.Y * pe2.Y + t.Z * pe2.Z;
                float C = b.X * pe1.X + b.Y * pe1.Y + b.Z * pe1.Z;
                float D = b.X * pe2.X + b.Y * pe2.Y + b.Z * pe2.Z;
                float invSizeX2 = invSizeX;
                float invSizeY2 = invSizeY;
                float dLu_dUC = (A * e2.Y - B * e1.Y) * invDet * invSizeX2;
                float dLu_dVC = (B * e1.X - A * e2.X) * invDet * invSizeX2;
                float dLv_dUC = (C * e2.Y - D * e1.Y) * invDet * invSizeY2;
                float dLv_dVC = (D * e1.X - C * e2.X) * invDet * invSizeY2;
                float du_dx = dLu_dUC * decalRefWidth * invW;
                float dv_dx = dLv_dUC * decalRefHeight * invW;
                float du_dy = dLu_dVC * decalRefWidth * invH;
                float dv_dy = dLv_dVC * decalRefHeight * invH;
                float rhoX = MathF.Sqrt(du_dx * du_dx + dv_dx * dv_dx);
                float rhoY = MathF.Sqrt(du_dy * du_dy + dv_dy * dv_dy);
                float rho = MathF.Max(rhoX, rhoY);
                int lod = rho > 1.5f ? Math.Clamp((int)MathF.Floor(MathF.Log2(rho)), 0, maxLod) : 0;
                perTriangleLod(lod);
            }

            for (int py = pyMin; py <= pyMax; py++)
            {
                float vCoord = (py + 0.5f) * invH;
                float dy = vCoord - uv0.Y;
                for (int px = pxMin; px <= pxMax; px++)
                {
                    float uCoord = (px + 0.5f) * invW;
                    float dx = uCoord - uv0.X;
                    float w1 = (dx * e2.Y - dy * e2.X) * invDet;
                    if (w1 < -tolW1 || w1 > 1f + tolW1) continue;
                    float w2 = (dy * e1.X - dx * e1.Y) * invDet;
                    if (w2 < -tolW2) continue;
                    float w0 = 1f - w1 - w2;
                    if (w0 < -tolW0) continue;

                    // Clamp barycentric to [0,1] so 3D reconstruction stays within
                    // the triangle plane; the texel was accepted because its
                    // footprint touches the triangle, but the center sample point
                    // shouldn't extrapolate past the triangle edge.
                    if (w0 < 0f) { w0 = 0f; }
                    else if (w0 > 1f) { w0 = 1f; }
                    if (w1 < 0f) { w1 = 0f; }
                    else if (w1 > 1f) { w1 = 1f; }
                    if (w2 < 0f) { w2 = 0f; }
                    else if (w2 > 1f) { w2 = 1f; }
                    float wSum = w0 + w1 + w2;
                    if (wSum < 1e-6f) continue;
                    if (wSum != 1f) { w0 /= wSum; w1 /= wSum; w2 /= wSum; }

                    var pos = w0 * p0 + w1 * p1 + w2 * p2;
                    var rel = pos - projOrigin;
                    float ln = rel.X * n.X + rel.Y * n.Y + rel.Z * n.Z;
                    if (ln < -halfZ || ln > halfZ) continue;
                    float lu = (rel.X * t.X + rel.Y * t.Y + rel.Z * t.Z) * invSizeX;
                    if (lu < -0.5f || lu > 0.5f) continue;
                    float lv = (rel.X * b.X + rel.Y * b.Y + rel.Z * b.Z) * invSizeY;
                    if (lv < -0.5f || lv > 0.5f) continue;

                    paint(px, py, lu, lv);
                }
            }
        }
    }

    /// <summary>
    /// Cheap UV-bbox estimate: union of UV-tile-fracted triangle bboxes that pass the
    /// 3D AABB + backface filter. Coarse upper bound -- covers ALL texels the projector
    /// could touch, possibly more. Used for dirty-rect tracking.
    /// </summary>
    public static Services.DirtyRect ComputeUvBbox(
        MeshData mesh, int texW, int texH,
        Vector3 projOrigin, Vector3 projNormal, Vector3 projTangent,
        Vector2 projSize, float projDepth, float rotRad,
        float backfaceCutoff = 0.2f)
    {
        if (mesh == null || mesh.TriangleCount == 0) return Services.DirtyRect.Empty;
        if (texW <= 0 || texH <= 0) return Services.DirtyRect.Empty;
        if (projSize.X <= 1e-6f || projSize.Y <= 1e-6f) return Services.DirtyRect.Empty;
        if (projDepth <= 0f) return Services.DirtyRect.Empty;

        if (!BuildFrame(projNormal, projTangent, rotRad, out var n, out var t, out var b))
            return Services.DirtyRect.Empty;

        float halfX = projSize.X * 0.5f;
        float halfY = projSize.Y * 0.5f;
        float halfZ = projDepth;
        float ex = MathF.Abs(t.X) * halfX + MathF.Abs(b.X) * halfY + MathF.Abs(n.X) * halfZ;
        float ey = MathF.Abs(t.Y) * halfX + MathF.Abs(b.Y) * halfY + MathF.Abs(n.Y) * halfZ;
        float ez = MathF.Abs(t.Z) * halfX + MathF.Abs(b.Z) * halfY + MathF.Abs(n.Z) * halfZ;
        var boxMin = projOrigin - new Vector3(ex, ey, ez);
        var boxMax = projOrigin + new Vector3(ex, ey, ez);

        var verts = mesh.Vertices;
        var indices = mesh.Indices;
        int triCount = indices.Length / 3;

        int pxLo = int.MaxValue, pyLo = int.MaxValue, pxHi = int.MinValue, pyHi = int.MinValue;

        for (int tIdx = 0; tIdx < triCount; tIdx++)
        {
            int i0 = indices[tIdx * 3];
            int i1 = indices[tIdx * 3 + 1];
            int i2 = indices[tIdx * 3 + 2];
            if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;

            var p0 = verts[i0].Position;
            var p1 = verts[i1].Position;
            var p2 = verts[i2].Position;

            float triMinX = MathF.Min(p0.X, MathF.Min(p1.X, p2.X));
            float triMaxX = MathF.Max(p0.X, MathF.Max(p1.X, p2.X));
            if (triMaxX < boxMin.X || triMinX > boxMax.X) continue;
            float triMinY = MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y));
            float triMaxY = MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y));
            if (triMaxY < boxMin.Y || triMinY > boxMax.Y) continue;
            float triMinZ = MathF.Min(p0.Z, MathF.Min(p1.Z, p2.Z));
            float triMaxZ = MathF.Max(p0.Z, MathF.Max(p1.Z, p2.Z));
            if (triMaxZ < boxMin.Z || triMinZ > boxMax.Z) continue;

            var avgN = verts[i0].Normal + verts[i1].Normal + verts[i2].Normal;
            // Reject grazing-angle triangles (cosine of angle vs projector normal
            // below the layer's configured cutoff). Higher cutoff = tighter alignment
            // required = fewer UV-overlap ghost artifacts but less wrap on curves.
            if (Vector3.Dot(avgN, n) < backfaceCutoff) continue;

            var uv0 = Fract(verts[i0].UV);
            var uv1 = Fract(verts[i1].UV);
            var uv2 = Fract(verts[i2].UV);
            float uvMaxX = MathF.Max(uv0.X, MathF.Max(uv1.X, uv2.X));
            float uvMinX = MathF.Min(uv0.X, MathF.Min(uv1.X, uv2.X));
            if (uvMaxX - uvMinX > 0.5f) continue;
            float uvMaxY = MathF.Max(uv0.Y, MathF.Max(uv1.Y, uv2.Y));
            float uvMinY = MathF.Min(uv0.Y, MathF.Min(uv1.Y, uv2.Y));
            if (uvMaxY - uvMinY > 0.5f) continue;

            // 1-texel expansion matches the conservative rasterization in Rasterize().
            int aPxMin = Math.Max(0, (int)MathF.Floor(uvMinX * texW) - 1);
            int aPxMax = Math.Min(texW - 1, (int)MathF.Ceiling(uvMaxX * texW) + 1);
            int aPyMin = Math.Max(0, (int)MathF.Floor(uvMinY * texH) - 1);
            int aPyMax = Math.Min(texH - 1, (int)MathF.Ceiling(uvMaxY * texH) + 1);
            if (aPxMax < aPxMin || aPyMax < aPyMin) continue;

            if (aPxMin < pxLo) pxLo = aPxMin;
            if (aPyMin < pyLo) pyLo = aPyMin;
            if (aPxMax > pxHi) pxHi = aPxMax;
            if (aPyMax > pyHi) pyHi = aPyMax;
        }

        if (pxHi < pxLo || pyHi < pyLo) return Services.DirtyRect.Empty;
        return new Services.DirtyRect(pxLo, pyLo, pxHi - pxLo + 1, pyHi - pyLo + 1);
    }

    private static Vector2 Fract(Vector2 uv)
        => new(uv.X - MathF.Floor(uv.X), uv.Y - MathF.Floor(uv.Y));

    /// <summary>
    /// Build an orthonormal projector frame (t, b, n). Gram-Schmidt the input tangent
    /// against the normal, fall back to a stable perpendicular when tangent is degenerate,
    /// then rotate tangent around the normal by <paramref name="rotRad"/>.
    /// </summary>
    private static bool BuildFrame(Vector3 projNormal, Vector3 projTangent, float rotRad,
        out Vector3 n, out Vector3 t, out Vector3 b)
    {
        n = default; t = default; b = default;
        if (projNormal.LengthSquared() < 1e-8f) return false;
        n = Vector3.Normalize(projNormal);

        var tIn = projTangent - Vector3.Dot(projTangent, n) * n;
        if (tIn.LengthSquared() < 1e-8f)
        {
            var fallback = MathF.Abs(n.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            tIn = fallback - Vector3.Dot(fallback, n) * n;
        }
        tIn = Vector3.Normalize(tIn);
        var bIn = Vector3.Cross(n, tIn);

        float cosR = MathF.Cos(rotRad);
        float sinR = MathF.Sin(rotRad);
        t = cosR * tIn + sinR * bIn;
        b = Vector3.Cross(n, t);
        return true;
    }
}
