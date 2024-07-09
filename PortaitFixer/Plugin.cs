﻿using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Veda;

namespace PortraitFixer
{
    public unsafe class Plugin : IDalamudPlugin
    {
        public unsafe string Name => "PortraitFixer";

        [PluginService] public static IDalamudPluginInterface PluginInterface { get; set; }
        [PluginService] public static ICommandManager Commands { get; set; }
        [PluginService] public static ICondition Conditions { get; set; }
        [PluginService] public static IDataManager Data { get; set; }
        [PluginService] public static IFramework Framework { get; set; }
        [PluginService] public static IGameGui GameGui { get; set; }
        [PluginService] public static ISigScanner SigScanner { get; set; }
        [PluginService] public static IKeyState KeyState { get; set; }
        [PluginService] public static IChatGui Chat { get; set; }
        [PluginService] public static IClientState ClientState { get; set; }
        [PluginService] public static IPluginLog PluginLog { get; set; }
        [PluginService] public static IGameInteropProvider HookProvider { get; private set; } = null!;

        private PluginCommandManager<Plugin> commandManager;
        private PluginUI ui;
        public static Configuration PluginConfig { get; set; }
        private bool drawConfigWindow;

        public static readonly Queue<Func<bool>> actionQueue = new();
        private readonly Stopwatch sw = new();
        private static uint Delay = 0;

        private Hook<RaptureGearsetDelegate> onUpdateGearsetHook;

        private delegate void RaptureGearsetDelegate(RaptureShellModule* RaptureShellModule, RaptureGearsetModule* GearsetStuff);

        public unsafe Plugin(IDalamudPluginInterface pluginInterface, IChatGui chat, ICommandManager commands, IFramework framework, ISigScanner sigScanner)
        {
            PluginInterface = pluginInterface;
            Chat = chat;
            Framework = framework;
            SigScanner = sigScanner;

            // Get or create a configuration object
            PluginConfig = (Configuration)PluginInterface.GetPluginConfig() ?? new Configuration();
            PluginConfig.Initialize(PluginInterface);

            ui = new PluginUI();
            PluginInterface.UiBuilder.Draw += new System.Action(ui.Draw);
            PluginInterface.UiBuilder.OpenConfigUi += () =>
            {
                PluginUI ui = this.ui;
                ui.IsVisible = !ui.IsVisible;
            };

            // Load all of our commands
            this.commandManager = new PluginCommandManager<Plugin>(this, commands);

            Framework.Update += OnFrameworkUpdate;

            onUpdateGearsetHook = HookProvider.HookFromAddress<RaptureGearsetDelegate>(new nint(RaptureGearsetModule.MemberFunctionPointers.UpdateGearset), OnUpdateGearset);
            onUpdateGearsetHook.Enable();
        }

        private unsafe void OnUpdateGearset(RaptureShellModule* RaptureShellModule, RaptureGearsetModule* GearsetStuff)
        {
            onUpdateGearsetHook?.Original(RaptureShellModule, GearsetStuff);
            if (PluginConfig.AutoUpdatePortaitFromGearsetUpdate)
            {
                if (PluginConfig.ShowMessageInChatWhenAutoUpdatingPortaitFromGearsetUpdate) 
                {
                    SavePortait("Gearset updated");
                }
                else
                {
                    SavePortait("",true);
                }
            }
            
        }

        [Command("/pfixsave")]
        [HelpMessage("Updates the portait for the currently selected gearset/equipment")]
        public void PortraitFixSave(string command, string args)
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("Character");
            if (addon == null || addon->IsVisible == false)
            {
                Chat.PrintError("You can only use this command while the character menu is open.");
                return;
            }
            SavePortait();
        }

        [Command("/pfixconfig")]
        [HelpMessage("Show the Portait Fixer settings")]
        public void ToggleConfig(string command, string args)
        {
            ui.IsVisible = !ui.IsVisible;
        }

        public void SavePortait(string ExtraInfo = "", bool Silent = false)
        {
            Plugin.actionQueue.Enqueue(Plugin.OpenGearSetMenu);
            Plugin.actionQueue.Enqueue(Plugin.RightClickOnGearSet);
            Plugin.actionQueue.Enqueue(Plugin.OpenPortaitMenu);
            Plugin.actionQueue.Enqueue(Plugin.CheckForPortaitEditor);
            Plugin.actionQueue.Enqueue(Plugin.PressSaveOnPortaitMenuAndClose);
            if (!Silent)
            {
                if (ExtraInfo == "")
                {
                    Plugin.Chat.Print("[PortraitFix] Portait saved!");
                }
                else
                {
                    Plugin.Chat.Print("[PortraitFix] " + ExtraInfo + " - portait saved!");
                }
            }
        }

        public static Func<bool> VariableDelay(uint frameDelay)
        {
            return () =>
            {
                Delay = frameDelay;
                return true;
            };
        }

        private void OnFrameworkUpdate(IFramework framework)
        {
            if (actionQueue.Count == 0)
            {
                if (sw.IsRunning) sw.Stop();
                return;
            }
            if (!sw.IsRunning) sw.Restart();

            if (Delay > 0)
            {
                Delay -= 1;
                return;
            }

            if (sw.ElapsedMilliseconds > 30000)
            {
                actionQueue.Clear();
                return;
            }

            try
            {
                var hasNext = actionQueue.TryPeek(out var next);
                if (hasNext)
                {
                    if (next())
                    {
                        actionQueue.Dequeue();
                        sw.Reset();
                    }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"Failed: {ex.ToString()}");
            }
        }

        public static unsafe bool OpenGearSetMenu()
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("Character");
            if (addon == null || addon->IsVisible == false) return false;
            PluginLog.Debug("Found Character Menu");
            GenerateCallback(addon, 14, 2297, 454);
            var nextAddon = (AtkUnitBase*)GameGui.GetAddonByName("GearSetList");
            if (nextAddon == null) return false;
            nextAddon->Hide(true, true, 0);
            PluginLog.Debug("Found GearSetList");
            return true;
        }

        public static unsafe bool RightClickOnGearSet()
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("GearSetList");
            if (addon == null) return false;
            PluginLog.Debug("Found GearSetList");
            GenerateCallback(addon, 5, 5);
            var nextAddon = (AtkUnitBase*)GameGui.GetAddonByName("ContextMenu");
            if (nextAddon == null) return false;
            nextAddon->Hide(true, true, 0);
            PluginLog.Debug("Found ContextMenu");
            return true;
        }

        public static unsafe bool OpenPortaitMenu()
        {
            var previousAddon = (AtkUnitBase*)GameGui.GetAddonByName("BannerEditor");
            if (previousAddon != null)
            {
                PluginLog.Debug("Found BannerEditor, not trying to open again");
                return true;
            }
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("ContextMenu");
            if (addon == null) return false;
            PluginLog.Debug("Found ContextMenu");
            addon->Hide(true, true, 0);
            GenerateCallback(addon, 0, 8, 0);
            actionQueue.Dequeue();
            return true;
        }

        public static unsafe bool CheckForPortaitEditor()
        {
            var nextAddon = (AtkUnitBase*)GameGui.GetAddonByName("BannerEditor");
            if (nextAddon != null)
            {
                if (nextAddon->IsFullyLoaded())
                {
                    PluginLog.Debug("Found Portrait Editor!");
                    nextAddon->Hide(true, true, 0);
                    return true;
                }
                else
                {
                    //PluginLog.Debug("Didn't find Portrait Editor :c");
                    return false;
                }
            }
            else
            {
                //PluginLog.Debug("Didn't find Portrait Editor :c");
                return false;
            }
        }

        public static unsafe bool PressSaveOnPortaitMenuAndClose()
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("BannerEditor");
            if (addon == null) return false;
            PluginLog.Debug("Found BannerEditor");
            //addon->Hide(true, true, 0);
            GenerateCallback(addon, 0, 9, -1, -1);
            actionQueue.Clear();
            addon->Close(true);
            var nextAddon = (AtkUnitBase*)GameGui.GetAddonByName("BannerEditor");
            if (nextAddon != null) return false;
            PluginLog.Debug("BannerEditor was closed!");
            return true;
        }

        public static unsafe bool CloseGearSetMenu()
        {
            var addon = (AtkUnitBase*)GameGui.GetAddonByName("GearSetList");
            if (addon == null || addon->IsVisible == false) return false;
            PluginLog.Debug("Found GearSetList");
            addon->Close(true);
            var nextAddon = (AtkUnitBase*)GameGui.GetAddonByName("GearSetList");
            if (nextAddon == null) return false;
            PluginLog.Debug("Found GearSetList");
            return true;
        }

        public static unsafe void GenerateCallback(AtkUnitBase* unitBase, params object[] values)
        {
            if (unitBase == null) throw new Exception("Null UnitBase");
            var atkValues = (AtkValue*)Marshal.AllocHGlobal(values.Length * sizeof(AtkValue));
            if (atkValues == null) return;
            try
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var v = values[i];
                    switch (v)
                    {
                        case uint uintValue:
                            atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt;
                            atkValues[i].UInt = uintValue;
                            break;

                        case int intValue:
                            atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                            atkValues[i].Int = intValue;
                            break;

                        case float floatValue:
                            atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Float;
                            atkValues[i].Float = floatValue;
                            break;

                        case bool boolValue:
                            atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool;
                            atkValues[i].Byte = (byte)(boolValue ? 1 : 0);
                            break;

                        case string stringValue:
                            {
                                atkValues[i].Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String;
                                var stringBytes = Encoding.UTF8.GetBytes(stringValue);
                                var stringAlloc = Marshal.AllocHGlobal(stringBytes.Length + 1);
                                Marshal.Copy(stringBytes, 0, stringAlloc, stringBytes.Length);
                                Marshal.WriteByte(stringAlloc, stringBytes.Length, 0);
                                atkValues[i].String = (byte*)stringAlloc;
                                break;
                            }
                        default:
                            throw new ArgumentException($"Unable to convert type {v.GetType()} to AtkValue");
                    }
                }

                unitBase->FireCallback((uint)values.Length, atkValues);
            }
            finally
            {
                for (var i = 0; i < values.Length; i++)
                {
                    if (atkValues[i].Type == FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String)
                    {
                        Marshal.FreeHGlobal(new IntPtr(atkValues[i].String));
                    }
                }
                Marshal.FreeHGlobal(new IntPtr(atkValues));
            }
        }

        #region IDisposable Support

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;

            this.commandManager.Dispose();

            PluginInterface.SavePluginConfig(PluginConfig);

            PluginInterface.UiBuilder.Draw -= ui.Draw;
            PluginInterface.UiBuilder.OpenConfigUi -= () =>
            {
                PluginUI ui = this.ui;
                ui.IsVisible = !ui.IsVisible;
            };

            Framework.Update -= OnFrameworkUpdate;
            onUpdateGearsetHook.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);

            //Marshal.FreeHGlobal(textPtr);
        }

        #endregion IDisposable Support
    }
}