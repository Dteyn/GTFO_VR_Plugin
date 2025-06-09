using GTFO_VR.Core;
using GTFO_VR.Core.PlayerBehaviours;
using GTFO_VR.Core.UI;
using GTFO_VR.Core.VR_Input;
using GTFO_VR.Events;
using GTFO_VR.Util;
using Player;
using SteamVR_Standalone_IL2CPP.Util;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using Valve.VR;
using Mathf = SteamVR_Standalone_IL2CPP.Util.Mathf;

/* Team Status changes implemented by Dteyn:
 * 
 * - Added a new state for 'Status' in WatchState
 * 
 * - Added an entry in SetupRadialMenu() for status display, re-used the 'hacking tool' icon for now
 * 
 * - Added TMP for m_statusDisplay, which is used in SetupStatusDisplay()
 *    - NOTE: For this I cloned 'WardenObjective' properties as I couldn't figure out how to get the text to fit on the watch face otherwise, so this will need improving but works for now.
 * 
 * - Added IEnumerator StatusUpdater() to update status while screen is active, refresh rate 250ms
 *    - NOTE: Not sure this is done correctly, but it seems to work okay. This may need changing / improvement.
 * 
 * - Added a dictionary for lookup of 'tool types' to display sentry type (Burst, Auto, etc)
 *    - NOTE: This is also a workaround, I found using 'ArchetypeName' wasn't specific enough for sentry gun so I used 'PublicName' and then created a dictionary to map publicname to what I wanted to display. This info must be available somewhere but I just couldn't find it in UnityExplorer / dnSpy so I made this workaround.
 * 
 * - Added a mapper for name colors to use based on slot index, similar to above I would prefer to pull the actual player name colors but this was a quick workaround just to get things working.
 * 
 * - Added helper routines for returning a text color for HP / ammo / infection based on percentage
 * 
 * - Added RefreshStatusDisplay() which loops through player backpack to get inventory info, and Dam_PlayerDamageBase to get HP and infection % and builds a string to update the status display
 * 
 * - Added a new case in SwitchState() to toggle status display, as well as a check to stop the status updater if it's not the active menu
 *   - Added ToggleStatusRendering()
 * 
 * - Added check in OnDestroy() to make sure the status updater isn't running
 */

namespace GTFO_VR.UI
{
    /// <summary>
    /// Handles all VR watch UI related functions
    /// </summary>
    
    // ToDO - Refactor this into something more manageable, or not, if no new UI is planned.

    public class Watch : MonoBehaviour
    {

        internal enum WatchState
        {
            Inventory,
            Objective,
            Chat,
            Status,  // Add a state for Status
        }

        public Watch(IntPtr value): base(value) { }

        public static Watch Current;


        RadialMenu m_watchRadialMenu;

        Dictionary<InventorySlot, DividedBarShaderController> m_inventoryToAmmoDisplayMapping = new Dictionary<InventorySlot, DividedBarShaderController>();
        DividedBarShaderController m_bulletsInMagDisplay;
        TextMeshPro m_numberBulletsInMagDisplay;

        DividedBarShaderController m_healthDisplay;
        DividedBarShaderController m_infectionDisplay;
        DividedBarShaderController m_oxygenDisplay;
        TextMeshPro m_objectiveDisplay;
        TextMeshPro m_chatDisplay;

        // Add a status display
        TextMeshPro m_statusDisplay;
        IEnumerator m_statusUpdater;  // coroutine for updating status while the display is active
        const float STATUS_REFRESH = 0.25f;  // refresh delay for status screen, default 0.25s
        static readonly Dictionary<string, string> ToolNameMap = new Dictionary<string, string>
        {
            /* Dictionary lookup for displaying tool names, specifically the sentry types
             * ArchetypeName works for the mines/C-Foam/bio but for sentries is not specific enough
             * We instead get the PublicName and map that to the type of sentry, or tool used
             * There may be a better way to do this, but this works for now */
            { "RAD Labs Meduza",     "HEL Auto Sentry" },
            { "Mechatronic SGB3",    "Burst Sentry" },
            { "Autotek 51 RSG",      "Sniper Sentry" },
            { "Mechatronic B5 LFR",  "Shotgun Sentry" },
            { "Krieger O4",          "Mine Deployer" },
            { "Stalwart Flow G2",    "C-Foam Launcher" },
            { "D-tek Optron IV",     "Bio Tracker" }
        };
        static readonly string[] nameColors = {
            /* Name colors for 2nd/3rd/4th player slots
             * Ideally, these colors should be looked up from the player's silhouette/name color,
             * but for now this solution is used to keep things simple */
            "#18935E", // 2nd player (Dauda)
            "#20558C", // 3rd player (Hackett)
            "#7A1A8E"  // 4th player (Bishop)
        };

        Queue<string> msgBuffer = new Queue<string>();

        readonly Color m_normalHealthCol = new Color(0.66f, 0f, 0f);
        readonly Color m_normalInfectionCol = new Color(0.533f, 1, 0.8f);
        readonly Color m_normalOxygenCol = Color.cyan;

        MeshRenderer[] m_inventoryMeshes;
        WatchState m_currentState = WatchState.Inventory;

        Vector3 m_handOffset = new Vector3(0, -.05f, -.15f);
        Quaternion m_leftHandRotationOffset = Quaternion.Euler(new Vector3(205, -100f, -180f));
        Quaternion m_rightHandRotationOffset = Quaternion.Euler(new Vector3(205, 100f, 180f));

        string m_ObjectiveText;
        Regex INDENT_REGEX = new Regex(@"<indent=\d{1,3}%>");

        SteamVR_Action_Boolean toggleWatchMode;
        SteamVR_Action_Boolean watchRadialMenu;

        void Awake()
        {
            watchRadialMenu = SteamVR_Input.GetBooleanAction("WatchRadialMenu");
            toggleWatchMode = SteamVR_Input.GetBooleanAction("ToggleWatchMode");
            Current = this;
            ItemEquippableEvents.OnPlayerWieldItem += ItemSwitched;
            InventoryAmmoEvents.OnInventoryAmmoUpdate += AmmoUpdate;
            Controllers.HandednessSwitched += SetHandedness;
            VRConfig.configWatchScaling.SettingChanged += WatchScaleChanged;
            VRConfig.configUseNumbersForAmmoDisplay.SettingChanged += AmmoDisplayChanged;
            VRConfig.configWatchColor.SettingChanged += WatchColorChanged;
            VRConfig.configWatchInfoText.SettingChanged += WatchRadialInfoTextChanged;
            ChatMsgEvents.OnChatMsgReceived += ChatMsgReceived;
        }

        private void WatchRadialInfoTextChanged(object sender, EventArgs e)
        {
            m_watchRadialMenu?.ToggleAllInfoText(VRConfig.configWatchInfoText.Value);
        }

        private void ChatMsgReceived(string msg)
        {
            if (msgBuffer.Contains(msg))
            {
                return;
            }
            SteamVR_InputHandler.TriggerHapticPulse(0.1f, 40f, .75f, Controllers.GetDeviceFromInteractionHandType(InteractionHand.Offhand));
            CellSound.Post(AK.EVENTS.GAME_MENU_CHANGE_PAGE, transform.position);
            msgBuffer.Enqueue(msg);
            if (msgBuffer.Count > 8) {
                msgBuffer.Dequeue();
            }
            m_chatDisplay.text = "";
            foreach(string chatMsg in msgBuffer)
            {
                m_chatDisplay.text += chatMsg + "\n";
            }
            m_chatDisplay.ForceMeshUpdate(false);
        }
        
        // Helper routines for mapping HP / Ammo / Infection % to colors for the status display
        static string GetHpOrAmmoColor(int pct)  // Helper: map % to #RRGGBB (HP/Ammo palette)
        {
            if (pct > 80) return "#00FF00"; // green
            if (pct >= 50) return "#FFFF00"; // yellow
            if (pct >= 20) return "#FFA500"; // orange
            return "#FF0000";                 // red
        }

        static string GetInfColor(int pct)  // Helper: map % to #RRGGBB (Infection palette; white for 0-19%)
        {
            if (pct < 20) return "#FFFFFF"; // white
            if (pct < 50) return "#FFFF00"; // yellow
            if (pct < 90) return "#FFA500"; // orange
            return "#FF0000";                 // red
        }

        void RefreshStatusDisplay()
        {
            var agents = PlayerManager.PlayerAgentsInLevel;
            var backpacks = PlayerBackpackManager.Current?.m_backpacks;
            if (agents == null || backpacks == null || m_statusDisplay == null) return;

            StringBuilder statusText = new StringBuilder(512);

            int playerIndex = 0;  // tracks 2nd, 3rd and 4th players for name coloring and dividers

            foreach (var agent in agents)
            {
                if (agent == null || agent.IsLocallyOwned) continue;
                if (!backpacks.TryGetValue(agent.Owner.Lookup, out var playerBackpack) || playerBackpack == null) continue;

                // Get HP and infection %
                int hpPct = Mathf.Clamp((int)(agent.Damage.Health * 4f), 0, 100);
                int infPct = Mathf.Clamp((int)(agent.Damage.Infection * 100f), 0, 100);

                // Get AmmoStorage and Slots from PlayerBackpack
                var ammo = playerBackpack.AmmoStorage;
                var slots = playerBackpack.Slots;

                // Helper to get ammo % for given slot
                // This could probably be done in a better way, but this seems to be working
                int GetAmmoPct(PlayerAmmoStorage ammo, InventorySlot slot)
                {
                    var slotAmmo = ammo.GetInventorySlotAmmo(slot);
                    if (slotAmmo != null)
                    {
                        // Use floats for precision before rounding to int
                        float reserve = slotAmmo.RelInPack * slotAmmo.BulletsMaxCap;
                        float clip = ammo.GetClipAmmoFromSlot(slot);
                        float total = reserve + clip;  // Calculate the ammo % based on the amount in reserve and clip
                        float max = slotAmmo.BulletsMaxCap;
                        return max > 0f ? Mathf.RoundToInt((total / max) * 100f) : 0;
                    }
                    return -1;
                }

                // Get ammo % based on total ammo in reserve and in clip
                int priPct = GetAmmoPct(ammo, InventorySlot.GearStandard);
                int secPct = GetAmmoPct(ammo, InventorySlot.GearSpecial);
                int toolPct = GetAmmoPct(ammo, InventorySlot.GearClass);

                // Look up the tool public name and map to our preferred display name
                string toolPublic = (slots?.Count > 3) ? slots[3]?.Instance?.PublicName ?? "Tool" : "Tool";
                string tool = ToolNameMap.TryGetValue(toolPublic, out var tooltype)
                    ? tooltype
                    : toolPublic;  // fall back to public name if not mapped

                // Get the resource pack name and amount remaining
                string packName = (slots?.Count > 4) ? slots[4]?.Instance?.ArchetypeName ?? "Pack" : "Pack";
                int packPct = (ammo == null) ? -1 : (int)(ammo.GetBulletsRelInPack(AmmoType.ResourcePackRel) * 100);
                int packUnits = Mathf.Clamp(Mathf.RoundToInt(packPct / 20f), 0, 6);  // convert 0-120% to units 0-6

                // Get the consumable name and amount remaining (we don't use amount for now)
                string consName = (slots?.Count > 5) ? slots[5]?.Instance?.ArchetypeName ?? "Item" : "Item";
                int consPct = (ammo == null) ? -1 : (int)(ammo.GetBulletsRelInPack(AmmoType.CurrentConsumable) * 100);

                // Player Name - color depending on player slot index:
                //  2nd player - Dauda - Green is #18935E
                //  3rd player - Hackett - Blue is #20558C
                //  4th player - Bishop - Purple is #7A1A8E
                string nameColor = "#66CCFF"; // fallback color, teal
                if (playerIndex < nameColors.Length)  // use fallback if more than 3 teammates (ie: lobby expansion mod)
                    nameColor = nameColors[playerIndex];  // use default name colors for players 2-4

                // Health
                // Palette: Green when above 90%, Yellow when at 50-89%, Orange when at 20-49%, and Red when below 20%
                // TODO: Add option for bars using these characters: ███████░░░░░░
                statusText.Append("<size=32><b><u><color=").Append(nameColor).Append('>')
                  .Append(agent.PlayerName)
                  .Append("</color></u></b> | <color=").Append(GetHpOrAmmoColor(hpPct))
                  .Append(">HP: ")
                  .Append(hpPct).Append("%</color></size><br>");

                // Infection (shown only if player has infection)
                // Palette: White up to 20%, Yellow from 20-49%, Orange from 50-89%, Red when above 90%
                if (infPct > 0)
                {
                    statusText.Append("<b><size=26><color=")
                      .Append(GetInfColor(infPct))
                      .Append(">INFECTION: ").Append(infPct).Append("%</color></size></b><br>");
                }

                // Primary / Secondary ammo
                // Palette: Green when above 90%, Yellow when at 50-89%, Orange when at 20-49%, and Red when below 20%
                statusText.Append("<size=26>PRI:</size> <b><size=28><color=")
                  .Append(GetHpOrAmmoColor(priPct)).Append('>').Append(priPct).Append("%</color></size></b> | ")
                  .Append("<size=26>SEC:</size> <b><size=28><color=")
                  .Append(GetHpOrAmmoColor(secPct)).Append('>').Append(secPct).Append("%</color></size></b><br>");

                // Tool ammo
                // Palette: Green when above 90%, Yellow when at 50-89%, Orange when at 20-49%, and Red when below 20%
                statusText.Append("<size=26>")
                  .Append(tool);  // Tool name
                if (!tool.Contains("Bio Tracker", StringComparison.OrdinalIgnoreCase))  // Only show ammo if not a Bio Tracker
                {
                    statusText.Append(": <color=")
                      .Append(GetHpOrAmmoColor(toolPct)).Append('>')
                      .Append(toolPct).Append("%</color>");
                }
                statusText.Append("</size><br>");

                // Resource Pack (shown only if player is carrying resources)
                if (!string.IsNullOrEmpty(packName) && !packName.Equals("Pack", StringComparison.OrdinalIgnoreCase))
                {
                    statusText.Append("<size=24>")
                      .Append(packName).Append(": ").Append(packUnits)
                      .Append("</size>");
                }

                // Consumables (shown only if player is carrying an item)
                if (!string.IsNullOrEmpty(consName) && !consName.Equals("Item", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(packName) && !packName.Equals("Pack", StringComparison.OrdinalIgnoreCase))
                        statusText.Append(" | "); // add separator if player is carrying both resources and item

                    // Size is not specified for consumable name so it auto-sizes - it's the least important info
                    statusText.Append(consName);
                }

                // If either Resources or Consumable line was printed, add <br>
                if (
                    (!string.IsNullOrEmpty(packName) && !packName.Equals("Pack", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(consName) && !consName.Equals("Item", StringComparison.OrdinalIgnoreCase))
                )
                {
                    statusText.Append("<br>");
                }

                playerIndex++;  // Increment the player index

                // Add a divider between players 2 and 3, but not after the 4th player (saves 1 line)
                if (playerIndex == 1 || playerIndex == 2)
                {
                    statusText.Append("---------------------------<br>");
                }
            }

            // Refresh the watch display with the updated team status
            m_statusDisplay.text = statusText.ToString();
            m_statusDisplay.ForceMeshUpdate();

        }

        // Below is a coroutine for updating the status panel while it's currently active
        // I'm not sure if this is the best way to do this, but it works for now
        IEnumerator StatusUpdater()
        {
            while (m_currentState == WatchState.Status)
            {
                RefreshStatusDisplay();
                yield return new WaitForSeconds(STATUS_REFRESH);  // delay between refreshes
            }
            m_statusUpdater = null; // clear tracking field when switching away from the status screen
        }

        void StartStatusUpdater()
        {
            if (m_statusUpdater == null)
                m_statusUpdater = MelonCoroutines.Start(StatusUpdater()) as IEnumerator;
        }

        public void Setup(Transform parent)
        {
            m_inventoryMeshes = transform.FindDeepChild("Inventory_UI").GetComponentsInChildren<MeshRenderer>();

            SetupRadialMenu(parent);
            SetHandedness();
            SetupObjectiveDisplay();
            SetupChatDisplay();
            SetupStatusDisplay();  // Status Display
            SetupInventoryLinkData();
            SetInitialPlayerStatusValues();
            SwitchState(m_currentState);
            SetWatchScale();
        }

        private void SetupChatDisplay()
        {
            GameObject chatParent = transform.FindDeepChild("Chat").gameObject;

            RectTransform chatTransform = chatParent.GetComponent<RectTransform>();
            m_chatDisplay = chatParent.AddComponent<TextMeshPro>();

            m_chatDisplay.enableAutoSizing = true;
            m_chatDisplay.fontSizeMin = 18;
            m_chatDisplay.fontSizeMax = 36;
            m_chatDisplay.alignment = TextAlignmentOptions.Center;
            MelonCoroutines.Start(SetRectSize(chatTransform, new Vector2(34, 43f)));
        }

        void SetupStatusDisplay()
        {
            // Create a new object for status display
            GameObject statusParent = new GameObject("Status");
            statusParent.transform.SetParent(transform, false);

            // Clone the properties from the WardenObjective object so text fits properly
            // This is not the proper way to do this, but it was the easiest fix for now
            // We use the WardenObjective object and clone its position and scale for our Status object
            GameObject template = transform.FindDeepChild("WardenObjective").gameObject;
            statusParent.transform.localPosition = template.transform.localPosition;
            statusParent.transform.localRotation = template.transform.localRotation;
            statusParent.transform.localScale = template.transform.localScale;

            RectTransform statusTransform = statusParent.AddComponent<RectTransform>();
            m_statusDisplay = statusParent.AddComponent<TextMeshPro>();

            // Set text size and other properties
            m_statusDisplay.enableAutoSizing = true;
            m_statusDisplay.fontSizeMin = 18;
            m_statusDisplay.fontSizeMax = 36;
            m_statusDisplay.alignment = TextAlignmentOptions.Left;
            m_statusDisplay.richText = true;
            m_statusDisplay.enabled = false; // make invisible until selected
            MelonCoroutines.Start(SetRectSize(statusTransform, new Vector2(34, 43f)));
        }

        private void SetupRadialMenu(Transform parent)
        {
            m_watchRadialMenu = new GameObject("WatchRadial").AddComponent<RadialMenu>();
            m_watchRadialMenu.Setup(InteractionHand.Offhand, gameObject);
            m_watchRadialMenu.transform.SetParent(parent);
            m_watchRadialMenu.AddRadialItem("Inventory", SwitchToInventory, out RadialItem inventory);
            inventory.SetIcon(VRAssets.PrimaryFallback);
            inventory.SetInfoText("Inventory");

            m_watchRadialMenu.AddRadialItem("Objective", SwitchToObjective, out RadialItem objective);
            objective.SetIcon(VRAssets.Objective);
            objective.SetInfoText("Objective");

            m_watchRadialMenu.AddRadialItem("ChatType", TypeInChat, out RadialItem chatType);
            chatType.SetIcon(VRAssets.ChatType);
            chatType.SetInfoText("Type In Chat");

            m_watchRadialMenu.AddRadialItem("Chat", SwitchToChat, out RadialItem chat);
            chat.SetIcon(VRAssets.Chat);
            chat.SetInfoText("Chat");

            // Add a Status radial item
            m_watchRadialMenu.AddRadialItem("Status", SwitchToStatus, out RadialItem status);
            status.SetIcon(VRAssets.HackingToolFallback);  // Re-use the hacking tool icon for now
            status.SetInfoText("Team Status");
        }

        public void TypeInChat()
        {
            if (PlayerChatManager.Current != null && !PlayerChatManager.InChatMode)
            {
                PlayerChatManager.Current.EnterChatMode();
            }
        }

        public void SwitchToChat()
        {
            SwitchState(WatchState.Chat);
        }

        public void SwitchToInventory()
        {
            SwitchState(WatchState.Inventory);
        }

        public void SwitchToObjective()
        {
            SwitchState(WatchState.Objective);
        }

        public void SwitchToStatus()
        {
            SwitchState(WatchState.Status);
        }

        private void WatchColorChanged(object sender, EventArgs e)
        {
            SetWatchColor();
        }

        private void AmmoDisplayChanged(object sender, EventArgs e)
        {
            SwitchState(m_currentState);
        }

        void Start()
        {
            SetWatchColor();
        }

        private void SetWatchColor()
        {
            transform.FindDeepChild("STRAP_LP").GetComponent<MeshRenderer>().material.color = ExtensionMethods.FromString(VRConfig.configWatchColor.Value);
            transform.FindDeepChild("WATCH_LP").GetComponent<MeshRenderer>().material.color = ExtensionMethods.FromString(VRConfig.configWatchColor.Value);
        }

        void Update()
        {
            UpdateInput();
        }

        private void UpdateInput()
        {
            if (watchRadialMenu.GetStateDown(SteamVR_Input_Sources.Any))
            {
                m_watchRadialMenu.Show();
            }
            if (watchRadialMenu.GetStateUp(SteamVR_Input_Sources.Any))
            {
                m_watchRadialMenu.Hide();
            }
            if (toggleWatchMode.GetStateDown(SteamVR_Input_Sources.Any))
            {
                SwitchState();
            }
        }

        
        public void UpdateObjective(PUI_GameObjectives gameObjectives)
        {
            StringBuilder builder = new StringBuilder();

            // Main object is added to progressions too, as its subobjective. 
            foreach ( PUI_ProgressionObjective progression in gameObjectives.m_progressionObjectives)
            {
                if (progression == null)
                    continue;

                string header = progression.m_header?.text;
                if ( header != null)
                {
                    header = INDENT_REGEX.Replace(header, "");  // Indent breaks formatting
                    builder.Append(header);
                    builder.Append("\n");
                }

                string txt = progression.m_text?.text;
                if ( txt != null)
                {
                    txt = INDENT_REGEX.Replace(txt, "");
                    builder.Append(txt);
                }
            }
            this.m_ObjectiveText = builder.ToString();
            UpdateObjectiveDisplay();
        }
        public void UpdateObjectiveDisplay() {
            if (m_objectiveDisplay != null)
            {
                m_objectiveDisplay.text = m_ObjectiveText;
                m_objectiveDisplay.ForceMeshUpdate(false);
                SteamVR_InputHandler.TriggerHapticPulse(0.01f, 1 / .025f, 0.2f, Controllers.GetDeviceFromHandType(Controllers.offHandControllerType));
            }
        }

        public void UpdateInfection(float infection)
        {
            if (m_infectionDisplay)
            {
                if (infection < 0.01f)
                {
                    m_infectionDisplay.ToggleRendering(false);
                } else if(m_currentState == WatchState.Inventory)
                {
                    m_infectionDisplay.ToggleRendering(true);
                    m_infectionDisplay.UpdateFill((int) (infection * 100f));
                    m_infectionDisplay.SetFill(infection);
                    m_infectionDisplay.SetColor(Color.Lerp(m_normalInfectionCol, m_normalInfectionCol * 1.6f, infection));
                }
            }
        }

        public void UpdateHealth(float health)
        {
            if (m_healthDisplay)
            {
                m_healthDisplay.UpdateFill((int)(health * 100f));
                m_healthDisplay.SetColor(Color.Lerp(m_normalHealthCol, m_normalHealthCol * 1.8f, 1 - health));
            }
        }

        public void UpdateAir(float val)
        {
            if (m_oxygenDisplay)
            {
                if (val < .95f && m_currentState == WatchState.Inventory)
                {
                    m_oxygenDisplay.SetFill(val);
                    m_oxygenDisplay.UpdateFill((int)(val * 100f));
                    m_oxygenDisplay.ToggleRendering(true);

                    if (val < 0.5)
                    {
                        m_oxygenDisplay.SetColor(Color.Lerp(Color.red, m_normalOxygenCol, val * 1.6f));
                    }
                    else
                    {
                        m_oxygenDisplay.SetColor(Color.cyan);
                    }
                } else
                {
                    m_oxygenDisplay.ToggleRendering(false);
                }
            }
        }
        private void ItemSwitched(ItemEquippable item)
        {
            HandleSelectionEffect(item);
            UpdateBulletGridDivisions(item);
        }

        private void AmmoUpdate(InventorySlotAmmo item, int clipLeft)
        {
            UpdateBulletDisplayAmount(item, clipLeft);
            UpdateInventoryAmmoGrids(item, clipLeft);
        }

        private void HandleSelectionEffect(ItemEquippable item)
        {
            foreach (DividedBarShaderController d in m_inventoryToAmmoDisplayMapping.Values)
            {
                d.SetUnselected();
            }
            m_inventoryToAmmoDisplayMapping.TryGetValue(item.ItemDataBlock.inventorySlot, out DividedBarShaderController UIBar);

            if (UIBar)
            {
                UIBar.SetSelected();
            }
        }

        private void UpdateInventoryAmmoGrids(InventorySlotAmmo item, int clipLeft)
        {
            m_inventoryToAmmoDisplayMapping.TryGetValue(item.Slot, out DividedBarShaderController bar);
            if (bar)
            {
                bar.MaxValue = item.BulletsMaxCap;
                bar.CurrentValue = (int)(bar.MaxValue * item.RelInPack) + clipLeft;
                bar.SetFill(item.RelInPack);

                if (item.Slot.Equals(InventorySlot.GearStandard) || item.Slot.Equals(InventorySlot.GearSpecial))
                {
                    bar.UpdateWeaponMagDivisions(item.BulletClipSize, item.BulletsMaxCap);
                }

                if (item.Slot.Equals(InventorySlot.Consumable) || item.Slot.Equals(InventorySlot.ResourcePack) || item.Slot.Equals(InventorySlot.ConsumableHeavy))
                {
                    bar.UpdatePackOrConsumableDivisions();
                }
            }
        }

        private void UpdateBulletDisplayAmount(InventorySlotAmmo item, int clipLeft)
        {
            if (ItemEquippableEvents.IsCurrentItemShootableWeapon() &&
                ItemEquippableEvents.currentItem.ItemDataBlock.inventorySlot.Equals(item.Slot))
            {
                m_numberBulletsInMagDisplay.text = clipLeft + "\n----\n" + ((int)(item.BulletsMaxCap * item.RelInPack)).ToString();
                m_numberBulletsInMagDisplay.ForceMeshUpdate(false);

                m_bulletsInMagDisplay.MaxValue = Mathf.Max(item.BulletClipSize, 1);
                m_bulletsInMagDisplay.UpdateCurrentAmmo(clipLeft);
                m_bulletsInMagDisplay.UpdateAmmoGridDivisions();
            }
        }

        private void UpdateBulletGridDivisions(ItemEquippable item)
        {

            if (ItemEquippableEvents.IsCurrentItemShootableWeapon())
            {
                if (!VRConfig.configUseNumbersForAmmoDisplay.Value)
                {
                    m_bulletsInMagDisplay.MaxValue = item.GetMaxClip();
                    m_bulletsInMagDisplay.CurrentValue = item.GetCurrentClip();
                    m_bulletsInMagDisplay.UpdateAmmoGridDivisions();
                }
            }
            else
            {
                m_bulletsInMagDisplay.CurrentValue = 0;
                m_bulletsInMagDisplay.UpdateShaderVals(1, 1);

                m_numberBulletsInMagDisplay.text = "";
                m_numberBulletsInMagDisplay.ForceMeshUpdate(false);
            }
        }

        private void SetHandedness()
        {
            transform.SetParent(Controllers.OffhandController.transform);
            transform.localPosition = m_handOffset;
            if (!VRConfig.configUseLeftHand.Value)
            {
                transform.localRotation = m_leftHandRotationOffset;
            }
            else
            {
                transform.localRotation = m_rightHandRotationOffset;
            }
        }

        private void SetupObjectiveDisplay()
        {
            GameObject objectiveParent = transform.FindDeepChild("WardenObjective").gameObject;

            RectTransform watchObjectiveTransform = objectiveParent.GetComponent<RectTransform>();
            m_objectiveDisplay = objectiveParent.AddComponent<TextMeshPro>();

            m_objectiveDisplay.enableAutoSizing = true;
            m_objectiveDisplay.fontSizeMin = 18;
            m_objectiveDisplay.fontSizeMax = 36;
            m_objectiveDisplay.alignment = TextAlignmentOptions.Center;
            m_objectiveDisplay.faceColor = new Color32(255, 255, 255, 25); // Adjust alpha so all the text isn't just pure white
            MelonCoroutines.Start(SetRectSize(watchObjectiveTransform, new Vector2(34, 43f)));
        }

        IEnumerator SetRectSize(RectTransform t, Vector2 size)
        {
            yield return new WaitForEndOfFrame();
            t.sizeDelta = size;
        }

        private void SetupInventoryLinkData()
        {
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.GearStandard, transform.FindDeepChild("MainWeapon").gameObject.AddComponent<DividedBarShaderController>());
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.GearSpecial, transform.FindDeepChild("SubWeapon").gameObject.AddComponent<DividedBarShaderController>());
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.GearClass, transform.FindDeepChild("Tool").gameObject.AddComponent<DividedBarShaderController>());
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.ResourcePack, transform.FindDeepChild("Pack").gameObject.AddComponent<DividedBarShaderController>());
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.Consumable, transform.FindDeepChild("Consumable").gameObject.AddComponent<DividedBarShaderController>());
            m_inventoryToAmmoDisplayMapping.Add(InventorySlot.ConsumableHeavy, m_inventoryToAmmoDisplayMapping[InventorySlot.Consumable]);

            m_healthDisplay = transform.FindDeepChild("HP").gameObject.AddComponent<DividedBarShaderController>();
            m_oxygenDisplay = transform.FindDeepChild("Air").gameObject.AddComponent<DividedBarShaderController>();
            m_infectionDisplay = transform.FindDeepChild("Infection").gameObject.AddComponent<DividedBarShaderController>();

            m_numberBulletsInMagDisplay = transform.FindDeepChild("NumberedAmmo").gameObject.AddComponent<TextMeshPro>();

            m_numberBulletsInMagDisplay.lineSpacing = -30f;

            m_numberBulletsInMagDisplay.alignment = TextAlignmentOptions.Center;
            m_numberBulletsInMagDisplay.fontSize = 80f;
            m_numberBulletsInMagDisplay.enableWordWrapping = false;
            m_numberBulletsInMagDisplay.fontStyle = FontStyles.Bold;
            m_numberBulletsInMagDisplay.richText = true;
            m_numberBulletsInMagDisplay.color = DividedBarShaderController.NormalColor;
            m_bulletsInMagDisplay = transform.FindDeepChild("Ammo").gameObject.AddComponent<DividedBarShaderController>();
        }

        private void SetInitialPlayerStatusValues()
        {
            m_healthDisplay.SetColor(m_normalHealthCol);
            m_infectionDisplay.SetColor(m_normalInfectionCol);
            m_oxygenDisplay.SetColor(m_normalOxygenCol);

            m_healthDisplay.MaxValue = 100;
            m_healthDisplay.CurrentValue = 100;

            m_oxygenDisplay.MaxValue = 100;
            m_oxygenDisplay.CurrentValue = 100;

            m_infectionDisplay.MaxValue = 100;
            m_infectionDisplay.CurrentValue = 0;

            m_healthDisplay.UpdateShaderVals(5, 2);
            m_infectionDisplay.UpdateShaderVals(5, 2);
            m_oxygenDisplay.UpdateShaderVals(5, 2);

            UpdateAir(100f);
        }
        public void SwitchState()
        {
            int maxStateIndex = Enum.GetValues(typeof(WatchState)).Length - 1;
            int nextIndex = (int)m_currentState;

            while(true)
            {
                nextIndex++;
                if (nextIndex > maxStateIndex)
                    nextIndex = 0;

                // If current index is chat and we want to skip it, repeat loop.
                if (nextIndex == (int)WatchState.Chat && !VRConfig.configDisplayChatOnWatch.Value)
                    continue;

                break;
            }

            SwitchState((WatchState)nextIndex);
            SteamVR_InputHandler.TriggerHapticPulse(0.025f, 1 / .025f, 0.3f, Controllers.GetDeviceFromHandType(Controllers.offHandControllerType));
        }

        void SwitchState(WatchState state)
        {
            // Stop the status updater if we switched away from the status screen
            if (m_currentState == WatchState.Status && state != WatchState.Status && m_statusUpdater != null)
            {
                MelonCoroutines.Stop(m_statusUpdater);
                m_statusUpdater = null;
            }

            m_currentState = state;
            switch (state)
            {
                case (WatchState.Inventory):
                    ToggleInventoryRendering(true);
                    ToggleObjectiveRendering(false);
                    ToggleChatRendering(false);
                    ToggleStatusRendering(false);
                    break;
                case (WatchState.Objective):
                    ToggleInventoryRendering(false);
                    ToggleObjectiveRendering(true);
                    ToggleChatRendering(false);
                    ToggleStatusRendering(false);
                    break;
                case (WatchState.Chat):
                    ToggleInventoryRendering(false);
                    ToggleObjectiveRendering(false);
                    ToggleChatRendering(true);
                    ToggleStatusRendering(false);
                    break;
                case (WatchState.Status):
                    ToggleInventoryRendering(false);
                    ToggleObjectiveRendering(false);
                    ToggleChatRendering(false);
                    ToggleStatusRendering(true);
                    RefreshStatusDisplay();  // update the status info immediately
                    StartStatusUpdater();  // then keep upating while the Status panel is active
                    break;
            }
        }

        private void ToggleChatRendering(bool toggle)
        {
            m_chatDisplay.enabled = toggle;
            m_chatDisplay.ForceMeshUpdate(false);
        }

        void ToggleInventoryRendering(bool toggle)
        {
            foreach (MeshRenderer m in m_inventoryMeshes)
            {
                m.enabled = toggle;
            }

            if (VRConfig.configUseNumbersForAmmoDisplay.Value)
            {
                m_numberBulletsInMagDisplay.gameObject.SetActive(toggle);
                m_bulletsInMagDisplay.gameObject.SetActive(false);
                
            } else
            {
                m_numberBulletsInMagDisplay.gameObject.SetActive(false);
                m_bulletsInMagDisplay.gameObject.SetActive(toggle);
            }
            m_numberBulletsInMagDisplay.ForceMeshUpdate();
            //Force update to possibly disable/enable those bars depending on oxygen level/infection level
            UpdateAir(m_oxygenDisplay.CurrentValue / 100f);
            UpdateInfection(m_infectionDisplay.CurrentValue / 100f);
        }

        void ToggleObjectiveRendering(bool toggle)
        {
            m_objectiveDisplay.enabled = toggle;
            m_objectiveDisplay.ForceMeshUpdate();
        }

        private void ToggleStatusRendering(bool toggle)
        {
            m_statusDisplay.enabled = toggle;
            m_statusDisplay.ForceMeshUpdate();
        }

        private void WatchScaleChanged(object sender, EventArgs e)
        {
            SetWatchScale();
        }

        void SetWatchScale()
        {
            Vector3 watchScale = new Vector3(1.25f, 1.25f, 1.25f);
            watchScale *= VRConfig.configWatchScaling.Value;
            transform.localScale = watchScale;
        }

        void OnDestroy()
        {
            if(m_watchRadialMenu)
            {
                Destroy(m_watchRadialMenu);
            }
            if (m_statusUpdater != null)
            {
                MelonCoroutines.Stop(m_statusUpdater);
                m_statusUpdater = null;
            }
            ItemEquippableEvents.OnPlayerWieldItem -= ItemSwitched;
            InventoryAmmoEvents.OnInventoryAmmoUpdate -= AmmoUpdate;
            Controllers.HandednessSwitched -= SetHandedness;
            VRConfig.configUseNumbersForAmmoDisplay.SettingChanged -= AmmoDisplayChanged;
            VRConfig.configWatchScaling.SettingChanged -= WatchScaleChanged;
            VRConfig.configWatchColor.SettingChanged -= WatchColorChanged;
            VRConfig.configWatchInfoText.SettingChanged -= WatchRadialInfoTextChanged;
            ChatMsgEvents.OnChatMsgReceived -= ChatMsgReceived;
        }
    }
}
