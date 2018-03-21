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


using System.Collections.Generic;
using OctoPlus.Dtos.Interfaces;

namespace OctoPlus.Dtos
{
    public class EnvironmentDeployment : IOctoJob
    {
        public string EnvironmentId { get; set; }
        public string EnvironmentName { get; set; }
        public List<ProjectDeployment> ProjectDeployments { get; set; }
        public string ChannelName { get; set; }
        public bool DeployAsync { get; set; }
    }
}