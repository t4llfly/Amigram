using System;
using System.Linq;
using Microsoft.Phone.Shell;

namespace MetroTelegram
{
    public static class TileService
    {
        public static void UpdatePrimaryTile(int unreadCount, string lastSender, string lastMessage)
        {
            try
            {
                ShellTile primaryTile = ShellTile.ActiveTiles.FirstOrDefault();
                if (primaryTile == null) return;

                FlipTileData tileData = new FlipTileData();
                tileData.Title = "Amigram";
                tileData.Count = unreadCount > 0 ? unreadCount : 0;

                if (!string.IsNullOrEmpty(lastMessage))
                {
                    tileData.BackTitle = !string.IsNullOrEmpty(lastSender) ? lastSender : "Amigram";
                    tileData.BackContent = lastMessage;
                }
                else
                {
                    tileData.BackTitle = string.Empty;
                    tileData.BackContent = string.Empty;
                }

                primaryTile.Update(tileData);
            }
            catch { }
        }
    }
}