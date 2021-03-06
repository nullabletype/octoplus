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


using McMaster.Extensions.CommandLineUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NuGet.Versioning;
using OctoPlusCore.Deployment.Interfaces;
using OctoPlusCore.Models;
using OctoPlusCore.Octopus.Interfaces;
using OctoPlusCore.Language;

namespace OctoPlus.Console.Commands {
    abstract class BaseCommand 
    {
        protected abstract bool SupportsInteractiveMode { get; }
        public abstract string CommandName { get; }
        protected abstract Task<int> Run(CommandLineApplication command);

        protected const string HelpOption = "-?|-h|--help";
        private readonly Dictionary<string, CommandOption> _optionRegister;
        private readonly List<BaseCommand> _subCommands;
        protected bool InInteractiveMode { get; private set; }
        protected IOctopusHelper octoHelper;
        protected ILanguageProvider languageProvider;


        protected BaseCommand(IOctopusHelper octoHelper, ILanguageProvider languageProvider)
        {
            _optionRegister = new Dictionary<string, CommandOption>();
            _subCommands = new List<BaseCommand>();
            this.octoHelper = octoHelper;
            this.languageProvider = languageProvider;
        }

        public virtual void Configure(CommandLineApplication command) 
        {
            command.HelpOption(HelpOption);
            command.ThrowOnUnexpectedArgument = true;
            AddToRegister(OptionNames.ApiKey, command.Option("-a|--apikey", languageProvider.GetString(LanguageSection.OptionsStrings, "ApiKey"), CommandOptionType.SingleValue));
            AddToRegister(OptionNames.Url, command.Option("-u|--url", languageProvider.GetString(LanguageSection.OptionsStrings, "Url"), CommandOptionType.SingleValue));
            if (this.SupportsInteractiveMode)
            {
                AddToRegister(OptionNames.Interactive, command.Option("-i|--interactive", languageProvider.GetString(LanguageSection.OptionsStrings, "InteractiveDeploy"), CommandOptionType.NoValue));
            }
            command.OnExecute(async () =>
            {
                if (this.SupportsInteractiveMode && GetOption(OptionNames.Interactive).HasValue())
                {
                    SetInteractiveMode((true));
                }

                var code = await this.Run(command);
                if (code != 0)
                {
                    if (code == -1)
                    {
                        command.ShowHelp();
                    }
                }
            });
        }

        protected void ConfigureSubCommand(BaseCommand child, CommandLineApplication command)
        {
            command.Command(child.CommandName, child.Configure);
        }

        protected void SetInteractiveMode(bool mode)
        {
            this.InInteractiveMode = mode;
        }

        protected void AddToRegister(string key, CommandOption option)
        {
            _optionRegister.Add(key, option);
        }

        protected CommandOption GetOption(string key)
        {
            return _optionRegister[key];
        }

        protected string GetStringValueFromOption(string key)
        {
            var option = GetOption(key);
            if (option.HasValue())
            {
                return option.Value();
            }
            return string.Empty;
        }

        protected string GetStringFromUser(string optionName, string prompt, bool allowEmpty = false)
        {
            var option = GetStringValueFromOption(optionName);

            if (InInteractiveMode)
            {
                if (allowEmpty)
                {
                    if (string.IsNullOrEmpty(option))
                    {
                        option = Prompt.GetString(prompt);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(option) && !allowEmpty)
                    {
                        option = PromptForStringWithoutQuitting(prompt);
                    }
                }
            }

            return option;
        }

        protected string PromptForStringWithoutQuitting(string prompt)
        {
            string channel;
            do
            {
                channel = Prompt.GetString(prompt);
            } while (string.IsNullOrEmpty(channel));

            return channel;
        }

        protected string PromptForReleaseName()
        {
            string releaseName;

            do
            {
                releaseName = GetStringFromUser(OptionNames.ReleaseName, languageProvider.GetString(LanguageSection.UiStrings, "ReleaseNamePrompt"), allowEmpty: true);
            } while (InInteractiveMode && !string.IsNullOrEmpty(releaseName) && !SemanticVersion.TryParse(releaseName, out _));

            return releaseName;
        }

        protected async Task<bool> ValidateDeployment(EnvironmentDeployment deployment, IDeployer deployer)
        {
            if (deployment == null)
            {
                return true;
            }

            var result = await deployer.CheckDeployment(deployment);
            if (result.Success)
            {
                return true;
            }
            else
            {
                System.Console.WriteLine(languageProvider.GetString(LanguageSection.UiStrings, "Error") + result.ErrorMessage);
            }

            return false;
        }

        protected async Task<OctoPlusCore.Models.Environment> FetchEnvironmentFromUserInput(string environmentName)
        {
            var matchingEnvironments = await octoHelper.GetMatchingEnvironments(environmentName);

            if (matchingEnvironments.Count() > 1)
            {
                System.Console.WriteLine(languageProvider.GetString(LanguageSection.UiStrings, "TooManyMatchingEnvironments") + string.Join(", ", matchingEnvironments.Select(e => e.Name)));
                return null;
            }
            else if (!matchingEnvironments.Any())
            {
                System.Console.WriteLine(languageProvider.GetString(LanguageSection.UiStrings, "NoMatchingEnvironments"));
                return null;
            }

            return matchingEnvironments.First();
        }

        protected void FillRequiredVariables(List<ProjectDeployment> projects)
        {
            foreach (var project in projects)
            {
                if (project.RequiredVariables != null)
                {
                    foreach (var requirement in project.RequiredVariables)
                    {
                        do
                        {
                            var prompt = String.Format(languageProvider.GetString(LanguageSection.UiStrings, "VariablePrompt"), requirement.Name, project.ProjectName);
                            if (!string.IsNullOrEmpty(requirement.ExtraOptions))
                            {
                                prompt = prompt + String.Format(languageProvider.GetString(LanguageSection.UiStrings, "VariablePromptAllowedValues"), requirement.ExtraOptions);
                            }
                            requirement.Value = PromptForStringWithoutQuitting(prompt);
                        } while (InInteractiveMode && string.IsNullOrEmpty(requirement.Value));
                    }

                }
            }
        }

        public struct OptionNames
        {
            public const string Interactive = "interactive";
            public const string ApiKey = "apikey";
            public const string Url = "url";
            public const string ReleaseName = "ReleaseName";
        }

    }
}
