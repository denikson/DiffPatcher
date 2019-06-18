using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using NetDiffPatch;

namespace CorlibDiff
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            using (var from = AssemblyDefinition.ReadAssembly("from_mscorlib.dll"))
            using (var to = AssemblyDefinition.ReadAssembly("to_mscorlib.dll"))
            {
                var nsBlacklist = new HashSet<string>();
                var typeBlacklist = new HashSet<string>
                {
                    "System.AppContextSwitches",
                    "System.Activator",
                    "System.Attribute",
                    "System.CharEnumerator",
                    "System.ConsoleCancelEventArgs",
                    "System.Delegate",
                    "System.Environment",
                    "System.Exception",
                    "System.LocalDataStoreSlot",
                    "System.MarshalByRefObject",
                    "System.MonoCustomAttrs",
                    "System.TimeZoneInfo",
                    "System.Variant"
                };


                foreach (var s in from.MainModule.Types.Select(t => t.Namespace))
                    nsBlacklist.Add(s);
                foreach (var s in to.MainModule.Types.Select(t => t.Namespace))
                    nsBlacklist.Add(s);

                nsBlacklist.Remove("System");
                nsBlacklist.Remove("System.Reflection");
                nsBlacklist.Remove("System.Reflection.Emit");
                nsBlacklist.Remove("Mono.Interop");

                using (var differ = new NetDiffer(from.MainModule, to.MainModule, nsBlacklist, typeBlacklist))
                using (var diff = differ.CreateDiffAssembly())
                {
                    diff.Write("mscorlib_diff.dll");
                }
            }
        }
    }
}