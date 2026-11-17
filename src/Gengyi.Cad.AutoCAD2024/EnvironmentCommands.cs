using System;
using System.Globalization;
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(Gengyi.Cad.AutoCAD2024.Plugin))]
[assembly: CommandClass(typeof(Gengyi.Cad.AutoCAD2024.EnvironmentCommands))]
[assembly: CommandClass(typeof(Gengyi.Cad.AutoCAD2024.Commands))]
[assembly: CommandClass(typeof(Gengyi.Cad.AutoCAD2024.BlockCommands))]

namespace Gengyi.Cad.AutoCAD2024
{
    public sealed class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            Host.Start();
            CadApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                "\nGENGYI loaded. Run GYSETUP to open the three-room layout panel; GYENV for host diagnostics.\n");
        }

        public void Terminate() { Host.Stop(); }
    }

    /// <summary>Read-only host diagnostics. Does not modify drawings or security settings.</summary>
    public sealed class EnvironmentCommands
    {
        [CommandMethod("GENGYI", "GYENV", CommandFlags.Modal)]
        public void ReportEnvironment()
        {
            var document = CadApplication.DocumentManager.MdiActiveDocument;
            if (document == null) return;

            var editor = document.Editor;
            string version = ReadVariable("ACADVER");
            editor.WriteMessage("\nGENGYI environment report");
            editor.WriteMessage("\nExpected host: AutoCAD 2024 / R24.3 / Windows x64 / .NET Framework 4.8");
            editor.WriteMessage("\nPRODUCT: " + ReadVariable("PRODUCT"));
            editor.WriteMessage("\nACADVER: " + version);
            editor.WriteMessage("\nVERNUM (host build): " + ReadVariable("VERNUM"));
            editor.WriteMessage("\nPLATFORM: " + ReadVariable("PLATFORM"));
            editor.WriteMessage("\nOS: " + Environment.OSVersion);
            editor.WriteMessage("\n64-bit process: " + Environment.Is64BitProcess);
            // CLR 4.0.30319 does not identify the installed Framework 4.x patch.
            editor.WriteMessage("\nCLR (not Framework patch): " + Environment.Version);
            editor.WriteMessage("\nManaged API: " + typeof(CadApplication).Assembly.GetName().Version);
            editor.WriteMessage("\nPlugin target: .NETFramework,Version=v4.8");
            editor.WriteMessage("\nPlugin: " + typeof(EnvironmentCommands).Assembly.GetName().Version);
            editor.WriteMessage("\nINSUNITS: " + document.Database.Insunits);
            editor.WriteMessage("\nUCS to WCS: " + editor.CurrentUserCoordinateSystem);
            if (!version.StartsWith("24.3", StringComparison.Ordinal))
                editor.WriteMessage("\nHost mismatch: this build targets R24.3; loading in other hosts is unverified.");
            if (document.Database.Insunits != Autodesk.AutoCAD.DatabaseServices.UnitsValue.Millimeters)
                editor.WriteMessage("\nDrawing is not explicitly in millimeters; conversion/calibration is required before layout.");
            editor.WriteMessage("\nGYENV is read-only; verify the three-room workflow, original blocks, undo and reopen using USAGE.md.\n");
        }

        private static string ReadVariable(string name)
        {
            try
            {
                return Convert.ToString(CadApplication.GetSystemVariable(name),
                    CultureInfo.InvariantCulture) ?? "unavailable";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return "unavailable";
            }
        }
    }
}
