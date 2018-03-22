﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.MSBuild
{
    public partial class MSBuildProjectLoader
    {
        private partial class Worker
        {
            private readonly struct ResolvedReferences
            {
                public ImmutableArray<ProjectReference> ProjectReferences { get; }
                public ImmutableArray<MetadataReference> MetadataReferences { get; }

                public ResolvedReferences(ImmutableArray<ProjectReference> projectReferences, ImmutableArray<MetadataReference> metadataReferences)
                {
                    ProjectReferences = projectReferences;
                    MetadataReferences = metadataReferences;
                }
            }

            private class MetadataReferenceSet
            {
                private readonly ImmutableArray<MetadataReference> _metadataReferences;
                private readonly ImmutableDictionary<string, HashSet<int>> _pathToIndexMap;
                private readonly HashSet<int> _indecesToRemove;

                public MetadataReferenceSet(ImmutableArray<MetadataReference> metadataReferences)
                {
                    _metadataReferences = metadataReferences;
                    _pathToIndexMap = CreatePathToIndexMap(metadataReferences);
                    _indecesToRemove = new HashSet<int>();
                }

                private static ImmutableDictionary<string, HashSet<int>> CreatePathToIndexMap(IEnumerable<MetadataReference> metadataReferences)
                {
                    var pathToIndexMap = ImmutableDictionary.CreateBuilder<string, HashSet<int>>(PathUtilities.Comparer);

                    var index = 0;
                    foreach (var metadataReference in metadataReferences)
                    {
                        var filePath = GetFilePath(metadataReference);
                        if (!pathToIndexMap.TryGetValue(filePath, out var indeces))
                        {
                            indeces = new HashSet<int>();
                            pathToIndexMap.Add(filePath, indeces);
                        }

                        indeces.Add(index);

                        index++;
                    }

                    return pathToIndexMap.ToImmutable();
                }

                public bool Contains(string filePath) => _pathToIndexMap.ContainsKey(filePath);

                public void Remove(string filePath)
                {
                    if (filePath != null && _pathToIndexMap.TryGetValue(filePath, out var indexSet))
                    {
                        _indecesToRemove.AddRange(indexSet);
                    }
                }

                public ImmutableArray<MetadataReference> GetRemaining()
                {
                    var results = ImmutableArray.CreateBuilder<MetadataReference>(initialCapacity: _metadataReferences.Length);

                    var index = 0;
                    foreach (var metadataReference in _metadataReferences)
                    {
                        if (!_indecesToRemove.Contains(index))
                        {
                            results.Add(metadataReference);
                        }

                        index++;
                    }

                    return results.ToImmutable();
                }

                public ProjectInfo Find(IEnumerable<ProjectInfo> projectInfos)
                {
                    foreach (var projectInfo in projectInfos)
                    {
                        if ((projectInfo.OutputRefFilePath != null && _pathToIndexMap.ContainsKey(projectInfo.OutputRefFilePath)) ||
                            (projectInfo.OutputFilePath != null && _pathToIndexMap.ContainsKey(projectInfo.OutputFilePath)))
                        {
                            return projectInfo;
                        }
                    }

                    return null;
                }

                public ProjectFileInfo Find(IEnumerable<ProjectFileInfo> projectFileInfos)
                {
                    foreach (var projectInfo in projectFileInfos)
                    {
                        if ((projectInfo.OutputRefFilePath != null && _pathToIndexMap.ContainsKey(projectInfo.OutputRefFilePath)) ||
                            (projectInfo.OutputFilePath != null && _pathToIndexMap.ContainsKey(projectInfo.OutputFilePath)))
                        {
                            return projectInfo;
                        }
                    }

                    return null;
                }
            }

            private async Task<ResolvedReferences> ResolveReferencesAsync(ProjectId id, ProjectFileInfo projectFileInfo, CommandLineArguments commandLineArgs, CancellationToken cancellationToken)
            {
                // First, gather all of the metadata references from the command-line arguments.
                var resolvedMetadataReferences = commandLineArgs.ResolveMetadataReferences(
                    new WorkspaceMetadataFileReferenceResolver(
                        metadataService: GetWorkspaceService<IMetadataService>(),
                        pathResolver: new RelativePathResolver(commandLineArgs.ReferencePaths, commandLineArgs.BaseDirectory))).ToImmutableArray();

                var projectReferences = new List<ProjectReference>(capacity: projectFileInfo.ProjectReferences.Count);
                var metadataReferenceSet = new MetadataReferenceSet(resolvedMetadataReferences);

                var projectDirectory = Path.GetDirectoryName(projectFileInfo.FilePath);

                // Next, iterate through all project references in the file and create project references.
                foreach (var projectFileReference in projectFileInfo.ProjectReferences)
                {
                    if (_pathResolver.TryGetAbsoluteProjectPath(projectFileReference.Path, baseDirectory: projectDirectory, _discoveredProjectOptions.OnPathFailure, out var absoluteProjectPath))
                    {
                        // If the project is already loaded, add a reference to it and remove its output from the metadata references.
                        if (TryAddProjectReferenceToLoadedOrMappedProject(id, absoluteProjectPath, metadataReferenceSet, projectFileReference.Aliases, projectReferences))
                        {
                            continue;
                        }

                        _projectFileLoaderRegistry.TryGetLoaderFromProjectPath(absoluteProjectPath, DiagnosticReportingMode.Ignore, out var loader);
                        if (loader == null)
                        {
                            // We don't have a full project loader, but we can try to use project evaluation to get the output file path.
                            // If that works, we can check to see if the output path is in the metadata references. If it is, we're done:
                            // Leave the metadata reference and don't create a project reference.
                            var outputFilePath = await _buildManager.TryGetOutputFilePathAsync(absoluteProjectPath, _globalProperties, cancellationToken).ConfigureAwait(false);
                            if (!string.IsNullOrEmpty(outputFilePath) &&
                                metadataReferenceSet.Contains(outputFilePath) &&
                                File.Exists(outputFilePath))
                            {
                                continue;
                            }
                        }

                        // If metadata is preferred or there's no loader for this project, see if the output path exists.
                        // If it does, don't create a project reference.
                        if (_preferMetadataForReferencedProjects)
                        {
                            var projectRefFileInfos = await LoadProjectFileInfosAsync(
                                absoluteProjectPath, DiagnosticReportingOptions.IgnoreAll, cancellationToken).ConfigureAwait(false);

                            var done = false;
                            foreach (var projectRefFileInfo in projectRefFileInfos)
                            {
                                if (!string.IsNullOrEmpty(projectRefFileInfo.OutputFilePath) && metadataReferenceSet.Contains(projectRefFileInfo.OutputFilePath))
                                {
                                    // We found it!
                                    if (File.Exists(projectRefFileInfo.OutputFilePath))
                                    {
                                        done = true;
                                        break;
                                    }
                                }
                            }

                            if (done)
                            {
                                continue;
                            }

                            // We didn't find the output file path in this project's metadata references, or it doesn't exist on disk.
                            // In that case, carry on and load the project.
                        }

                        // OK, we've got to load the project.
                        var projectRefInfos = await LoadProjectInfosFromPathAsync(absoluteProjectPath, _discoveredProjectOptions, cancellationToken).ConfigureAwait(false);

                        var projectRefInfo = metadataReferenceSet.Find(projectRefInfos);
                        if (projectRefInfo != null)
                        {
                            // Don't add a reference if the project already has a reference on us. Otherwise, it will cause a circularity.
                            if (projectRefInfo.ProjectReferences.Any(pr => pr.ProjectId == id))
                            {
                                // If the metadata doesn't exist on disk, we'll have to remove it from the metadata references.

                                if (!File.Exists(projectRefInfo.OutputRefFilePath))
                                {
                                    metadataReferenceSet.Remove(projectRefInfo.OutputRefFilePath);
                                }

                                if (!File.Exists(projectRefInfo.OutputFilePath))
                                {
                                    metadataReferenceSet.Remove(projectRefInfo.OutputFilePath);
                                }
                            }
                            else
                            {
                                projectReferences.Add(CreateProjectReference(id, projectRefInfo.Id, projectFileReference.Aliases));
                                metadataReferenceSet.Remove(projectRefInfo.OutputRefFilePath);
                                metadataReferenceSet.Remove(projectRefInfo.OutputFilePath);
                            }

                            continue;
                        }
                    }

                    // We weren't able to handle this project reference, so add it without further processing.
                    var unknownProjectId = _projectMap.GetOrCreateProjectId(projectFileReference.Path);
                    projectReferences.Add(CreateProjectReference(id, unknownProjectId, projectFileReference.Aliases));
                }

                return new ResolvedReferences(
                    projectReferences.ToImmutableArray(),
                    metadataReferenceSet.GetRemaining());
            }

            private ProjectReference CreateProjectReference(ProjectId from, ProjectId to, ImmutableArray<string> aliases)
            {
                var result = new ProjectReference(to, aliases);
                if (!_projectIdToProjectReferencesMap.TryGetValue(from, out var references))
                {
                    references = new List<ProjectReference>();
                    _projectIdToProjectReferencesMap.Add(from, references);
                }

                references.Add(result);

                return result;
            }

            private bool ProjectReferenceExists(ProjectId to, ProjectId from)
                => _projectIdToProjectReferencesMap.TryGetValue(from, out var references)
                && references.Contains(pr => pr.ProjectId == to);

            /// <summary>
            /// Try to get the <see cref="ProjectId"/> for an already loaded project whose output path is in the given <paramref name="metadataReferenceSet"/>.
            /// </summary>
            private bool TryAddProjectReferenceToLoadedOrMappedProject(ProjectId id, string projectReferencePath, MetadataReferenceSet metadataReferenceSet, ImmutableArray<string> aliases, List<ProjectReference> projectReferences)
            {
                if (_projectMap.TryGetIdsByProjectPath(projectReferencePath, out var mappedIds))
                {
                    foreach (var mappedId in mappedIds)
                    {
                        // Don't add a reference if the project already has a reference on us. Otherwise, it will cause a circularity.
                        if (ProjectReferenceExists(to: id, from: mappedId))
                        {
                            return false;
                        }

                        if (_projectMap.TryGetOutputFilePathById(mappedId, out var outputFilePath))
                        {
                            if (metadataReferenceSet.Contains(outputFilePath))
                            {
                                metadataReferenceSet.Remove(outputFilePath);
                                projectReferences.Add(CreateProjectReference(id, mappedId, aliases));
                                return true;
                            }
                        }
                    }
                }

                return false;
            }

            private static string GetFilePath(MetadataReference metadataReference)
            {
                switch (metadataReference)
                {
                    case PortableExecutableReference portableExecutableReference:
                        return portableExecutableReference.FilePath;
                    case UnresolvedMetadataReference unresolvedMetadataReference:
                        return unresolvedMetadataReference.Reference;
                    default:
                        return null;
                }
            }
        }
    }
}
