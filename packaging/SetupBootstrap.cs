using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AslainAssistant
{
    // Load the embedded UI dependency before the CLR resolves any UI types.
    // Setup and Uninstall remain standalone executables, including on a clean PC.
    static class SetupBootstrap
    {
        static Assembly uiAssembly;
        [STAThread] static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender,resolve) => new AssemblyName(resolve.Name).Name == "AntdUI" ? uiAssembly : null;
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("AntdUIPayload"))
            using (var buffer = new MemoryStream())
            {
                if (input == null) return 2;
                input.CopyTo(buffer); uiAssembly = Assembly.Load(buffer.ToArray());
            }
            return Run(args);
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run(string[] args) { return Setup.Run(args); }
    }
}
