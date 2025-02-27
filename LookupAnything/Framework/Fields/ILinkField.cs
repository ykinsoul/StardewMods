using Pathoschild.Stardew.LookupAnything.Framework.Lookups;

namespace Pathoschild.Stardew.LookupAnything.Framework.Fields
{
    /// <summary>A field which links to another entry.</summary>
    internal interface ILinkField : ICustomField
    {
        /*********
        ** Public methods
        *********/
        /// <summary>Get the subject the link points to, or <c>null</c> to stay on the current subject.</summary>
        ISubject? GetLinkSubject();
    }
}
