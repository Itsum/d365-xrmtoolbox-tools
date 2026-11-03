using System;
using System.Collections.Generic;
using System.Linq;

namespace D365Tools.TranslationManager
{
    internal enum ItemKind
    {
        EntityDisplayName,
        EntityPluralName,
        EntityDescription,
        AttributeDisplayName,
        AttributeDescription,
        OptionLabel,
        StateLabel,
        BooleanLabel,
        FormTab,
        FormSection,
        FormCell
    }

    internal enum ItemArea { Entity = 0, Fields = 1, Options = 2, Forms = 3 }

    /// <summary>One translatable text (one grid row) with a value per language.</summary>
    internal sealed class TranslationItem
    {
        public ItemKind Kind;
        public string Entity;
        public string EntityLabel;
        public bool EntityIsManaged;

        public string Attribute;
        public string AttributeLabel;
        public string AttributeType;

        public int OptionValue;
        public string GlobalOptionSet;
        public List<string> UsedByAttributes = new List<string>();

        public Guid FormId;
        public string FormName;
        public string FormType;
        public string ElementId;
        public string ElementName;
        public string ElementKind;

        public bool IsManaged;
        public bool CanEdit = true;
        public string ReadOnlyReason;
        public string Error;

        /// <summary>Saved values (LCID → text) and the values currently shown/edited.</summary>
        public readonly Dictionary<int, string> Original = new Dictionary<int, string>();
        public readonly Dictionary<int, string> Current = new Dictionary<int, string>();

        /// <summary>Values being written by the running save (taken when the save starts).</summary>
        public Dictionary<int, string> Snapshot;

        /// <summary>Display columns in the grid (meaning differs per tab).</summary>
        public string Info1, Info2, Info3;

        public ItemArea Area
        {
            get
            {
                switch (Kind)
                {
                    case ItemKind.EntityDisplayName:
                    case ItemKind.EntityPluralName:
                    case ItemKind.EntityDescription:
                        return ItemArea.Entity;
                    case ItemKind.AttributeDisplayName:
                    case ItemKind.AttributeDescription:
                        return ItemArea.Fields;
                    case ItemKind.OptionLabel:
                    case ItemKind.StateLabel:
                    case ItemKind.BooleanLabel:
                        return ItemArea.Options;
                    default:
                        return ItemArea.Forms;
                }
            }
        }

        public bool IsDescription => Kind == ItemKind.EntityDescription || Kind == ItemKind.AttributeDescription;
        public bool AllowEmpty => IsDescription;

        public string Get(int lcid) => Current.TryGetValue(lcid, out var v) ? v : string.Empty;
        public string GetOriginal(int lcid) => Original.TryGetValue(lcid, out var v) ? v : string.Empty;
        public void Set(int lcid, string value) => Current[lcid] = value ?? string.Empty;

        public bool IsDirty(int lcid) => !string.Equals(Get(lcid), GetOriginal(lcid), StringComparison.Ordinal);
        public List<int> DirtyLanguages => Current.Keys.Union(Original.Keys).Where(IsDirty).ToList();
        public bool Dirty => Current.Keys.Union(Original.Keys).Any(IsDirty);

        public void Load(Dictionary<int, string> labels)
        {
            foreach (var pair in labels)
            {
                Original[pair.Key] = pair.Value;
                Current[pair.Key] = pair.Value;
            }
        }

        public void Revert()
        {
            Current.Clear();
            foreach (var pair in Original) Current[pair.Key] = pair.Value;
            Error = null;
        }

        /// <summary>Missing translation: name is empty – for descriptions only if another visible language has one.</summary>
        public bool IsMissing(int lcid, IList<int> visibleLanguages)
        {
            if (!string.IsNullOrWhiteSpace(Get(lcid))) return false;
            if (!IsDescription) return true;
            return visibleLanguages.Any(l => l != lcid && !string.IsNullOrWhiteSpace(Get(l)));
        }

        public bool IsMissingAny(IList<int> visibleLanguages) => visibleLanguages.Any(l => IsMissing(l, visibleLanguages));

        public bool Matches(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            return new[] { Info1, Info2, Info3, Attribute, FormName, ElementName }.Concat(Current.Values)
                .Any(v => v != null && v.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    internal sealed class EntityTranslations
    {
        public string LogicalName;
        public string Label;
        public readonly List<TranslationItem> EntityItems = new List<TranslationItem>();
        public readonly List<TranslationItem> FieldItems = new List<TranslationItem>();
        public readonly List<TranslationItem> OptionItems = new List<TranslationItem>();
        public readonly List<TranslationItem> FormItems = new List<TranslationItem>();

        public IEnumerable<TranslationItem> All => EntityItems.Concat(FieldItems).Concat(OptionItems).Concat(FormItems);

        public List<TranslationItem> ItemsFor(ItemArea area)
        {
            switch (area)
            {
                case ItemArea.Entity: return EntityItems;
                case ItemArea.Fields: return FieldItems;
                case ItemArea.Options: return OptionItems;
                default: return FormItems;
            }
        }
    }

    internal sealed class EntityListItem
    {
        public string LogicalName;
        public Dictionary<int, string> Labels = new Dictionary<int, string>();
        public bool IsCustom;
        public bool IsManaged;

        public string Label(int lcid) => Labels.TryGetValue(lcid, out var v) ? v : string.Empty;

        public bool Matches(string search)
        {
            if (string.IsNullOrWhiteSpace(search)) return true;
            return new[] { LogicalName }.Concat(Labels.Values).Any(v => v != null && v.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
