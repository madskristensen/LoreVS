using System;
using System.Globalization;

using Microsoft.VisualStudio.Shell;

namespace LoreVS
{
    /// <summary>
    /// Registration attribute that records this package as the owner of a named solution-persistence
    /// section under <c>SolutionPersistence\&lt;PropName&gt;</c>. When Visual Studio opens a solution
    /// containing that GlobalSection it consults this registration, loads the owning package, and
    /// drives its <see cref="Microsoft.VisualStudio.Shell.Interop.IVsPersistSolutionProps"/>
    /// implementation. The attribute is not part of the SDK; source-control packages traditionally
    /// ship their own copy (see the Microsoft SccProvider sample).
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    internal sealed class ProvideSolutionPropsAttribute : RegistrationAttribute
    {
        public ProvideSolutionPropsAttribute(string propName)
        {
            PropName = propName;
        }

        public string PropName { get; }

        public override void Register(RegistrationContext context)
        {
            Key childKey = null;
            try
            {
                childKey = context.CreateKey(string.Format(CultureInfo.InvariantCulture, "SolutionPersistence\\{0}", PropName));
                childKey.SetValue(string.Empty, context.ComponentType.GUID.ToString("B"));
            }
            finally
            {
                childKey?.Close();
            }
        }

        public override void Unregister(RegistrationContext context)
        {
            context.RemoveKey(string.Format(CultureInfo.InvariantCulture, "SolutionPersistence\\{0}", PropName));
        }
    }
}
