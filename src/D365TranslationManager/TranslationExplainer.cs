using System.Collections.Generic;
using System.Linq;
using System.Text;
using D365Tools.Shared;

namespace D365Tools.TranslationManager
{
    /// <summary>Explains in plain words WHAT you are changing, WHERE it is stored and WHERE it shows up.</summary>
    internal static class TranslationExplainer
    {
        public static (string title, string body) Explain(TranslationItem item, int lcid, EntityTranslations details, string envName, IList<int> visibleLanguages)
        {
            var lang = lcid == 0 ? string.Empty : $" – {Languages.Label(lcid)}";
            var lines = new List<string>();
            string title;

            switch (item.Kind)
            {
                case ItemKind.EntityDisplayName:
                    title = $"Display name of table “{item.EntityLabel}” ({item.Entity}){lang}";
                    lines.Add("What: singular name of the table (EntityMetadata.DisplayName).");
                    lines.Add("Shown in: form header, “New …” buttons, search/Advanced Find, lookup dialogs, view and chart pickers.");
                    lines.Add($"Stored in: the table metadata in {envName} (UpdateEntityRequest).");
                    break;
                case ItemKind.EntityPluralName:
                    title = $"Plural name of table “{item.EntityLabel}” ({item.Entity}){lang}";
                    lines.Add("What: plural name of the table (EntityMetadata.DisplayCollectionName).");
                    lines.Add("Shown in: navigation/sitemap (when the entry has no own title), list and subgrid headers.");
                    lines.Add("Not affected: view names such as “My Active …” – those are texts of the view itself.");
                    lines.Add($"Stored in: the table metadata in {envName} (UpdateEntityRequest).");
                    break;
                case ItemKind.EntityDescription:
                    title = $"Description of table “{item.EntityLabel}” ({item.Entity}){lang}";
                    lines.Add("What: description of the table (EntityMetadata.Description).");
                    lines.Add("Shown in: mainly the maker portal / solution explorer; rarely to end users.");
                    lines.Add($"Stored in: the table metadata in {envName} (UpdateEntityRequest).");
                    break;
                case ItemKind.AttributeDisplayName:
                    title = $"Display name of column “{item.AttributeLabel}” ({item.Entity}.{item.Attribute}){lang}";
                    lines.Add($"What: name of the column (AttributeMetadata.DisplayName), type: {item.AttributeType}.");
                    lines.Add("Shown in: view column headers, filters and Advanced Find, charts, Excel export, flow/workflow designer and forms.");
                    AddFormOverrides(item, details, lines);
                    lines.Add($"Stored in: the column metadata in {envName} (UpdateAttributeRequest).");
                    break;
                case ItemKind.AttributeDescription:
                    title = $"Description of column “{item.AttributeLabel}” ({item.Entity}.{item.Attribute}){lang}";
                    lines.Add("What: description / help text of the column (AttributeMetadata.Description).");
                    lines.Add("Shown in: the tooltip when hovering the column label on a form.");
                    lines.Add($"Stored in: the column metadata in {envName} (UpdateAttributeRequest).");
                    break;
                case ItemKind.OptionLabel when item.GlobalOptionSet != null:
                    title = $"Value {item.OptionValue} of the GLOBAL choice “{item.GlobalOptionSet}”{lang}";
                    lines.Add("⚠ Global choice: the change applies everywhere this choice is used – in other tables too.");
                    lines.Add($"Used in this table by: {string.Join(", ", item.UsedByAttributes)}.");
                    lines.Add("Shown in: choice fields on forms, views, filters, charts.");
                    lines.Add($"Stored in: the global choice in {envName} (UpdateOptionValueRequest); the choice is published.");
                    break;
                case ItemKind.OptionLabel:
                    title = $"Value {item.OptionValue} of column “{item.AttributeLabel}” ({item.Entity}.{item.Attribute}){lang}";
                    lines.Add(item.AttributeType == "Status reason"
                        ? "What: text of a status reason (statuscode). Local choice – only this column."
                        : "What: text of a choice value. Local choice – only this one column.");
                    lines.Add("Shown in: the choice field on forms, views, filters, charts.");
                    lines.Add($"Stored in: the column metadata in {envName} (UpdateOptionValueRequest).");
                    break;
                case ItemKind.StateLabel:
                    title = $"Status {item.OptionValue} (statecode) of table {item.Entity}{lang}";
                    lines.Add("What: text of a status value (e.g. Active/Inactive).");
                    lines.Add("Shown in: status column in views, form footer, filters.");
                    lines.Add($"Stored in: the column metadata in {envName} (UpdateStateValueRequest).");
                    break;
                case ItemKind.BooleanLabel:
                    title = $"Text for “{(item.OptionValue == 1 ? "Yes" : "No")}” of column “{item.AttributeLabel}” ({item.Entity}.{item.Attribute}){lang}";
                    lines.Add($"What: label of value {item.OptionValue} of a yes/no column.");
                    lines.Add("Shown in: toggles/check boxes on forms, views, filters.");
                    lines.Add(item.GlobalOptionSet != null
                        ? $"⚠ Global yes/no choice “{item.GlobalOptionSet}” – applies everywhere it is used (UpdateOptionSetRequest)."
                        : $"Stored in: the column metadata in {envName} (UpdateAttributeRequest).");
                    break;
                case ItemKind.FormTab:
                case ItemKind.FormSection:
                case ItemKind.FormCell:
                    title = $"{item.ElementKind} “{item.ElementName}” on form “{item.FormName}” ({item.FormType}){lang}";
                    if (item.Kind == ItemKind.FormCell && item.Attribute != null)
                    {
                        var field = details?.FieldItems.FirstOrDefault(f => f.Kind == ItemKind.AttributeDisplayName && f.Attribute == item.Attribute);
                        var columnName = field == null ? null : field.Get(lcid != 0 ? lcid : visibleLanguages.FirstOrDefault());
                        lines.Add($"What: label of column {item.Attribute} on this form only. It wins over the column's display name"
                                  + (string.IsNullOrEmpty(columnName) ? "." : $" (“{columnName}”, see the Columns tab)."));
                    }
                    else
                    {
                        lines.Add($"What: caption of this {item.ElementKind.ToLowerInvariant()} on the form.");
                    }
                    lines.Add($"Stored in: the FormXml of this form (systemform {item.FormId:D}) in {envName}. Applies to THIS form only.");
                    lines.Add("Other forms and the metadata (column/table names) stay unchanged.");
                    break;
                default:
                    title = item.Kind.ToString();
                    break;
            }

            if (item.IsManaged || (item.EntityIsManaged && item.Area != ItemArea.Forms))
                lines.Add($"Note: comes from a managed solution – the change becomes an unmanaged customization (active layer) in {envName} and overrides values from later solution updates.");

            if (!item.CanEdit)
                lines.Add("LOCKED – " + item.ReadOnlyReason);

            if (lcid != 0)
            {
                lines.Add(item.IsDirty(lcid)
                    ? $"Staged: “{item.GetOriginal(lcid)}” → “{item.Get(lcid)}” (yellow, not saved yet)."
                    : $"Currently saved: “{item.Get(lcid)}”.");
            }
            else
            {
                lines.Add(string.Join("   ·   ", visibleLanguages.Select(l => $"{Languages.Name(l)}: “{item.Get(l)}”")));
            }

            if (item.Error != null) lines.Add("ERROR on last save: " + item.Error);
            lines.Add("Flow: edit → yellow (staged) → “Save” writes everything in the background and then publishes the affected components once.");

            var body = new StringBuilder();
            foreach (var line in lines) body.Append("• ").AppendLine(line);
            return (title, body.ToString());
        }

        private static void AddFormOverrides(TranslationItem item, EntityTranslations details, List<string> lines)
        {
            if (details == null) return;
            var forms = details.FormItems
                .Where(f => f.Kind == ItemKind.FormCell && f.Attribute == item.Attribute)
                .Select(f => $"{f.FormName} ({f.FormType})")
                .Distinct()
                .ToList();
            if (forms.Count == 0)
            {
                lines.Add("Forms: no form has its own label for this column – this display name is shown everywhere.");
                return;
            }
            lines.Add($"⚠ On {forms.Count} form(s) the column has its OWN label that wins there: {string.Join(", ", forms.Take(6))}{(forms.Count > 6 ? ", …" : "")}. Change those in the Forms tab.");
        }
    }
}
