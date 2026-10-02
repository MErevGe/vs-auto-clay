using AutoClay.Planning;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Common;
using Vintagestory.GameContent;

namespace AutoClay.Client;

internal sealed class ClayProtocol(ICoreClientAPI api)
{
    public void SetToolMode(ItemSlot slot, BlockPos pos, int mode)
    {
        if (slot.Itemstack is not { Collectible: ItemClay clay } stack) return;
        if (stack.Attributes.GetInt("toolMode") == mode) return;
        clay.SetToolMode(slot, api.World.Player, new BlockSelection { Position = pos }, mode);
        api.Network.SendPacketClient(new Packet_Client
        {
            Id = Packet_ClientIdEnum.SetToolMode,
            ToolMode = new Packet_ToolMode { Mode = mode, X = pos.X, Y = pos.InternalY, Z = pos.Z, Face = BlockFacing.UP.Index }
        });
    }

    public void Execute(BlockEntityClayForm form, ItemSlot slot, ClayAction action)
    {
        SetToolMode(slot, form.Pos, action.ToolMode);
        if (!action.Remove && form.AvailableVoxels <= 0)
        {
            RefillAndAdd(form, slot, action);
            return;
        }
        form.OnUseOver(api.World.Player, new Vec3i(action.X, action.Y, action.Z), BlockFacing.UP, action.Remove);
    }

    private void RefillAndAdd(BlockEntityClayForm form, ItemSlot slot, ClayAction action)
    {
        var boxes = form.Block.GetSelectionBoxes(api.World.BlockAccessor, form.Pos);
        int index = Array.FindIndex(boxes, box => (int)(box.X1 * 16) == action.X
            && (int)(box.Y1 * 16) == action.Y && (int)(box.Z1 * 16) == action.Z);
        if (index < 0) throw new InvalidOperationException("The clay voxel is no longer selectable.");
        BlockSelection selection = new()
        {
            Position = form.Pos.Copy(),
            Block = form.Block,
            Face = BlockFacing.UP,
            SelectionBoxIndex = index,
            HitPosition = new Vec3d((action.X + 0.5) / 16, (action.Y + 1.0) / 16, (action.Z + 0.5) / 16)
        };
        if (slot.Itemstack?.Collectible is not ItemClay clay)
            throw new InvalidOperationException("Matching clay is no longer held.");
        SendInteraction(selection, EnumHandInteractNw.StartHeldItemUse);
        SendInteraction(selection, EnumHandInteractNw.StopHeldItemUse);
        clay.OnHeldInteractStop(0, slot, api.World.Player.Entity, selection, null);
    }

    private void SendInteraction(BlockSelection selection, EnumHandInteractNw state)
    {
        api.Network.SendPacketClient(new Packet_Client
        {
            Id = Packet_ClientIdEnum.HandInteraction,
            HandInteraction = new Packet_ClientHandInteraction
            {
                SlotId = api.World.Player.InventoryManager.ActiveHotbarSlotNumber,
                MouseButton = 2,
                X = selection.Position.X,
                Y = selection.Position.InternalY,
                Z = selection.Position.Z,
                HitX = CollectibleNet.SerializeDoublePrecise(selection.HitPosition.X),
                HitY = CollectibleNet.SerializeDoublePrecise(selection.HitPosition.Y),
                HitZ = CollectibleNet.SerializeDoublePrecise(selection.HitPosition.Z),
                OnBlockFace = selection.Face.Index,
                SelectionBoxIndex = selection.SelectionBoxIndex,
                UseType = (int)EnumHandInteract.HeldItemInteract,
                EnumHandInteract = (int)state,
                UsingCount = 0,
                FirstEvent = state == EnumHandInteractNw.StartHeldItemUse ? 1 : 0
            }
        });
    }
}
