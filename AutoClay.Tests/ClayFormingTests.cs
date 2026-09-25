using System.Reflection;
using AutoClay.Client;
using AutoClay.Planning;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Xunit;

namespace AutoClay.Tests;

public sealed class ClayFormingTests
{
    private static readonly MethodInfo Add = typeof(BlockEntityClayForm).GetMethod("OnAdd", BindingFlags.Instance | BindingFlags.NonPublic,
        [typeof(int), typeof(Vec3i), typeof(BlockFacing), typeof(int)])!;
    private static readonly MethodInfo Remove = typeof(BlockEntityClayForm).GetMethod("OnRemove", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo Copy = typeof(BlockEntityClayForm).GetMethod("OnCopyLayer", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static IEnumerable<object[]> Recipes => Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "recipes"), "*.json")
        .Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(Recipes))]
    public void CompletesEveryVanillaRecipeUsingRealGameBrushes(string path)
    {
        var json = JToken.Parse(File.ReadAllText(path));
        foreach (JToken entry in json is JArray array ? array : new JArray(json))
        {
            var recipe = new ClayFormingRecipe { Pattern = entry["pattern"]!.ToObject<string[][]>()! };
            recipe.GenVoxels();
            var form = CreateForm(recipe.Voxels);
            form.CreateInitialWorkItem();
            form.AvailableVoxels = 25;
            Complete(form, recipe.Voxels);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(42)]
    public void RepairsPartiallyWorkedFormsAndPreservesMaterial(int seed)
    {
        var random = new Random(seed);
        var target = new bool[16, 16, 16];
        var form = CreateForm(target);
        for (int y = 0; y < 4; y++)
            for (int x = 3; x < 12; x++)
                for (int z = 3; z < 12; z++)
                {
                    target[x, y, z] = random.Next(3) != 0;
                    form.Voxels[x, y, z] = random.Next(2) == 0;
                }
        Complete(form, target);
    }

    [Fact]
    public void DoesNotDestroyTheLastVoxelBeforeAddingReplacement()
    {
        var target = new bool[16, 16, 16];
        target[12, 0, 12] = true;
        var form = CreateForm(target);
        form.Voxels[1, 0, 1] = true;
        var action = ClayPlanner.Next(form.Voxels, target, 0);
        Assert.NotNull(action);
        Assert.False(action.Value.Remove);
        Assert.Equal(0, action.Value.ToolMode);
        Complete(form, target);
    }

    [Fact]
    public void UsesLargeBrushesWithoutFillingHoles()
    {
        var target = new bool[16, 16, 16];
        for (int x = 4; x < 7; x++)
            for (int z = 4; z < 7; z++) target[x, 0, z] = true;
        var current = new bool[16, 16, 16];
        Assert.Equal(9, ClayPlanner.Next(current, target, 25)!.Value.VoxelCount);
        target[5, 0, 5] = false;
        var form = CreateForm(target);
        form.Voxels[4, 0, 4] = true;
        Complete(form, target);
    }

    [Fact]
    public void UsesCopyForSparseRepeatedLayers()
    {
        var target = new bool[16, 16, 16];
        var current = new bool[16, 16, 16];
        foreach (int x in new[] { 2, 6, 10, 14 })
        {
            current[x, 0, 8] = true;
            target[x, 0, 8] = true;
            target[x, 1, 8] = true;
        }
        Assert.Equal(3, ClayPlanner.Next(current, target, 25)!.Value.ToolMode);
        Assert.NotEqual(3, ClayPlanner.Next(current, target, 1)!.Value.ToolMode);
        target[14, 1, 8] = false;
        Assert.NotEqual(3, ClayPlanner.Next(current, target, 25)!.Value.ToolMode);
    }

    [Fact]
    public void RejectsMalformedVoxelArrays()
    {
        Assert.Throws<ArgumentNullException>(() => ClayPlanner.Next(null!, new bool[16, 16, 16], 25));
        foreach (var dimensions in new[] { new[] { 15, 16, 16 }, new[] { 16, 15, 16 }, new[] { 16, 16, 15 } })
            Assert.Throws<ArgumentException>(() => ClayPlanner.Next(new bool[dimensions[0], dimensions[1], dimensions[2]], new bool[16, 16, 16], 25));
    }

    [Fact]
    public void WaitsForAnExactServerConfirmationAndTimesOut()
    {
        var voxels = new bool[16, 16, 16];
        var pending = new PendingBatch(voxels, 7, 3, 100);
        Assert.True(pending.Matches(voxels, 7, 3));
        Assert.False(pending.Matches(voxels, 6, 3));
        Assert.False(pending.Matches(voxels, 7, 4));
        voxels[4, 4, 4] = true;
        Assert.False(pending.Matches(voxels, 7, 3));
        Assert.False(pending.HasTimedOut(5099, 5000));
        Assert.True(pending.HasTimedOut(5100, 5000));
    }

    [Fact]
    public void BoundsConfigurationAndLoadsOnlyOnClient()
    {
        var config = new AutoClayConfig { ActionsPerBatch = int.MaxValue, ServerTimeoutMilliseconds = -1 };
        config.Validate();
        Assert.Equal(64, config.ActionsPerBatch);
        Assert.Equal(1000, config.ServerTimeoutMilliseconds);
        var mod = new AutoClayModSystem();
        Assert.True(mod.ShouldLoad(EnumAppSide.Client));
        Assert.False(mod.ShouldLoad(EnumAppSide.Server));
        mod.Dispose();
    }

    [Fact]
    public void HarmonyPatchesInstallAgainstVintageStory1227()
    {
        var harmony = new Harmony("autoclay.tests");
        try
        {
            harmony.PatchAll(typeof(AutoClayModSystem).Assembly);
            Assert.Equal(4, harmony.GetPatchedMethods().Count());
        }
        finally
        {
            harmony.UnpatchAll("autoclay.tests");
        }
    }

    private static BlockEntityClayForm CreateForm(bool[,,] target)
    {
        var form = new BlockEntityClayForm();
        typeof(BlockEntityClayForm).GetField("selectedRecipe", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(form, new ClayFormingRecipe { Voxels = target });
        return form;
    }

    private static void Complete(BlockEntityClayForm form, bool[,,] target)
    {
        int totalMaterial = form.Voxels.Cast<bool>().Count(voxel => voxel) + form.AvailableVoxels;
        for (int steps = 0; steps < 8192; steps++)
        {
            var next = ClayPlanner.Next(form.Voxels, target, form.AvailableVoxels);
            if (next == null)
            {
                Assert.Equal(16, ClayPlanner.FirstDifferentLayer(form.Voxels, target));
                return;
            }
            var action = next.Value;
            if (!action.Remove && form.AvailableVoxels <= 0)
            {
                form.AvailableVoxels += 25;
                totalMaterial += 25;
                Assert.Equal(0, action.ToolMode);
            }
            int before = form.AvailableVoxels;
            var position = new Vec3i(action.X, action.Y, action.Z);
            var method = action.Remove ? Remove : action.ToolMode == 3 ? Copy : Add;
            object[] args = action.ToolMode == 3 ? [action.Y] : [action.Y, position, BlockFacing.UP, action.ToolMode];
            Assert.Equal(true, method.Invoke(form, args));
            Assert.Equal(action.VoxelCount, Math.Abs(form.AvailableVoxels - before));
            Assert.True(form.AvailableVoxels >= 0);
            int count = form.Voxels.Cast<bool>().Count(voxel => voxel);
            Assert.True(count > 0);
            Assert.Equal(totalMaterial, count + form.AvailableVoxels);
        }
        Assert.Fail("The planner did not converge.");
    }
}
