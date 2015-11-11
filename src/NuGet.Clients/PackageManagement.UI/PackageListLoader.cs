﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Packaging.Core;
using NuGet.ProjectManagement;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.VisualStudio;
using NuGet.Versioning;
using NuGet.VisualStudio;
using Mvs = Microsoft.VisualStudio.Shell;

namespace NuGet.PackageManagement.UI
{
    public class PackageListLoader
    {
        private bool _isSolution;
        private bool _includePrerelease;
        private IList<NuGetProject> _projects;
        private readonly IEnumerable<IVsPackageManagerProvider> _packageManagerProviders;
        private InstalledPackages _installedPackages;

        public PackageListLoader(
            InstalledPackages installedPackages,
            IList<NuGetProject> projects,
            IEnumerable<IVsPackageManagerProvider> packageManagerProviders,
            bool isSolution,
            bool includePrerelease)
        {
            _installedPackages = installedPackages;
            _projects = projects;
            _packageManagerProviders = packageManagerProviders;
            _isSolution = isSolution;
            _includePrerelease = includePrerelease;
        }

        public async Task<List<PackageItemListViewModel>> GetPackagesAsync(
            InstalledPackages installedPackages,
            NuGetPackageManager packageManager,
            SourceRepository sourceRepository,
            CancellationToken cancellationToken)
        {
            var localResource = await packageManager.PackagesFolderSourceRepository
                .GetResourceAsync<UIMetadataResource>(cancellationToken);

            // UIMetadataResource may not be available
            // Given that this is the 'Installed' filter, we ignore failures in reaching the remote server
            // Instead, we will use the local UIMetadataResource
            UIMetadataResource metadataResource;
            try
            {
                if (sourceRepository == null)
                {
                    metadataResource = null;
                }
                else
                {
                    metadataResource = await sourceRepository.GetResourceAsync<UIMetadataResource>(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                metadataResource = null;
                // Write stack to activity log
                Mvs.ActivityLog.LogError(Constants.LogEntrySource, ex.ToString());
            }

            // group installed packages
            var groupedPackages = installedPackages.Select(
                p => new PackageIdentity(p.Key, p.Value.Max()));

            List<PackageItemListViewModel> packages = new List<PackageItemListViewModel>();

            foreach (var pair in installedPackages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var id = pair.Key;
                HashSet<NuGetVersion> installedVersions = pair.Value;

                var package = new PackageItemListViewModel(_includePrerelease);
                package.Id = id;
                package.Version = installedVersions.Max();
                package.MetadataLoader = new BackgroundLoader<MetadataLoaderResult>(
                    new Lazy<Task<MetadataLoaderResult>>(() =>
                    {
                        return InstalledTabMetadataLoader.GetPackageMetadataAsync(
                            localResource,
                            metadataResource,
                            new PackageIdentity(package.Id, package.Version),
                            CancellationToken.None);
                    }));

                if (!_isSolution)
                {
                    if (installedVersions.Count == 1)
                    {
                        package.InstalledVersion = installedVersions.First();
                    }
                }

                package.BackgroundLoader = new BackgroundLoader<BackgroundLoaderResult>(                    
                    new Lazy<Task<BackgroundLoaderResult>>(
                        () => BackgroundLoad(package)));

                if (!_isSolution && _packageManagerProviders.Any())
                {
                    package.ProvidersLoader = new BackgroundLoader<AlternativePackageManagerProviders>(
                        new Lazy<Task<AlternativePackageManagerProviders>>(
                        () => AlternativePackageManagerProviders.CalculateAlternativePackageManagersAsync(
                            _packageManagerProviders,
                            package.Id,
                            _projects[0])));
                }

                packages.Add(package);
            }

            return packages;
        }

        // Load info in the background
        public async Task<BackgroundLoaderResult> BackgroundLoad(
            PackageItemListViewModel package)
        {
            HashSet<NuGetVersion> installedVersions;
            if (_installedPackages.TryGetValue(package.Id, out installedVersions))
            {
                var versions = await package.GetVersionsAsync();
                var highestAvailableVersion = versions
                    .Select(v => v.Version)
                    .Max();

                var lowestInstalledVersion = installedVersions.Min();

                if (VersionComparer.VersionRelease.Compare(lowestInstalledVersion, highestAvailableVersion) < 0)
                {
                    return new BackgroundLoaderResult()
                    {
                        LatestVersion = highestAvailableVersion,
                        InstalledVersion = lowestInstalledVersion,
                        Status = PackageStatus.UpdateAvailable
                    };
                }

                return new BackgroundLoaderResult()
                {
                    LatestVersion = null,
                    InstalledVersion = lowestInstalledVersion,
                    Status = PackageStatus.Installed
                };
            }

            // the package is not installed. In this case, the latest version is the version
            // of the search result.
            return new BackgroundLoaderResult()
            {
                LatestVersion = package.Version,
                InstalledVersion = null,
                Status = PackageStatus.NotInstalled
            };
        }
    }

    // supports offline
    public class InstalledTabMetadataLoader
    {
        /// <summary>
        /// Get the metadata of an installed package.
        /// </summary>
        /// <param name="localResource">The local resource, i.e. the package folder of the solution.</param>
        /// <param name="metadataResource">The remote metadata resource.</param>
        /// <param name="identity">The installed package.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The metadata of the package.</returns>
        public static async Task<MetadataLoaderResult> GetPackageMetadataAsync(
            UIMetadataResource localResource,
            UIMetadataResource metadataResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            if (metadataResource == null)
            {
                return await GetPackageMetadataWhenRemoteSourceUnavailable(
                    localResource,
                    identity,
                    cancellationToken);
            }

            try
            {
                var metadata = await GetPackageMetadataFromMetadataResourceAsync(
                    metadataResource,
                    identity,
                    cancellationToken);

                // if the package does not exist in the remote source, NuGet should
                // try getting metadata from the local resource.
                if (String.IsNullOrEmpty(metadata.Summary) && localResource != null)
                {
                    return await GetPackageMetadataWhenRemoteSourceUnavailable(
                        localResource,
                        identity,
                        cancellationToken);
                }
                else
                {
                    return metadata;
                }
            }
            catch
            {
                // When a v2 package source throws, it throws an InvalidOperationException or WebException
                // When a v3 package source throws, it throws an HttpRequestException

                // The remote source is not available. NuGet should not fail but
                // should use the local resource instead.
                if (localResource != null)
                {
                    return await GetPackageMetadataWhenRemoteSourceUnavailable(
                        localResource,
                        identity,
                        cancellationToken);
                }
                else
                {
                    throw;
                }
            }
        }

        // Gets the package metadata from the local resource when the remote source
        // is not available.
        private static async Task<MetadataLoaderResult> GetPackageMetadataWhenRemoteSourceUnavailable(
            UIMetadataResource localResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            UIPackageMetadata packageMetadata = null;
            IEnumerable<VersionInfo> versions = Enumerable.Empty<VersionInfo>();

            if (localResource != null)
            {
                var localMetadata = await localResource.GetMetadata(
                    identity.Id,
                    includePrerelease: true,
                    includeUnlisted: true,
                    token: cancellationToken);
                packageMetadata = localMetadata.FirstOrDefault(p => p.Identity.Version == identity.Version);
                versions = localMetadata.Select(p => new VersionInfo(p.Identity.Version, p.DownloadCount));
            }

            string summary = string.Empty;
            string title = identity.Id;
            string author = string.Empty;
            if (packageMetadata != null)
            {
                summary = packageMetadata.Summary;
                if (string.IsNullOrEmpty(summary))
                {
                    summary = packageMetadata.Description;
                }
                if (!string.IsNullOrEmpty(packageMetadata.Title))
                {
                    title = packageMetadata.Title;
                }

                author = string.Join(", ", packageMetadata.Authors);
            }

            return new MetadataLoaderResult(
                author,
                packageMetadata?.IconUrl,
                packageMetadata?.DownloadCount,
                summary,
                versions);
        }

        private static async Task<MetadataLoaderResult> GetPackageMetadataFromMetadataResourceAsync(
            UIMetadataResource metadataResource,
            PackageIdentity identity,
            CancellationToken cancellationToken)
        {
            var uiPackageMetadatas = await metadataResource.GetMetadata(
                identity.Id,
                includePrerelease: true,
                includeUnlisted: false,
                token: cancellationToken);
            var packageMetadata = uiPackageMetadatas.FirstOrDefault(p => p.Identity.Version == identity.Version);
            var versions = uiPackageMetadatas.Select(p => new VersionInfo(p.Identity.Version, p.DownloadCount));

            string summary = string.Empty;
            string title = identity.Id;
            string author = string.Empty;
            if (packageMetadata != null)
            {
                summary = packageMetadata.Summary;
                if (string.IsNullOrEmpty(summary))
                {
                    summary = packageMetadata.Description;
                }
                if (!string.IsNullOrEmpty(packageMetadata.Title))
                {
                    title = packageMetadata.Title;
                }

                author = string.Join(", ", packageMetadata.Authors);
            }

            return new MetadataLoaderResult(
                author,
                packageMetadata?.IconUrl,
                packageMetadata?.DownloadCount,
                summary,
                versions);
        }
    }
}