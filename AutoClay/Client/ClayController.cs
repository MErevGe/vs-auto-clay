using System.Diagnostics;
using AutoClay.Planning;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AutoClay.Client;

internal sealed class ClayController : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly AutoClayConfig config;
    private readonly ClayProtocol protocol;
    private readonly long tickListener;
    private BlockEntityClayForm? form;
    private ItemSlot? slot;
    private PendingBatch? pending;
    private BlockPos? pendingPosition;
    private int recipeId;
    private bool held;
    private bool active;
    private float yaw;
    private float pitch;
    private bool disposed;

    public bool Enabled { get; set; }
    public double RenderOrder => -1;
    public int RenderRange => 0;

    public ClayController(ICoreClientAPI api, AutoClayConfig config)
    {
        this.api = api;
        this.config = config;
        protocol = new ClayProtocol(api);
        api.Input.InWorldAction += OnAction;
        api.Event.MouseUp += OnMouseUp;
        api.Event.MouseMove += OnMouseMove;
        api.Event.LeaveWorld += OnLeaveWorld;
        api.Event.RegisterRenderer(this, EnumRenderStage.Before, "autoclay-camera");
        tickListener = api.Event.RegisterGameTickListener(OnTick, 20);
    }

    private void OnAction(EnumEntityAction action, bool on, ref EnumHandling handling)
    {
        if (action != EnumEntityAction.InWorldRightMouseDown || !on) return;
        if (held)
        {
            handling = EnumHandling.PreventDefault;
            return;
        }
        if (!Enabled || handling != EnumHandling.PassThrough || !CanUseMouse()) return;
        var player = api.World.Player;
        var selection = player.CurrentBlockSelection;
        var candidateSlot = player.InventoryManager.ActiveHotbarSlot;
        if (selection == null || candidateSlot.Itemstack?.Collectible is not ItemClay) return;
        if (api.World.BlockAccessor.GetBlockEntity(selection.Position) is not BlockEntityClayForm candidate
            || candidate.SelectedRecipe == null) return;
        handling = EnumHandling.PreventDefault;
        held = true;
        if (pending != null)
        {
            Notify("waiting");
            return;
        }
        if (!CanWork(candidate, candidateSlot)) return;
        form = candidate;
        slot = candidateSlot;
        recipeId = candidate.SelectedRecipe.RecipeId;
        yaw = api.Input.MouseYaw;
        pitch = api.Input.MousePitch;
        active = true;
    }

    private bool CanUseMouse() => api.Input.MouseGrabbed
        && !api.Gui.OpenedGuis.Any(dialog => dialog.DialogType == EnumDialogType.Dialog);

    private bool CanWork(BlockEntityClayForm target, ItemSlot targetSlot)
    {
        var player = api.World.Player;
        var eye = player.Entity.Pos.XYZ.Add(player.Entity.LocalEyePos);
        double range = player.WorldData.PickingRange;
        return player.Entity.Alive
            && player.WorldData.CurrentGameMode != EnumGameMode.Spectator
            && target.CanWorkCurrent
            && targetSlot.Itemstack?.Collectible is ItemClay
            && targetSlot.Itemstack.Collectible.Code.Equals(target.BaseMaterial?.Collectible.Code)
            && player.Entity.Pos.Dimension == target.Pos.dimension
            && eye.SquareDistanceTo(target.Pos.X + 0.5, target.Pos.Y + 0.5, target.Pos.Z + 0.5) <= range * range
            && api.World.Claims.TryAccess(player, target.Pos, EnumBlockAccessFlags.Use);
    }

    private void OnTick(float dt)
    {
        try
        {
            Tick();
        }
        catch (Exception exception)
        {
            Stop();
            Notify("error");
            api.Logger.Error("Auto Clay stopped: {0}", exception);
        }
    }

    private void Tick()
    {
        if (!api.Input.MouseButton.Right)
        {
            held = false;
            Stop();
        }
        if (pending != null && pending.HasTimedOut(api.World.ElapsedMilliseconds, config.ServerTimeoutMilliseconds))
        {
            pending = null;
            pendingPosition = null;
            Stop();
            Notify("timeout");
        }
        if (!active || form == null || slot == null) return;
        if (!Enabled || !CanUseMouse() || api.Input.MouseButton.Left
            || !ReferenceEquals(api.World.Player.InventoryManager.ActiveHotbarSlot, slot)
            || !ReferenceEquals(api.World.BlockAccessor.GetBlockEntity(form.Pos), form)
            || form.SelectedRecipe?.RecipeId != recipeId || !CanWork(form, slot))
        {
            Stop();
            return;
        }
        if (pending != null) return;
        bool executed = false;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < config.ActionsPerBatch; i++)
        {
            var action = ClayPlanner.Next(form.Voxels, form.SelectedRecipe.Voxels, form.AvailableVoxels);
            if (action == null)
            {
                if (ClayPlanner.FirstDifferentLayer(form.Voxels, form.SelectedRecipe.Voxels) != ClayPlanner.Size)
                {
                    Stop();
                    Notify("unsupported");
                    return;
                }
                break;
            }
            bool refill = !action.Value.Remove && form.AvailableVoxels <= 0;
            protocol.Execute(form, slot, action.Value);
            executed = true;
            if (slot.Empty)
            {
                Stop();
                Notify("material");
                return;
            }
            if (refill || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 6) break;
        }
        if (executed)
        {
            pendingPosition = form.Pos.Copy();
            pending = new PendingBatch(form.Voxels, form.AvailableVoxels, recipeId, api.World.ElapsedMilliseconds);
        }
        if (ClayPlanner.FirstDifferentLayer(form.Voxels, form.SelectedRecipe.Voxels) == ClayPlanner.Size)
            Stop();
    }

    public void Observe(BlockEntityClayForm updated)
    {
        if (pending == null || !updated.Pos.Equals(pendingPosition)) return;
        if (pending.Matches(updated.Voxels, updated.AvailableVoxels, updated.SelectedRecipe?.RecipeId ?? -1))
        {
            pending = null;
            pendingPosition = null;
        }
    }

    public void Removed(BlockEntityClayForm removed)
    {
        if (!removed.Pos.Equals(pendingPosition)) return;
        pending = null;
        pendingPosition = null;
        Stop();
    }

    public void Stop()
    {
        active = false;
        var previousForm = form;
        var previousSlot = slot;
        form = null;
        slot = null;
        if (previousForm != null && previousSlot != null && ReferenceEquals(api.World.Player?.InventoryManager.ActiveHotbarSlot, previousSlot))
            protocol.SetToolMode(previousSlot, previousForm.Pos, 0);
    }

    private void OnMouseUp(MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Right) return;
        held = false;
        Stop();
    }

    private void OnMouseMove(MouseEvent args)
    {
        if (active && api.Input.MouseButton.Right && CanUseMouse()) args.Handled = true;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!active || !api.Input.MouseButton.Right || !CanUseMouse()) return;
        api.Input.MouseYaw = yaw;
        api.Input.MousePitch = pitch;
    }

    private void Notify(string code) => api.TriggerIngameError(this, "autoclay-" + code, Lang.Get("autoclay:" + code));

    private void OnLeaveWorld()
    {
        active = false;
        held = false;
        Enabled = false;
        form = null;
        slot = null;
        pending = null;
        pendingPosition = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        OnLeaveWorld();
        api.Input.InWorldAction -= OnAction;
        api.Event.MouseUp -= OnMouseUp;
        api.Event.MouseMove -= OnMouseMove;
        api.Event.LeaveWorld -= OnLeaveWorld;
        api.Event.UnregisterGameTickListener(tickListener);
        api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
    }
}
