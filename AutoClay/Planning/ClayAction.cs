namespace AutoClay.Planning;

public readonly record struct ClayAction(int X, int Y, int Z, int ToolMode, bool Remove, int VoxelCount);
