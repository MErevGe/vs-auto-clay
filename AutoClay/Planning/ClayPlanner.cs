namespace AutoClay.Planning;

public static class ClayPlanner
{
    public const int Size = 16;

    public static ClayAction? Next(bool[,,] current, bool[,,] target, int availableVoxels)
    {
        Validate(current);
        Validate(target);
        int layer = FirstDifferentLayer(current, target);
        if (layer == Size) return null;

        int total = Count(current);
        ClayAction? removal = FindBrush(current, target, layer, true, total - 1);
        if (removal is not null) return removal;

        int budget = Math.Max(1, availableVoxels);
        ClayAction? addition = FindBrush(current, target, layer, false, budget, availableVoxels > 0 ? 2 : 0);
        ClayAction? copy = availableVoxels > 0 ? FindCopy(current, target, layer, budget) : null;
        return copy?.VoxelCount > (addition?.VoxelCount ?? 0) ? copy : addition;
    }

    public static int FirstDifferentLayer(bool[,,] current, bool[,,] target)
    {
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                    if (current[x, y, z] != target[x, y, z]) return y;
        return Size;
    }

    private static ClayAction? FindBrush(bool[,,] current, bool[,,] target, int layer, bool remove, int budget, int largestMode = 2)
    {
        ClayAction? best = null;
        var bounds = Bounds(target, layer);
        for (int mode = largestMode; mode >= 0; mode--)
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                {
                    int count = 0;
                    bool valid = true;
                    for (int dx = -(mode + 1) / 2; dx <= mode / 2 && valid; dx++)
                        for (int dz = -(mode + 1) / 2; dz <= mode / 2; dz++)
                        {
                            int px = x + dx;
                            int pz = z + dz;
                            if (px < 0 || px >= Size || pz < 0 || pz >= Size) continue;
                            if (!remove && (px < bounds.MinX || px > bounds.MaxX || pz < bounds.MinZ || pz > bounds.MaxZ)) continue;
                            if (current[px, layer, pz] != remove) continue;
                            if (target[px, layer, pz] == remove)
                            {
                                valid = false;
                                break;
                            }
                            count++;
                        }
                    if (valid && count > (best?.VoxelCount ?? 0) && count <= budget)
                        best = new ClayAction(x, layer, z, mode, remove, count);
                }
        return best;
    }

    private static ClayAction? FindCopy(bool[,,] current, bool[,,] target, int layer, int budget)
    {
        if (layer == 0) return null;
        int count = 0;
        for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            {
                if (!current[x, layer - 1, z] || current[x, layer, z]) continue;
                if (!target[x, layer, z]) return null;
                count++;
                if (count == 4) return count <= budget ? new ClayAction(x, layer, z, 3, false, count) : null;
            }
        return count > 0 && count <= budget ? new ClayAction(0, layer, 0, 3, false, count) : null;
    }

    private static (int MinX, int MaxX, int MinZ, int MaxZ) Bounds(bool[,,] target, int layer)
    {
        int minX = 8, maxX = 8, minZ = 8, maxZ = 8;
        for (int x = 0; x < Size; x++)
            for (int z = 0; z < Size; z++)
            {
                if (!target[x, layer, z]) continue;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        return (minX, maxX, minZ, maxZ);
    }

    private static int Count(bool[,,] voxels)
    {
        int count = 0;
        foreach (bool voxel in voxels) if (voxel) count++;
        return count;
    }

    private static void Validate(bool[,,] voxels)
    {
        ArgumentNullException.ThrowIfNull(voxels);
        if (voxels.GetLength(0) != Size || voxels.GetLength(1) != Size || voxels.GetLength(2) != Size)
            throw new ArgumentException("Clay forms must contain 16 × 16 × 16 voxels.", nameof(voxels));
    }
}
