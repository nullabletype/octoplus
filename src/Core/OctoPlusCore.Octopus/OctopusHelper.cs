﻿#region copyright
/*
    OctoPlus Deployment Coordinator. Provides extra tooling to help 
    deploy software through Octopus Deploy.

    Copyright (C) 2018  Steven Davies

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/
#endregion


using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Octopus.Client;
using Octopus.Client.Model;
using OctoPlusCore.Models;
using OctoPlusCore.Octopus.Interfaces;
using Environment = OctoPlusCore.Models.Environment;
using Microsoft.Extensions.Caching.Memory;
using Octopus.Client.Extensibility;
using OctoPlusCore.Models.Variables;

namespace OctoPlusCore.Octopus
{
    public class OctopusHelper : IOctopusHelper 
    {
        private IOctopusAsyncClient client;
        private IMemoryCache cache;
        private int cacheTimeout = 20;
        public static IOctopusHelper Default;

        public OctopusHelper() { }

        public OctopusHelper(string url, string apiKey, IMemoryCache memoryCache) 
        { 
            this.client = InitClient(url, apiKey);
            cache = memoryCache;
        }

        public OctopusHelper(IOctopusAsyncClient client, IMemoryCache memoryCache = null)
        {
            this.client = client;
            cache = memoryCache;
        }

        public static IOctopusHelper Init(string url, string apikey, IMemoryCache memoryCache = null) {
            var client = InitClient(url, apikey);
            Default = new OctopusHelper(client, memoryCache);
            return Default;
        }

        private static IOctopusAsyncClient InitClient(string url, string apikey) {
            var endpoint = new OctopusServerEndpoint(url, apikey);
            IOctopusAsyncClient client = null;
            Task.Run(async () => { client = await OctopusAsyncClient.Create(endpoint); }).Wait();
            return client;
        }

        public void SetCacheImplementation(IMemoryCache cache, int cacheTimeout)
        {
            this.cache = cache;
            this.cacheTimeout = cacheTimeout;
            if (cacheTimeout < 1)
            {
                this.cacheTimeout = 1;
            }
        }

        public async Task<IList<PackageStep>> GetPackages(string projectIdOrHref, string versionRange, string tag) 
        {
            return await GetPackages(await GetProject(projectIdOrHref), versionRange, tag);
        }

        private async Task<PackageIdResult> GetPackageId(ProjectResource project, string stepName, string actionName) 
        {
            var process = await GetDeploymentProcess(project.DeploymentProcessId);
            if (process != null) {
                foreach (var step in process.Steps.Where(s => s.Name == stepName)) 
                {
                    foreach (var action in step.Actions.Where(a => a.Name == actionName)) 
                    {
                        if (action.Properties.ContainsKey("Octopus.Action.Package.FeedId") &&
                            action.Properties["Octopus.Action.Package.FeedId"].Value == "feeds-builtin") 
                        {
                            if (action.Properties.ContainsKey("Octopus.Action.Package.PackageId") &&
                                !string.IsNullOrEmpty(action.Properties["Octopus.Action.Package.PackageId"].Value)) 
                            {
                                var packageId = action.Properties["Octopus.Action.Package.PackageId"].Value;
                                if (!string.IsNullOrEmpty(packageId)) 
                                {
                                    return new PackageIdResult 
                                    {
                                        PackageId = packageId,
                                        StepName = step.Name,
                                        StepId = step.Id
                                    };
                                }
                            }
                        }

                    }
                }
            }
            return null;
        }

        private async Task<IList<PackageIdResult>> GetPackages(ProjectResource project)
        {
            var results = new List<PackageIdResult>();
            var process = await GetDeploymentProcess(project.DeploymentProcessId);
            if (process != null)
            {
                foreach (var step in process.Steps)
                {
                    foreach (var action in step.Actions)
                    {
                        if (action.Properties.ContainsKey("Octopus.Action.Package.FeedId") &&
                            action.Properties["Octopus.Action.Package.FeedId"].Value == "feeds-builtin")
                        {
                            if (action.Properties.ContainsKey("Octopus.Action.Package.PackageId") &&
                                !string.IsNullOrEmpty(action.Properties["Octopus.Action.Package.PackageId"].Value))
                            {
                                var packageId = action.Properties["Octopus.Action.Package.PackageId"].Value;
                                if (!string.IsNullOrEmpty(packageId))
                                {
                                    results.Add(new PackageIdResult
                                    {
                                        PackageId = packageId,
                                        StepName = step.Name,
                                        StepId = step.Id
                                    });
                                }
                            }
                        }

                    }
                }
            }
            return results;
        }

        private async Task<IList<PackageStep>> GetPackages(ProjectResource project, string versionRange, string tag, int take = 5) 
        {
            var packageIdResult = await this.GetPackages(project);
            var allPackages = new List<PackageStep>();
            foreach (var package in packageIdResult)
            {
                if (package != null && !string.IsNullOrEmpty(package.PackageId))
                {
                    var template = GetCachedObject<Href>("feeds-builtin");

                    if (template == null)
                    {
                        template =
                            (await client.Repository.Feeds.Get("feeds-builtin")).Links["SearchTemplate"];
                        CacheObject("feeds-builtin", template);
                    }

                    var packages =
                        await
                            client.Get<List<PackageFromBuiltInFeedResource>>(template,
                                new
                                {
                                    packageId = package.PackageId,
                                    partialMatch = false,
                                    includeMultipleVersions = true,
                                    take,
                                    includePreRelease = true,
                                    versionRange,
                                    preReleaseTag = tag
                                });

                    var finalPackages = new List<PackageStub>();
                    foreach (var currentPackage in packages)
                    {
                        finalPackages.Add(ConvertPackage(currentPackage, package.StepName));
                    }
                    allPackages.Add(new PackageStep { AvailablePackages = finalPackages, StepName = package.StepName, StepId = package.StepId });
                }
            }

            return allPackages;
        }

        public async Task<(Release Release, Deployment Deployment)> GetReleasedVersion(string projectId, string envId) 
        {
            var deployment =
                (await client.Repository.Deployments.FindOne(resource => Search(resource, projectId, envId), pathParameters: new { take = 1, projects = projectId, environments = envId }));
            if (deployment != null) 
            {
                var release = await GetReleaseInternal(deployment.ReleaseId);
                if (release != null) 
                {
                    return (Release: await this.ConvertRelease(release), Deployment: this.ConvertDeployment(deployment));
                }
            }
            return (new Release { Id = "", Version = "None" }, new Deployment());
        }

        public async Task<bool> UpdateReleaseVariables(string releaseId)
        {
            var release = await this.client.Repository.Releases.Get(releaseId);
            if(release == null)
            {
                return false;
            }
            await this.client.Repository.Releases.SnapshotVariables(release);
            return true;
        }

        public async Task<List<Environment>> GetEnvironments() 
        {
            var envs = await client.Repository.Environments.GetAll();
            return envs.Select(ConvertEnvironment).ToList();
        }

        public async Task<List<Environment>> GetMatchingEnvironments(string keyword, bool extactMatch = false)
        {
            var environments = await GetEnvironments();
            var matchingEnvironments = environments.Where(env => env.Name.Equals(keyword, StringComparison.CurrentCultureIgnoreCase));
            if (matchingEnvironments.Count() == 0 && !extactMatch)
            {
                matchingEnvironments = environments.Where(env => env.Name.ToLower().Contains(keyword.ToLower()));
            }
            return matchingEnvironments.ToList();
        }

        public async Task<Environment> CreateEnvironment(string name, string description) 
        {
            var env = new EnvironmentResource {
                Name = name,
                Description = description
            };
            env = await client.Repository.Environments.Create(env);
            
            return ConvertEnvironment(env);
        }

        public async Task<Environment> GetEnvironment(string idOrName) 
        {
            return ConvertEnvironment(await client.Repository.Environments.Get(idOrName));
        }

        public async Task<IEnumerable<Environment>> GetEnvironment(string[] idOrNames)
        {
            return (await client.Repository.Environments.Get(idOrNames)).Select(e => ConvertEnvironment(e));
        }

        public async Task DeleteEnvironment(string idOrhref) 
        {
            var env = await client.Repository.Environments.Get(idOrhref);
            await client.Repository.Environments.Delete(env);
        }

        public async Task<List<ProjectGroup>> GetFilteredProjectGroups(string filter) 
        {
            var groups = await client.Repository.ProjectGroups.GetAll();
            return groups.Where(g => g.Name.ToLower().Contains(filter.ToLower())).Select(ConvertProjectGroup).ToList();
        }

        public async Task<List<ProjectGroup>> GetProjectGroups() 
        {
            return (await client.Repository.ProjectGroups.GetAll()).Select(ConvertProjectGroup).ToList();
        }

        public async Task<List<Project>> GetProjects(string environment, string channelRange, string tag) 
        {
            var projects = await client.Repository.Projects.GetAll();
            var converted = new List<Project>();
            foreach (var project in projects) {
                converted.Add(await ConvertProject(project, environment, channelRange, tag));
            }
            return converted;
        }

        public async Task<List<ProjectStub>> GetProjectStubs() 
        {
            var projects = await client.Repository.Projects.GetAll();
            var converted = new List<ProjectStub>();
            foreach (var project in projects) {
                CacheObject(project.Id, project);
                converted.Add(ConvertProject(project));
            }
            return converted;
        }

        public async Task<Project> GetProject(string idOrHref, string environment, string channelRange, string tag) 
        {
            return await ConvertProject(await GetProject(idOrHref), environment, channelRange, tag);
        }

        public async Task<bool> ValidateProjectName(string name) 
        {
            var project = await client.Repository.Projects.FindOne(resource => resource.Name == name);
            return project != null;
        }

        public async Task<Project> GetProjectByName(string name, string environment, string channelRange, string tag) 
        {
            return await ConvertProject(await client.Repository.Projects.FindOne(resource => resource.Name == name),
                environment,
                channelRange,
                tag);
        }

        public async Task<Channel> GetChannelByProjectNameAndChannelName(string name, string channelName) 
        {
            var project = await client.Repository.Projects.FindOne(resource => resource.Name == name);
            return ConvertChannel(await client.Repository.Channels.FindByName(project, channelName));
        }

        public async Task<List<Channel>> GetChannelsByProjectName(string name) 
        {
            var project = await client.Repository.Projects.FindOne(resource => resource.Name == name);
            var channels = await client.Repository.Projects.GetChannels(project);
            return channels.Items.Select(ConvertChannel).ToList();
        }

        public async Task<Channel> GetChannelByName(string projectIdOrName, string channelName) 
        {
            var project = await GetProject(projectIdOrName);
            return ConvertChannel(await client.Repository.Channels.FindByName(project, channelName));
        }

        public async Task<Channel> GetChannel(string channelIdOrHref) 
        {
            return ConvertChannel(await client.Repository.Channels.Get(channelIdOrHref));
        }

        public async Task<List<Channel>> GetChannelsForProject(string projectIdOrHref) 
        {
            var project = await GetProject(projectIdOrHref);
            var channels = await client.Repository.Projects.GetChannels(project);
            return channels.Items.Select(ConvertChannel).ToList();
        }

        public async Task<Release> GetRelease(string releaseIdOrHref) 
        {
            return await ConvertRelease(await GetReleaseInternal(releaseIdOrHref));
        }

        public async Task<LifeCycle> GetLifeCycle(string idOrHref) 
        {
            return ConvertLifeCycle(await client.Repository.Lifecycles.Get(idOrHref));
        }

        public async Task<PackageFull> GetFullPackage(PackageStub stub) 
        {
            var package = new PackageFull {
                Id = stub.Id,
                Version = stub.Version,
                StepName = stub.StepName
            };
            var template = (await client.Repository.Feeds.Get("feeds-builtin")).Links["NotesTemplate"];
            package.Message =
                await
                    client.Get<string>(template,
                        new {
                            packageId = stub.Id,
                            version = stub.Version
                        });
            return package;
        }

        public async Task<Release> CreateRelease(ProjectDeployment project, bool ignoreChannelRules = false) 
        {
            var user = await client.Repository.Users.GetCurrent();
            var split = project.Packages.First().PackageName.Split('.');
            var releaseName = project.ReleaseVersion ?? split[0] + "." + split[1] + ".i";
            if (project.ReleaseVersion == null && !string.IsNullOrEmpty(project.ChannelName)) 
            {
                releaseName += "-" + project.ChannelName;
            }
            var release = new ReleaseResource
            {
                Assembled = DateTimeOffset.UtcNow,
                ChannelId = project.ChannelId,
                LastModifiedBy = user.Username,
                LastModifiedOn = DateTimeOffset.UtcNow,
                ProjectId = project.ProjectId,
                ReleaseNotes = project.ReleaseMessage ?? string.Empty,
                Version = releaseName,
            };
            foreach (var package in project.Packages)
            {
                release.SelectedPackages.Add(new SelectedPackage { Version = package.PackageName, ActionName = package.StepName });
            }
            var result =
                    await
                        client.Repository.Releases.Create(release, ignoreChannelRules: ignoreChannelRules);
            return new Release {
                Version = result.Version,
                Id = result.Id,
                ReleaseNotes = result.ReleaseNotes
            };
        }

        public async Task<Deployment> CreateDeploymentTask(ProjectDeployment project, string environmentId, string releaseId) 
        {
            var user = await client.Repository.Users.GetCurrent();
            var deployment = new DeploymentResource
            {
                ChannelId = project.ChannelId,
                Comments = "Initiated by OctoPlus",
                Created = DateTimeOffset.UtcNow,
                EnvironmentId = environmentId,
                LastModifiedBy = user.Username,
                LastModifiedOn = DateTimeOffset.UtcNow,
                Name = project.ProjectName + ":" + project.Packages?.First().PackageName,
                ProjectId = project.ProjectId,
                ReleaseId = releaseId,
            };
            if (project.RequiredVariables != null)
            {
                foreach (var variable in project.RequiredVariables)
                {
                    deployment.FormValues.Add(variable.Id, variable.Value);
                }
            }
            var deployResult = await client.Repository.Deployments.Create(deployment);
            return new Deployment {
                TaskId = deployResult.TaskId
            };
        }

        public async Task<TaskDetails> GetTaskDetails(string taskId) 
        {
            var task = await client.Repository.Tasks.Get(taskId);
            var taskDeets = await client.Repository.Tasks.GetDetails(task);

            return new TaskDetails 
            {
                PercentageComplete = taskDeets.Progress.ProgressPercentage,
                TimeLeft = taskDeets.Progress.EstimatedTimeRemaining,
                State = taskDeets.Task.State == TaskState.Success ? Models.TaskStatus.Done :
                    taskDeets.Task.State == TaskState.Executing ? Models.TaskStatus.InProgress :
                    taskDeets.Task.State == TaskState.Queued ? Models.TaskStatus.Queued : Models.TaskStatus.Failed,
                TaskId = taskId,
                Links = taskDeets.Links.ToDictionary(l => l.Key, l => l.Value.ToString())
            };
        }

        public async Task<IEnumerable<TaskStub>> GetDeploymentTasks(int skip, int take) 
        {
            //var taskDeets = await client.Repository.Tasks.FindAll(pathParameters: new { skip, take, name = "Deploy" });

            var taskDeets = await client.Get<ResourceCollection<TaskResource>>((await client.Repository.LoadRootDocument()).Links["Tasks"], new { skip, take, name = "Deploy" });

            var tasks = new List<TaskStub>();

            foreach (var currentTask in taskDeets.Items) 
            {
                String deploymentId = null;
                if(currentTask.Arguments != null && currentTask.Arguments.ContainsKey("DeploymentId"))
                {
                    deploymentId = currentTask.Arguments["DeploymentId"].ToString();
                }
                tasks.Add(new TaskStub 
                {
                    State = currentTask.State == TaskState.Success ? Models.TaskStatus.Done :
                        currentTask.State == TaskState.Executing ? Models.TaskStatus.InProgress :
                        currentTask.State == TaskState.Queued ? Models.TaskStatus.Queued : Models.TaskStatus.Failed,
                    ErrorMessage = currentTask.ErrorMessage,
                    FinishedSuccessfully = currentTask.FinishedSuccessfully,
                    HasWarningsOrErrors = currentTask.HasWarningsOrErrors,
                    IsComplete = currentTask.IsCompleted,
                    TaskId = currentTask.Id,
                    Links = currentTask.Links.ToDictionary(l => l.Key, l => l.Value.ToString()),
                    DeploymentId = deploymentId
                });
            }

            return tasks;
        }

        public async Task<IEnumerable<Deployment>> GetDeployments(string[] deploymentIds)
        {
            return (await client.Repository.Deployments.Get(deploymentIds)).Select(d => ConvertDeployment(d));
        }

        public async Task RemoveEnvironmentsFromTeams(string envId) 
        {
            var teams = await client.Repository.Teams.FindAll();
            foreach (var team in teams) 
            {
                var scopes = await client.Repository.Teams.GetScopedUserRoles(team);
                
                foreach (var scope in scopes.Where(s => s.EnvironmentIds.Contains(envId))) 
                {
                    scope.EnvironmentIds.Remove(envId);
                    var saved = await client.Repository.ScopedUserRoles.Modify(scope);
                }
                
            }
        }


        public async Task RemoveEnvironmentsFromLifecycles(string envId) 
        {
            var env = await client.Repository.Environments.Get(envId);
            var lifecycles = await client.Repository.Lifecycles.FindMany(lifecycle => 
                { 
                    return lifecycle.Phases.Any(phase => 
                        {
                            if (phase.AutomaticDeploymentTargets != null && phase.AutomaticDeploymentTargets.Contains(envId))
                            {
                                return true;
                            }
                            if (phase.OptionalDeploymentTargets != null && phase.OptionalDeploymentTargets.Contains(envId))
                            {
                                return true;
                            }
                            return false;
                        }
                    ); 
                });
            foreach(var lifecycle in lifecycles) 
            {
                foreach (var phase in lifecycle.Phases) 
                {
                    if (phase.AutomaticDeploymentTargets != null)
                    {
                        phase.AutomaticDeploymentTargets.RemoveWhere(phaseEnvId => phaseEnvId.Equals(envId));
                    }
                    if (phase.OptionalDeploymentTargets != null)
                    {
                        phase.OptionalDeploymentTargets.RemoveWhere(phaseEnvId => phaseEnvId.Equals(envId));
                    }
                }
                await client.Repository.Lifecycles.Modify(lifecycle);
            }
        }

        public async Task AddEnvironmentToTeam(string envId, string teamId) 
        {
            var team = await client.Repository.Teams.Get(teamId);
            var environment = await client.Repository.Environments.Get(envId);
            if (team == null || environment == null) 
            {
                return;
            }
            var scopes = await client.Repository.Teams.GetScopedUserRoles(team);
            foreach (var scope in scopes) 
            {
                if (!scope.EnvironmentIds.Contains(envId))
                {
                    scope.EnvironmentIds.Add(envId);
                    var saved = await client.Repository.ScopedUserRoles.Modify(scope);
                }
            }
        }


        public async Task<(bool Success, LifecycleErrorType ErrorType, string Error)> AddEnvironmentToLifecyclePhase(string envId, string lcId, int phaseId, bool automatic) {
            LifecycleResource lifecycle;
            EnvironmentResource environment;
            try 
            {
                lifecycle = await client.Repository.Lifecycles.Get(lcId);
                environment = await client.Repository.Environments.Get(envId);
            } 
            catch (Exception e) 
            {
                return (false,LifecycleErrorType.UnexpectedError, e.Message);
            }
            if (lifecycle.Phases.Count < phaseId) 
            {
                return (false, LifecycleErrorType.PhaseInLifeCycleNotFound, string.Empty);
            }
            if (automatic) 
            {
                if (!lifecycle.Phases[phaseId].AutomaticDeploymentTargets.Contains(envId)) 
                {
                    lifecycle.Phases[phaseId].AutomaticDeploymentTargets.Add(envId);
                }
            } 
            else 
            {
                if (!lifecycle.Phases[phaseId].OptionalDeploymentTargets.Contains(envId)) 
                {
                    lifecycle.Phases[phaseId].OptionalDeploymentTargets.Add(envId);
                }
            }
            try 
            {
                await client.Repository.Lifecycles.Modify(lifecycle);
            } 
            catch (Exception e) 
            {
                return (false, LifecycleErrorType.UnexpectedError, e.Message);
            }
            return (false, LifecycleErrorType.None, string.Empty); ;


        }

        public async Task<string> GetTaskRawLog(string taskId) 
        {
            var task = await client.Repository.Tasks.Get(taskId);
            return await client.Repository.Tasks.GetRawOutputLog(task);
        }

        public async Task<IEnumerable<Deployment>> GetDeployments(string releaseId)
        {
            if(string.IsNullOrEmpty(releaseId))
            {
                return new Deployment[0];
            }
            var deployments = await client.Repository.Releases.GetDeployments(await GetReleaseInternal(releaseId), 0, 100);
            return deployments.Items.ToList().Select(ConvertDeployment);
        }

        public bool Search(DeploymentResource deploymentResource, string projectId, string envId)
        {
            return deploymentResource.ProjectId == projectId && deploymentResource.EnvironmentId == envId;
        }

        public async Task<(string error, bool success)> RenameRelease(string releaseId, string newReleaseVersion)
        {
            try
            {
                var release = await GetReleaseInternal(releaseId);
                release.Version = newReleaseVersion;
                await client.Repository.Releases.Modify(release);
            }
            catch (Exception e)
            {
                return (e.Message, false);
            }

            return (string.Empty, true);
        }

        public async Task UpdateVariableSet(VariableSet varSet) 
        {
            var id = varSet.Id;
            if (varSet.IdType == VariableSet.VariableIdTypes.Library) 
            {
                id = (await client.Repository.LibraryVariableSets.Get(id)).VariableSetId;
            }
            var set = await client.Repository.VariableSets.Get(id);
            foreach(var variable in varSet.Variables) 
            {
                var scope = new ScopeSpecification();

                if (variable.EnvironmentIds.Any()) 
                {
                    scope.Add(ScopeField.Environment, new ScopeValue(variable.EnvironmentIds));
                }
                if (variable.TargetIds.Any()) 
                {
                    scope.Add(ScopeField.Machine, new ScopeValue(variable.TargetIds));
                }
                if (variable.RoleIds.Any()) 
                {
                    scope.Add(ScopeField.Role, new ScopeValue(variable.RoleIds));
                }

                set.AddOrUpdateVariableValue(variable.Key, variable.Value, scope);
            }

            await client.Repository.VariableSets.Modify(set);
        }

        private Environment ConvertEnvironment(EnvironmentResource env)
        {
            return new Environment {Id = env.Id, Name = env.Name};
        }

        private Deployment ConvertDeployment(DeploymentResource dep)
        {
            return new Deployment
            {
                EnvironmentId = dep.EnvironmentId,
                ReleaseId = dep.ReleaseId,
                TaskId = dep.TaskId,
                LastModifiedBy = dep.LastModifiedBy,
                LastModifiedOn = dep.LastModifiedOn,
                Created = dep.Created
            };
        }

        public async Task<Project> ConvertProject(ProjectStub project, string env, string channelRange, string tag)
        {
            var projectRes = await this.GetProject(project.ProjectId);
            var packages = channelRange == null ? null : await this.GetPackages(projectRes, channelRange, tag);
            List<RequiredVariable> requiredVariables = await GetVariables(projectRes.VariableSetId);
            return new Project {
                CurrentRelease = (await this.GetReleasedVersion(project.ProjectId, env)).Release,
                ProjectName = project.ProjectName,
                ProjectId = project.ProjectId,
                Checked = true,
                ProjectGroupId = project.ProjectGroupId,
                AvailablePackages = packages,
                LifeCycleId = project.LifeCycleId,
                RequiredVariables = requiredVariables
            };
        }

        private async Task<Project> ConvertProject(ProjectResource project, string env, string channelRange, string tag)
        {
            var packages = await this.GetPackages(project, channelRange, tag);
            List<RequiredVariable> requiredVariables = await GetVariables(project.VariableSetId);
            return new Project
            {
                CurrentRelease = (await this.GetReleasedVersion(project.Id, env)).Release,
                ProjectName = project.Name,
                ProjectId = project.Id,
                Checked = true,
                ProjectGroupId = project.ProjectGroupId,
                AvailablePackages = packages,
                LifeCycleId = project.LifecycleId,
                RequiredVariables = requiredVariables
            };
        }

        private async Task<List<RequiredVariable>> GetVariables(string variableSetId)
        {
            var variables = await this.client.Repository.VariableSets.Get(variableSetId);
            var requiredVariables = new List<RequiredVariable>();
            foreach (var variable in variables.Variables)
            {
                if (variable.Prompt != null && variable.Prompt.Required)
                {
                    var requiredVariable = new RequiredVariable { Name = variable.Name, Type = variable.Type.ToString(), Id = variable.Id };
                    if (variable.Prompt.DisplaySettings.ContainsKey("Octopus.SelectOptions"))
                    {
                        requiredVariable.ExtraOptions = string.Join(", ", variable.Prompt.DisplaySettings["Octopus.SelectOptions"].Split('\n').Select(s => s.Split('|')[0]));
                    }
                    requiredVariables.Add(requiredVariable);
                }
            }

            return requiredVariables;
        }

        private ProjectStub ConvertProject(ProjectResource project) 
        {
            return new ProjectStub {
                ProjectName = project.Name,
                ProjectId = project.Id,
                Checked = true,
                ProjectGroupId = project.ProjectGroupId,
                LifeCycleId = project.LifecycleId
            };
        }

        private LifeCycle ConvertLifeCycle(LifecycleResource lifeCycle)
        {
            var lc = new LifeCycle
            {
                Name = lifeCycle.Name,
                Id = lifeCycle.Id,
                Description = lifeCycle.Description
            };
            if (lifeCycle.Phases != null)
            {
                foreach (var phase in lifeCycle.Phases)
                {
                    
                    var newPhase = new Phase
                    {
                        Name = phase.Name,
                        Id = phase.Id,
                        MinimumEnvironmentsBeforePromotion = phase.MinimumEnvironmentsBeforePromotion,
                        Optional = phase.IsOptionalPhase
                    };
                    if (phase.OptionalDeploymentTargets != null)
                    {
                        newPhase.OptionalDeploymentTargetEnvironmentIds = phase.OptionalDeploymentTargets.ToList();
                    }
                    if (phase.AutomaticDeploymentTargets != null)
                    {
                        newPhase.AutomaticDeploymentTargetEnvironmentIds = phase.AutomaticDeploymentTargets.ToList();
                    }
                    if (newPhase.AutomaticDeploymentTargetEnvironmentIds.Any() || newPhase.OptionalDeploymentTargetEnvironmentIds.Any())
                    {
                        lc.Phases.Add(newPhase);
                    }
                }
            }
            return lc;
        }

        private Channel ConvertChannel(ChannelResource channel)
        {
            if (channel == null)
            {
                return null;
            }
            var versionRange = String.Empty;
            var versionTag = String.Empty;
            if (channel.Rules.Any())
            {
                versionRange = channel.Rules[0].VersionRange;
                versionTag = channel.Rules[0].Tag;
            }
            return new Channel {Id = channel.Id, Name = channel.Name, VersionRange = versionRange, VersionTag = versionTag};
        }

        private ProjectGroup ConvertProjectGroup(ProjectGroupResource projectGroup)
        {
            return new ProjectGroup() {Id = projectGroup.Id, Name = projectGroup.Name};
        }

        private async Task<Release> ConvertRelease(ReleaseResource release)
        {
            var project = await GetProject(release.ProjectId);
            var packages =
                release.SelectedPackages.Select(
                    async p => await ConvertPackage(project, p)).Select(t => t.Result).ToList();
            return new Release
            {
                Id = release.Id,
                Version = release.Version,
                SelectedPackages = packages,
                DisplayPackageVersion = packages.Any() ? packages.First().Version : string.Empty,
                LastModifiedBy = release.LastModifiedBy,
                LastModifiedOn = release.LastModifiedOn,
                ReleaseNotes = release.ReleaseNotes
            };
        }

        private async Task<PackageStub> ConvertPackage(ProjectResource project, SelectedPackage package)
        {
            var packageDetails = await GetPackageId(project, package.ActionName, package.ActionName);
            return new PackageStub { Version = package.Version, StepName = packageDetails.StepName, StepId = packageDetails.StepId, Id = packageDetails.PackageId };
        }

        private PackageStub ConvertPackage(PackageResource package, string stepName)
        {
            return new PackageStub {Id = package.PackageId, Version = package.Version, StepName = stepName};
        }

        private async Task<DeploymentProcessResource> GetDeploymentProcess(string deploymentProcessId)
        {
            var cached = GetCachedObject<DeploymentProcessResource>(deploymentProcessId);
            if (cached == default(DeploymentProcessResource))
            {
                var deployment = await client.Repository.DeploymentProcesses.Get(deploymentProcessId);
                CacheObject(deployment.Id, deployment);
                return deployment;
            }
            return cached;
        }

        private async Task<ProjectResource> GetProject(string projectId)
        {
            var cached = GetCachedObject<ProjectResource>(projectId);
            if (cached == default(ProjectResource))
            {
                var project = await client.Repository.Projects.Get(projectId);
                CacheObject(project.Id, project);
                return project;
            }
            return cached;
        }

        private async Task<ReleaseResource> GetReleaseInternal(string releaseId)
        {
            var cached = GetCachedObject<ReleaseResource>(releaseId);
            if (cached == default(ReleaseResource))
            {
                var release = await client.Repository.Releases.Get(releaseId);
                CacheObject(release.Id, release);
                return release;
            }
            return cached;
        }

        private T GetCachedObject<T>(string key)
        {
            if (cache != null && this.cache.TryGetValue(key + typeof(T).Name, out T cachedValue))
            {
                return cachedValue;
            }
            return default(T);
        }

        private void CacheObject<T>(string key, T value)
        {
            if(cache == null)
            {
                return;
            }
            var cacheEntryOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromSeconds(cacheTimeout));
            cache.Set(key + typeof(T).Name, value, cacheEntryOptions);
        }

        private class PackageIdResult
        {
            public string PackageId { get; set; }
            public string StepName { get; set; }
            public string StepId { get; set; }
        }

        public enum LifecycleErrorType 
        {
            LifeCycleNotFound,
            EnvironmentNotFound,
            PhaseInLifeCycleNotFound,
            UnexpectedError,
            None
        }
    }
}