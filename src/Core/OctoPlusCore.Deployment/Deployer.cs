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
using System.Threading.Tasks;
using Octopus.Client;
using Octopus.Client.Model;
using OctoPlusCore.Configuration.Interfaces;
using OctoPlusCore.Deployment.Interfaces;
using OctoPlusCore.Models;
using OctoPlusCore.Models.Interfaces;
using OctoPlusCore.Logging.Interfaces;
using OctoPlusCore.Octopus.Interfaces;
using OctoPlusCore.Utilities;
using OctoPlusCore.Deployment.Resources;

namespace OctoPlusCore.Deployment {
    public class Deployer : IDeployer
    {
        private IOctopusHelper helper;
        private IConfiguration configuration;

        public Deployer(IOctopusHelper helper, IConfiguration configuration)
        {
            this.helper = helper;
            this.configuration = configuration;
        }

        public async Task<DeploymentCheckResult> CheckDeployment(EnvironmentDeployment deployment)
        {
            foreach (var project in deployment.ProjectDeployments)
            {
                var lifeCyle = await this.helper.GetLifeCycle(project.LifeCycleId);
                if (lifeCyle.Phases.Any())
                {
                    var safe = false;
                    if (lifeCyle.Phases[0].OptionalDeploymentTargetEnvironmentIds.Any())
                    {
                        if (lifeCyle.Phases[0].OptionalDeploymentTargetEnvironmentIds.Contains(deployment.EnvironmentId))
                        {
                            safe = true;
                        }
                    }
                    if (!safe && lifeCyle.Phases[0].AutomaticDeploymentTargetEnvironmentIds.Any())
                    {
                        if (lifeCyle.Phases[0].AutomaticDeploymentTargetEnvironmentIds.Contains(deployment.EnvironmentId))
                        {
                            safe = true;
                        }
                    }
                    if (!safe)
                    {
                        var phaseCheck = true;
                        var deployments = await this.helper.GetDeployments(project.ReleaseId);
                        var previousEnvs = deployments.Select(d => d.EnvironmentId);
                        foreach(var phase in lifeCyle.Phases)
                        {
                            if (!phase.Optional)
                            {
                                if(phase.MinimumEnvironmentsBeforePromotion == 0)
                                {
                                    if(!previousEnvs.All(e => phase.OptionalDeploymentTargetEnvironmentIds.Contains(e)) && !phase.OptionalDeploymentTargetEnvironmentIds.Contains(deployment.EnvironmentId))
                                    {
                                        phaseCheck = false;
                                        break;
                                    }
                                }
                                else
                                {
                                    if(phase.MinimumEnvironmentsBeforePromotion > previousEnvs.Intersect(phase.OptionalDeploymentTargetEnvironmentIds).Count()){
                                        phaseCheck = false;
                                        break;
                                    }
                                }
                            }
                            else
                            {
                                continue;
                            }
                        }
                        safe = phaseCheck;
                    }
                    if (!safe)
                    {

                        return new DeploymentCheckResult {
                            Success = false,
                            ErrorMessage = DeploymentStrings.FailedValidation.Replace("{{projectname}}", project.ProjectName).Replace("{{environmentname}}", deployment.EnvironmentName)
                        };
                    }
                }
            }
            return new DeploymentCheckResult { Success = true };
        }

        public async Task StartJob(IOctoJob job, IUiLogger uiLogger, bool suppressMessages = false)
        {
            if (job is EnvironmentDeployment)
            {
                await this.ProcessEnvironmentDeployment((EnvironmentDeployment) job, suppressMessages, uiLogger);
            }
        }

        private async Task ProcessEnvironmentDeployment(EnvironmentDeployment deployment, bool suppressMessages,
            IUiLogger uiLogger)
        {
            uiLogger.WriteLine("Starting deployment!");
            var failedProjects = new Dictionary<ProjectDeployment, TaskDetails>();

            var taskRegister = new Dictionary<string, TaskDetails>();
            var projectRegister = new Dictionary<string, ProjectDeployment>();

            foreach (var project in deployment.ProjectDeployments) 
            {
                Release result;
                if (string.IsNullOrEmpty(project.ReleaseId))
                {
                    uiLogger.WriteLine("Creating a release for project " + project.ProjectName + "... ");
                    result = await helper.CreateRelease(project, deployment.FallbackToDefaultChannel);
                }
                else
                {
                    uiLogger.WriteLine("Fetching existing release for project " + project.ProjectName + "... ");
                    result = await helper.GetRelease(project.ReleaseId);
                }

                uiLogger.WriteLine("Creating deployment task for " + result.Version + " to " + deployment.EnvironmentName);
                var deployResult = await helper.CreateDeploymentTask(project, deployment.EnvironmentId, result.Id);
                uiLogger.WriteLine("Created");

                var taskDeets = await helper.GetTaskDetails(deployResult.TaskId);
                //taskDeets = await StartDeployment(uiLogger, taskDeets, !deployment.DeployAsync);
                if (deployment.DeployAsync) 
                {
                    taskRegister.Add(taskDeets.TaskId, taskDeets);
                    projectRegister.Add(taskDeets.TaskId, project);
                } 
                else 
                {
                    if (taskDeets.State == Models.TaskStatus.Failed) {
                        uiLogger.WriteLine("Failed deploying " + project.ProjectName);
                        failedProjects.Add(project, taskDeets);
                    }
                    uiLogger.WriteLine("Deployed!");
                    uiLogger.WriteLine("Full Log: " + System.Environment.NewLine +
                                     await this.helper.GetTaskRawLog(taskDeets.TaskId));
                    taskDeets = await helper.GetTaskDetails(deployResult.TaskId);
                }
            }


            // This needs serious improvement.
            if (deployment.DeployAsync) {
                await DeployAsync(uiLogger, failedProjects, taskRegister, projectRegister);
            }

            uiLogger.WriteLine("Done deploying!");
            if (failedProjects.Any())
            {
                uiLogger.WriteLine("Some projects didn't deploy successfully: ");
                foreach (var failure in failedProjects)
                {
                    var link = string.Empty;
                    if (failure.Value.Links != null)
                    {
                        if (failure.Value.Links.ContainsKey("Web"))
                        {
                            link = configuration.OctopusUrl + failure.Value.Links["Web"];
                        }
                    }
                    uiLogger.WriteLine(failure.Key.ProjectName + ": " + link);
                }
            }
            if (!suppressMessages)
            {
                uiLogger.WriteLine("Done deploying!" +
                                (failedProjects.Any() ? " There were failures though. Check the log." : string.Empty));
            }
        }

        private async Task DeployAsync(IUiLogger uiLogger, Dictionary<ProjectDeployment, TaskDetails> failedProjects, Dictionary<string, TaskDetails> taskRegister, Dictionary<string, ProjectDeployment> projectRegister) 
        {
            var done = false;
            int totalCount = taskRegister.Count();

            while (!done) 
            {
                var tasks = await this.helper.GetDeploymentTasks(0, 100);
                foreach (var currentTask in taskRegister.ToList()) 
                {
                    var found = tasks.FirstOrDefault(t => t.TaskId == currentTask.Key);
                    if (found == null) 
                    {
                        uiLogger.CleanCurrentLine();
                        uiLogger.WriteLine($"Couldn't find {currentTask.Key} in the tasks list?");
                        taskRegister.Remove(currentTask.Key);
                    } 
                    else 
                    {
                        if (found.State == Models.TaskStatus.Done) 
                        {
                            var finishedTask = await this.helper.GetTaskDetails(found.TaskId);
                            var project = projectRegister[currentTask.Key];
                            uiLogger.CleanCurrentLine();
                            uiLogger.WriteLine($"{project.ProjectName} deployed successfully");
                            taskRegister.Remove(currentTask.Key);
                        }
                        else if (found.State == Models.TaskStatus.Failed) 
                        {
                            var finishedTask = await this.helper.GetTaskDetails(found.TaskId);
                            var project = projectRegister[currentTask.Key];
                            uiLogger.CleanCurrentLine();
                            uiLogger.WriteLine($"{currentTask.Key} failed to deploy with error: {found.ErrorMessage}");
                            failedProjects.Add(project, finishedTask);
                            taskRegister.Remove(currentTask.Key);
                        }
                    }
                }

                if (taskRegister.Count == 0) 
                {
                    done = true;
                }

                uiLogger.WriteProgress(totalCount - taskRegister.Count, totalCount, $"Deploying the requested projects ({totalCount - taskRegister.Count} of {totalCount} done)...");

                await Task.Delay(3000);
            }
            uiLogger.CleanCurrentLine();
        }

        private async Task<TaskDetails> StartDeployment(IUiLogger uiLogger, TaskDetails taskDeets, bool doWait) 
        {
            do 
            {
                WriteStatus(uiLogger, taskDeets);
                if (doWait) 
                {
                    await Task.Delay(1000);
                }
                taskDeets = await this.helper.GetTaskDetails(taskDeets.TaskId);
            }
            while (doWait && (taskDeets.State == Models.TaskStatus.InProgress || taskDeets.State == Models.TaskStatus.Queued));
            return taskDeets;
        }

        private static void WriteStatus(IUiLogger uiLogger, TaskDetails taskDeets) 
        {
            if (taskDeets.State != Models.TaskStatus.Queued) 
            {
                if (taskDeets.PercentageComplete < 100) 
                {
                    uiLogger.WriteLine("Current status: " + taskDeets.State + " Percentage: " +
                                     taskDeets.PercentageComplete+ " estimated time remaining: " +
                                     taskDeets.TimeLeft);
                }
            } else 
            {
                uiLogger.WriteLine("Currently queued... waiting");
            }
        }
    }
}