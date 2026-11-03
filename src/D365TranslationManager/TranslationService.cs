using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using D365Tools.Shared;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;

namespace D365Tools.TranslationManager
{
    /// <summary>Loads the labels of a table and writes changed labels back (runs on a background thread).</summary>
    internal static class TranslationService
    {
        private static readonly int[] FormTypes = { 2, 6, 7, 11 };

        public static string FormTypeName(int type)
        {
            switch (type)
            {
                case 2: return "Main";
                case 6: return "Quick view";
                case 7: return "Quick create";
                case 11: return "Card";
                default: return "Type " + type;
            }
        }

        #region Loading

        public static List<EntityListItem> LoadEntityList(IOrganizationService service, ICollection<string> names)
        {
            var metas = MetadataHelper.RetrieveEntities(service, names,
                new[] { "LogicalName", "DisplayName", "IsCustomEntity", "IsManaged" });
            return metas
                .Select(m => new EntityListItem
                {
                    LogicalName = m.LogicalName,
                    Labels = MetadataHelper.LabelsOf(m.DisplayName),
                    IsCustom = m.IsCustomEntity == true,
                    IsManaged = m.IsManaged == true
                })
                // Hide internal tables without any display name (unless explicitly requested)
                .Where(e => names.Count > 0 || e.Labels.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
                .OrderBy(e => e.LogicalName)
                .ToList();
        }

        public static EntityTranslations LoadEntity(IOrganizationService service, string entity, int preferredLanguage)
        {
            var em = ((RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Entity | EntityFilters.Attributes,
                RetrieveAsIfPublished = true
            })).EntityMetadata;

            string Display(Label label, string fallback) =>
                FirstNonEmpty(MetadataHelper.GetLabel(label, preferredLanguage), label?.LocalizedLabels?.Select(l => l.Label).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)), fallback);

            var entityLabel = Display(em.DisplayName, entity);
            var result = new EntityTranslations { LogicalName = entity, Label = entityLabel };
            var entityManaged = em.IsManaged == true;
            var entityRenameable = em.IsRenameable?.Value ?? true;

            TranslationItem NewItem(ItemKind kind, Label label)
            {
                var item = new TranslationItem
                {
                    Kind = kind,
                    Entity = entity,
                    EntityLabel = entityLabel,
                    EntityIsManaged = entityManaged,
                    IsManaged = entityManaged
                };
                item.Load(MetadataHelper.LabelsOf(label));
                return item;
            }

            // --- Table ---
            void AddEntityItem(ItemKind kind, Label label, string property, string metaName)
            {
                var item = NewItem(kind, label);
                item.Info1 = property;
                item.Info2 = metaName;
                item.Info3 = entity;
                if (!entityRenameable)
                {
                    item.CanEdit = false;
                    item.ReadOnlyReason = "This table must not be renamed (managed property IsRenameable = false).";
                }
                result.EntityItems.Add(item);
            }
            AddEntityItem(ItemKind.EntityDisplayName, em.DisplayName, "Display name", "DisplayName");
            AddEntityItem(ItemKind.EntityPluralName, em.DisplayCollectionName, "Plural name", "DisplayCollectionName");
            AddEntityItem(ItemKind.EntityDescription, em.Description, "Description", "Description");

            // --- Columns + choices ---
            var globalOptionItems = new Dictionary<string, List<TranslationItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in em.Attributes.Where(MetadataHelper.IsRelevantAttribute).OrderBy(a => a.LogicalName))
            {
                var attrLabel = Display(attr.DisplayName, attr.LogicalName);
                var typeName = MetadataHelper.FriendlyType(attr);
                var attrRenameable = attr.IsRenameable?.Value ?? true;
                var attrManaged = attr.IsManaged == true;

                foreach (var (kind, label, property) in new[]
                         {
                             (ItemKind.AttributeDisplayName, attr.DisplayName, "Display name"),
                             (ItemKind.AttributeDescription, attr.Description, "Description")
                         })
                {
                    var item = NewItem(kind, label);
                    item.Attribute = attr.LogicalName;
                    item.AttributeLabel = attrLabel;
                    item.AttributeType = typeName;
                    item.IsManaged = attrManaged;
                    item.Info1 = attr.LogicalName;
                    item.Info2 = typeName;
                    item.Info3 = property;
                    if (!attrRenameable)
                    {
                        item.CanEdit = false;
                        item.ReadOnlyReason = "This column must not be renamed (managed property IsRenameable = false).";
                    }
                    result.FieldItems.Add(item);
                }

                if (attr is EntityNameAttributeMetadata) continue;

                if (attr is EnumAttributeMetadata enumAttr && enumAttr.OptionSet != null)
                {
                    var os = enumAttr.OptionSet;
                    var global = os.IsGlobal == true ? os.Name : null;
                    if (global != null && globalOptionItems.TryGetValue(global, out var existing))
                    {
                        foreach (var e in existing) e.UsedByAttributes.Add(attr.LogicalName);
                        continue;
                    }

                    var kind = attr is StateAttributeMetadata ? ItemKind.StateLabel : ItemKind.OptionLabel;
                    var created = new List<TranslationItem>();
                    foreach (var option in os.Options.Where(o => o.Value.HasValue))
                    {
                        var item = NewItem(kind, option.Label);
                        item.Attribute = attr.LogicalName;
                        item.AttributeLabel = attrLabel;
                        item.AttributeType = typeName;
                        item.OptionValue = option.Value.Value;
                        item.GlobalOptionSet = global;
                        item.IsManaged = os.IsManaged == true;
                        item.UsedByAttributes.Add(attr.LogicalName);
                        item.Info2 = option.Value.Value.ToString();
                        item.Info3 = global != null ? "Global" : kind == ItemKind.StateLabel ? "Status" : typeName;
                        if (os.IsCustomizable != null && !os.IsCustomizable.Value)
                        {
                            item.CanEdit = false;
                            item.ReadOnlyReason = "The choice is not customizable (IsCustomizable = false).";
                        }
                        created.Add(item);
                        result.OptionItems.Add(item);
                    }
                    if (global != null) globalOptionItems[global] = created;
                }
                else if (attr is BooleanAttributeMetadata boolAttr && boolAttr.OptionSet != null)
                {
                    var os = boolAttr.OptionSet;
                    var global = os.IsGlobal == true ? os.Name : null;
                    foreach (var (option, value) in new[] { (os.TrueOption, 1), (os.FalseOption, 0) })
                    {
                        if (option == null) continue;
                        var item = NewItem(ItemKind.BooleanLabel, option.Label);
                        item.Attribute = attr.LogicalName;
                        item.AttributeLabel = attrLabel;
                        item.AttributeType = typeName;
                        item.OptionValue = value;
                        item.GlobalOptionSet = global;
                        item.IsManaged = attrManaged;
                        item.UsedByAttributes.Add(attr.LogicalName);
                        item.Info2 = value == 1 ? "1 (Yes)" : "0 (No)";
                        item.Info3 = global != null ? "Global yes/no" : "Yes/No";
                        result.OptionItems.Add(item);
                    }
                }
            }

            foreach (var item in result.OptionItems)
            {
                item.Info1 = item.GlobalOptionSet != null
                    ? $"{item.GlobalOptionSet}  (global – {string.Join(", ", item.UsedByAttributes)})"
                    : item.Attribute;
            }

            // --- Forms ---
            LoadForms(service, result, entityManaged);
            return result;
        }

        private static void LoadForms(IOrganizationService service, EntityTranslations result, bool entityManaged)
        {
            var query = new QueryExpression("systemform")
            {
                ColumnSet = new ColumnSet("name", "type", "formxml", "ismanaged")
            };
            query.Criteria.AddCondition("objecttypecode", ConditionOperator.Equal, result.LogicalName);
            query.Criteria.AddCondition("type", ConditionOperator.In, FormTypes.Cast<object>().ToArray());
            query.AddOrder("type", OrderType.Ascending);
            query.AddOrder("name", OrderType.Ascending);

            foreach (var form in service.RetrieveMultiple(query).Entities)
            {
                var xml = form.GetAttributeValue<string>("formxml");
                if (string.IsNullOrWhiteSpace(xml)) continue;
                XDocument doc;
                try { doc = XDocument.Parse(xml); }
                catch { continue; }

                var formName = form.GetAttributeValue<string>("name");
                var formType = FormTypeName(form.GetAttributeValue<OptionSetValue>("type")?.Value ?? 0);
                var formManaged = form.GetAttributeValue<bool>("ismanaged");

                void Add(ItemKind kind, XElement element, string elementKind, string name, string attribute)
                {
                    var id = (string)element.Attribute("id");
                    var labels = element.Element("labels");
                    if (string.IsNullOrEmpty(id) || labels == null || !labels.Elements("label").Any()) return;
                    var item = new TranslationItem
                    {
                        Kind = kind,
                        Entity = result.LogicalName,
                        EntityLabel = result.Label,
                        EntityIsManaged = entityManaged,
                        IsManaged = formManaged,
                        FormId = form.Id,
                        FormName = formName,
                        FormType = formType,
                        ElementId = id,
                        ElementName = name,
                        ElementKind = elementKind,
                        Attribute = attribute,
                        Info1 = $"{formName} ({formType})",
                        Info2 = elementKind,
                        Info3 = name
                    };
                    item.Load(GetFormLabels(element));
                    result.FormItems.Add(item);
                }

                foreach (var tab in doc.Descendants("tab"))
                    Add(ItemKind.FormTab, tab, "Tab", (string)tab.Attribute("name") ?? "(no name)", null);
                foreach (var section in doc.Descendants("section"))
                    Add(ItemKind.FormSection, section, "Section", (string)section.Attribute("name") ?? "(no name)", null);
                foreach (var cell in doc.Descendants("cell"))
                {
                    var control = cell.Element("control");
                    var field = (string)control?.Attribute("datafieldname");
                    var name = field ?? (string)control?.Attribute("id") ?? "(element)";
                    var where = cell.Ancestors("header").Any() ? "Header field" : cell.Ancestors("footer").Any() ? "Footer field" : field != null ? "Field" : "Control";
                    Add(ItemKind.FormCell, cell, where, name, field);
                }
            }
        }

        private static Dictionary<int, string> GetFormLabels(XElement element)
        {
            var result = new Dictionary<int, string>();
            var labels = element.Element("labels");
            if (labels == null) return result;
            foreach (var label in labels.Elements("label"))
            {
                if (int.TryParse((string)label.Attribute("languagecode"), out var lcid))
                    result[lcid] = (string)label.Attribute("description") ?? string.Empty;
            }
            return result;
        }

        private static string FirstNonEmpty(params string[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

        #endregion

        #region Saving

        internal sealed class SaveOperation
        {
            public string Description;
            public List<TranslationItem> Items;
            public string PublishEntity;
            public string PublishOptionSet;
            public Action<IOrganizationService> Execute;
        }

        /// <summary>
        /// Groups changes into as few requests as possible (one per table/column/choice value/form)
        /// and takes a snapshot of the values to write.
        /// </summary>
        public static List<SaveOperation> BuildPlan(IList<TranslationItem> dirty)
        {
            foreach (var item in dirty)
                item.Snapshot = item.DirtyLanguages.ToDictionary(l => l, item.Get);

            var ops = new List<SaveOperation>();

            foreach (var g in dirty.Where(i => i.Area == ItemArea.Entity).GroupBy(i => i.Entity))
            {
                var items = g.ToList();
                ops.Add(new SaveOperation
                {
                    Description = $"Table {g.Key}: {string.Join(", ", items.Select(i => i.Info1.ToLowerInvariant()))}",
                    Items = items,
                    PublishEntity = g.Key,
                    Execute = svc => SaveEntityLabels(svc, g.Key, items)
                });
            }

            var attributeItems = dirty.Where(i => i.Area == ItemArea.Fields || (i.Kind == ItemKind.BooleanLabel && i.GlobalOptionSet == null));
            foreach (var g in attributeItems.GroupBy(i => (i.Entity, i.Attribute)))
            {
                var items = g.ToList();
                ops.Add(new SaveOperation
                {
                    Description = $"Column {g.Key.Entity}.{g.Key.Attribute}: {string.Join(", ", items.Select(Describe))}",
                    Items = items,
                    PublishEntity = g.Key.Entity,
                    Execute = svc => SaveAttributeLabels(svc, g.Key.Entity, g.Key.Attribute, items)
                });
            }

            foreach (var item in dirty.Where(i => i.Kind == ItemKind.OptionLabel || i.Kind == ItemKind.StateLabel))
            {
                var current = item;
                ops.Add(new SaveOperation
                {
                    Description = current.GlobalOptionSet != null
                        ? $"Global choice {current.GlobalOptionSet}: value {current.OptionValue}"
                        : $"Choice {current.Entity}.{current.Attribute} = {current.OptionValue}",
                    Items = new List<TranslationItem> { current },
                    PublishEntity = current.GlobalOptionSet == null ? current.Entity : null,
                    PublishOptionSet = current.GlobalOptionSet,
                    Execute = svc => SaveOptionLabel(svc, current)
                });
            }

            foreach (var g in dirty.Where(i => i.Kind == ItemKind.BooleanLabel && i.GlobalOptionSet != null).GroupBy(i => i.GlobalOptionSet))
            {
                var items = g.ToList();
                ops.Add(new SaveOperation
                {
                    Description = $"Global yes/no choice {g.Key}",
                    Items = items,
                    PublishOptionSet = g.Key,
                    Execute = svc => SaveGlobalBooleanLabels(svc, g.Key, items)
                });
            }

            foreach (var g in dirty.Where(i => i.Area == ItemArea.Forms).GroupBy(i => i.FormId))
            {
                var items = g.ToList();
                ops.Add(new SaveOperation
                {
                    Description = $"Form “{items[0].FormName}” ({items[0].Entity}): {items.Count} label(s)",
                    Items = items,
                    PublishEntity = items[0].Entity,
                    Execute = svc => SaveFormLabels(svc, g.Key, items)
                });
            }

            return ops;
        }

        public static void Execute(IOrganizationService service, List<SaveOperation> operations, bool publish, IProgress<SaveProgress> progress)
        {
            var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optionSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var op in operations)
            {
                try
                {
                    op.Execute(service);
                    foreach (var item in op.Items)
                    {
                        foreach (var pair in item.Snapshot) item.Original[pair.Key] = pair.Value;
                        item.Error = null;
                        item.Snapshot = null;
                    }
                    if (op.PublishEntity != null) entities.Add(op.PublishEntity);
                    if (op.PublishOptionSet != null) optionSets.Add(op.PublishOptionSet);
                    progress.Report(new SaveProgress(true, op.Description));
                }
                catch (Exception ex)
                {
                    var message = MetadataHelper.ErrorText(ex);
                    foreach (var item in op.Items)
                    {
                        item.Error = message;
                        item.Snapshot = null;
                    }
                    progress.Report(new SaveProgress(false, $"{op.Description}: {message}"));
                }
            }

            if (!publish) return;
            if (entities.Count == 0 && optionSets.Count == 0)
            {
                progress.Report(new SaveProgress(true, "Nothing to publish."));
                return;
            }
            try
            {
                MetadataHelper.Publish(service, entities, optionSets);
                var what = entities.Concat(optionSets.Select(o => o + " (choice)"));
                progress.Report(new SaveProgress(true, "Published: " + string.Join(", ", what)));
            }
            catch (Exception ex)
            {
                progress.Report(new SaveProgress(false, "Publishing failed: " + MetadataHelper.ErrorText(ex)));
            }
        }

        private static string Describe(TranslationItem item)
        {
            switch (item.Kind)
            {
                case ItemKind.AttributeDisplayName: return "display name";
                case ItemKind.AttributeDescription: return "description";
                case ItemKind.BooleanLabel: return item.OptionValue == 1 ? "yes label" : "no label";
                default: return item.Kind.ToString();
            }
        }

        private static Label ApplyLabels(Label label, TranslationItem item)
        {
            foreach (var pair in item.Snapshot) label = MetadataHelper.SetLabel(label, pair.Key, pair.Value);
            return label;
        }

        private static Label ChangedLabelsOnly(TranslationItem item)
        {
            var label = new Label();
            foreach (var pair in item.Snapshot) label.LocalizedLabels.Add(new LocalizedLabel(pair.Value, pair.Key));
            return label;
        }

        private static void SaveEntityLabels(IOrganizationService service, string entity, List<TranslationItem> items)
        {
            var em = ((RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true
            })).EntityMetadata;

            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case ItemKind.EntityDisplayName: em.DisplayName = ApplyLabels(em.DisplayName, item); break;
                    case ItemKind.EntityPluralName: em.DisplayCollectionName = ApplyLabels(em.DisplayCollectionName, item); break;
                    case ItemKind.EntityDescription: em.Description = ApplyLabels(em.Description, item); break;
                }
            }
            service.Execute(new UpdateEntityRequest { Entity = em, MergeLabels = true });
        }

        private static void SaveAttributeLabels(IOrganizationService service, string entity, string attribute, List<TranslationItem> items)
        {
            var am = ((RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = entity,
                LogicalName = attribute,
                RetrieveAsIfPublished = true
            })).AttributeMetadata;

            var touchesBooleanOptions = false;
            foreach (var item in items)
            {
                switch (item.Kind)
                {
                    case ItemKind.AttributeDisplayName:
                        am.DisplayName = ApplyLabels(am.DisplayName, item);
                        break;
                    case ItemKind.AttributeDescription:
                        am.Description = ApplyLabels(am.Description, item);
                        break;
                    case ItemKind.BooleanLabel when am is BooleanAttributeMetadata boolAttr && boolAttr.OptionSet != null:
                        var option = item.OptionValue == 1 ? boolAttr.OptionSet.TrueOption : boolAttr.OptionSet.FalseOption;
                        option.Label = ApplyLabels(option.Label, item);
                        touchesBooleanOptions = true;
                        break;
                }
            }

            // Don't send choices along unnecessarily (choice values go through UpdateOptionValueRequest)
            if (am is EnumAttributeMetadata enumAttr) enumAttr.OptionSet = null;
            if (am is BooleanAttributeMetadata b && !touchesBooleanOptions) b.OptionSet = null;

            service.Execute(new UpdateAttributeRequest { EntityName = entity, Attribute = am, MergeLabels = true });
        }

        private static void SaveOptionLabel(IOrganizationService service, TranslationItem item)
        {
            var label = ChangedLabelsOnly(item);
            if (item.Kind == ItemKind.StateLabel)
            {
                service.Execute(new UpdateStateValueRequest
                {
                    EntityLogicalName = item.Entity,
                    AttributeLogicalName = item.Attribute,
                    Value = item.OptionValue,
                    Label = label,
                    MergeLabels = true
                });
                return;
            }

            var request = new UpdateOptionValueRequest { Value = item.OptionValue, Label = label, MergeLabels = true };
            if (item.GlobalOptionSet != null)
            {
                request.OptionSetName = item.GlobalOptionSet;
            }
            else
            {
                request.EntityLogicalName = item.Entity;
                request.AttributeLogicalName = item.Attribute;
            }
            service.Execute(request);
        }

        private static void SaveGlobalBooleanLabels(IOrganizationService service, string optionSetName, List<TranslationItem> items)
        {
            var os = (BooleanOptionSetMetadata)((RetrieveOptionSetResponse)service.Execute(new RetrieveOptionSetRequest
            {
                Name = optionSetName,
                RetrieveAsIfPublished = true
            })).OptionSetMetadata;

            foreach (var item in items)
            {
                var option = item.OptionValue == 1 ? os.TrueOption : os.FalseOption;
                option.Label = ApplyLabels(option.Label, item);
            }
            service.Execute(new UpdateOptionSetRequest { OptionSet = os, MergeLabels = true });
        }

        private static void SaveFormLabels(IOrganizationService service, Guid formId, List<TranslationItem> items)
        {
            var form = service.Retrieve("systemform", formId, new ColumnSet("formxml"));
            var doc = XDocument.Parse(form.GetAttributeValue<string>("formxml"));

            foreach (var item in items)
            {
                var tag = item.Kind == ItemKind.FormTab ? "tab" : item.Kind == ItemKind.FormSection ? "section" : "cell";
                var element = doc.Descendants(tag).FirstOrDefault(e => string.Equals((string)e.Attribute("id"), item.ElementId, StringComparison.OrdinalIgnoreCase));
                if (element == null)
                    throw new InvalidOperationException($"Element “{item.ElementName}” ({item.ElementId}) no longer exists in the form – please reload.");
                foreach (var pair in item.Snapshot) SetFormLabel(element, pair.Key, pair.Value);
            }

            service.Update(new Entity("systemform", formId) { ["formxml"] = doc.ToString(SaveOptions.DisableFormatting) });
        }

        private static void SetFormLabel(XElement element, int lcid, string text)
        {
            var labels = element.Element("labels");
            if (labels == null)
            {
                labels = new XElement("labels");
                element.AddFirst(labels);
            }
            var label = labels.Elements("label").FirstOrDefault(l => (string)l.Attribute("languagecode") == lcid.ToString());
            if (label == null)
                labels.Add(new XElement("label", new XAttribute("description", text), new XAttribute("languagecode", lcid)));
            else
                label.SetAttributeValue("description", text);
        }

        #endregion
    }
}
