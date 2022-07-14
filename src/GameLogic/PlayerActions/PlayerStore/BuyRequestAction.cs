﻿// <copyright file="BuyRequestAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.PlayerStore;

using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.Interfaces;

/// <summary>
/// Action to buy an item from another player shop.
/// </summary>
public class BuyRequestAction
{
    private readonly CloseStoreAction _closeStoreAction = new ();

    /// <summary>
    /// Buys the item from another player shop.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="requestedPlayer">The requested player.</param>
    /// <param name="slot">The slot.</param>
    public async ValueTask BuyItemAsync(Player player, Player requestedPlayer, byte slot)
    {
        using var loggerScope = player.Logger.BeginScope(this.GetType());
        if (!(requestedPlayer.ShopStorage?.StoreOpen ?? false))
        {
            player.Logger.LogDebug("Store not open, Character {0}", requestedPlayer.SelectedCharacter?.Name);
            await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Player's Store not open.", MessageType.BlueNormal)).ConfigureAwait(false); // Code: 3
            return;
        }

        if (slot < InventoryConstants.FirstStoreItemSlotIndex)
        {
            player.Logger.LogWarning("Store Slot too low: {0}, possible hacker", slot);
            return;
        }

        var item = requestedPlayer.ShopStorage.GetItem(slot);
        if (item?.StorePrice is null)
        {
            player.Logger.LogDebug("Item unavailable, Slot {0}", slot);
            await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Item unavailable.", MessageType.BlueNormal)).ConfigureAwait(false); // Code 5?
            return;
        }

        var itemPrice = item.StorePrice.Value;

        if (player.Money < itemPrice)
        {
            await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Not enough Zen.", MessageType.BlueNormal)).ConfigureAwait(false);
            return;
        }

        // Check Inv Space
        var freeslot = player.Inventory?.CheckInvSpace(item);
        if (freeslot is null)
        {
            await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Not enough Space in your Inventory.", MessageType.BlueNormal)).ConfigureAwait(false);
            return;
        }

        bool itemSold = false;
        using (await requestedPlayer.ShopStorage.StoreLock.LockAsync())
        {
            if (!requestedPlayer.ShopStorage.StoreOpen)
            {
                player.Logger.LogDebug("Store not open anymore, Character {0}", requestedPlayer.SelectedCharacter?.Name);
                await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Player's Store not open anymore.", MessageType.BlueNormal)).ConfigureAwait(false);
                return;
            }

            item = requestedPlayer.ShopStorage.GetItem(slot);
            if (item is null)
            {
                await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("Sorry, Item was sold in the meantime.", MessageType.BlueNormal)).ConfigureAwait(false);
                return;
            }

            player.Logger.LogDebug("BuyRequest, Item Price: {0}", itemPrice);
            if (player.TryRemoveMoney(itemPrice))
            {
                if (requestedPlayer.TryAddMoney(itemPrice))
                {
                    using var itemContext = requestedPlayer.GameContext.PersistenceContextProvider.CreateNewTradeContext();
                    itemContext.Attach(item);
                    await requestedPlayer.ShopStorage.RemoveItemAsync(item);
                    await requestedPlayer.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                    await requestedPlayer.InvokeViewPlugInAsync<IItemSoldByPlayerShopPlugIn>(p => p.ItemSoldByPlayerShopAsync(slot, player));
                    await requestedPlayer.InvokeViewPlugInAsync<IItemRemovedPlugIn>(p => p.RemoveItemAsync(slot));
                    item.ItemSlot = (byte)freeslot;
                    item.StorePrice = null;
                    await player.Inventory!.AddItemAsync(item);
                    requestedPlayer.PersistenceContext.Detach(item);
                    await itemContext.SaveChangesAsync().ConfigureAwait(false);
                    player.PersistenceContext.Attach(item);
                    await player.InvokeViewPlugInAsync<IItemBoughtFromPlayerShopPlugIn>(p => p.ItemBoughtFromPlayerShopAsync(item)).ConfigureAwait(false);
                    await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
                    itemSold = true;

                    player.GameContext.PlugInManager.GetPlugInPoint<IItemSoldToOtherPlayerPlugIn>()?.ItemSold(requestedPlayer, item, player);
                }
                else
                {
                    await player.InvokeViewPlugInAsync<IShowMessagePlugIn>(p => p.ShowMessageAsync("The inventory of the seller is full.", MessageType.BlueNormal)).ConfigureAwait(false);
                    player.TryAddMoney(itemPrice);
                }
            }
        }

        if (itemSold)
        {
            if (requestedPlayer.ShopStorage.Items.Any())
            {
                // this update may be sent to other players as well which are currently looking at the store
                await player.InvokeViewPlugInAsync<Views.PlayerShop.IShowShopItemListPlugIn>(p => p.ShowShopItemListAsync(requestedPlayer, true)).ConfigureAwait(false);
            }
            else
            {
                await this._closeStoreAction.CloseStoreAsync(requestedPlayer);
            }
        }
    }
}