using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using Mono.Cecil;
using MonoMod;

namespace BepInEx.MonoMod.Loader
{
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs => CollectTargetDLLs();

        private static ManualLogSource Logger = Logging.Logger.CreateLogSource("MonoMod");

        private static bool Debugging = false;

        public static string[] ResolveDirectories { get; set; } =
        {
            Paths.BepInExAssemblyDirectory,
            Paths.ManagedPath,
            Paths.PatcherPluginPath,
            Paths.PluginPath
        };

        private static readonly HashSet<string> UnpatchableAssemblies =
            new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { "mscorlib" };

        private static IEnumerable<string> CollectTargetDLLs()
        {
            string monoModPath = Path.Combine(Paths.BepInExRootPath, "monomod");

            if (!Directory.Exists(monoModPath))
                Directory.CreateDirectory(monoModPath);

            Logger.LogInfo(typeof(MonoModder).Assembly.FullName);

            LogLevel logLevel = ((ConfigEntry<LogLevel>)typeof(ConsoleLogListener).GetField("ConfigConsoleDisplayedLevel", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null)).Value;
            Debugging = (logLevel == LogLevel.Debug) || logLevel == LogLevel.All;

            Logger.LogInfo("Collecting target assemblies from mods");


            var result = new HashSet<string>();

            foreach (var modDll in Directory.GetFiles(monoModPath, "*.mm.dll", SearchOption.AllDirectories))
            {
                Logger.LogDebug($"Checking {modDll}.");
                var fileName = Path.GetFileNameWithoutExtension(modDll);
                try
                {
                    using (var ass = AssemblyDefinition.ReadAssembly(modDll))
                        foreach (var assRef in ass.MainModule.AssemblyReferences)
                            if (!UnpatchableAssemblies.Contains(assRef.Name) &&
                                (fileName.StartsWith(assRef.Name, StringComparison.InvariantCultureIgnoreCase) ||
                                 fileName.StartsWith(assRef.Name.Replace(" ", ""),
                                                     StringComparison.InvariantCultureIgnoreCase)))
                            {
                                Logger.LogDebug($"\tAdded {assRef.Name}.dll");
                                result.Add($"{assRef.Name}.dll");
                            }
                }
                catch (Exception e)
                {
                    Logger.LogDebug($"\tRejected: {e.Message}");
                }
            }

            return result;
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", "Cecil");

            string monoModPath = Path.Combine(Paths.BepInExRootPath, "monomod");

            if (!Directory.Exists(monoModPath))
                Directory.CreateDirectory(monoModPath);

            Logger.LogDebug($"Patch: {assembly.Name}");


            using (var monoModder = new RuntimeMonoModder(assembly, Logger))
            {
                monoModder.LogVerboseEnabled = Debugging;

                monoModder.DependencyDirs.AddRange(ResolveDirectories);

                var resolver = (BaseAssemblyResolver)monoModder.AssemblyResolver;
                var moduleResolver = (BaseAssemblyResolver)monoModder.Module.AssemblyResolver;

                foreach (var dir in ResolveDirectories)
                    resolver.AddSearchDirectory(dir);

                resolver.ResolveFailure += ResolverOnResolveFailure;
                // Add our dependency resolver to the assembly resolver of the module we are patching
                moduleResolver.ResolveFailure += ResolverOnResolveFailure;

                monoModder.PerformPatches(monoModPath);

                // Then remove our resolver after we are done patching to not interfere with other patchers
                moduleResolver.ResolveFailure -= ResolverOnResolveFailure;
            }
        }

        private static AssemblyDefinition ResolverOnResolveFailure(object sender, AssemblyNameReference reference)
        {
            foreach (var directory in ResolveDirectories)
            {
                var potentialDirectories = new List<string> { directory };

                potentialDirectories.AddRange(Directory.GetDirectories(directory, "*", SearchOption.AllDirectories));

                var potentialFiles = potentialDirectories.Select(x => Path.Combine(x, $"{reference.Name}.dll"))
                                                         .Concat(potentialDirectories.Select(
                                                                     x => Path.Combine(x, $"{reference.Name}.exe")));

                foreach (string path in potentialFiles)
                {
                    if (!File.Exists(path))
                        continue;

                    var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters(ReadingMode.Deferred));

                    if (assembly.Name.Name == reference.Name)
                        return assembly;

                    assembly.Dispose();
                }
            }

            return null;
        }
    }
}