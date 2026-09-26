namespace AutoClay.Planning;

public sealed class PendingBatch(bool[,,] voxels, int availableVoxels, int recipeId, long sentAt)
{
    private readonly bool[,,] expected = (bool[,,])voxels.Clone();

    public bool Matches(bool[,,] actual, int actualAvailable, int actualRecipeId)
    {
        return actualAvailable == availableVoxels && actualRecipeId == recipeId
            && ClayPlanner.FirstDifferentLayer(expected, actual) == ClayPlanner.Size;
    }

    public bool HasTimedOut(long now, int timeoutMilliseconds) => now - sentAt >= timeoutMilliseconds;
}
