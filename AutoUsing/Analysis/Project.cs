﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoUsing
{
    /// <summary>
    ///     Loads and keeps `.csproj` file's data.
    ///     Optional: Watch the `.csproj` file for changes.
    /// </summary>
    public class Project : IDisposable
    {
        private XmlDocument Document { get; set; }
        private XmlNamespaceManager NamespaceManager { get; set; }
        private FileWatcher FileWatcher { get; set; }

        public List<PackageReference> References { get; set; }
        public Dictionary<string, List<string>> LibraryAssemblies { get; set; }
        public string RootDirectory { get; private set; }
        public string Name { get; private set; }
        public string NuGetPackageRoot { get; private set; }
        public string FilePath { get; private set; }
        public string FileName { get; private set; }

        /// <summary>
        ///     Loads and keeps `.csproj` file's data.
        ///     Optional: Watch the `.csproj` file for changes.
        /// </summary>
        /// <param name="filePath">The path to the `.csproj` file to laod.</param>
        /// <param name="watch">Whether to watch for further file changes.</param>
        public Project(string filePath, bool watch)
        {
            Document = new XmlDocument();
            References = new List<PackageReference>();

            // Namespace for msbuild.
            NamespaceManager = new XmlNamespaceManager(Document.NameTable);
            NamespaceManager.AddNamespace("x", "http://schemas.microsoft.com/developer/msbuild/2003");

            // Essential info about given project file.
            LoadBasicInfo(filePath);

            // Using NuGet Packages Location, We won't need to wait for project
            // builds to get completions for referenced dependencies. As we'll query
            // the dlls directly from the NuGet installation folder set by the user.
            LoadNuGetRootDirectory();

            // Loads the relative pathes to the dll files for each referenced package.
            LoadLibraryAssemblies();

            // Package References
            LoadPackageReferences();

            // Optional: Watch for changes.
            if (watch) Watch();
        }

        /// <summary>
        ///     Loads the basic info about the specified project file.
        /// </summary>
        /// <param name="filePath">Full path to the project's `.csproj` file.</param>
        private void LoadBasicInfo(string filePath)
        {
            RootDirectory = Path.GetDirectoryName(filePath);
            Name = Path.GetFileNameWithoutExtension(filePath);
            FileName = Path.GetFileName(filePath);
            FilePath = filePath;
        }

        /// <summary>
        ///     Starts watching the project file for changes.
        /// </summary>
        private void Watch()
        {
            FileWatcher = new FileWatcher(FilePath);
            FileWatcher.Changed += (s, e) =>
            {
                if (e.ChangeType is WatcherChangeTypes.Renamed) LoadBasicInfo(e.Name);

                if (e.ChangeType is WatcherChangeTypes.Deleted)
                {
                    Dispose();
                    return;
                }

                LoadPackageReferences();
            };
            FileWatcher.EnableRaisingEvents = true;
        }

        /// <summary>
        ///     Gets the current NuGet packages installation directory
        ///     used by this project.
        /// </summary>
        private void LoadNuGetRootDirectory()
        {
            Document.Load(Path.Combine(RootDirectory, $"obj/{Name}.csproj.nuget.g.props"));

            NuGetPackageRoot = Document.SelectSingleNode("//x:NuGetPackageRoot", NamespaceManager)?.InnerText;
        }

        /// <summary>
        ///     Loads the relative dll paths of all the libraries of the project.
        /// </summary>
        private void LoadLibraryAssemblies()
        {
            var assets = JObject.Parse(File.ReadAllText(Path.Combine(RootDirectory, "obj/project.assets.json")));

            // TODO: Need to see what we do when we have multiple targets
            var targets = assets["targets"];
            var targetLibs = targets.First().First();

            LibraryAssemblies = targetLibs.ToDictionary(lib => ((JProperty)lib).Name, lib =>
            {
                return lib.First()["compile"]?.Select(assembly => ((JProperty)assembly).Name).ToList();
            }).Where(kv => kv.Value != null).ToDictionary();
        }

        /// <summary>
        ///     Loads full package info for each reference of the project.
        /// </summary>
        private void LoadPackageReferences()
        {
            Document.Load(FilePath);

            References = new List<PackageReference>();

            foreach (XmlNode node in Document.SelectNodes("//PackageReference"))
            {
                var packageName = node?.Attributes?.GetNamedItem("Include")?.InnerText;
                var packageVersion = node?.Attributes?.GetNamedItem("Version")?.InnerText;

                if (packageName.IsNullOrEmpty() || packageVersion.IsNullOrEmpty()) continue;

                var packagePath = Path.Combine(NuGetPackageRoot, $"{packageName}/{packageVersion}/");

                foreach (var assemblyPath in LibraryAssemblies[packageName + "/" + packageVersion])
                {
                    References.Add(new PackageReference
                    {
                        Name = packageName,
                        Version = packageVersion,
                        Path = Path.Combine(packagePath, assemblyPath)
                    });
                }
            }
        }

        /// <summary>
        ///     Stops the <see cref="FileWatcher"/> used by this instance
        ///     before disposal.
        /// </summary>
        public void Dispose()
        {
            FileWatcher.EnableRaisingEvents = false;
            FileWatcher?.Dispose();
        }
    }
}