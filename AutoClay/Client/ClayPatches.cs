using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.GameContent;

namespace AutoClay.Client;

internal static class ClayPatches
{
    private static ICoreClientAPI? api;
    private static ClayController? controller;
    private static SkillItem? mode;

    public static void Initialize(ICoreClientAPI clientApi, ClayController clientController)
    {
        api = clientApi;
        controller = clientController;
    }

    public static void Dispose()
    {
        controller = null;
        api = null;
        mode?.Dispose();
        mode = null;
    }

    [HarmonyPatch(typeof(ItemClay), nameof(ItemClay.GetToolModes))]
    private static class ToolModes
    {
        private static void Postfix(ref SkillItem[]? __result)
        {
            if (__result == null || api == null) return;
            mode ??= new SkillItem { Code = new AssetLocation("autoclay", "automatic") }.WithLetterIcon(api, "A");
            mode.Name = Lang.Get("autoclay:toolmode");
            if (!__result.Any(item => item.Code.Equals(mode.Code))) __result = [.. __result, mode];
        }
    }

    [HarmonyPatch(typeof(GuiDialogToolMode), "OnSlotClick")]
    private static class SelectMode
    {
        private static void Prefix(ref int num, BlockSelection? ___blockSele)
        {
            if (api == null || controller == null || mode == null) return;
            var player = api.World.Player;
            var slot = player.InventoryManager.ActiveHotbarSlot;
            if (slot.Itemstack?.Collectible is not ItemClay clay) return;
            var modes = clay.GetToolModes(slot, player, ___blockSele);
            controller.Stop();
            controller.Enabled = modes != null && num >= 0 && num < modes.Length && modes[num].Code.Equals(mode.Code);
            if (controller.Enabled || modes == null) num = 0;
        }
    }

    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.FromTreeAttributes))]
    private static class ServerState
    {
        private static void Postfix(BlockEntityClayForm __instance)
        {
            if (__instance.Api?.Side == EnumAppSide.Client) controller?.Observe(__instance);
        }
    }

    [HarmonyPatch(typeof(BlockEntityClayForm), nameof(BlockEntityClayForm.OnBlockRemoved))]
    private static class RemovedForm
    {
        private static void Prefix(BlockEntityClayForm __instance)
        {
            if (__instance.Api?.Side == EnumAppSide.Client) controller?.Removed(__instance);
        }
    }
}
