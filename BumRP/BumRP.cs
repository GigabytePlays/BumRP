using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
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
    private class SellSpot
    {
        public Vector3 Pos;
        public string Name;
        public float PriceMultiplier;

        public SellSpot(Vector3 pos, string name, float mult = 1.0f)
        { Pos = pos; Name = name; PriceMultiplier = mult; }
    }

    private readonly List<SellSpot> sellSpots = new List<SellSpot>
    {
        new SellSpot(new Vector3(-428.8f, -1728.2f, 19.8f), "Scrap Yard - Los Santos", 0.8f),
        new SellSpot(new Vector3(2341.0f, 3133.5f, 48.2f), "Scrap Yard - Sandy Shores", 1.0f),
        new SellSpot(new Vector3(-67.2f, 6428.4f, 31.4f), "Scrap Yard - Paleto Bay", 1.2f),
    };

    private readonly List<Blip> sellBlips = new List<Blip>();

    // Modelos de lixeira/caçamba
    private readonly string[] DumpsterModels = new string[]
    {
        "p_dumpster_t",
        "prop_bin_01a",
        "prop_bin_02a",
        "prop_bin_03a",
        "prop_bin_04a",
        "prop_bin_05a",
        "prop_bin_06a",
        "prop_bin_07a",
        "prop_bin_07b",
        "prop_bin_07c",
        "prop_bin_07d",
        "prop_bin_08a",
        "prop_bin_08open",
        "prop_bin_10a",
        "prop_bin_10b",
        "prop_bin_11a",
        "prop_bin_11b",
        "prop_bin_12a",
        "prop_bin_14a",
        "prop_bin_14b",
        "prop_bin_delpiero",
        "prop_bin_delpiero_b",
        "prop_cs_bin_01",
        "prop_cs_bin_01_skinned",
        "prop_cs_bin_02",
        "prop_cs_bin_03",
        "prop_cs_dumpster_01a",
        "prop_dumpster_01a",
        "prop_dumpster_02a",
        "prop_dumpster_02b",
        "prop_dumpster_3a",
        "prop_dumpster_4a",
        "prop_dumpster_4b",
        "prop_recyclebin_03_a",
        "zprop_bin_01a_old",
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
        foreach (var s in sellSpots)
        {
            var blip = World.CreateBlip(s.Pos);
            blip.Sprite = BlipSprite.DollarSign;
            blip.Color = BlipColor.Green;
            blip.Name = s.Name;
            blip.IsShortRange = true;
            sellBlips.Add(blip);
        }

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
        SellSpot nearest = GetNearestSellSpot(ped.Position, InteractDistance + 1.0f);
        if (nearest != null)
        {
            UIScreen.ShowSubtitle("~g~G~s~ Sell scrap", 10);
        }

        // Desenhar marcadores nos 3 locais
        foreach (var s in sellSpots)
        {
            World.DrawMarker(MarkerType.Halo, s.Pos, Vector3.Zero, Vector3.Zero,
                new Vector3(1f, 1f, 1f), System.Drawing.Color.Green);
        }

        // Finaliza a busca quando o tempo acabar
        if (_isSearching && Game.GameTime >= _searchEndAt)
        {
            FinishSearchAnimationAndRoll();
        }
    }

    private SellSpot GetNearestSellSpot(Vector3 pos, float maxDist)
    {
        SellSpot best = null;
        float bestDist = maxDist;
        foreach (var s in sellSpots)
        {
            float d = pos.DistanceTo(s.Pos);
            if (d < bestDist)
            { bestDist = d; best = s; }
        }
        return best;
    }

    private bool _isSearching = false;
    private int _searchEndAt = 0;
    private Prop _activeDumpster = null;
    private const int SearchDurationMs = 3000;

    private void StartSearchAnimation(Prop dumpster)
    {
        Ped ped = Game.Player.Character;

        // Escolhe o lado MAIS PRÓXIMO do player (frente ou trás) e o heading correto
        Vector3 standPos;
        float headingFaceProp;
        ComputeBestStandPosAndHeading(dumpster, ped, 0.70f, out standPos, out headingFaceProp);

        // Cola o ped no chão nessa posição
        SnapPedToGround(ped, standPos, headingFaceProp, 0.12f);

        // Impede andar durante a busca
        ped.Task.StandStill(SearchDurationMs + 500);

        // Carrega e toca a animação (fica no lugar)
        const string dict = "amb@prop_human_bum_bin@base";
        const string name = "base";
        Function.Call(Hash.REQUEST_ANIM_DICT, dict);
        int t0 = Game.GameTime;
        while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) && Game.GameTime - t0 < 1500)
            Script.Yield();

        // flags: 1 = loop; 0.0 = blend in/out padrão
        Function.Call(Hash.TASK_PLAY_ANIM,
            ped, dict, name,
            8.0f, -8.0f,
            SearchDurationMs, 1,
            0.0f, false, false, false);

        // Estado interno
        _isSearching = true;
        _activeDumpster = dumpster;
        _searchEndAt = Game.GameTime + SearchDurationMs;

        GTA.UI.Screen.ShowSubtitle("Searching...", 1000);
    }

    private void FinishSearchAnimationAndRoll()
    {
        Ped ped = Game.Player.Character;

        // Para a animação
        ped.Task.ClearAll();

        // Segurança: guarda e limpa estado
        Prop dumpster = _activeDumpster;
        _isSearching = false;
        _activeDumpster = null;

        if (dumpster == null)
            return;

        if (rng.NextDouble() < 0.50) // % do "Nada"
        {
            Notification.Show("You searched, but ~o~found nothing~s~.");
            SetDumpsterCooldown(dumpster.Handle);
            return;
        }

        // rolls
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
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        Ped ped = Game.Player.Character;

        // Vasculhar
        if (e.KeyCode == KeySearch)
        {
            if (_isSearching) return;

            Prop dumpster = GetClosestDumpster(ped.Position, 2.5f);
            if (dumpster == null) return;

            if (!CanSearchDumpster(dumpster.Handle))
            {
                Notification.Show("Nothing found... try another place or wait.");
                return;
            }

            StartSearchAnimation(dumpster);
        }

        // Vender
        if (e.KeyCode == KeySell)
        {
            var ped2 = Game.Player.Character;
            SellSpot spot = GetNearestSellSpot(ped2.Position, InteractDistance + 0.2f);
            if (spot == null) return;

            int total = ComputeInventoryValue();
            if (total <= 0)
            {
                Notification.Show("You have nothing to sell.");
                return;
            }

            // multiplicador por ponto
            int payout = (int)Math.Round(total * spot.PriceMultiplier);

            Game.Player.Money += payout;
            inventory.Clear();
            SaveInventory();

            // Notificação no feed
            Notification.Show(
                NotificationIcon.Lester,
                spot.Name,
                "~g~Sucessful sale",
                $"All items sold for ~g~${total}~s~.");
        }
    }

    private float GetGroundZRobusto(Vector3 pos)
    {
        float[] alturas = { pos.Z + 30f, pos.Z + 10f, pos.Z + 3f, pos.Z + 1f };
        var outZ = new OutputArgument();
        foreach (float probeZ in alturas)
        {
            Function.Call(Hash.REQUEST_COLLISION_AT_COORD, pos.X, pos.Y, probeZ);
            bool ok = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, pos.X, pos.Y, probeZ, outZ, false);
            if (ok) return outZ.GetResult<float>();
        }
        return pos.Z;
    }

    private void SnapPedToGround(Ped ped, Vector3 nearPos, float heading, float lift = 0.12f)
    {
        Function.Call(Hash.REQUEST_COLLISION_AT_COORD, nearPos.X, nearPos.Y, nearPos.Z);
        float z = GetGroundZRobusto(nearPos) + lift;

        Function.Call(Hash.SET_ENTITY_COORDS_NO_OFFSET, ped, nearPos.X, nearPos.Y, z, false, false, false);
        ped.Heading = heading;

        // dá 1 frame pra engine assentar
        Script.Yield();
    }

    private void ComputeBestStandPosAndHeading(Prop prop, Ped ped, float extraBuffer, out Vector3 standPos, out float headingFaceProp)
    {
        // profundidade do modelo para calcular o offset
        Vector3 min, max;
        prop.Model.GetDimensions(out min, out max);
        float depth = Math.Abs(max.Y - min.Y);
        float offset = (depth * 0.5f) + extraBuffer;

        Vector3 forward = prop.ForwardVector;

        // duas candidatas: frente e trás, e os headings correspondentes
        Vector3 posFront = prop.Position + forward * offset;   // “frente” do prop
        Vector3 posBack = prop.Position - forward * offset;   // “trás” do prop

        float dFront = ped.Position.DistanceTo(posFront);
        float dBack = ped.Position.DistanceTo(posBack);

        if (dFront <= dBack)
        {
            standPos = posFront;
            headingFaceProp = (prop.Heading + 180f) % 360f; // de frente para o prop
        }
        else
        {
            standPos = posBack;
            headingFaceProp = prop.Heading; // também de frente para o prop
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