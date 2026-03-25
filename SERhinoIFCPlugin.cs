using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

[assembly: PlugInDescription(DescriptionType.Address, "")]
[assembly: PlugInDescription(DescriptionType.Country, "")]
[assembly: PlugInDescription(DescriptionType.Email, "")]
[assembly: PlugInDescription(DescriptionType.Phone, "")]
[assembly: PlugInDescription(DescriptionType.Fax, "")]
[assembly: PlugInDescription(DescriptionType.Organization, "Sixth Eye")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "")]
[assembly: PlugInDescription(DescriptionType.WebSite, "")]

namespace SERhinoIFC
{
    [Guid("C06A81B4-E2D0-4732-9AAC-601B0592B58C")]
    public class SERhinoIFCPlugin : PlugIn
    {
        public static SERhinoIFCPlugin Instance { get; private set; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Instance = this;
            RhinoApp.WriteLine("SERhinoIFC plugin loaded.");
            return LoadReturnCode.Success;
        }
    }
}
