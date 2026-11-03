using System;
using System.Collections.Generic;
using System.Linq;
using D365Tools.Shared;

namespace D365Tools.AuditManager
{
    /// <summary>Audit state of one table or column in exactly one environment.</summary>
    internal sealed class AuditFlag
    {
        public bool Exists;
        public bool CanChange;
        public bool Original;
        public bool Current;
        public string Error;

        public bool IsDirty => Exists && Current != Original;

        public static AuditFlag Missing() => new AuditFlag { Exists = false };
    }

    internal abstract class AuditRowBase
    {
        public string LogicalName;
        public bool IsCustom;

        /// <summary>Display name per language (LCID → text), merged from all environments.</summary>
        public readonly Dictionary<int, string> Labels = new Dictionary<int, string>();

        public readonly Dictionary<DataverseEnvironment, AuditFlag> Flags = new Dictionary<DataverseEnvironment, AuditFlag>();

        public AuditFlag Flag(DataverseEnvironment env) => Flags.TryGetValue(env, out var f) ? f : AuditFlag.Missing();

        public string Label(int lcid) => Labels.TryGetValue(lcid, out var text) ? text : string.Empty;

        public string AnyLabel(IEnumerable<int> preferred) =>
            preferred.Select(Label).Concat(Labels.Values).FirstOrDefault(l => !string.IsNullOrEmpty(l)) ?? string.Empty;

        /// <summary>Different between environments (missing somewhere or different value).</summary>
        public bool HasDifference(IList<DataverseEnvironment> envs)
        {
            if (envs.Count < 2) return false;
            var flags = envs.Select(Flag).ToList();
            if (flags.Any(f => !f.Exists)) return true;
            return flags.Select(f => f.Current).Distinct().Count() > 1;
        }

        public bool IsLockedEverywhere(IList<DataverseEnvironment> envs)
        {
            return envs.Select(Flag).Where(f => f.Exists).All(f => !f.CanChange);
        }

        public bool Matches(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            return Contains(LogicalName, search) || Labels.Values.Any(l => Contains(l, search));
        }

        private static bool Contains(string value, string search) =>
            value != null && value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    internal sealed class AuditEntityRow : AuditRowBase
    {
        public readonly Dictionary<string, AuditAttributeRow> Attributes =
            new Dictionary<string, AuditAttributeRow>(StringComparer.OrdinalIgnoreCase);

        public bool AttributesLoaded;
        public bool AttributesLoading;
    }

    internal sealed class AuditAttributeRow : AuditRowBase
    {
        public AuditEntityRow Entity;
        public string TypeName;
    }

    internal sealed class OrgAuditInfo
    {
        public Guid OrganizationId;
        public bool IsAuditEnabled;
        public bool IsUserAccessAuditEnabled;
        public string Error;
    }

    /// <summary>One pending change (snapshot taken when "Save" is clicked).</summary>
    internal sealed class AuditChange
    {
        public DataverseEnvironment Env;
        public AuditEntityRow Entity;
        public AuditAttributeRow Attribute;
        public AuditFlag Flag;
        public bool NewValue;

        public string Display => Attribute == null
            ? $"{Entity.LogicalName} (table)"
            : $"{Entity.LogicalName}.{Attribute.LogicalName}";
    }
}
