using System;
using System.Runtime.InteropServices;
using Rhino;
using Rhino.PlugIns;

namespace SERhinoIFC
{
    [Guid("B7A3F2E1-9C4D-4A6B-8E5F-1D2C3B4A5E6F")]
    public class SERhinoIFCPlugin : PlugIn
    {
        public static SERhinoIFCPlugin Instance { get; private set; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Instance = this;
            RhinoApp.WriteLine("SERhinoIFC plugin loaded.");
            return LoadReturnCode.Success;
        }
    }
}
