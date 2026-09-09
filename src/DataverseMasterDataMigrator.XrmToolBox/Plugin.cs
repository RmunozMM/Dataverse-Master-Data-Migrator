using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using DataverseMasterDataMigrator.XrmToolBox.UI;

namespace DataverseMasterDataMigrator.XrmToolBox
{
    // Icons: 32x32 (SmallImageBase64) and 80x80 (BigImageBase64), matching the sizes actually
    // used by Metadata Dataverse Document (verified by decoding its own compiled DLL) — source
    // PNGs kept under Resources/ for future regeneration. Re-run tools/verify_attr_blobs.py
    // against the compiled DLL any time these change (see ARCHITECTURE.md section 25 /
    // PLAN_CORRECCION_CRASH_EXPORT.md): any ExportMetadata string over 16383 characters risks
    // the exact CustomAttributeFormatException that once took down Metadata Dataverse Document.
    [Export(typeof(IXrmToolBoxPlugin)),
        ExportMetadata("Name", "Dataverse Master Data Migrator"),
        ExportMetadata("Description", "Migrate master data between Dataverse environments using reusable, persistent migration profiles instead of manual table-by-table transfers."),
        ExportMetadata("PluginType", "DataMigration"),
        ExportMetadata("SmallImageBase64", "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"),
        ExportMetadata("BigImageBase64", "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"),
        ExportMetadata("BackgroundColor", "Lavender"),
        ExportMetadata("PrimaryFontColor", "Black"),
        ExportMetadata("SecondaryFontColor", "Gray")]
    public class Plugin : PluginBase
    {
        // Same reasoning as Metadata Dataverse Document's Plugin.cs: AssemblyResolve is a
        // process-wide event, not a per-plugin one. Register exactly once per process.
        private static int _resolverRegistered;

        // ClosedXML's own transitive dependency graph pulls in a HIGHER version of these four
        // assemblies than the exact version their consumer (SixLabors.Fonts.dll, a ClosedXML
        // dependency used for font metrics) was actually compiled against - a genuine version
        // mismatch, not a missing-file problem (verified: SixLabors.Fonts.dll references
        // System.Buffers 4.0.2.0 / System.Numerics.Vectors 4.1.3.0, but the versions restored
        // and shipped here are 4.0.3.0 / 4.1.4.0). Strong-named assemblies require an EXACT
        // version match by default; as a plugin hosted inside XrmToolBox.exe we cannot ship an
        // app.config bindingRedirect for that process, so this is the standard manual-redirect
        // substitute: for exactly this small, explicit set of known-mismatched assemblies,
        // always load whatever copy we actually shipped, no matter which exact version any
        // requestor asks for - and regardless of which assembly is doing the asking (unlike the
        // general path below, a mismatch here can be requested by an assembly several levels
        // down the dependency chain, e.g. SixLabors.Fonts.dll itself, not just by this plugin's
        // own top-level assembly). Kept as a narrow, explicit allowlist checked first so it can
        // never change resolution behavior for any other assembly this resolver already handles
        // correctly today.
        private static readonly HashSet<string> ForcedOwnDependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Buffers",
            "System.Memory",
            "System.Numerics.Vectors",
            "System.Runtime.CompilerServices.Unsafe",
        };

        public Plugin()
        {
            if (Interlocked.Exchange(ref _resolverRegistered, 1) == 0)
            {
                AppDomain.CurrentDomain.AssemblyResolve += AssemblyResolveEventHandler;
            }
        }

        public override IXrmToolBoxPluginControl GetControl()
        {
            return new PluginControl();
        }

        /// <summary>
        /// Resolves ONLY this plugin's own dependencies, and only from its own subfolder
        /// (Plugins\DataverseMasterDataMigrator\). Never touches the Plugins root, and never
        /// answers a request that isn't this assembly's own declared dependency — see
        /// ARCHITECTURE.md section "Assembly isolation" and Metadata Dataverse Document's
        /// Plugin.cs for the full rationale (this is a direct, line-for-line port of that
        /// already-validated logic).
        /// </summary>
        private static Assembly AssemblyResolveEventHandler(object sender, ResolveEventArgs args)
        {
            try
            {
                var thisAssembly = typeof(Plugin).Assembly;
                var requested = new AssemblyName(args.Name);

                string pluginsDir = Path.GetDirectoryName(thisAssembly.Location);
                string ownFolder = Path.GetFileNameWithoutExtension(thisAssembly.Location);
                string ownSubfolder = Path.Combine(pluginsDir, ownFolder);

                // Forced redirect for the small, explicit set of known version-mismatched
                // transitive dependencies (see ForcedOwnDependencies above). Checked first, and
                // returns unconditionally either way, so it can never fall through into (or be
                // affected by) the general path below.
                if (ForcedOwnDependencies.Contains(requested.Name))
                {
                    string forcedCandidate = Path.Combine(ownSubfolder, requested.Name + ".dll");
                    if (!File.Exists(forcedCandidate))
                    {
                        // Fallback for a flat layout (e.g. straight out of bin\Release, before
                        // the installer splits the main assembly from its own subfolder) where
                        // the dependency sits next to the main assembly instead of inside its
                        // dedicated subfolder.
                        forcedCandidate = Path.Combine(pluginsDir, requested.Name + ".dll");
                    }

                    return File.Exists(forcedCandidate) ? Assembly.LoadFrom(forcedCandidate) : null;
                }

                if (args.RequestingAssembly != null && args.RequestingAssembly != thisAssembly)
                {
                    return null;
                }

                bool isOwnDependency = thisAssembly
                    .GetReferencedAssemblies()
                    .Any(a => string.Equals(a.Name, requested.Name, StringComparison.OrdinalIgnoreCase));
                if (!isOwnDependency)
                {
                    return null;
                }

                string candidate = Path.Combine(ownSubfolder, requested.Name + ".dll");

                if (!File.Exists(candidate))
                {
                    return null;
                }

                if (requested.Version != null)
                {
                    var candidateName = AssemblyName.GetAssemblyName(candidate);
                    if (candidateName.Version != null && candidateName.Version < requested.Version)
                    {
                        return null;
                    }
                }

                return Assembly.LoadFrom(candidate);
            }
            catch
            {
                return null;
            }
        }
    }
}
