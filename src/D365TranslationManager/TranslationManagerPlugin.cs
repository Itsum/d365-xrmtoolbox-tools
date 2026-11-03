using System.ComponentModel.Composition;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;

namespace D365Tools.TranslationManager
{
    [Export(typeof(IXrmToolBoxPlugin)),
     ExportMetadata("Name", "D365 Translation Manager"),
     ExportMetadata("Description", "Edit display names, descriptions, choice labels and form labels in all installed languages. Explains what you change and where; saves in the background and publishes only what changed."),
     ExportMetadata("SmallImageBase64", PluginIcons.Small),
     ExportMetadata("BigImageBase64", PluginIcons.Big),
     ExportMetadata("BackgroundColor", "White"),
     ExportMetadata("PrimaryFontColor", "#201F1E"),
     ExportMetadata("SecondaryFontColor", "#605E5C")]
    public class TranslationManagerPlugin : PluginBase
    {
        public override IXrmToolBoxPluginControl GetControl()
        {
            return new TranslationManagerControl();
        }
    }
}
