using System.Reflection;
using System.Runtime.CompilerServices;
using AutoClay.Client;
using HarmonyLib;
using NSubstitute;
using NSubstitute.Extensions;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AutoClay.Tests;

public sealed class ClientInteractionTests
{
    [Fact]
    public void RefillingUsesVanillaInventoryConsumptionAndOrderedServerPackets()
    {
        using var game = new GameFixture();
        game.Form.AvailableVoxels = 0;
        var action = Planning.ClayPlanner.Next(game.Form.Voxels, game.Recipe.Voxels, 0)!.Value;
        new ClayProtocol(game.Api).Execute(game.Form, game.Slot, action);
        Assert.Equal(3, game.Wire.Count);
        Assert.Equal((int)EnumHandInteractNw.StartHeldItemUse, ((Packet_Client)game.Wire[0]).HandInteraction.EnumHandInteract);
        Assert.Equal((int)EnumHandInteractNw.StopHeldItemUse, ((Packet_Client)game.Wire[1]).HandInteraction.EnumHandInteract);
        Assert.IsType<byte[]>(game.Wire[2]);
        Assert.Equal(9, game.Slot.StackSize);
        Assert.Equal(24, game.Form.AvailableVoxels);
        Assert.True(game.Form.Voxels[action.X, action.Y, action.Z]);
    }

    [Fact]
    public void LastClayItemIsConsumedWithoutFreeVoxelPlacement()
    {
        using var game = new GameFixture();
        game.Form.AvailableVoxels = 0;
        game.Slot.Itemstack!.StackSize = 1;
        var action = Planning.ClayPlanner.Next(game.Form.Voxels, game.Recipe.Voxels, 0)!.Value;
        new ClayProtocol(game.Api).Execute(game.Form, game.Slot, action);
        Assert.True(game.Slot.Empty);
        Assert.Equal(25, game.Form.AvailableVoxels);
        Assert.False(game.Form.Voxels[action.X, action.Y, action.Z]);
    }

    [Fact]
    public void LocksViewWaitsForConfirmationAndReleasesOnMouseUp()
    {
        using var game = new GameFixture();
        game.Controller.Enabled = true;
        game.Press();
        game.Api.Input.MouseYaw = 1.5f;
        game.Controller.OnRenderFrame(0.02f, EnumRenderStage.Before);
        Assert.Equal(0, game.Api.Input.MouseYaw);
        game.Tick();
        int firstBatch = game.Wire.Count;
        Assert.True(firstBatch > 0);
        game.Tick();
        Assert.Equal(firstBatch, game.Wire.Count);
        game.Controller.Observe(game.Form);
        game.Tick();
        Assert.True(game.Wire.Count > firstBatch);
        game.Release();
        game.Api.Input.MouseYaw = 2;
        game.Controller.OnRenderFrame(0.02f, EnumRenderStage.Before);
        Assert.Equal(2, game.Api.Input.MouseYaw);
        int stopped = game.Wire.Count;
        game.Tick();
        Assert.Equal(stopped, game.Wire.Count);
    }

    [Theory]
    [InlineData("range")]
    [InlineData("slot")]
    [InlineData("focus")]
    [InlineData("claim")]
    [InlineData("left")]
    [InlineData("recipe")]
    [InlineData("removed")]
    [InlineData("disabled")]
    public void StopsWhenTheInteractionBecomesInvalid(string reason)
    {
        using var game = new GameFixture();
        game.Controller.Enabled = true;
        game.Press();
        switch (reason)
        {
            case "range": game.Player.Entity.Pos.X += 100; break;
            case "slot": game.Player.InventoryManager.ActiveHotbarSlotNumber = 1; break;
            case "focus": game.Api.Input.MouseGrabbed.Returns(false); break;
            case "claim": game.World.Claims.TryAccess(game.Player, game.Form.Pos, EnumBlockAccessFlags.Use).Returns(false); break;
            case "left": game.Buttons.Left = true; break;
            case "recipe": game.Recipe.RecipeId++; break;
            case "removed": game.World.BlockAccessor.GetBlockEntity(game.Form.Pos).Returns((BlockEntity?)null); break;
            case "disabled": game.Controller.Enabled = false; break;
        }
        game.Tick();
        Assert.Empty(game.Wire);
        game.Api.Input.MouseYaw = 3;
        game.Controller.OnRenderFrame(0.02f, EnumRenderStage.Before);
        Assert.Equal(3, game.Api.Input.MouseYaw);
    }

    [Fact]
    public void TimeoutStopsSendingAndRequiresAReleaseBeforeRestarting()
    {
        using var game = new GameFixture();
        game.Controller.Enabled = true;
        game.Press();
        game.Tick();
        game.World.ElapsedMilliseconds.Returns(6000);
        game.Tick();
        int stopped = game.Wire.Count;
        game.Press();
        game.Tick();
        Assert.Equal(stopped, game.Wire.Count);
        game.Release();
        game.Press();
        game.Tick();
        Assert.True(game.Wire.Count > stopped);
    }

    [Fact]
    public void ToolMenuUsesTheSelectionCapturedWhenItWasOpened()
    {
        using var game = new GameFixture();
        var mod = new AutoClayModSystem();
        try
        {
            mod.StartClientSide(game.Api);
            game.Api.Received().LoadModConfig<AutoClayConfig>("vsautoclay.json");
            game.Api.Received().StoreModConfig(Arg.Any<AutoClayConfig>(), "vsautoclay.json");
            ClayPatches.Initialize(game.Api, game.Controller);
            var automatic = new SkillItem { Code = new AssetLocation("vsautoclay", "automatic") };
            typeof(ClayPatches).GetField("mode", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, automatic);
            SkillItem[] vanilla = Enumerable.Range(0, 4).Select(index => new SkillItem { Code = new AssetLocation("mode" + index) }).ToArray();
            typeof(ItemClay).GetField("toolModes", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(game.Slot.Itemstack!.Collectible, vanilla);
            var captured = game.Player.CurrentBlockSelection;
            game.Player.Entity.BlockSelection = null;
            var prefix = typeof(ClayPatches).GetNestedType("SelectMode", BindingFlags.NonPublic)!
                .GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!;
            object?[] selection = [4, captured];
            prefix.Invoke(null, selection);
            Assert.True(game.Controller.Enabled);
            Assert.Equal(0, selection[0]);
            selection = [2, captured];
            prefix.Invoke(null, selection);
            Assert.False(game.Controller.Enabled);
            Assert.Equal(2, selection[0]);
            selection = [4, null];
            prefix.Invoke(null, selection);
            Assert.False(game.Controller.Enabled);
            Assert.Equal(0, selection[0]);
            Assert.Equal(4, Harmony.GetAllPatchedMethods().Count(method => Harmony.GetPatchInfo(method)?.Owners.Contains("vsautoclay.client") == true));
        }
        finally
        {
            mod.Dispose();
        }
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("unfocused")]
    [InlineData("emptyhand")]
    [InlineData("selection")]
    [InlineData("recipe")]
    [InlineData("claim")]
    public void DoesNotStartWithoutAValidClayInteraction(string reason)
    {
        using var game = new GameFixture();
        game.Controller.Enabled = reason != "disabled";
        switch (reason)
        {
            case "unfocused": game.Api.Input.MouseGrabbed.Returns(false); break;
            case "emptyhand": game.Slot.Itemstack = null; break;
            case "selection": game.Player.Entity.BlockSelection = null; break;
            case "recipe": typeof(BlockEntityClayForm).GetField("selectedRecipe", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(game.Form, null); break;
            case "claim": game.World.Claims.TryAccess(game.Player, game.Form.Pos, EnumBlockAccessFlags.Use).Returns(false); break;
        }
        game.Press();
        game.Tick();
        Assert.Empty(game.Wire);
    }

    [Fact]
    public void MouseEventsUnlockTheCameraAndWorldExitResetsTheMode()
    {
        using var game = new GameFixture();
        game.Controller.Enabled = true;
        game.Press();
        var movement = new MouseEvent(0, 0);
        game.Api.Event.MouseMove += Raise.Event<MouseEventDelegate>(movement);
        Assert.True(movement.Handled);
        game.Api.Event.MouseUp += Raise.Event<MouseEventDelegate>(new MouseEvent(0, 0, EnumMouseButton.Left, 0));
        movement = new MouseEvent(0, 0);
        game.Api.Event.MouseMove += Raise.Event<MouseEventDelegate>(movement);
        Assert.True(movement.Handled);
        game.Api.Event.MouseUp += Raise.Event<MouseEventDelegate>(new MouseEvent(0, 0, EnumMouseButton.Right, 0));
        movement = new MouseEvent(0, 0);
        game.Api.Event.MouseMove += Raise.Event<MouseEventDelegate>(movement);
        Assert.False(movement.Handled);
        game.Api.Event.LeaveWorld += Raise.Event<Action>();
        Assert.False(game.Controller.Enabled);
    }

    [Fact]
    public void CompletionUnlocksTheViewAndDoesNotRestartWhileHeld()
    {
        using var game = new GameFixture();
        game.Form.Voxels = (bool[,,])game.Recipe.Voxels.Clone();
        game.Form.Voxels[3, 0, 3] = false;
        game.Controller.Enabled = true;
        game.Press();
        game.Tick();
        game.Api.Input.MouseYaw = 2;
        game.Controller.OnRenderFrame(0.02f, EnumRenderStage.Before);
        Assert.Equal(2, game.Api.Input.MouseYaw);
        game.Controller.Removed(game.Form);
        int completed = game.Wire.Count;
        game.Press();
        game.Tick();
        Assert.Equal(completed, game.Wire.Count);
    }

    [Fact]
    public void EmptyHandAfterRefillingStopsAndNotifies()
    {
        using var game = new GameFixture();
        game.Form.AvailableVoxels = 0;
        game.Slot.Itemstack!.StackSize = 1;
        game.Controller.Enabled = true;
        game.Press();
        game.Tick();
        Assert.True(game.Slot.Empty);
        game.Api.Received().TriggerIngameError(game.Controller, "vsautoclay-material", Arg.Any<string>());
        game.Api.Input.MouseYaw = 1;
        game.Controller.OnRenderFrame(0.02f, EnumRenderStage.Before);
        Assert.Equal(1, game.Api.Input.MouseYaw);
    }

    private sealed class GameFixture : IDisposable
    {
        public ICoreClientAPI Api { get; } = Substitute.For<ICoreClientAPI>();
        public IClientWorldAccessor World { get; } = Substitute.For<IClientWorldAccessor>();
        public IClientPlayer Player { get; } = (ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        public MouseButtonState Buttons { get; } = new() { Right = true };
        public ItemSlot Slot { get; }
        public BlockEntityClayForm Form { get; }
        public ClayFormingRecipe Recipe { get; }
        public ClayController Controller { get; }
        public List<object> Wire { get; } = [];

        public GameFixture()
        {
            Lang.ChangeLanguage("en");
            Lang.AvailableLanguages["en"] = Substitute.For<ITranslationService>();
            Api.World.Returns(World);
            ((ICoreAPI)Api).World.Returns(World);
            Api.Side.Returns(EnumAppSide.Client);
            var main = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
            var data = (ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
            data.EntityPlayer = new EntityPlayer { World = World };
            data.PlayerUID = "autoclay-test";
            var inventories = new Vintagestory.API.Datastructures.OrderedDictionary<string, InventoryBase>();
            var inventoryManager = new ClientPlayerInventoryManager(inventories, Player, main);
            SetMember(Player, "game", main);
            SetMember(Player, "worlddata", data);
            SetMember(Player, "inventoryMgr", inventoryManager);
            SetMember(main, "player", Player);
            World.ReturnsForAll<IClientPlayer>(Player);
            World.ReturnsForAll<IPlayer>(Player);
            World.FrameProfiler.Returns(new FrameProfilerUtil("autoclay-tests"));
            Player.Entity.World = World;
            Player.Entity.Pos.SetPos(10, 10, 10);
            Player.WorldData.PickingRange = 5;
            Player.WorldData.CurrentGameMode = EnumGameMode.Survival;
            Api.Input.MouseButton.Returns(Buttons);
            Api.Input.MouseGrabbed.Returns(true);
            Api.Gui.OpenedGuis.Returns([]);
            var clay = new ItemClay { Code = new AssetLocation("clay-blue") };
            var hotbar = new InventoryGeneric(12, "hotbar-autoclay-test", Api);
            inventories.Add("hotbar-autoclay-test", hotbar);
            Slot = hotbar[0];
            Slot.Itemstack = new ItemStack(clay, 10);
            Recipe = new ClayFormingRecipe { RecipeId = 7, Pattern = [Enumerable.Repeat("##########", 10).ToArray()] };
            Recipe.GenVoxels();
            Form = new BlockEntityClayForm
            {
                Api = Api,
                Pos = new BlockPos(10, 10, 11),
                Block = new BlockClayForm(),
                AvailableVoxels = 25
            };
            SetField("selectedRecipe", Recipe);
            SetField("workItemStack", new ItemStack(clay));
            SetField("baseMaterial", new ItemStack(clay));
            Form.CreateInitialWorkItem();
            typeof(BlockEntityClayForm).GetMethod("RegenMeshAndSelectionBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Form, [0]);
            World.BlockAccessor.GetBlockEntity(Form.Pos).Returns(Form);
            World.BlockAccessor.GetBlock(Form.Pos).Returns(Form.Block);
            World.Claims.TryAccess(Player, Form.Pos, EnumBlockAccessFlags.Use).Returns(true);
            Player.Entity.BlockSelection = new BlockSelection { Position = Form.Pos, Block = Form.Block, Face = BlockFacing.UP };
            Api.Network.When(network => network.SendPacketClient(Arg.Any<object>())).Do(call => Wire.Add(call[0]));
            Api.Network.When(network => network.SendBlockEntityPacket(Arg.Any<BlockPos>(), Arg.Any<int>(), Arg.Any<byte[]>())).Do(call => Wire.Add(call[2]));
            Controller = new ClayController(Api, new AutoClayConfig { ActionsPerBatch = 1 });
        }

        public void Press()
        {
            Buttons.Right = true;
            Invoke("OnAction", EnumEntityAction.InWorldRightMouseDown, true, EnumHandling.PassThrough);
        }

        public void Release()
        {
            Buttons.Right = false;
            Tick();
        }

        public void Tick() => Invoke("Tick");
        private void Invoke(string name, params object[] args) => typeof(ClayController)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Controller, args);
        private void SetField(string name, object value) => typeof(BlockEntityClayForm)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Form, value);
        private static void SetMember(object instance, string name, object value) => instance.GetType()
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.SetValue(instance, value);
        public void Dispose() => Controller.Dispose();
    }
}
