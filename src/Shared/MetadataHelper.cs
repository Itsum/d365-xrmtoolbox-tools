using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Metadata.Query;
using Microsoft.Xrm.Sdk.Query;

namespace D365Tools.Shared
{
    internal static class MetadataHelper
    {
        /// <summary>
        /// Fast metadata query (RetrieveMetadataChanges) that only returns the requested properties.
        /// Labels are returned for all installed languages.
        /// </summary>
        public static EntityMetadata[] RetrieveEntities(IOrganizationService service, ICollection<string> logicalNames,
            string[] entityProperties, string[] attributeProperties = null)
        {
            var criteria = new MetadataFilterExpression(LogicalOperator.And);
            if (logicalNames != null && logicalNames.Count > 0)
            {
                criteria.Conditions.Add(new MetadataConditionExpression("LogicalName", MetadataConditionOperator.In, logicalNames.ToArray()));
            }

            var props = entityProperties.ToList();
            if (attributeProperties != null && !props.Contains("Attributes")) props.Add("Attributes");

            var query = new EntityQueryExpression
            {
                Criteria = criteria,
                Properties = new MetadataPropertiesExpression(props.ToArray())
            };

            if (attributeProperties != null)
            {
                query.AttributeQuery = new AttributeQueryExpression
                {
                    Properties = new MetadataPropertiesExpression(attributeProperties)
                };
            }

            var response = (RetrieveMetadataChangesResponse)service.Execute(new RetrieveMetadataChangesRequest { Query = query });
            return response.EntityMetadata.ToArray();
        }

        /// <summary>Base language (organization.languagecode) and installed languages.</summary>
        public static EnvLanguages RetrieveLanguages(IOrganizationService service)
        {
            var result = new EnvLanguages();
            var org = service.RetrieveMultiple(new QueryExpression("organization") { ColumnSet = new ColumnSet("languagecode"), TopCount = 1 }).Entities.FirstOrDefault();
            result.Base = org?.GetAttributeValue<int>("languagecode") ?? 0;
            try
            {
                result.Provisioned = ((RetrieveProvisionedLanguagesResponse)service.Execute(new RetrieveProvisionedLanguagesRequest())).RetrieveProvisionedLanguages ?? new int[0];
            }
            catch
            {
                result.Provisioned = new[] { result.Base };
            }
            return result;
        }

        /// <summary>Publishes only the given tables / global choices (instead of "Publish all").</summary>
        public static void Publish(IOrganizationService service, IEnumerable<string> entities, IEnumerable<string> globalOptionSets = null)
        {
            var entityList = entities?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            var optionSetList = globalOptionSets?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            if (entityList.Count == 0 && optionSetList.Count == 0) return;

            var xml = new StringBuilder("<importexportxml>");
            if (entityList.Count > 0)
            {
                xml.Append("<entities>");
                foreach (var e in entityList) xml.Append("<entity>").Append(e).Append("</entity>");
                xml.Append("</entities>");
            }
            if (optionSetList.Count > 0)
            {
                xml.Append("<optionsets>");
                foreach (var o in optionSetList) xml.Append("<optionset>").Append(o).Append("</optionset>");
                xml.Append("</optionsets>");
            }
            xml.Append("</importexportxml>");

            service.Execute(new PublishXmlRequest { ParameterXml = xml.ToString() });
        }

        public static string GetLabel(Label label, int lcid)
        {
            return label?.LocalizedLabels?.FirstOrDefault(l => l.LanguageCode == lcid)?.Label ?? string.Empty;
        }

        /// <summary>All languages of a label as LCID → text.</summary>
        public static Dictionary<int, string> LabelsOf(Label label)
        {
            var result = new Dictionary<int, string>();
            if (label?.LocalizedLabels == null) return result;
            foreach (var l in label.LocalizedLabels) result[l.LanguageCode] = l.Label ?? string.Empty;
            return result;
        }

        /// <summary>Copies labels that are still missing (first environment wins).</summary>
        public static void MergeLabels(Dictionary<int, string> target, Label source)
        {
            foreach (var pair in LabelsOf(source))
            {
                if (!target.TryGetValue(pair.Key, out var existing) || string.IsNullOrEmpty(existing))
                    target[pair.Key] = pair.Value;
            }
        }

        /// <summary>Sets the text of exactly one language; other languages are left untouched.</summary>
        public static Label SetLabel(Label label, int lcid, string text)
        {
            if (label == null) label = new Label();
            var existing = label.LocalizedLabels.FirstOrDefault(l => l.LanguageCode == lcid);
            if (existing != null) existing.Label = text;
            else label.LocalizedLabels.Add(new LocalizedLabel(text, lcid));
            if (label.UserLocalizedLabel != null && label.UserLocalizedLabel.LanguageCode == lcid)
                label.UserLocalizedLabel.Label = text;
            return label;
        }

        /// <summary>Columns that make sense in the UI (skips virtual helper columns such as "createdbyname").</summary>
        public static bool IsRelevantAttribute(AttributeMetadata a)
        {
            if (!string.IsNullOrEmpty(a.AttributeOf)) return false;
            if (a.AttributeType == AttributeTypeCode.Virtual)
                return a.AttributeTypeName?.Value == "MultiSelectPicklistType"
                    || a.AttributeTypeName?.Value == "ImageType"
                    || a.AttributeTypeName?.Value == "FileType";
            return true;
        }

        public static string FriendlyType(AttributeMetadata a)
        {
            var name = a.AttributeTypeName?.Value ?? a.AttributeType?.ToString() ?? "";
            if (name.EndsWith("Type")) name = name.Substring(0, name.Length - 4);
            switch (name)
            {
                case "String": return "Text";
                case "Memo": return "Multiline text";
                case "Picklist": return "Choice";
                case "MultiSelectPicklist": return "Choices";
                case "Lookup": return "Lookup";
                case "Customer": return "Customer";
                case "Owner": return "Owner";
                case "Money": return "Currency";
                case "DateTime": return "Date/time";
                case "Boolean": return "Yes/No";
                case "Integer": return "Whole number";
                case "BigInt": return "Big integer";
                case "Decimal": return "Decimal";
                case "Double": return "Float";
                case "State": return "Status";
                case "Status": return "Status reason";
                case "Uniqueidentifier": return "Unique identifier";
                case "EntityName": return "Table name";
                case "PartyList": return "Party list";
                case "Image": return "Image";
                case "File": return "File";
                default: return name;
            }
        }

        /// <summary>"account, contact; salesorder" → list of names. Empty = all.</summary>
        public static List<string> ParseNames(string text)
        {
            return (text ?? string.Empty)
                .Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();
        }

        /// <summary>Readable error text (CrmServiceClient already puts the fault details into Message).</summary>
        public static string ErrorText(Exception ex)
        {
            while (ex is AggregateException agg && agg.InnerExceptions.Count == 1) ex = agg.InnerException;
            return ex?.Message ?? "Unknown error";
        }
    }
}
