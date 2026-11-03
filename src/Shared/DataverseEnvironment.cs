using System.Drawing;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;

namespace D365Tools.Shared
{
    /// <summary>One connected environment (XrmToolBox connection + service).</summary>
    internal sealed class DataverseEnvironment
    {
        public DataverseEnvironment(ConnectionDetail detail, IOrganizationService service, bool isPrimary)
        {
            Detail = detail;
            Service = service;
            IsPrimary = isPrimary;
        }

        public ConnectionDetail Detail { get; }
        public IOrganizationService Service { get; }
        public bool IsPrimary { get; }

        /// <summary>Filled when the environment is loaded.</summary>
        public EnvLanguages Languages { get; set; }

        public string Name => string.IsNullOrWhiteSpace(Detail?.ConnectionName) ? Detail?.OrganizationFriendlyName ?? "?" : Detail.ConnectionName;
        public string Url => Detail?.WebApplicationUrl ?? Detail?.OriginalUrl ?? string.Empty;
        public Color? Color => Detail?.EnvironmentColor;

        /// <summary>Heuristic for an extra warning before saving.</summary>
        public bool LooksLikeProduction
        {
            get
            {
                var text = (Name + " " + Detail?.EnvironmentText).ToLowerInvariant();
                return text.Contains("prod") || text.Contains("live") || text.Contains("prd");
            }
        }

        public override string ToString() => Name;
    }
}
