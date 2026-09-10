using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Dataverse Master Data Migrator - Core")]
[assembly: AssemblyDescription("Motor de migración de datos maestros entre ambientes Microsoft Dataverse, independiente de UI y de XrmToolBox.")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Rogelio Muñoz")]
[assembly: AssemblyProduct("Dataverse Master Data Migrator")]
[assembly: AssemblyCopyright("Copyright © Rogelio Muñoz 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("c2a1f9e0-0000-4a2b-9c3d-000000000001")]

[assembly: AssemblyVersion("0.2.3.0")]
[assembly: AssemblyFileVersion("0.2.3.0")]

// Permite que el proyecto de tests acceda a tipos internos si en el futuro se necesita marcar
// algo como "internal" en vez de "public" sin perder cobertura de tests.
[assembly: InternalsVisibleTo("DataverseMasterDataMigrator.Tests")]
