using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace D365Tools.Shared
{
    /// <summary>Base language + installed (provisioned) languages of one environment.</summary>
    internal sealed class EnvLanguages
    {
        public int Base;
        public int[] Provisioned = new int[0];

        /// <summary>Base language first, then the other installed languages in the order Dataverse returns them.</summary>
        public IEnumerable<int> All => new[] { Base }.Concat(Provisioned.Where(l => l != Base)).Where(l => l > 0).Distinct();

        /// <summary>Default columns: base language + first additional language.</summary>
        public List<int> Defaults => All.Take(2).ToList();
    }

    internal static class Languages
    {
        /// <summary>English language name, e.g. 1031 → "German".</summary>
        public static string Name(int lcid)
        {
            try
            {
                var culture = CultureInfo.GetCultureInfo(lcid);
                var neutral = culture.IsNeutralCulture || culture.Parent == CultureInfo.InvariantCulture ? culture : culture.Parent;
                return neutral.EnglishName;
            }
            catch (CultureNotFoundException)
            {
                return "LCID " + lcid;
            }
        }

        /// <summary>"German (1031)"</summary>
        public static string Label(int lcid) => $"{Name(lcid)} ({lcid})";

        /// <summary>Restores a saved selection for this environment; falls back to the defaults.</summary>
        public static List<int> Resolve(int[] saved, EnvLanguages env)
        {
            var available = env.All.ToList();
            var selection = (saved ?? new int[0]).Where(available.Contains).Distinct().ToList();
            return selection.Count > 0 ? selection : env.Defaults;
        }

        /// <summary>Orders a selection: base language first, then in installation order.</summary>
        public static List<int> Order(IEnumerable<int> selection, EnvLanguages env)
        {
            var order = env.All.ToList();
            return selection.Distinct().OrderBy(l => order.IndexOf(l) < 0 ? int.MaxValue : order.IndexOf(l)).ToList();
        }
    }
}
