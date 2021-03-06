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
using CommandLine;
using OctoPlus.Startup;
using OctoPlusConsole.Console;
using OctoPlusConsole.Console.Interfaces;
using OctoPlusCore.Configuration;

namespace OctoPlusConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            System.Console.WriteLine("Starting in console mode...");
            var initResult = BootStrap.CheckConfigurationAndInit().GetAwaiter().GetResult();
            if (!initResult.ConfigResult.Success)
            {
                initResult.container.Configure(c => 
                {
                    c.For<IConsoleDoJob>().Use<ConsoleDoJob>();
                });
                System.Console.Write(string.Join(Environment.NewLine, initResult.ConfigResult.Errors));
                return;
            }
            var container = initResult.container;
            Parser.Default.ParseArguments<OctoPlusOptions>(args).WithParsed(opts => RunConsole(container, opts));
        }

        private static void RunConsole(StructureMap.IContainer container, OctoPlusOptions consoleOptions) {
            System.Console.WriteLine("Using key " + consoleOptions.ApiKey + " and profile at path " +
                                              consoleOptions.ProfileFile);
            var doJob = container.GetInstance<IConsoleDoJob>();
            doJob.StartJob(consoleOptions.ProfileFile, consoleOptions.ReleaseMessage, consoleOptions.ReleaseVersion,
                consoleOptions.ForceDeploymentIfSamePackage).GetAwaiter().GetResult();
        }
    }
}