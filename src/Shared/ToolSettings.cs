using System;
using System.Linq;
using System.Reflection;
using XrmToolBox.Extensibility;

namespace D365Tools.Shared
{
    /// <summary>Per-tool settings, stored by XrmToolBox's SettingsManager (survive restarts and updates).</summary>
    public class ToolSettings
    {
        public ThemeMode Theme { get; set; } = ThemeMode.System;

        /// <summary>LCIDs of the language columns the user picked. Empty = base language + first additional language.</summary>
        public int[] Languages { get; set; } = new int[0];

        public static ToolSettings Load(Type pluginType)
        {
            try
            {
                if (SettingsManager.Instance.TryLoad(pluginType, out ToolSettings settings, "Default") && settings != null)
                    return settings;
            }
            catch
            {
                // Settings are a convenience – never block the tool because of them
            }
            return new ToolSettings();
        }

        public void Save(Type pluginType)
        {
            try
            {
                SettingsManager.Instance.Save(pluginType, this, "Default");
            }
            catch
            {
                // ignore (e.g. read-only profile)
            }
        }
    }

    /// <summary>Project links, read from assembly metadata that MSBuild generates from Directory.Build.props.</summary>
    internal static class ToolInfo
    {
        private static string Meta(string key) =>
            typeof(ToolInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value ?? string.Empty;

        public static string RepositoryUrl => Meta("RepositoryUrl").TrimEnd('/');

        private static string[] RepoParts
        {
            get
            {
                if (!Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var uri)) return new string[0];
                return uri.AbsolutePath.Trim('/').Split('/');
            }
        }

        public static string GitHubUser => RepoParts.Length > 0 ? RepoParts[0] : string.Empty;
        public static string GitHubRepository => RepoParts.Length > 1 ? RepoParts[1] : string.Empty;
        public static string HelpUrl => RepositoryUrl.Length > 0 ? RepositoryUrl + "#readme" : string.Empty;
    }
}
