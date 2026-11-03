using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace ioBroker_NewGen.Startup
{
    /// <summary>
    /// Resolves dependency assemblies (and their native libraries) from the <c>lib</c> folder
    /// next to the executable, so the output root only contains the application itself.
    /// </summary>
    internal static class DependencyResolver
    {
        private static readonly string LibDirectory = Path.Combine(AppContext.BaseDirectory, "lib");

        [ModuleInitializer]
        internal static void Initialize()
        {
            AssemblyLoadContext.Default.Resolving += ResolveManaged;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveNative;
        }

        private static Assembly? ResolveManaged(AssemblyLoadContext context, AssemblyName name)
        {
            if (name.Name is null)
            {
                return null;
            }

            var path = Path.Combine(LibDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        private static IntPtr ResolveNative(Assembly assembly, string libraryName)
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            var fileName = OperatingSystem.IsWindows() ? libraryName + ".dll"
                : OperatingSystem.IsMacOS() ? "lib" + libraryName + ".dylib"
                : "lib" + libraryName + ".so";

            string[] candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName),
                Path.Combine(LibDirectory, "runtimes", rid, "native", fileName),
                Path.Combine(LibDirectory, fileName)
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }
    }
}
