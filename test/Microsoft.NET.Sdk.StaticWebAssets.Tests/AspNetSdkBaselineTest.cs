// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.StaticWebAssets.Tasks;

namespace Microsoft.NET.Sdk.StaticWebAssets.Tests
{
    [Trait("AspNetCore", "BaselineTest")]
    public class AspNetSdkBaselineTest : AspNetSdkTest
    {
        private static readonly JsonSerializerOptions BaselineSerializationOptions = new() { WriteIndented = true };
        private readonly StaticWebAssetsBaselineComparer _comparer;
        private readonly StaticWebAssetsBaselineFactory _baselineFactory;

        private string _baselinesFolder;

#if GENERATE_SWA_BASELINES
        public static bool GenerateBaselines = true;
#else
        public static bool GenerateBaselines = bool.TryParse(Environment.GetEnvironmentVariable("ASPNETCORE_TEST_BASELINES"), out var result) && result;
#endif

        private bool _generateBaselines = GenerateBaselines;

        public AspNetSdkBaselineTest(ITestOutputHelper log) : base(log)
        {
            TestAssembly = Assembly.GetCallingAssembly();
            var testAssemblyMetadata = TestAssembly.GetCustomAttributes<AssemblyMetadataAttribute>();
            RuntimeVersion = testAssemblyMetadata.SingleOrDefault(a => a.Key == "NetCoreAppRuntimePackageVersion").Value;
            DefaultPackageVersion = testAssemblyMetadata.SingleOrDefault(a => a.Key == "DefaultTestBaselinePackageVersion").Value;
            _comparer = CreateBaselineComparer();
            _baselineFactory = CreateBaselineFactory();
        }

        protected void EnsureLocalPackagesExists()
        {
            var packTransitiveDependency = CreatePackCommand(ProjectDirectory, "RazorPackageLibraryTransitiveDependency");
            ExecuteCommand(packTransitiveDependency).Should().Pass();

            var packDirectDependency = CreatePackCommand(ProjectDirectory, "RazorPackageLibraryDirectDependency");
            ExecuteCommand(packDirectDependency).Should().Pass();
        }

        public AspNetSdkBaselineTest(ITestOutputHelper log, bool generateBaselines) : this(log)
        {
            _generateBaselines = generateBaselines;
            _comparer = CreateBaselineComparer();
        }

        public TestAsset ProjectDirectory { get; set; }

        public string RuntimeVersion { get; set; }

        public string DefaultPackageVersion { get; set; }

        public string BaselinesFolder =>
            _baselinesFolder ??= ComputeBaselineFolder();

        protected Assembly TestAssembly { get; }

        protected virtual StaticWebAssetsBaselineComparer CreateBaselineComparer() => StaticWebAssetsBaselineComparer.Instance;

        private static StaticWebAssetsBaselineFactory CreateBaselineFactory() => StaticWebAssetsBaselineFactory.Instance;

        protected virtual string ComputeBaselineFolder() =>
            Path.Combine(TestContext.GetRepoRoot() ?? AppContext.BaseDirectory, "test", "Microsoft.NET.Sdk.StaticWebAssets.Tests", "StaticWebAssetsBaselines");

        protected virtual string EmbeddedResourcePrefix => string.Join('.', "Microsoft.NET.Sdk.StaticWebAssets.Tests", "StaticWebAssetsBaselines");

        public StaticWebAssetsManifest LoadBuildManifest(string suffix = "", [CallerMemberName] string name = "")
        {
            if (_generateBaselines)
            {
                return default;
            }
            else
            {
                using var stream = GetManifestEmbeddedResource(suffix, name, "Build");
                var manifest = StaticWebAssetsManifest.FromStream(stream);
                return manifest;
            }
        }

        public StaticWebAssetsManifest LoadPublishManifest(string suffix = "", [CallerMemberName] string name = "")
        {
            if (_generateBaselines)
            {
                return default;
            }
            else
            {
                using var stream = GetManifestEmbeddedResource(suffix, name, "Publish");
                var manifest = StaticWebAssetsManifest.FromStream(stream);
                return manifest;
            }
        }

        protected void AssertBuildAssets(
            StaticWebAssetsManifest manifest,
            string outputFolder,
            string intermediateOutputPath,
            string suffix = "",
            [CallerMemberName] string name = "")
        {
            var fileEnumerationOptions = new EnumerationOptions { RecurseSubdirectories = true };
            var wwwRootFolder = Path.Combine(outputFolder, "wwwroot");
            var wwwRootFiles = Directory.Exists(wwwRootFolder) ?
                Directory.GetFiles(wwwRootFolder, "*", fileEnumerationOptions) :
                [];

            var computedFiles = manifest.Assets
                .Where(a => a.SourceType is StaticWebAsset.SourceTypes.Computed &&
                            a.AssetKind is not StaticWebAsset.AssetKinds.Publish);

            // We keep track of assets that need to be copied to the output folder.
            // In addition to that, we copy assets that are defined somewhere different
            // from their content root folder when the content root does not match the output folder.
            // We do this to allow copying things like Publish assets to temporary locations during the
            // build process if they are later on going to be transformed.
            var copyToOutputDirectoryAssets = manifest.Assets.Where(a => a.ShouldCopyToOutputDirectory()).ToArray();
            var temporaryAsssets = manifest.Assets
                .Where(a =>
                    !a.HasContentRoot(Path.Combine(outputFolder, "wwwroot")) &&
                    File.Exists(a.Identity) &&
                    !File.Exists(Path.Combine(a.ContentRoot, a.RelativePath)) &&
                    a.AssetTraitName != "Content-Encoding").ToArray();

            var copyToOutputDirectoryFiles = copyToOutputDirectoryAssets
                .Select(a => Path.GetFullPath(Path.Combine(outputFolder, "wwwroot", a.RelativePath)))
                .Concat(temporaryAsssets
                    .Select(a => Path.GetFullPath(Path.Combine(a.ContentRoot, a.RelativePath))))
                .ToArray();

            var existingFiles = _baselineFactory.TemplatizeExpectedFiles(
                wwwRootFiles
                    .Concat(computedFiles.Select(a => a.Identity))
                    .Concat(copyToOutputDirectoryFiles)
                    .Distinct()
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToArray(),
                GetNuGetCachePath() ?? TestContext.Current.NuGetCachePath,
                ProjectDirectory.TestRoot,
                intermediateOutputPath,
                outputFolder).ToArray();


            if (!_generateBaselines)
            {
                var expected = LoadExpectedFilesBaseline(manifest.ManifestType, suffix, name)
                    .OrderBy(f => f, StringComparer.Ordinal);

                AssertFilesCore(existingFiles, expected);
            }
            else
            {
                File.WriteAllText(
                    GetExpectedFilesPath(suffix, name, manifest.ManifestType),
                    JsonSerializer.Serialize(existingFiles, BaselineSerializationOptions));
            }
        }

        private static void AssertFilesCore(IEnumerable<string> existingFiles, IEnumerable<string> expected)
        {
            var existingSet = new HashSet<string>(existingFiles);
            var expectedSet = new HashSet<string>(expected);
            var different = new HashSet<string>(existingFiles);

            different.SymmetricExceptWith(expectedSet);

            var messages = new List<string>();
            if (existingSet.Count < expectedSet.Count)
            {
                messages.Add("The build produced less files than expected.");
            }
            else if (expectedSet.Count < existingSet.Count)
            {
                messages.Add("The build produced more files than expected.");
            }
            else if (different.Count > 0)
            {
                messages.Add("The build produced different files than expected.");
            }

            ComputeDifferences(expectedSet, different, messages);
            string.Join(Environment.NewLine, messages).Should().BeEmpty();

            static void ComputeDifferences(HashSet<string> existingSet, HashSet<string> different, List<string> messages)
            {
                foreach (var file in different)
                {
                    if (existingSet.Contains(file))
                    {
                        messages.Add($"The file '{file}' is not in the baseline.");
                    }
                    else
                    {
                        messages.Add($"The file '{file}' is missing from the build.");
                    }
                }
            }
        }

        protected void AssertPublishAssets(
            StaticWebAssetsManifest manifest,
            string publishFolder,
            string intermediateOutputPath,
            string suffix = "",
            [CallerMemberName] string name = "")
        {
            var fileEnumerationOptions = new EnumerationOptions { RecurseSubdirectories = true };
            string wwwRootFolder = Path.Combine(publishFolder, "wwwroot");
            var wwwRootFiles = Directory.Exists(wwwRootFolder) ?
                Directory.GetFiles(wwwRootFolder, "*", fileEnumerationOptions)
                    .Select(f => _baselineFactory.TemplatizeFilePath(f, null, null, intermediateOutputPath, publishFolder, null)) :
                [];

            // Computed publish assets must exist on disk (we do this check to quickly identify when something is not being
            // generated vs when its being copied to the wrong place)
            var computedFiles = manifest.Assets
                .Where(a => a.SourceType is StaticWebAsset.SourceTypes.Computed &&
                            a.AssetKind is not StaticWebAsset.AssetKinds.Build);

            // For assets that are copied to the publish folder, the path is always based on
            // the wwwroot folder, the relative path and the base path for project or package
            // assets.
            var copyToPublishDirectoryFiles = manifest.Assets
                .Where(a => !string.Equals(a.SourceId, manifest.Source, StringComparison.Ordinal) ||
                            !string.Equals(a.AssetMode, StaticWebAsset.AssetModes.Reference))
                .Select(a => Path.Combine(wwwRootFolder, a.ComputeTargetPath("", Path.DirectorySeparatorChar)));

            var existingFiles = _baselineFactory.TemplatizeExpectedFiles(
                [.. wwwRootFiles
                    .Concat(computedFiles.Select(a => a.Identity))
                    .Concat(copyToPublishDirectoryFiles)
                    .Distinct()
                    .OrderBy(f => f, StringComparer.Ordinal)],
                GetNuGetCachePath() ?? TestContext.Current.NuGetCachePath,
                ProjectDirectory.TestRoot,
                intermediateOutputPath,
                publishFolder);

            if (!_generateBaselines)
            {
                var expected = LoadExpectedFilesBaseline(manifest.ManifestType, suffix, name);
                existingFiles.Should().BeEquivalentTo(expected);
            }
            else
            {
                File.WriteAllText(
                    GetExpectedFilesPath(suffix, name, manifest.ManifestType),
                    JsonSerializer.Serialize(existingFiles, BaselineSerializationOptions));
            }
        }

        public string[] LoadExpectedFilesBaseline(
            string type,
            string suffix,
            string name)
        {
            if (!_generateBaselines)
            {
                using var filesBaselineStream = GetExpectedFilesEmbeddedResource(suffix, name, type);
                return JsonSerializer.Deserialize<string[]>(filesBaselineStream);
            }
            else
            {
                return [];
            }
        }

        internal void AssertManifest(
            StaticWebAssetsManifest actual,
            StaticWebAssetsManifest expected,
            string suffix = "",
            string runtimeIdentifier = null,
            [CallerMemberName] string name = "")
        {
            if (!_generateBaselines)
            {
                // We are going to compare the generated manifest with the current manifest.
                // For that, we "templatize" the current manifest to avoid issues with hashes, versions, etc.
                _baselineFactory.ToTemplate(
                    actual,
                    ProjectDirectory.Path,
                    GetNuGetCachePath() ?? TestContext.Current.NuGetCachePath,
                    runtimeIdentifier);

                _comparer.AssertManifest(expected, actual);
            }
            else
            {
                var template = Templatize(actual, ProjectDirectory.Path, GetNuGetCachePath() ?? TestContext.Current.NuGetCachePath, runtimeIdentifier);
                if (!Directory.Exists(Path.Combine(BaselinesFolder)))
                {
                    Directory.CreateDirectory(Path.Combine(BaselinesFolder));
                }

                File.WriteAllText(GetManifestPath(suffix, name, actual.ManifestType), template);
            }
        }

        private string GetManifestPath(string suffix, string name, string manifestType)
            => Path.Combine(BaselinesFolder, $"{name}{(!string.IsNullOrEmpty(suffix) ? $"_{suffix}" : "")}.{manifestType}.staticwebassets.json");

        private Stream GetManifestEmbeddedResource(string suffix, string name, string manifestType)
            => TestAssembly.GetManifestResourceStream(string.Join('.', EmbeddedResourcePrefix, $"{name}{(!string.IsNullOrEmpty(suffix) ? $"_{suffix}" : "")}.{manifestType}.staticwebassets.json"));


        private string GetExpectedFilesPath(string suffix, string name, string manifestType)
            => Path.Combine(BaselinesFolder, $"{name}{(!string.IsNullOrEmpty(suffix) ? $"_{suffix}" : "")}.{manifestType}.files.json");

        private Stream GetExpectedFilesEmbeddedResource(string suffix, string name, string manifestType)
            => TestAssembly.GetManifestResourceStream(string.Join('.', EmbeddedResourcePrefix, $"{name}{(!string.IsNullOrEmpty(suffix) ? $"_{suffix}" : "")}.{manifestType}.files.json"));

        private string Templatize(StaticWebAssetsManifest manifest, string projectRoot, string restorePath, string runtimeIdentifier)
        {
            _baselineFactory.ToTemplate(manifest, projectRoot, restorePath, runtimeIdentifier);
            return JsonSerializer.Serialize(manifest, BaselineSerializationOptions);
        }
    }
}
