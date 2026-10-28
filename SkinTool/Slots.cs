using HSDRaw;
using HSDRaw.Melee.Pl;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkinTool
{
    /// <summary>
    /// Reads which mesh slots a fighter's data file switches on and off, per costume.
    /// </summary>
    public static class Slots
    {
        public static SBM_FighterData Load(string fighterDat)
        {
            var file = new HSDRawFile(fighterDat);
            return file.Roots[0].Data as SBM_FighterData ?? new SBM_FighterData() { _s = file.Roots[0].Data._s };
        }

        /// <summary>
        /// Slots in the order real meshes should fill them: ones the game always shows and also uses
        /// for the metal effect, then other always-shown ones. Slots any table lists for the far-away
        /// low-detail model are left out, since the game hides them in normal play. Some fighters have
        /// several tables (hats, crowns, swords) and a slot is only "always shown" if every table shows it.
        /// Slots in the body's group come before ones a later group switches on and off.
        /// </summary>
        public static List<int> Preferred(SBM_FighterData data, int slotCount)
        {
            var lookups = data?.ModelLookupTables?.CostumeVisibilityLookups?.Array;
            if (lookups == null || lookups.Length == 0)
                return Enumerable.Range(0, slotCount).ToList();

            var low = new HashSet<int>();
            var main = new HashSet<int>();
            HashSet<int> high = null, metal = null;
            foreach (var table in lookups.Where(t => t.HighPoly != null))
            {
                low.UnionWith(Entries(table.LowPoly));
                // the first group is the body; later ones switch on and off (weapons, hats)
                var groups = table.HighPoly.Array;
                if (groups.Length > 0)
                    foreach (var e in groups[0].LookupEntries?.Array ?? Array.Empty<SBM_LookupEntry>())
                        foreach (var b in e.Entries)
                            main.Add(b);
                var h = Entries(table.HighPoly);
                var m = Entries(table.MetalPoly);
                if (high == null) { high = h; metal = m; }
                else { high.IntersectWith(h); metal.IntersectWith(m); }
            }
            high ??= new HashSet<int>();
            metal ??= new HashSet<int>();

            return Enumerable.Range(0, slotCount)
                .Where(i => !low.Contains(i))
                .OrderBy(i => main.Contains(i) ? 0 : 1)
                .ThenBy(i => high.Contains(i) && metal.Contains(i) ? 0 : high.Contains(i) ? 1 : 2)
                .ThenBy(i => i)
                .ToList();
        }

        /// <summary>
        /// Slots the game hides unless something switches them on: the far-away model, and every choice
        /// but the first of each switchable group (a sheathed sword, Peach's pan and racket, Yoshi's egg).
        /// </summary>
        public static HashSet<int> HiddenByDefault(SBM_FighterData data)
        {
            var hidden = new HashSet<int>();
            var shown = new HashSet<int>();
            foreach (var table in data?.ModelLookupTables?.CostumeVisibilityLookups?.Array?.Where(t => t.HighPoly != null) ?? Enumerable.Empty<SBM_CostumeLookupTable>())
            {
                hidden.UnionWith(Entries(table.LowPoly));
                var groups = table.HighPoly.Array;
                for (int g = 0; g < groups.Length; g++)
                {
                    var choices = groups[g].LookupEntries?.Array ?? Array.Empty<SBM_LookupEntry>();
                    for (int c = 0; c < choices.Length; c++)
                        (c == 0 ? shown : hidden).UnionWith(choices[c].Entries.Select(b => (int)b));
                }
            }
            hidden.ExceptWith(shown);
            return hidden;
        }

        static HashSet<int> Entries(HSDArrayAccessor<SBM_LookupTable> t)
        {
            var set = new HashSet<int>();
            if (t == null) return set;
            foreach (var table in t.Array)
                foreach (var e in table.LookupEntries?.Array ?? Array.Empty<SBM_LookupEntry>())
                    foreach (var b in e.Entries)
                        set.Add(b);
            return set;
        }

        public static int Dump(string[] args)
        {
            var data = Load(args[1]);
            var tables = data.ModelLookupTables;
            Console.WriteLine($"costumes {tables.VisibilityLookupLength}");
            var lookups = tables.CostumeVisibilityLookups.Array;
            for (int c = 0; c < lookups.Length; c++)
            {
                Console.WriteLine($"costume {c}");
                Print("  high", lookups[c].HighPoly);
                Print("  low", lookups[c].LowPoly);
                Print("  metal", lookups[c].MetalPoly);
                Print("  metal main", lookups[c].MetalMainModel);
            }

            if (data.ModelPartAnimations != null)
                for (int i = 0; i < data.ModelPartAnimations.Length; i++)
                {
                    var mp = data.ModelPartAnimations[i];
                    Console.WriteLine($"model part {i}: bones {mp.StartingBone}+{mp.Count}");
                }
            return 0;
        }

        static void Print(string label, HSDArrayAccessor<SBM_LookupTable> t)
        {
            if (t == null) { Console.WriteLine($"{label}: none"); return; }
            var arr = t.Array;
            for (int i = 0; i < arr.Length; i++)
            {
                var entries = arr[i].LookupEntries?.Array ?? Array.Empty<SBM_LookupEntry>();
                Console.WriteLine($"{label}[{i}]: " + string.Join(" | ", entries.Select(e => string.Join(",", e.Entries))));
            }
        }
    }
}
