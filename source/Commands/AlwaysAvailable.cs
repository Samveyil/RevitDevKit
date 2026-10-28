using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitDevKit.Commands
{
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => applicationData != null;
    }
}
