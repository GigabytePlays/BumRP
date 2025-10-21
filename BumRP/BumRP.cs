using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using UIScreen = GTA.UI.Screen;

public class BumRP : Script
{
    // ==== CONFIG ====
    private readonly Keys KeySearch = Keys.E;
    private readonly Keys KeySell = Keys.G;
    private const float InteractDistance = 1.5f;
    private const int DumpsterCooldownSeconds = 120;
    private const string SaveFile = "scripts\\BumRP_inventory.txt";

    // Ponto de venda
    private readonly Vector3 SellPoint = new Vector3(-428.8f, -1728.2f, 19.8f);
    private Blip sellBlip;

    // Modelos de lixeira/caçamba
    private readonly string[] DumpsterModels = new string[]
    {
        "prop_bin_05a",
        "prop_dumpster_01a",
        "prop_dumpster_02a",
        "prop_dumpster_02b",
    };

    // Inventário nome->qtd
    private readonly Dictionary<string, int> inventory =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    // Cooldown por handle do prop
    private readonly Dictionary<int, DateTime> dumpsterCooldown =
        new Dictionary<int, DateTime>();

    // Tabela de loot
    private readonly List<LootItem> lootTable;
    private readonly Random rng = new Random();

    public BumRP()
    {
        lootTable = new List<LootItem>();
        // Comuns
        lootTable.Add(new LootItem("Crushed Can", 1, 1800, 3));
        lootTable.Add(new LootItem("Wet Cardboard", 2, 1700, 3));
        lootTable.Add(new LootItem("Plastic Spoon", 1, 1500, 3));
        lootTable.Add(new LootItem("Broken Rope", 3, 1100, 1));
        lootTable.Add(new LootItem("Rusty Nails", 3, 1500, 2));
        lootTable.Add(new LootItem("Torn Fabric", 2, 2010, 2));
        // Incomuns
        lootTable.Add(new LootItem("Burned Circuit Board", 12, 65, 1));
        lootTable.Add(new LootItem("Broken Tool", 25, 55, 1));
        lootTable.Add(new LootItem("Lighter", 9, 70, 2));
        lootTable.Add(new LootItem("Old Flash Drive", 28, 45, 1));
        lootTable.Add(new LootItem("Copper Wire", 15, 65, 1));
        // Raros
        lootTable.Add(new LootItem("Broken Phone", 45, 35, 1));
        lootTable.Add(new LootItem("Old Charger", 22, 40, 1));
        lootTable.Add(new LootItem("Silver Coins", 20, 45, 2));
        // Épicos
        lootTable.Add(new LootItem("Broken Watch", 60, 20, 1));
        lootTable.Add(new LootItem("Old Phone", 70, 20, 1));
        // Lendários
        lootTable.Add(new LootItem("Gold Ring", 200, 15, 1));

        Tick += OnTick;
        KeyDown += OnKeyDown;

        // Blip do ponto de venda
        sellBlip = World.CreateBlip(SellPoint);
        sellBlip.Sprite = BlipSprite.DollarSign;
        sellBlip.Color = BlipColor.Green;
        sellBlip.Name = "Scrap Yard";
        sellBlip.IsShortRange = true;

        LoadInventory();

        Notification.Show("~b~BumRP~s~ loaded! Press ~y~E~s~ to search dumpsters/bins and ~g~G~s~ to sell at $.");
    }

    private void OnTick(object sender, EventArgs e)
    {
        Ped ped = Game.Player.Character;

        // Dica quando perto de lixeira
        Prop dumpster = GetClosestDumpster(ped.Position, 2.5f);
        if (dumpster != null)
        {
            bool can = CanSearchDumpster(dumpster.Handle);
            UIScreen.ShowSubtitle(can ? "~y~E~s~ Search for scrap"
                                      : "~o~Wait for this dumpster's cooldown...", 10);
        }

        // Dica no ponto de venda
        if (ped.Position.DistanceTo(SellPoint) <= InteractDistance + 1.0f)
        {
            UIScreen.ShowSubtitle("~g~G~s~ Sell scrap", 10);
            World.DrawMarker(MarkerType.Halo, SellPoint, Vector3.Zero, Vector3.Zero,
                             new Vector3(1f, 1f, 1f), Color.Green);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        Ped ped = Game.Player.Character;

        // Vasculhar
        if (e.KeyCode == KeySearch)
        {
            Prop dumpster = GetClosestDumpster(ped.Position, 2.5f);
            if (dumpster == null) return;

            if (!CanSearchDumpster(dumpster.Handle))
            {
                Notification.Show("Nothing found... try another place or wait.");
                return;
            }

            // 50% chance de nada
            if (rng.NextDouble() < 0.50)
            {
                Notification.Show("You searched, but ~o~found nothing~s~.");
                SetDumpsterCooldown(dumpster.Handle);
                return;
            }

            int rolls = RandomInt(1, 2);
            List<string> foundList = new List<string>();

            for (int i = 0; i < rolls; i++)
            {
                LootItem item = RollLoot();
                int qty = RandomInt(1, item.MaxQtyPerRoll);
                AddItem(item.Name, qty);
                foundList.Add(item.Name + " x" + qty);
            }

            Notification.Show("You found: ~b~" + string.Join(", ", foundList) + "~s~.");
            SetDumpsterCooldown(dumpster.Handle);

            Function.Call(Hash.TASK_PAUSE, ped, 700);
        }

        // Vender
        if (e.KeyCode == KeySell)
        {
            if (ped.Position.DistanceTo(SellPoint) > InteractDistance + 0.2f) return;

            int total = ComputeInventoryValue();
            if (total <= 0)
            {
                Notification.Show("You have nothing to sell.");
                return;
            }

            Game.Player.Money += total;
            inventory.Clear();
            SaveInventory();
            Notification.Show(
                NotificationIcon.Lester,
                "Scrap Yard",
                "~g~Sucessful sale",
                $"All items sold for ~g~${total}~s~."
            );
        }
    }

    // ==== Lixeira ====
    private Prop GetClosestDumpster(Vector3 pos, float radius)
    {
        Model[] models = DumpsterModels.Select(m => new Model(m)).ToArray();
        Prop[] props = World.GetNearbyProps(pos, radius, models);
        return props.FirstOrDefault();
    }

    private bool CanSearchDumpster(int handle)
    {
        DateTime next;
        if (!dumpsterCooldown.TryGetValue(handle, out next)) return true;
        return DateTime.UtcNow >= next;
    }

    private void SetDumpsterCooldown(int handle)
    {
        dumpsterCooldown[handle] = DateTime.UtcNow.AddSeconds(DumpsterCooldownSeconds);
    }

    private LootItem RollLoot()
    {
        int total = lootTable.Sum(i => i.Weight);
        int roll = rng.Next(1, total + 1);
        int acc = 0;
        foreach (LootItem it in lootTable)
        {
            acc += it.Weight;
            if (roll <= acc) return it;
        }
        return lootTable[0];
    }

    private int RandomInt(int min, int maxInclusive)
    {
        return rng.Next(min, maxInclusive + 1);
    }

    // ==== Inventário ====
    private void AddItem(string name, int qty)
    {
        if (!inventory.ContainsKey(name)) inventory[name] = 0;
        inventory[name] += qty;
        SaveInventory();
        UIScreen.ShowSubtitle(name + " x" + qty + " (Total: " + inventory[name] + ")", 3000);
    }

    private int ComputeInventoryValue()
    {
        int total = 0;
        foreach (KeyValuePair<String, int> kv in inventory)
        {
            LootItem item = lootTable.FirstOrDefault(i => i.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
            if (item != null) total += kv.Value * item.Value;
        }
        return total;
    }

    private void SaveInventory()
    {
        try
        {
            Directory.CreateDirectory("scripts");
            using (var sw = new StreamWriter(SaveFile, false))
            {
                foreach (KeyValuePair<String, int> kv in inventory)
                    sw.WriteLine(kv.Key + "=" + kv.Value);
            }
        }
        catch (Exception ex)
        {
            Notification.Show("Error saving inventory: " + ex.Message);
        }
    }

    private void LoadInventory()
    {
        try
        {
            if (!File.Exists(SaveFile)) return;
            foreach (string line in File.ReadAllLines(SaveFile))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains("=")) continue;
                string[] parts = line.Split(new[] { '=' }, 2);
                string name = parts[0].Trim();
                int qty;
                if (int.TryParse(parts[1].Trim(), out qty) && qty > 0)
                    inventory[name] = qty;
            }
        }
        catch (Exception ex)
        {
            Notification.Show("Error loading inventory: " + ex.Message);
        }
    }

    // Classe simples (compatível com C# 7.3)
    private class LootItem
    {
        public string Name;
        public int Value;
        public int Weight;
        public int MaxQtyPerRoll;
        public LootItem(string name, int value, int weight, int maxQtyPerRoll = 3)
        {
            Name = name; Value = value; Weight = weight; MaxQtyPerRoll = maxQtyPerRoll;
        }
    }
}