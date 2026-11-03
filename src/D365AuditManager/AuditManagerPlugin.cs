using System.ComponentModel.Composition;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace D365Tools.AuditManager
{
    [Export(typeof(IXrmToolBoxPlugin)),
     ExportMetadata("Name", "D365 Audit Manager"),
     ExportMetadata("Description", "Compare and change auditing of tables and columns across several environments (e.g. DEV/TEST/PROD) side by side. Tick, save in the background, publish only what changed."),
     ExportMetadata("SmallImageBase64", PluginIcons.Small),
     ExportMetadata("BigImageBase64", PluginIcons.Big),
     ExportMetadata("BackgroundColor", "White"),
     ExportMetadata("PrimaryFontColor", "#201F1E"),
     ExportMetadata("SecondaryFontColor", "#605E5C")]
    public class AuditManagerPlugin : PluginBase
    {
        public override IXrmToolBoxPluginControl GetControl()
        {
            return new AuditManagerControl();
        }
    }
}
