﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace EditedPreload
{
    public class EditedPreload : BaseSettingsPlugin<PreloadAlertSettings>
    {
        public string Name = "Edited Plugin";
        private string PRELOAD_ALERTS => Path.Combine(DirectoryFullName, "config", "preload_alerts.txt");
        private string PRELOAD_ALERTS_PERSONAL => Path.Combine(DirectoryFullName, "config", "preload_alerts_personal.txt");

        public static Dictionary<string, PreloadConfigLine> PerandusLeague;
        public static Dictionary<string, PreloadConfigLine> Strongboxes;
        public static Dictionary<string, PreloadConfigLine> Preload;
        public static Dictionary<string, PreloadConfigLine> Bestiary;
        public static Color AreaNameColor;
        private readonly object _locker = new object();
        private Dictionary<string, PreloadConfigLine> alertStrings;
        private bool canRender;
        private DebugInformation debugInformation;
        private List<PreloadConfigLine> DrawAlerts = new List<PreloadConfigLine>();
        private bool essencefound;
        private readonly List<long> filesPtr = new List<long>();
        private bool foundSpecificPerandusChest;
        private bool isLoading;
        private Vector2 lastLine;
        private float maxWidth;
        private Dictionary<string, PreloadConfigLine> personalAlertStrings;
        private readonly List<string> PreloadDebug = new List<string>();
        private Action PreloadDebugAction;
        private bool working;

        public EditedPreload()
        {
            Order = -40;
        }

        // Ekranın üst köşesindeki alert listesi
        private Dictionary<string, PreloadConfigLine> alerts { get; } = new Dictionary<string, PreloadConfigLine>();
        private Action<string, Color> AddPreload => ExternalPreloads;

        public override void DrawSettings()
        {
            if (ImGui.Button("Dump preloads"))
            {
                Directory.CreateDirectory(Path.Combine(DirectoryFullName, "Dumps"));
                var path = Path.Combine(DirectoryFullName, "Dumps",
                    $"{GameController.Area.CurrentArea.Name} ({DateTime.Now}).txt");

                File.WriteAllLines(path, PreloadDebug);
            }

            if (ImGui.Button("Dump grouped preloads"))
            {
                var groupBy = PreloadDebug.OrderBy(x => x).GroupBy(x => x.IndexOf('/'));
                var serializeObject = JsonConvert.SerializeObject(groupBy, Formatting.Indented);

                var path = Path.Combine(DirectoryFullName, "Dumps",
                    $"{GameController.Area.CurrentArea.Name} ({DateTime.Now}).txt");

                File.WriteAllText(path, serializeObject);
            }

            if (ImGui.Button("Show all preloads"))
            {
                var groupBy = PreloadDebug.OrderBy(x => x).GroupBy(x => x.IndexOf('/')).ToList();
                var result = new Dictionary<string, List<string>>(groupBy.Count);

                foreach (var gr in groupBy)
                {
                    var g = gr.ToList();

                    if (gr.Key != -1)
                    {
                        var list = new List<string>(g.Count);
                        result[g.First().Substring(0, gr.Key)] = list;

                        foreach (var str in g)
                        {
                            list.Add(str);
                        }
                    }
                    else
                    {
                        var list = new List<string>(g.Count);
                        var key = gr.Key.ToString();
                        result[key] = list;

                        foreach (var str in g)
                        {
                            list.Add(str);
                        }
                    }
                }

                groupBy = null;

                PreloadDebugAction = () =>
                {
                    foreach (var res in result)
                    {
                        if (ImGui.TreeNode(res.Key))
                        {
                            foreach (var str in res.Value)
                            {
                                ImGui.Text(str);
                            }

                            ImGui.TreePop();
                        }
                    }

                    ImGui.Separator();

                    if (alerts.Count > 0)
                    {
                        if (ImGui.TreeNode("DrawAlerts"))
                        {
                            foreach (var alert in DrawAlerts)
                            {
                                ImGui.TextColored((alert.FastColor?.Invoke() ?? alert.Color ?? Settings.DefaultTextColor).ToImguiVec4(),
                                    $"{alert.Text}");
                            }

                            ImGui.TreePop();
                        }
                    }

                    if (ImGui.Button("Close")) PreloadDebugAction = null;
                };
            }

            base.DrawSettings();
        }

        private void ExternalPreloads(string text, Color color)
        {
            if (working)
            {
                Task.Run(async () =>
                {
                    var tries = 0;

                    while (working && tries < 20)
                    {
                        await Task.Delay(200);
                        tries++;
                    }

                    if (!working && tries < 20)
                    {
                        alerts.Add(text, new PreloadConfigLine { Text = text, FastColor = () => color });

                        lock (_locker)
                        {
                            DrawAlerts = alerts.OrderBy(x => x.Value.Text).Select(x => x.Value).ToList();
                        }
                    }
                });
            }
            else
            {
                alerts.Add(text, new PreloadConfigLine { Text = text, FastColor = () => color });

                lock (_locker)
                {
                    DrawAlerts = alerts.OrderBy(x => x.Value.Text).Select(x => x.Value).ToList();
                }
            }
        }

        public override void OnLoad()
        {
            alertStrings = LoadConfig(PRELOAD_ALERTS);
            SetupPredefinedConfigs();
            Graphics.InitImage("preload-start.png");
            Graphics.InitImage("preload-end.png");
            Graphics.InitImage("preload-new.png");
            if (File.Exists(PRELOAD_ALERTS_PERSONAL))
                alertStrings = alertStrings.MergeLeft(LoadConfig(PRELOAD_ALERTS_PERSONAL));
            else
                File.Create(PRELOAD_ALERTS_PERSONAL);
        }

        public override bool Initialise()
        {
            GameController.PluginBridge.SaveMethod($"{nameof(EditedPreload)}.{nameof(AddPreload)}", AddPreload);
            AreaNameColor = Settings.AreaTextColor;
            debugInformation = new DebugInformation("Preload alert parsing", false);
            /*GameController.Files.LoadedFiles += (sender, dictionary) =>
            {
                ParseByFiles(dictionary);
            };*/

            GameController.LeftPanel.WantUse(() => Settings.Enable);
            AreaChange(GameController.Area.CurrentArea);
            return true;
        }

        public override void AreaChange(AreaInstance area)
        {
            isLoading = true;
            alerts.Clear();

            lock (_locker)
            {
                DrawAlerts.Clear();
            }
            PreloadDebugAction = null;
            if (GameController.Area.CurrentArea.IsHideout && !Settings.ShowInHideout)
            {
                isLoading = false;
                return;
            }
            Core.ParallelRunner.Run(new Coroutine(Parse(), this, "Preload parse"));

            isLoading = false;
        }

        private IEnumerator Parse()
        {
            if (working) yield return null;

            working = true;
            PreloadDebug.Clear();

            Task.Run(() =>
            {
                debugInformation.TickAction(() =>
                {
                    try
                    {
                        GameController.Files.ReloadFiles();
                        var allFiles = GameController.Files.AllFiles;
                        foreach (var file in allFiles)
                        {
                            if (file.Value.ChangeCount != GameController.Game.AreaChangeCount) continue;

                            var text = file.Key;
                            if (text.Contains('@')) text = text.Split('@')[0];

                            lock (_locker)
                            {
                                PreloadDebug.Add(text);
                            }
                            CheckForPreload(text);
                        }
                    }
                    catch (Exception e)
                    {
                        DebugWindow.LogError($"{nameof(EditedPreload)} -> {e}");
                    }

                    lock (_locker)
                    {
                        DrawAlerts = alerts.OrderBy(x => x.Value.Text).Select(x => x.Value).ToList();
                    }
                });

                working = false;
            });

            yield return null;
        }

        public override Job Tick()
        {
            canRender = true;

            if (!Settings.Enable || GameController.Area.CurrentArea != null && GameController.Area.CurrentArea.IsTown ||
                GameController.IsLoading || !GameController.InGame)
            {
                canRender = false;
                return null;
            }

            if (GameController.Game.IngameState.IngameUi.StashElement.IsVisibleLocal)
            {
                canRender = false;
                return null;
            }

            var UIHover = GameController.Game.IngameState.UIHover;
            var miniMap = GameController.Game.IngameState.IngameUi.Map.SmallMiniMap;

            if (Settings.Enable.Value && UIHover?.Tooltip != null && UIHover.IsValid && UIHover.Address != 0x00 &&
                UIHover.Tooltip.Address != 0x00 && UIHover.Tooltip.IsVisibleLocal &&
                UIHover.Tooltip.GetClientRectCache.Intersects(miniMap.GetClientRectCache))
            {
                canRender = false;
                return null;
            }

            if (UIHover?.Tooltip != null && (!UIHover.IsValid || UIHover.Address == 0x00 || UIHover.Tooltip.Address == 0x00 ||
                                             !UIHover.Tooltip.IsVisibleLocal))
                canRender = true;

            if (Input.GetKeyState(Keys.F5)) AreaChange(GameController.Area.CurrentArea);

            return null;
        }

        public override void Render()
        {
            PreloadDebugAction?.Invoke();
            if (!canRender) return;
            var startDrawPoint = GameController.LeftPanel.StartDrawPoint;
            var f = startDrawPoint.Y;
            maxWidth = 0;

            if (isLoading)
            {
                lastLine = Graphics.DrawText("Loading...", startDrawPoint, Color.Orange, FontAlign.Right);
                startDrawPoint.Y += lastLine.Y;
                maxWidth = Math.Max(lastLine.X, maxWidth);
            }
            else
            {
                foreach (var line in DrawAlerts)
                {
                    lastLine = Graphics.DrawText(line.Text, startDrawPoint,
                        line.FastColor?.Invoke() ?? line.Color ?? Settings.DefaultTextColor, FontAlign.Right);

                    startDrawPoint.Y += lastLine.Y;
                    maxWidth = Math.Max(lastLine.X, maxWidth);
                }
            }

            var bounds = new RectangleF(GameController.LeftPanel.StartDrawPoint.X - maxWidth - 55,
                GameController.LeftPanel.StartDrawPoint.Y, maxWidth + 60, startDrawPoint.Y - f);

            Graphics.DrawImage("preload-new.png", bounds, Settings.BackgroundColor);
            GameController.LeftPanel.StartDrawPoint = startDrawPoint;
        }

        public Dictionary<string, PreloadConfigLine> LoadConfig(string path)
        {
            return LoadConfigBase(path, 3).ToDictionary(line => line[0], line =>
            {
                var preloadAlerConfigLine = new PreloadConfigLine { Text = line[1], Color = line.ConfigColorValueExtractor(2) };
                return preloadAlerConfigLine;
            });
        }

        protected static IEnumerable<string[]> LoadConfigBase(string path, int columnsCount = 2)
        {
            return File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line) && line.IndexOf(';') >= 0 && !line.StartsWith("#"))
                .Select(line => line.Split(new[] { ';' }, columnsCount).Select(parts => parts.Trim()).ToArray());
        }

        private void SetupPredefinedConfigs()
        {
            PerandusLeague = new Dictionary<string, PreloadConfigLine>
            {
                {
                    "Metadata/Chests/PerandusChests/PerandusChestStandard",
                    new PreloadConfigLine {Text = "Perandus Chest", FastColor = () => Settings.PerandusChestStandard}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestRarity",
                    new PreloadConfigLine {Text = "Perandus Cache", FastColor = () => Settings.PerandusChestRarity}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestQuantity",
                    new PreloadConfigLine {Text = "Perandus Hoard", FastColor = () => Settings.PerandusChestQuantity}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestCoins",
                    new PreloadConfigLine {Text = "Perandus Coffer", FastColor = () => Settings.PerandusChestCoins}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestJewellery",
                    new PreloadConfigLine {Text = "Perandus Jewellery Box", FastColor = () => Settings.PerandusChestJewellery}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestGems",
                    new PreloadConfigLine {Text = "Perandus Safe", FastColor = () => Settings.PerandusChestGems}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestCurrency",
                    new PreloadConfigLine {Text = "Perandus Treasury", FastColor = () => Settings.PerandusChestCurrency}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestInventory",
                    new PreloadConfigLine {Text = "Perandus Wardrobe", FastColor = () => Settings.PerandusChestInventory}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestDivinationCards",
                    new PreloadConfigLine {Text = "Perandus Catalogue", FastColor = () => Settings.PerandusChestDivinationCards}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestKeepersOfTheTrove",
                    new PreloadConfigLine {Text = "Perandus Trove", FastColor = () => Settings.PerandusChestKeepersOfTheTrove}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestUniqueItem",
                    new PreloadConfigLine {Text = "Perandus Locker", FastColor = () => Settings.PerandusChestUniqueItem}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestMaps",
                    new PreloadConfigLine {Text = "Perandus Archive", FastColor = () => Settings.PerandusChestMaps}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusChestFishing",
                    new PreloadConfigLine {Text = "Perandus Tackle Box", FastColor = () => Settings.PerandusChestFishing}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorUniqueChest",
                    new PreloadConfigLine {Text = "Cadiro's Locker", FastColor = () => Settings.PerandusManorUniqueChest}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorCurrencyChest",
                    new PreloadConfigLine {Text = "Cadiro's Treasury", FastColor = () => Settings.PerandusManorCurrencyChest}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorMapsChest",
                    new PreloadConfigLine {Text = "Cadiro's Archive", FastColor = () => Settings.PerandusManorMapsChest}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorJewelryChest",
                    new PreloadConfigLine {Text = "Cadiro's Jewellery Box", FastColor = () => Settings.PerandusManorJewelryChest}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorDivinationCardsChest",
                    new PreloadConfigLine {Text = "Cadiro's Catalogue", FastColor = () => Settings.PerandusManorDivinationCardsChest}
                },
                {
                    "Metadata/Chests/PerandusChests/PerandusManorLostTreasureChest",
                    new PreloadConfigLine {Text = "Grand Perandus Vault", FastColor = () => Settings.PerandusManorLostTreasureChest}
                }
            };

            Strongboxes = new Dictionary<string, PreloadConfigLine>
            {
                {
                    "Metadata/Chests/StrongBoxes/Arcanist",
                    new PreloadConfigLine {Text = "Arcanist's Strongbox", FastColor = () => Settings.ArcanistStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Artisan",
                    new PreloadConfigLine {Text = "Artisan's Strongbox", FastColor = () => Settings.ArtisanStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Cartographer",
                    new PreloadConfigLine {Text = "Cartographer's Strongbox", FastColor = () => Settings.CartographerStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Diviner",
                    new PreloadConfigLine {Text = "Diviner's Strongbox", FastColor = () => Settings.DivinerStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/StrongboxDivination",
                    new PreloadConfigLine {Text = "Diviner's Strongbox", FastColor = () => Settings.DivinerStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Gemcutter",
                    new PreloadConfigLine {Text = "Gemcutter's Strongbox", FastColor = () => Settings.GemcutterStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Jeweller",
                    new PreloadConfigLine {Text = "Jeweller's Strongbox", FastColor = () => Settings.JewellerStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Arsenal",
                    new PreloadConfigLine {Text = "Blacksmith's Strongbox", FastColor = () => Settings.BlacksmithStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Armory",
                    new PreloadConfigLine {Text = "Armourer's Strongbox", FastColor = () => Settings.ArmourerStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Ornate",
                    new PreloadConfigLine {Text = "Ornate Strongbox", FastColor = () => Settings.OrnateStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Large",
                    new PreloadConfigLine {Text = "Large Strongbox", FastColor = () => Settings.LargeStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/Strongbox",
                    new PreloadConfigLine {Text = "Simple Strongbox", FastColor = () => Settings.SimpleStrongbox}
                },
                {
                    "Metadata/Chests/CopperChests/CopperChestEpic3",
                    new PreloadConfigLine {Text = "Epic Chest", FastColor = () => Settings.EpicStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/PerandusBox",
                    new PreloadConfigLine {Text = "Perandus Strongbox", FastColor = () => Settings.PerandusStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/KaomBox",
                    new PreloadConfigLine {Text = "Kaom Strongbox", FastColor = () => Settings.KaomStrongbox}
                },
                {
                    "Metadata/Chests/StrongBoxes/MalachaisBox",
                    new PreloadConfigLine {Text = "Malachai Strongbox", FastColor = () => Settings.MalachaiStrongbox}
                }
            };

            Preload = new Dictionary<string, PreloadConfigLine>
            {
                {"Wild/StrDexInt", new PreloadConfigLine {Text = "Zana, Master Cartographer", FastColor = () => Settings.MasterZana}},
                {"Wild/Int", new PreloadConfigLine {Text = "Catarina, Master of the Dead", FastColor = () => Settings.MasterCatarina}},
                {"Wild/Dex", new PreloadConfigLine {Text = "Tora, Master of the Hunt", FastColor = () => Settings.MasterTora}},
                {"Wild/DexInt", new PreloadConfigLine {Text = "Vorici, Master Assassin", FastColor = () => Settings.MasterVorici}},
                {"Wild/Str", new PreloadConfigLine {Text = "Haku, Armourmaster", FastColor = () => Settings.MasterHaku}},
                {"Wild/StrInt", new PreloadConfigLine {Text = "Elreon, Loremaster", FastColor = () => Settings.MasterElreon}},
                {"Wild/Fish", new PreloadConfigLine {Text = "Krillson, Master Fisherman", FastColor = () => Settings.MasterKrillson}},
                {
                    "MasterStrDex1",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (2HSword)", FastColor = () => Settings.MasterVagan}
                },
                {"MasterStrDex2", new PreloadConfigLine {Text = "Vagan, Weaponmaster (Staff)", FastColor = () => Settings.MasterVagan}},
                {"MasterStrDex3", new PreloadConfigLine {Text = "Vagan, Weaponmaster (Bow)", FastColor = () => Settings.MasterVagan}},
                {
                    "MasterStrDex4",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (DaggerRapier)", FastColor = () => Settings.MasterVagan}
                },
                {"MasterStrDex5", new PreloadConfigLine {Text = "Vagan, Weaponmaster (Blunt)", FastColor = () => Settings.MasterVagan}},
                {
                    "MasterStrDex6",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Blades)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex7",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (SwordAxe)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex8",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Punching)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex9",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Flickerstrike)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex10",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Elementalist)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex11",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Cyclone)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex12",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (PhysSpells)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex13",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (Traps)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex14",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (RighteousFire)", FastColor = () => Settings.MasterVagan}
                },
                {
                    "MasterStrDex15",
                    new PreloadConfigLine {Text = "Vagan, Weaponmaster (CastOnHit)", FastColor = () => Settings.MasterVagan}
                },
                {"ExileDuelist1", new PreloadConfigLine {Text = "Exile Torr Olgosso", FastColor = () => Settings.TorrOlgosso}},
                {"ExileDuelist2", new PreloadConfigLine {Text = "Exile Armios Bell", FastColor = () => Settings.ArmiosBell}},
                {
                    "ExileDuelist4",
                    new PreloadConfigLine {Text = "Exile Zacharie Desmarais", FastColor = () => Settings.ZacharieDesmarais}
                },
                {"ExileDuelist5", new PreloadConfigLine {Text = "Exile Oyra Ona", FastColor = () => Settings.OyraOna}},
                {"ExileMarauder1", new PreloadConfigLine {Text = "Exile Jonah Unchained", FastColor = () => Settings.JonahUnchained}},
                {"ExileMarauder2", new PreloadConfigLine {Text = "Exile Damoi Tui", FastColor = () => Settings.DamoiTui}},
                {
                    "ExileMarauder3",
                    new PreloadConfigLine {Text = "Exile Xandro Blooddrinker", FastColor = () => Settings.XandroBlooddrinker}
                },
                {"ExileMarauder5", new PreloadConfigLine {Text = "Exile Vickas Giantbone", FastColor = () => Settings.VickasGiantbone}},
                {"ExileMarauder6__", new PreloadConfigLine {Text = "Exile Bolt Brownfur", FastColor = () => Settings.BoltBrownfur}},
                {"ExileRanger1", new PreloadConfigLine {Text = "Exile Orra Greengate", FastColor = () => Settings.OrraGreengate}},
                {"ExileRanger2", new PreloadConfigLine {Text = "Exile Thena Moga", FastColor = () => Settings.ThenaMoga}},
                {"ExileRanger3", new PreloadConfigLine {Text = "Exile Antalie Napora", FastColor = () => Settings.AntalieNapora}},
                {"ExileRanger5", new PreloadConfigLine {Text = "Exile Ailentia Rac", FastColor = () => Settings.AilentiaRac}},
                {"ExileScion2", new PreloadConfigLine {Text = "Exile Augustina Solaria", FastColor = () => Settings.AugustinaSolaria}},
                {"ExileScion3", new PreloadConfigLine {Text = "Exile Lael Furia", FastColor = () => Settings.LaelFuria}},
                {"ExileScion4", new PreloadConfigLine {Text = "Exile Vanth Agiel", FastColor = () => Settings.VanthAgiel}},
                {"ExileShadow1_", new PreloadConfigLine {Text = "Exile Ion Darkshroud", FastColor = () => Settings.IonDarkshroud}},
                {"ExileShadow2", new PreloadConfigLine {Text = "Exile Ash Lessard", FastColor = () => Settings.AshLessard}},
                {
                    "ExileShadow4",
                    new PreloadConfigLine {Text = "Exile Wilorin Demontamer", FastColor = () => Settings.WilorinDemontamer}
                },
                {"ExileShadow5", new PreloadConfigLine {Text = "Exile Ulysses Morvant", FastColor = () => Settings.UlyssesMorvant}},
                {"ExileTemplar1", new PreloadConfigLine {Text = "Exile Eoin Greyfur", FastColor = () => Settings.EoinGreyfur}},
                {"ExileTemplar2", new PreloadConfigLine {Text = "Exile Tinevin Highdove", FastColor = () => Settings.TinevinHighdove}},
                {
                    "ExileTemplar4",
                    new PreloadConfigLine {Text = "Exile Magnus Stonethorn", FastColor = () => Settings.MagnusStonethorn}
                },
                {
                    "ExileTemplar5",
                    new PreloadConfigLine {Text = "Exile Aurelio Voidsinger", FastColor = () => Settings.AurelioVoidsinger}
                },
                {"ExileWitch1", new PreloadConfigLine {Text = "Exile Minara Anenima", FastColor = () => Settings.MinaraAnenima}},
                {"ExileWitch2", new PreloadConfigLine {Text = "Exile Igna Phoenix", FastColor = () => Settings.IgnaPhoenix}},
                {"ExileWitch4", new PreloadConfigLine {Text = "Exile Dena Lorenni", FastColor = () => Settings.DenaLorenni}}
            };

            //Old stuff from bestiary league
            Bestiary = new Dictionary<string, PreloadConfigLine>();
        }

        private void CheckForPreload(string text)
        {
            if (alertStrings.ContainsKey(text))
            {
                lock (_locker)
                {
                    alerts[alertStrings[text].Text] = alertStrings[text];
                }

                return;
            }

            if (text.Contains("Metadata/Terrain/Doodads/vaal_sidearea_effects/soulcoaster.ao"))
            {
                if (Settings.CorruptedTitle)
                {
                    // using corrupted titles so set the color here, XpRatePlugin will grab the color to use when drawing the title.
                    AreaNameColor = Settings.CorruptedAreaColor;
                    GameController.Area.CurrentArea.AreaColorName = AreaNameColor;
                }
                else
                {
                    // not using corrupted titles, so throw it in a preload alert
                    lock (_locker)
                    {
                        alerts[text] = new PreloadConfigLine { Text = "Corrupted Area", FastColor = () => Settings.CorruptedAreaColor };
                    }
                }

                return;
            }

            var perandus_alert = PerandusLeague.Where(kv => text.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value).FirstOrDefault();

            if (perandus_alert != null && Settings.PerandusBoxes)
            {
                foundSpecificPerandusChest = true;

                if (alerts.ContainsKey("Unknown Perandus Chest"))
                {
                    lock (_locker)
                    {
                        alerts.Remove("Unknown Perandus Chest");
                    }
                }

                lock (_locker)
                {
                    alerts.Add(perandus_alert.Text, perandus_alert);
                }

                return;
            }

            if (Settings.PerandusBoxes && !foundSpecificPerandusChest && text.StartsWith("Metadata/Chests/PerandusChests"))
            {
                lock (_locker)
                {
                    alerts["Unknown Perandus Chest"] = new PreloadConfigLine
                    {
                        Text = "Unknown Perandus Chest",
                        FastColor = () => Settings.PerandusChestStandard
                    };
                }
            }

            var _alert = Strongboxes.Where(kv => text.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value)
                .FirstOrDefault();

            if (_alert != null && Settings.Strongboxes)
            {
                lock (_locker)
                {
                    alerts[_alert.Text] = _alert;
                }

                return;
            }

            var alert = Preload.Where(kv => text.EndsWith(kv.Key, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value)
                .FirstOrDefault();

            if (alert != null && Settings.Exiles)
            {
                lock (_locker)
                {
                    alerts[alert.Text] = alert;
                }
            }
        }
    }
}