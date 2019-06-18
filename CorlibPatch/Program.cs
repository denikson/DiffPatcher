using System;
using System.IO;
using System.Reflection;
using Mono.Cecil;
using NetDiffPatch;

namespace CorlibPatch
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length == 0 || !File.Exists(args[0]))
            {
                Console.WriteLine("The first (and only argument) must be a path to mscorlib.dll");
                return 1;
            }

            var from = args[0]; 

            Console.WriteLine("Loading assemblies into Cecil");
            using (var diff =
                AssemblyDefinition.ReadAssembly(Assembly.GetExecutingAssembly()
                                                        .GetManifestResourceStream("CorlibPatch.mscorlib_diff.dll")))
                using (var corlib = AssemblyDefinition.ReadAssembly(new MemoryStream(File.ReadAllBytes(from))))
                    using (var patcher = new NetPatcher(diff.MainModule))
                    {
                        Console.WriteLine("Backing up old mscorlib");
                        File.Move(from,
                                  Path.Combine(Path.GetDirectoryName(from),
                                               $"{Path.GetFileName(from)}.netstandard.bak"));

                        Console.WriteLine("Patching mscorlib");
                        patcher.Patch(corlib.MainModule);

                        Console.WriteLine("Writing mscorlib to file");
                        corlib.Write("mscorlib.dll");
                    }

            return 0;
        }
    }
}